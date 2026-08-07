using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

/// <summary>
/// 持久化跨重启恢复协调器。
/// 当前桌面观察是“现在发生了什么”的唯一权威事实；RecoverySession 只提供历史与意图。
/// R3 要求恢复决策建立在连续一致的 Stable Observation 上。
/// </summary>
public sealed class PersistedRecoveryCoordinator
{
    private const int UnresolvedCandidateLimit = 50;

    private readonly IRecoverySessionStore _store;
    private readonly IDesktopIconPositionWriter _positionWriter;
    private readonly StaThreadExecutor _staExecutor;
    private readonly RecoveryDesktopStateObserver _stateObserver;

    public PersistedRecoveryCoordinator(
        IRecoverySessionStore store,
        IDisplayTopologyReader topologyReader,
        IDesktopIconReader iconReader,
        IDesktopIconPositionWriter positionWriter,
        StaThreadExecutor staExecutor)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(topologyReader);
        ArgumentNullException.ThrowIfNull(iconReader);
        _positionWriter = positionWriter ?? throw new ArgumentNullException(nameof(positionWriter));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
        _stateObserver = new RecoveryDesktopStateObserver(topologyReader, iconReader, staExecutor);
    }

    /// <summary>
    /// 返回经过 Core 权威投影后的未解决会话。
    /// Store 只提供候选；Lifecycle / Disposition 拥有最终业务解释权。
    /// </summary>
    public async Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
        CancellationToken ct = default)
    {
        var candidates = await _store.ListUnresolvedAsync(UnresolvedCandidateLimit, ct);
        var projection = RecoverySessionCandidateProjector.Project(candidates);
        if (projection.HasCorruption)
        {
            throw new InvalidDataException(
                "恢复候选集合包含无法由 Core 安全解释的损坏记录。");
        }

        return projection.Unresolved;
    }

    public async Task<bool> HasUnresolvedAsync(CancellationToken ct = default)
    {
        try
        {
            // 不能在权威投影前用 limit=1 截断候选，否则一个被过滤的 resolved 候选
            // 可能遮住后续真正 unresolved 的记录。
            var candidates = await _store.ListUnresolvedAsync(UnresolvedCandidateLimit, ct);
            var projection = RecoverySessionCandidateProjector.Project(candidates);
            return projection.HasCorruption || projection.Unresolved.Count > 0;
        }
        catch (InvalidDataException)
        {
            return true;
        }
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
                "跨重启恢复 R3 暂不支持含临时中转的执行计划。",
                cancellationToken);
        }

        // 防止外部变化造成同一 StableId 在一次恢复调用中被反复自动写。
        // 一旦已经尝试过而它又回到 Desired，转人工处理，不做第二次猜测式写入。
        var attemptedStableIds = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StableReconciliationObservation observation;
            try
            {
                observation = await ObserveStableAndReconcileAsync(execPlan, cancellationToken);
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

            if (!observation.Stability.IsStable
                || observation.Stability.StableSample is null
                || observation.Reconciliation is null)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "RecoveryObservationNotStable",
                    FormatStabilityIssues(observation.Stability),
                    cancellationToken);
            }

            var stableSample = observation.Stability.StableSample;
            var reconciliation = observation.Reconciliation;

            // Stable Observe → Compare 先收敛所有不需要 Writer 的情况。
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

            // Stable Observation 与 Writer 之间仍存在极小 TOCTOU 窗口。
            // 在真正写入前再读一次 topology，只要物理坐标系有任何变化就立即停手。
            try
            {
                if (!_stateObserver.IsTopologyStillEquivalent(stableSample))
                {
                    return await PersistPositionUncertainAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        "RecoveryTopologyChangedBeforeWrite",
                        "稳定观察完成后、Writer 调用前显示拓扑发生变化；拒绝在已变化的物理坐标系中写入。",
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "RecoveryTopologyGuardFailed",
                    $"Writer 前 topology guard 失败：{ex.Message}",
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

            // Act 之后无论 Writer 返回成功、非精确还是抛异常，都必须再次获得稳定观察。
            // Writer 的返回值只进入 Journal，不直接决定当前事实。
            StableReconciliationObservation postObservation;
            try
            {
                postObservation = await ObserveStableAndReconcileAsync(execPlan, cancellationToken);
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

            if (!postObservation.Stability.IsStable
                || postObservation.Reconciliation is null)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "RecoveryPostWriteObservationNotStable",
                    FormatStabilityIssues(postObservation.Stability),
                    cancellationToken);
            }

            var postWriteReconciliation = postObservation.Reconciliation;

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

            // 如果 Writer 虽然抛异常/返回非精确，但稳定观察已证明全部 Desired，
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
                    ? $"Writer 抛出 {writerException.GetType().Name}，且稳定观察后“{step.StableId}”仍位于 Desired。"
                    : $"稳定观察后“{step.StableId}”仍位于 Desired。";

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

            // 当前 item 已由稳定观察证明回到 Original。
            // 先持久化这次 Writer 的历史证据，再进入下一轮 Stable Observe。
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

    private async Task<StableReconciliationObservation> ObserveStableAndReconcileAsync(
        TargetDisplayLayoutExecutionPlan executionPlan,
        CancellationToken ct)
    {
        var stability = await _stateObserver.ObserveStableAsync(executionPlan, ct);
        var reconciliation = stability.IsStable && stability.StableSample is not null
            ? RecoveryReconciler.Reconcile(executionPlan, stability.StableSample.Desktop)
            : null;

        return new StableReconciliationObservation(stability, reconciliation);
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

        return TargetDisplayLayoutExecutionRunResult.FromPersistedRecoveryTerminal(
            persisted.Fresh);
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

        return TargetDisplayLayoutExecutionRunResult.FromPersistedRecoveryTerminal(
            persisted.Fresh);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> PersistTerminalAsync(
        RecoverySessionRecord currentCp,
        RecoverySessionRecord final,
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

        return TargetDisplayLayoutExecutionRunResult.FromPersistedRecoveryTerminal(
            persisted.Fresh,
            isNoOp);
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

    private static string FormatStabilityIssues(RecoveryObservationStabilityResult stability)
    {
        if (stability.Issues.Count == 0)
            return $"连续观察状态为 {stability.Status}，无法证明当前桌面稳定。";

        return string.Join(" | ", stability.Issues.Select(issue => issue.Message));
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

    private sealed record StableReconciliationObservation(
        RecoveryObservationStabilityResult Stability,
        RecoveryReconciliationResult? Reconciliation);
}
