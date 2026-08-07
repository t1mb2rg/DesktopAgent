using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

/// <summary>
/// 持久化跨重启恢复协调器。
/// 当前桌面观察是“现在发生了什么”的唯一权威事实；RecoverySession 只提供历史与意图。
/// </summary>
public sealed class PersistedRecoveryCoordinator
{
    private readonly IRecoverySessionStore _store;
    private readonly IDisplayTopologyReader _topologyReader;
    private readonly IDesktopIconReader _iconReader;
    private readonly IDesktopIconPositionWriter _positionWriter;
    private readonly StaThreadExecutor _staExecutor;

    public PersistedRecoveryCoordinator(
        IRecoverySessionStore store,
        IDisplayTopologyReader topologyReader,
        IDesktopIconReader iconReader,
        IDesktopIconPositionWriter positionWriter,
        StaThreadExecutor staExecutor)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _topologyReader = topologyReader ?? throw new ArgumentNullException(nameof(topologyReader));
        _iconReader = iconReader ?? throw new ArgumentNullException(nameof(iconReader));
        _positionWriter = positionWriter ?? throw new ArgumentNullException(nameof(positionWriter));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
    }

    public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(CancellationToken ct = default)
        => _store.ListUnresolvedAsync(limit: 50, ct);

    public async Task<bool> HasUnresolvedAsync(CancellationToken ct = default)
    {
        try { return (await _store.ListUnresolvedAsync(limit: 1, ct)).Count > 0; }
        catch (InvalidDataException) { return true; }
    }

    public async Task<TargetDisplayLayoutExecutionRunResult> RecoverAsync(
        RecoverySessionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Journal 只作为历史/意图入口。开始恢复前必须 fresh Get，避免使用 UI 手里的旧副本。
        var currentCp = await _store.GetAsync(record.SessionId, cancellationToken);
        if (currentCp is null)
        {
            return UnpersistedPositionUncertain(
                record,
                record.RollbackAttempts,
                "SessionNotFound",
                "恢复会话不存在或已被删除。");
        }

        var execPlan = currentCp.ExecutionPlan;
        var rollbackAttempts = new List<TargetDisplayLayoutExecutionAttemptResult>(currentCp.RollbackAttempts);
        var completedRollbackIndexes = new HashSet<int>(currentCp.CompletedRollbackSourceStepIndexes);
        var nextAttemptIndex = currentCp.ForwardAttempts.Count + currentCp.RollbackAttempts.Count;

        if (execPlan.UsesTemporaryStaging)
        {
            return await PersistPositionUncertainAsync(
                currentCp,
                rollbackAttempts,
                completedRollbackIndexes,
                "RecoveryStagingNotSupported",
                "跨重启恢复 R2 暂不支持含临时中转的执行计划。",
                cancellationToken);
        }

        // 防止外部变化造成同一 StableId 在一次恢复调用中被反复自动写。
        // 一旦已经尝试过而它又回到 Desired，转人工处理，不做第二次猜测式写入。
        var attemptedStableIds = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RecoveryReconciliationResult reconciliation;
            try
            {
                reconciliation = await ObserveAndReconcileAsync(execPlan, cancellationToken);
            }
            catch (Exception ex)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "RecoveryObservationFailed",
                    $"读取当前真实桌面失败：{ex.Message}",
                    cancellationToken);
            }

            // Observe → Compare 先收敛所有不需要 Writer 的情况。
            switch (reconciliation.State)
            {
                case RecoveryReconciliationState.NoWork:
                    return await PersistCompletedAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        isNoOp: true,
                        cancellationToken);

                case RecoveryReconciliationState.Committed:
                    return await PersistCompletedAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        isNoOp: false,
                        cancellationToken);

                case RecoveryReconciliationState.Restored:
                    return await PersistRestoredAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        cancellationToken);

                case RecoveryReconciliationState.NeedsAttention:
                    return await PersistPositionUncertainAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        "RecoveryObservationNeedsAttention",
                        FormatReconciliationIssues(reconciliation),
                        cancellationToken);

                case RecoveryReconciliationState.MixedKnown:
                    break;

                default:
                    throw new InvalidOperationException(
                        $"未处理的 RecoveryReconciliationState：{reconciliation.State}。");
            }

            // MixedKnown：只对“当前真实位于 Desired”的项目执行补偿写。
            // 已在 Original 的项目不写；Journal 的 Completed* 索引不参与这里的选择。
            var itemToRestore = reconciliation.Items
                .Where(item => item.Relation == RecoveryItemRelation.AtDesired)
                .Select(item => new
                {
                    Item = item,
                    Step = execPlan.Steps.Single(
                        step => string.Equals(step.StableId, item.StableId, StringComparison.Ordinal))
                })
                .OrderByDescending(x => x.Step.StepIndex)
                .FirstOrDefault();

            if (itemToRestore is null)
            {
                return await PersistKnownIncompleteAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    reconciliation,
                    "RecoveryMixedStateHasNoRollbackCandidate",
                    "当前桌面为混合已知状态，但不存在可安全回滚的 Desired 项目。",
                    cancellationToken);
            }

            var step = itemToRestore.Step;
            if (!attemptedStableIds.Add(step.StableId))
            {
                return await PersistKnownIncompleteAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    reconciliation,
                    "RecoveryRepeatedDivergence",
                    $"“{step.StableId}”在本次恢复中已执行过补偿写，但再次出现在 Desired；停止自动写入。",
                    cancellationToken);
            }

            // Writer 前再次检查能力。Auto Arrange 打开时位置写入不具备确定性。
            DesktopIconPositioningCapabilities capabilities;
            try
            {
                capabilities = await _staExecutor.RunAsync(() => _positionWriter.GetCapabilities());
            }
            catch (Exception ex)
            {
                return await PersistKnownIncompleteAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    reconciliation,
                    "RecoveryCapabilitiesReadFailed",
                    $"无法确认桌面定位能力：{ex.Message}",
                    cancellationToken);
            }

            if (capabilities.IsAutoArrangeEnabled)
            {
                return await PersistKnownIncompleteAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    reconciliation,
                    "RecoveryAutoArrangeEnabled",
                    "桌面已启用自动排列，当前状态已知但不允许自动执行位置写入。",
                    cancellationToken);
            }

            DesktopIconMoveResult? moveResult = null;
            Exception? writerException = null;
            TargetDisplayLayoutExecutionAttemptResult attempt;

            try
            {
                moveResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(step.StableId, step.FromPosition));

                if (!string.Equals(moveResult.StableId, step.StableId, StringComparison.Ordinal))
                {
                    attempt = TargetDisplayLayoutExecutionAttemptResult.FromRollbackStableIdMismatch(
                        nextAttemptIndex,
                        step.StepIndex,
                        step,
                        step.FromPosition,
                        step.ToPosition,
                        moveResult);
                }
                else if (moveResult.PreviousPosition != step.ToPosition)
                {
                    attempt = TargetDisplayLayoutExecutionAttemptResult.FromRollbackPreviousPositionMismatch(
                        nextAttemptIndex,
                        step.StepIndex,
                        step,
                        step.FromPosition,
                        step.ToPosition,
                        moveResult);
                }
                else if (moveResult.IsExact)
                {
                    attempt = TargetDisplayLayoutExecutionAttemptResult.FromExactRollback(
                        nextAttemptIndex,
                        step.StepIndex,
                        step,
                        step.FromPosition,
                        step.ToPosition,
                        moveResult);
                    completedRollbackIndexes.Add(step.StepIndex);
                }
                else
                {
                    attempt = TargetDisplayLayoutExecutionAttemptResult.FromInexactRollback(
                        nextAttemptIndex,
                        step.StepIndex,
                        step,
                        step.FromPosition,
                        step.ToPosition,
                        moveResult);
                }
            }
            catch (Exception ex)
            {
                writerException = ex;
                attempt = TargetDisplayLayoutExecutionAttemptResult.FromRollbackException(
                    nextAttemptIndex,
                    step.StepIndex,
                    step,
                    step.FromPosition,
                    step.ToPosition,
                    ex);
            }

            rollbackAttempts.Add(attempt);
            nextAttemptIndex++;

            // Act 之后无论 Writer 返回成功、非精确还是抛异常，都重新读取真实桌面。
            // Writer 的返回值只进入 Journal，不直接决定当前事实。
            RecoveryReconciliationResult postWriteReconciliation;
            try
            {
                postWriteReconciliation = await ObserveAndReconcileAsync(execPlan, cancellationToken);
            }
            catch (Exception ex)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "RecoveryPostWriteObservationFailed",
                    $"Writer 后无法重新读取真实桌面：{ex.Message}",
                    cancellationToken);
            }

            // StableIdMismatch 可能意味着 Writer 影响了错误对象。
            // 即便目标图标看起来安全，也无法证明未影响计划外对象，因此必须 fail-closed。
            if (moveResult is not null
                && !string.Equals(moveResult.StableId, step.StableId, StringComparison.Ordinal))
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "RollbackStableIdMismatch",
                    $"Writer 返回 StableId “{moveResult.StableId}”，预期 “{step.StableId}”。",
                    cancellationToken);
            }

            if (postWriteReconciliation.State == RecoveryReconciliationState.NeedsAttention)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "RecoveryPostWriteNeedsAttention",
                    FormatReconciliationIssues(postWriteReconciliation),
                    cancellationToken);
            }

            // 如果 Writer 虽然抛异常/返回非精确，但真实桌面已证明全部 Desired，
            // 则当前事实是 Forward 已完整存在，直接识别为 Committed。
            if (postWriteReconciliation.State == RecoveryReconciliationState.Committed)
            {
                return await PersistCompletedAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    isNoOp: false,
                    cancellationToken);
            }

            if (postWriteReconciliation.State == RecoveryReconciliationState.Restored)
            {
                return await PersistRestoredAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    cancellationToken);
            }

            if (postWriteReconciliation.State == RecoveryReconciliationState.NoWork)
            {
                return await PersistCompletedAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    isNoOp: true,
                    cancellationToken);
            }

            var postItem = postWriteReconciliation.Items.Single(
                item => string.Equals(item.StableId, step.StableId, StringComparison.Ordinal));

            if (postItem.Relation == RecoveryItemRelation.AtDesired)
            {
                var detail = writerException is not null
                    ? $"Writer 抛出 {writerException.GetType().Name}，且重新观察后“{step.StableId}”仍位于 Desired。"
                    : $"重新观察后“{step.StableId}”仍位于 Desired。";

                return await PersistKnownIncompleteAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    postWriteReconciliation,
                    "RecoveryWriteDidNotRestore",
                    detail,
                    cancellationToken);
            }

            if (postItem.Relation != RecoveryItemRelation.AtOriginal)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "RecoveryPostWriteUnexpectedRelation",
                    $"“{step.StableId}”Writer 后关系为 {postItem.Relation}。",
                    cancellationToken);
            }

            // 当前 item 已由真实桌面证明回到 Original。
            // 先持久化这次 Writer 的历史证据，再进入下一轮全量 Observe。
            var checkpoint = await TryPersistExecutingCheckpointAsync(
                currentCp,
                rollbackAttempts,
                completedRollbackIndexes,
                cancellationToken);
            if (checkpoint is null)
            {
                return UnpersistedPositionUncertain(
                    currentCp,
                    rollbackAttempts,
                    "CheckpointPersistFailed",
                    "Writer 后真实桌面已可判定，但恢复检查点持久化/readback 失败。");
            }

            currentCp = checkpoint;
        }
    }

    private async Task<RecoveryReconciliationResult> ObserveAndReconcileAsync(
        TargetDisplayLayoutExecutionPlan executionPlan,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var displays = _topologyReader.GetDisplays();
        var rawRead = await _staExecutor.RunAsync(() => _iconReader.ReadDesktopIcons());

        var assignedIcons = rawRead.Icons.Select(icon =>
        {
            var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays);
            return icon with { MonitorDeviceName = monitor?.DeviceName };
        }).ToArray();

        var observed = new DesktopIconReadResult(
            assignedIcons,
            rawRead.FailedItemCount,
            rawRead.ConnectionMethod,
            rawRead.ReadTime,
            rawRead.PrimaryPathException);

        return RecoveryReconciler.Reconcile(executionPlan, observed);
    }

    private async Task<RecoverySessionRecord?> TryPersistExecutingCheckpointAsync(
        RecoverySessionRecord currentCp,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        IReadOnlyCollection<int> completedRollbackIndexes,
        CancellationToken ct)
    {
        var checkpoint = currentCp with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            RequiresManualRecovery = false,
            IsPositionUncertain = false,
            LastFailedStepIndex = null,
            RollbackAttempts = rollbackAttempts.OrderBy(a => a.AttemptIndex).ToArray(),
            CompletedRollbackSourceStepIndexes = completedRollbackIndexes.OrderBy(x => x).ToArray(),
            RecoveryFailureType = null,
            RecoveryFailureMessage = null,
            VerificationResult = null,
            Revision = currentCp.Revision,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var persisted = await TryPersistAndReadbackAsync(checkpoint, ct);
        return persisted.Ok ? persisted.Fresh : null;
    }

    private Task<TargetDisplayLayoutExecutionRunResult> PersistCompletedAsync(
        RecoverySessionRecord currentCp,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        IReadOnlyCollection<int> completedRollbackIndexes,
        bool isNoOp,
        CancellationToken ct)
    {
        var verification = TargetDisplayLayoutExecutionVerificationResult.ForwardPassed(DateTimeOffset.UtcNow);
        var final = currentCp with
        {
            Lifecycle = RecoverySessionLifecycle.Completed,
            RequiresManualRecovery = false,
            IsPositionUncertain = false,
            LastFailedStepIndex = null,
            RollbackAttempts = rollbackAttempts.OrderBy(a => a.AttemptIndex).ToArray(),
            CompletedRollbackSourceStepIndexes = completedRollbackIndexes.OrderBy(x => x).ToArray(),
            RecoveryFailureType = null,
            RecoveryFailureMessage = null,
            VerificationResult = verification,
            Revision = currentCp.Revision,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        return PersistTerminalAsync(
            currentCp,
            final,
            isNoOp ? TargetDisplayLayoutExecutionRunStatus.CompletedNoOp : TargetDisplayLayoutExecutionRunStatus.Completed,
            isSuccess: true,
            isNoOp,
            rollbackAttempts,
            "CompletedTerminalPersistFailed",
            ct);
    }

    private Task<TargetDisplayLayoutExecutionRunResult> PersistRestoredAsync(
        RecoverySessionRecord currentCp,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        IReadOnlyCollection<int> completedRollbackIndexes,
        CancellationToken ct)
    {
        var verification = TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(DateTimeOffset.UtcNow);
        var final = currentCp with
        {
            Lifecycle = RecoverySessionLifecycle.FailedAndRolledBack,
            RequiresManualRecovery = false,
            IsPositionUncertain = false,
            LastFailedStepIndex = null,
            RollbackAttempts = rollbackAttempts.OrderBy(a => a.AttemptIndex).ToArray(),
            CompletedRollbackSourceStepIndexes = completedRollbackIndexes.OrderBy(x => x).ToArray(),
            RecoveryFailureType = null,
            RecoveryFailureMessage = null,
            VerificationResult = verification,
            Revision = currentCp.Revision,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        return PersistTerminalAsync(
            currentCp,
            final,
            TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack,
            isSuccess: false,
            isNoOp: false,
            rollbackAttempts,
            "RestoredTerminalPersistFailed",
            ct);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> PersistKnownIncompleteAsync(
        RecoverySessionRecord currentCp,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        IReadOnlyCollection<int> completedRollbackIndexes,
        RecoveryReconciliationResult reconciliation,
        string recoveryFailureType,
        string recoveryFailureMessage,
        CancellationToken ct)
    {
        var issues = reconciliation.Items
            .Where(item => item.Relation == RecoveryItemRelation.AtDesired)
            .Select(item => $"“{item.StableId}”仍位于 Desired={item.DesiredPosition}。")
            .ToList();
        if (issues.Count == 0)
            issues.Add(recoveryFailureMessage);

        var verification = TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(
            issues.Count,
            issues,
            DateTimeOffset.UtcNow);

        var final = currentCp with
        {
            Lifecycle = RecoverySessionLifecycle.FailedRollbackIncomplete,
            RequiresManualRecovery = true,
            IsPositionUncertain = false,
            LastFailedStepIndex = null,
            RollbackAttempts = rollbackAttempts.OrderBy(a => a.AttemptIndex).ToArray(),
            CompletedRollbackSourceStepIndexes = completedRollbackIndexes.OrderBy(x => x).ToArray(),
            ForwardFailureType = currentCp.ForwardFailureType ?? "InterruptedExecution",
            ForwardFailureMessage = currentCp.ForwardFailureMessage
                ?? "应用重启时发现未终结的持久化执行会话。",
            RecoveryFailureType = recoveryFailureType,
            RecoveryFailureMessage = recoveryFailureMessage,
            VerificationResult = verification,
            Revision = currentCp.Revision,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var persisted = await TryPersistAndReadbackAsync(final, ct);
        if (!persisted.Ok || persisted.Fresh is null)
        {
            return UnpersistedPositionUncertain(
                currentCp,
                rollbackAttempts,
                "TerminalPersistFailed",
                $"{recoveryFailureType} 后终态持久化/readback 失败。");
        }

        var fresh = persisted.Fresh;
        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
            false,
            false,
            null,
            fresh.SafetySnapshotId,
            fresh.ForwardAttempts,
            fresh.RollbackAttempts,
            null,
            true,
            false,
            fresh.ForwardFailureType,
            fresh.ForwardFailureMessage,
            fresh.RecoveryFailureType,
            fresh.RecoveryFailureMessage,
            fresh.VerificationResult);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> PersistPositionUncertainAsync(
        RecoverySessionRecord currentCp,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        IReadOnlyCollection<int> completedRollbackIndexes,
        string recoveryFailureType,
        string recoveryFailureMessage,
        CancellationToken ct)
    {
        var final = currentCp with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
            LastFailedStepIndex = null,
            RollbackAttempts = rollbackAttempts.OrderBy(a => a.AttemptIndex).ToArray(),
            CompletedRollbackSourceStepIndexes = completedRollbackIndexes.OrderBy(x => x).ToArray(),
            ForwardFailureType = currentCp.ForwardFailureType ?? "InterruptedExecution",
            ForwardFailureMessage = currentCp.ForwardFailureMessage
                ?? "应用重启时发现未终结的持久化执行会话。",
            RecoveryFailureType = recoveryFailureType,
            RecoveryFailureMessage = recoveryFailureMessage,
            VerificationResult = null,
            Revision = currentCp.Revision,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var persisted = await TryPersistAndReadbackAsync(final, ct);
        if (!persisted.Ok || persisted.Fresh is null)
        {
            return UnpersistedPositionUncertain(
                currentCp,
                rollbackAttempts,
                "TerminalPersistFailed",
                $"{recoveryFailureType} 后 PositionUncertain 持久化/readback 失败。");
        }

        var fresh = persisted.Fresh;
        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
            false,
            false,
            null,
            fresh.SafetySnapshotId,
            fresh.ForwardAttempts,
            fresh.RollbackAttempts,
            null,
            true,
            true,
            fresh.ForwardFailureType,
            fresh.ForwardFailureMessage,
            fresh.RecoveryFailureType,
            fresh.RecoveryFailureMessage,
            fresh.VerificationResult);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> PersistTerminalAsync(
        RecoverySessionRecord currentCp,
        RecoverySessionRecord final,
        TargetDisplayLayoutExecutionRunStatus status,
        bool isSuccess,
        bool isNoOp,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        string persistFailureType,
        CancellationToken ct)
    {
        var persisted = await TryPersistAndReadbackAsync(final, ct);
        if (!persisted.Ok || persisted.Fresh is null)
        {
            return UnpersistedPositionUncertain(
                currentCp,
                rollbackAttempts,
                persistFailureType,
                "安全终态持久化或 readback 失败。");
        }

        var fresh = persisted.Fresh;
        return new TargetDisplayLayoutExecutionRunResult(
            status,
            isSuccess,
            isNoOp,
            null,
            fresh.SafetySnapshotId,
            fresh.ForwardAttempts,
            fresh.RollbackAttempts,
            null,
            false,
            false,
            fresh.ForwardFailureType,
            fresh.ForwardFailureMessage,
            fresh.RecoveryFailureType,
            fresh.RecoveryFailureMessage,
            fresh.VerificationResult);
    }

    private async Task<(bool Ok, RecoverySessionRecord? Fresh)> TryPersistAndReadbackAsync(
        RecoverySessionRecord candidate,
        CancellationToken ct)
    {
        try
        {
            candidate.Validate();
            var updated = await _store.UpdateAsync(candidate, ct);
            var fresh = await _store.GetAsync(candidate.SessionId, ct);
            if (fresh is null
                || !TargetDisplayLayoutExecutionCoordinator.RecordsAreEquivalent(updated, fresh))
            {
                return (false, null);
            }

            return (true, fresh);
        }
        catch
        {
            return (false, null);
        }
    }

    private static string FormatReconciliationIssues(RecoveryReconciliationResult reconciliation)
    {
        if (reconciliation.Issues.Count == 0)
            return $"当前桌面对账状态为 {reconciliation.State}，无法安全自动恢复。";

        return string.Join(" | ", reconciliation.Issues.Select(issue => issue.Message));
    }

    private static TargetDisplayLayoutExecutionRunResult UnpersistedPositionUncertain(
        RecoverySessionRecord record,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        string recoveryFailureType,
        string recoveryFailureMessage)
    {
        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
            false,
            false,
            null,
            record.SafetySnapshotId,
            record.ForwardAttempts,
            rollbackAttempts,
            null,
            true,
            true,
            record.ForwardFailureType ?? "InterruptedExecution",
            record.ForwardFailureMessage
                ?? "应用重启时发现未终结的持久化执行会话。",
            recoveryFailureType,
            recoveryFailureMessage,
            null);
    }
}
