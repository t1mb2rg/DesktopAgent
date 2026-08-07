using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

/// <summary>
/// temporary staging / cycle 计划的跨重启恢复器。
///
/// 只根据 fresh stable observation 唯一匹配的 ExecutionPlan prefix 决定下一步：
/// 当前为 prefix k 时，只允许撤销 step k-1；Writer 后必须再次稳定观察并精确收敛到
/// prefix k-1，或收敛到安全终态。Journal 仅记录尝试证据，不授权当前进度。
/// </summary>
internal sealed class PersistedStagingRecoveryExecutor
{
    private readonly IRecoverySessionStore _store;
    private readonly IDesktopIconPositionWriter _positionWriter;
    private readonly StaThreadExecutor _staExecutor;
    private readonly RecoveryDesktopStateObserver _stateObserver;

    public PersistedStagingRecoveryExecutor(
        IRecoverySessionStore store,
        IDesktopIconPositionWriter positionWriter,
        StaThreadExecutor staExecutor,
        RecoveryDesktopStateObserver stateObserver)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _positionWriter = positionWriter ?? throw new ArgumentNullException(nameof(positionWriter));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
        _stateObserver = stateObserver ?? throw new ArgumentNullException(nameof(stateObserver));
    }

    public async Task<TargetDisplayLayoutExecutionRunResult> RecoverAsync(
        RecoverySessionRecord initialCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialCheckpoint);

        var execPlan = initialCheckpoint.ExecutionPlan;
        if (!execPlan.UsesTemporaryStaging)
            throw new ArgumentException(
                "PersistedStagingRecoveryExecutor 只接受 UsesTemporaryStaging=true 的计划。",
                nameof(initialCheckpoint));

        var currentCp = initialCheckpoint;
        var rollbackAttempts = new List<TargetDisplayLayoutExecutionAttemptResult>(
            currentCp.RollbackAttempts);
        var completedRollbackIndexes = new HashSet<int>(
            currentCp.CompletedRollbackSourceStepIndexes);
        var nextAttemptIndex = currentCp.ForwardAttempts.Count + currentCp.RollbackAttempts.Count;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StablePrefixObservation observation;
            try
            {
                observation = await ObserveStablePrefixAsync(currentCp, cancellationToken);
            }
            catch (Exception ex)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "StagingRecoveryObservationFailed",
                    $"读取 staging 当前真实桌面失败：{ex.Message}",
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
                    "StagingRecoveryObservationNotStable",
                    FormatStabilityIssues(observation.Stability),
                    cancellationToken);
            }

            var stableSample = observation.Stability.StableSample;
            var reconciliation = observation.Reconciliation;

            switch (reconciliation.State)
            {
                case RecoveryExecutionPrefixState.NoWork:
                    return await PersistCompletedAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        isNoOp: true,
                        cancellationToken);

                case RecoveryExecutionPrefixState.Original:
                    return await PersistRestoredAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        cancellationToken);

                case RecoveryExecutionPrefixState.Committed:
                    return await PersistCompletedAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        isNoOp: false,
                        cancellationToken);

                case RecoveryExecutionPrefixState.NeedsAttention:
                    return await PersistPositionUncertainAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        "StagingRecoveryPrefixNeedsAttention",
                        FormatPrefixIssues(reconciliation),
                        cancellationToken);

                case RecoveryExecutionPrefixState.Intermediate:
                    break;

                default:
                    throw new InvalidOperationException(
                        $"未处理的 RecoveryExecutionPrefixState：{reconciliation.State}。");
            }

            if (reconciliation.AppliedStepCount is not int appliedStepCount
                || appliedStepCount <= 0
                || appliedStepCount >= execPlan.Steps.Count)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "StagingRecoveryInvalidPrefixProgress",
                    $"Intermediate prefix 的 AppliedStepCount={reconciliation.AppliedStepCount?.ToString() ?? "<null>"} 非法。",
                    cancellationToken);
            }

            var rollbackStepIndex = appliedStepCount - 1;
            var step = execPlan.Steps[rollbackStepIndex];

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
                    "StagingRecoveryCapabilitiesReadFailed",
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
                    "StagingRecoveryAutoArrangeEnabled",
                    "桌面已启用自动排列；当前 staging prefix 已知，但禁止执行补偿写。",
                    cancellationToken);
            }

            try
            {
                if (!_stateObserver.IsTopologyStillEquivalent(stableSample))
                {
                    return await PersistPositionUncertainAsync(
                        currentCp,
                        rollbackAttempts,
                        completedRollbackIndexes,
                        "StagingRecoveryTopologyChangedBeforeWrite",
                        "稳定观察完成后、Writer 调用前显示拓扑发生变化；拒绝在变化后的坐标系中回退 staging step。",
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "StagingRecoveryTopologyGuardFailed",
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

            StablePrefixObservation postObservation;
            try
            {
                postObservation = await ObserveStablePrefixAsync(currentCp, cancellationToken);
            }
            catch (Exception ex)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "StagingRecoveryPostWriteObservationFailed",
                    $"Writer 后无法重新读取 staging 当前事实：{ex.Message}",
                    cancellationToken);
            }

            if (!postObservation.Stability.IsStable
                || postObservation.Reconciliation is null)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "StagingRecoveryPostWriteObservationNotStable",
                    FormatStabilityIssues(postObservation.Stability),
                    cancellationToken);
            }

            var postReconciliation = postObservation.Reconciliation;

            // StableIdMismatch 可能意味着 Writer 改动了计划外对象；即使计划内图标又落在合法 prefix，
            // 也无法证明没有额外副作用，因此必须 PositionUncertain。
            if (moveResult is not null
                && !string.Equals(moveResult.StableId, step.StableId, StringComparison.Ordinal))
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "StagingRollbackStableIdMismatch",
                    $"Writer 返回 StableId “{moveResult.StableId}”，预期 “{step.StableId}”。",
                    cancellationToken);
            }

            if (postReconciliation.State == RecoveryExecutionPrefixState.NeedsAttention)
            {
                return await PersistPositionUncertainAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    "StagingRecoveryPostWritePrefixNeedsAttention",
                    FormatPrefixIssues(postReconciliation),
                    cancellationToken);
            }

            if (postReconciliation.State == RecoveryExecutionPrefixState.Committed)
            {
                return await PersistCompletedAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    isNoOp: false,
                    cancellationToken);
            }

            if (postReconciliation.State == RecoveryExecutionPrefixState.Original)
            {
                return await PersistRestoredAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    cancellationToken);
            }

            if (postReconciliation.State == RecoveryExecutionPrefixState.NoWork)
            {
                return await PersistCompletedAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    isNoOp: true,
                    cancellationToken);
            }

            var expectedAppliedStepCount = appliedStepCount - 1;
            if (postReconciliation.AppliedStepCount == expectedAppliedStepCount)
            {
                // Writer 返回值只记录历史。即便 Writer 抛异常或返回非精确，只要 fresh stable
                // observation 已证明严格进入 prefix k-1，就可以安全持久化 checkpoint 并继续。
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
                        "StagingCheckpointPersistFailed",
                        "Writer 后真实桌面已精确收敛到前一个 staging prefix，但恢复检查点持久化/readback 失败。");
                }

                currentCp = checkpoint;
                continue;
            }

            if (postReconciliation.AppliedStepCount == appliedStepCount)
            {
                var detail = writerException is not null
                    ? $"Writer 抛出 {writerException.GetType().Name}，且稳定观察仍停留在 prefix {appliedStepCount}。"
                    : $"稳定观察仍停留在 prefix {appliedStepCount}，本次补偿写未使计划回退。";

                return await PersistKnownIncompleteAsync(
                    currentCp,
                    rollbackAttempts,
                    completedRollbackIndexes,
                    postReconciliation,
                    "StagingRecoveryWriteDidNotRollbackPrefix",
                    detail,
                    cancellationToken);
            }

            // 当前事实仍然是某个唯一合法 prefix，因此位置本身可证明；但它没有严格从 k 变成 k-1，
            // 说明 Writer 窗口内存在额外变化。停止自动恢复，保留 KnownIncomplete 而不是继续猜。
            return await PersistKnownIncompleteAsync(
                currentCp,
                rollbackAttempts,
                completedRollbackIndexes,
                postReconciliation,
                "StagingRecoveryUnexpectedPrefixTransition",
                $"预期 Writer 后从 prefix {appliedStepCount} 收敛到 {expectedAppliedStepCount}，" +
                $"实际唯一匹配 prefix {postReconciliation.AppliedStepCount?.ToString() ?? "<null>"}。",
                cancellationToken);
        }
    }

    private async Task<StablePrefixObservation> ObserveStablePrefixAsync(
        RecoverySessionRecord checkpoint,
        CancellationToken ct)
    {
        var stability = await _stateObserver.ObserveStableAsync(checkpoint.ExecutionPlan, ct);
        var reconciliation = stability.IsStable && stability.StableSample is not null
            ? RecoveryExecutionPrefixReconciler.Reconcile(
                checkpoint.SourceRestorePlan,
                checkpoint.ExecutionPlan,
                stability.StableSample.Desktop)
            : null;

        return new StablePrefixObservation(stability, reconciliation);
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
            "StagingCompletedTerminalPersistFailed",
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
            "StagingRestoredTerminalPersistFailed",
            ct);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> PersistKnownIncompleteAsync(
        RecoverySessionRecord currentCp,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        IReadOnlyCollection<int> completedRollbackIndexes,
        RecoveryExecutionPrefixReconciliationResult reconciliation,
        string recoveryFailureType,
        string recoveryFailureMessage,
        CancellationToken ct)
    {
        var issue = reconciliation.AppliedStepCount is int prefix
            ? $"当前桌面唯一匹配合法 staging prefix {prefix}，但自动恢复未能安全继续。"
            : recoveryFailureMessage;

        var verification = TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(
            1,
            new[] { issue },
            DateTimeOffset.UtcNow);

        var final = currentCp with
        {
            Lifecycle = RecoverySessionLifecycle.FailedRollbackIncomplete,
            LastFailedStepIndex = null,
            RollbackAttempts = rollbackAttempts.OrderBy(a => a.AttemptIndex).ToArray(),
            CompletedRollbackSourceStepIndexes = completedRollbackIndexes.OrderBy(x => x).ToArray(),
            ForwardFailureType = currentCp.ForwardFailureType ?? "InterruptedExecution",
            ForwardFailureMessage = currentCp.ForwardFailureMessage
                ?? "应用重启时发现未终结的 staging 执行会话。",
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
                "StagingTerminalPersistFailed",
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
                ?? "应用重启时发现未终结的 staging 执行会话。",
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
                "StagingTerminalPersistFailed",
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
                "staging 安全终态持久化或 readback 失败。");
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

    private static string FormatPrefixIssues(
        RecoveryExecutionPrefixReconciliationResult reconciliation)
    {
        if (reconciliation.Issues.Count == 0)
            return $"staging prefix 对账状态为 {reconciliation.State}，无法安全自动恢复。";

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
        => new(
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
                ?? "应用重启时发现未终结的 staging 执行会话。",
            recoveryFailureType,
            recoveryFailureMessage,
            null);

    private sealed record StablePrefixObservation(
        RecoveryObservationStabilityResult Stability,
        RecoveryExecutionPrefixReconciliationResult? Reconciliation);
}
