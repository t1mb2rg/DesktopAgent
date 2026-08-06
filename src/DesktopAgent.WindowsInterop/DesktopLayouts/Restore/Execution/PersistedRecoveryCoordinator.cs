using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

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
        // ── 0. fresh Get ──
        var currentCp = await _store.GetAsync(record.SessionId, cancellationToken);
        if (currentCp is null)
            return PositionUncertain(record, new List<TargetDisplayLayoutExecutionAttemptResult>(),
                "SessionNotFound", "GetAsync 返回 null。");

        var execPlan = currentCp.ExecutionPlan;
        // ── 拒绝 staging ──
        if (execPlan.UsesTemporaryStaging)
        {
            return await PersistPreWriteFailureAsync(currentCp,
                new List<TargetDisplayLayoutExecutionAttemptResult>(),
                new List<int>(currentCp.CompletedRollbackSourceStepIndexes),
                "RecoveryStagingNotSupported", "跨重启恢复不支持含临时中转的计划。",
                cancellationToken);
        }

        var allRbAttempts = new List<TargetDisplayLayoutExecutionAttemptResult>(currentCp.RollbackAttempts);
        var attemptIndex = currentCp.ForwardAttempts.Count + currentCp.RollbackAttempts.Count;
        var rbIndexes = new List<int>(currentCp.CompletedRollbackSourceStepIndexes);

        // 待回滚: CompletedFwd − CompletedRb, 降序
        var pendingSteps = currentCp.CompletedForwardStepIndexes
            .Except(currentCp.CompletedRollbackSourceStepIndexes)
            .OrderByDescending(x => x)
            .ToList();

        if (pendingSteps.Count == 0)
        {
            // 无需回滚 → 持久化终态
            return await PersistRecoveryTerminalAsync(currentCp, allRbAttempts, rbIndexes,
                Array.Empty<string>(), 0, cancellationToken);
        }

        // 验证所有 StepIndex 在合法范围
        foreach (var si in pendingSteps)
        {
            if (si < 0 || si >= execPlan.Steps.Count)
            {
                return await PersistPreWriteFailureAsync(currentCp, allRbAttempts, rbIndexes,
                    "RecoveryInvalidStepIndex",
                    $"Pending step index {si} 超出范围 [0, {execPlan.Steps.Count})。",
                    cancellationToken);
            }
        }

        // ── 1. fresh 环境检查 ──
        DesktopIconPositioningCapabilities capabilities;
        IReadOnlyList<DisplayDescriptor> displays;
        DesktopIconReadResult currentRead;
        try
        {
            capabilities = await _staExecutor.RunAsync(() => _positionWriter.GetCapabilities());
            displays = _topologyReader.GetDisplays();
            var rawRead = await _staExecutor.RunAsync(() => _iconReader.ReadDesktopIcons());
            var assignedIcons = rawRead.Icons.Select(icon =>
            {
                var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays);
                return icon with { MonitorDeviceName = monitor?.DeviceName };
            }).ToList();
            currentRead = new DesktopIconReadResult(assignedIcons, rawRead.FailedItemCount,
                rawRead.ConnectionMethod, rawRead.ReadTime, rawRead.PrimaryPathException);
        }
        catch (Exception ex)
        {
            return await PersistPreWriteFailureAsync(currentCp, allRbAttempts, rbIndexes,
                "RecoveryEnvironmentReadFailed", $"环境读取失败：{ex.Message}",
                cancellationToken);
        }

        if (currentRead.FailedItemCount != 0)
        {
            return await PersistPreWriteFailureAsync(currentCp, allRbAttempts, rbIndexes,
                "RecoveryDesktopReadIncomplete",
                $"FailedItemCount={currentRead.FailedItemCount}。",
                cancellationToken);
        }

        // ── 2. 逐步骤 Rollback ──
        foreach (var si in pendingSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = execPlan.Steps[si];
            var rollbackTarget = step.FromPosition;

            // 身份检查 — Writer 前，不得创建 Attempt
            var currentIcon = currentRead.Icons.FirstOrDefault(
                i => string.Equals(i.StableId, step.StableId, StringComparison.Ordinal));
            if (currentIcon is null)
            {
                return await PersistPreWriteFailureAsync(currentCp, allRbAttempts, rbIndexes,
                    "RecoveryStableIdMissing", $"\"{step.StableId}\" 未找到。",
                    cancellationToken);
            }

            if (currentIcon.PixelPosition != step.ToPosition)
            {
                return await PersistPreWriteFailureAsync(currentCp, allRbAttempts, rbIndexes,
                    "RecoveryPreconditionPositionMismatch",
                    $"\"{step.StableId}\" 当前位置={currentIcon.PixelPosition} ≠ 预期={step.ToPosition}",
                    cancellationToken);
            }

            // ── 调用 Writer ──
            DesktopIconMoveResult moveResult;
            try
            {
                moveResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(step.StableId, rollbackTarget));
            }
            catch (Exception ex)
            {
                allRbAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackException(
                    attemptIndex, si, step, rollbackTarget, step.ToPosition, ex));
                attemptIndex++;
                var (ok, newCp) = await TryPersistCheckpointAsync(currentCp, allRbAttempts, rbIndexes,
                    RecoverySessionLifecycle.PositionUncertain, true, true, si,
                    currentCp.ForwardFailureType ?? "InterruptedExecution",
                    currentCp.ForwardFailureMessage ?? "应用重启时发现未终结的持久化执行会话。",
                    ex.GetType().Name, ex.Message, currentCp, cancellationToken);
                if (!ok || newCp is null)
                {
                    return PositionUncertain(currentCp, allRbAttempts,
                        "TerminalPersistFailed",
                        "RollbackWriterException 后 PositionUncertain 检查点持久化失败。");
                }
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.PositionUncertain, false, false,
                    null, newCp.SafetySnapshotId,
                    newCp.ForwardAttempts, newCp.RollbackAttempts,
                    null, true, true,
                    ex.GetType().Name, $"Writer 异常：{ex.Message}",
                    null, null, null);
            }

            // 验证 Writer 结果
            if (!string.Equals(moveResult.StableId, step.StableId, StringComparison.Ordinal))
            {
                allRbAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackStableIdMismatch(
                    attemptIndex, si, step, rollbackTarget, step.ToPosition, moveResult));
                attemptIndex++;
                var (ok, newCp) = await TryPersistCheckpointAsync(currentCp, allRbAttempts, rbIndexes,
                    RecoverySessionLifecycle.PositionUncertain, true, true, si,
                    currentCp.ForwardFailureType ?? "InterruptedExecution",
                    currentCp.ForwardFailureMessage ?? "应用重启时发现未终结的持久化执行会话。",
                    "RollbackStableIdMismatch",
                    $"StableId \"{moveResult.StableId}\" ≠ \"{step.StableId}\"。", currentCp, cancellationToken);
                if (!ok || newCp is null)
                {
                    return PositionUncertain(currentCp, allRbAttempts,
                        "TerminalPersistFailed",
                        "RollbackStableIdMismatch 后 PositionUncertain 检查点持久化失败。");
                }
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.PositionUncertain, false, false,
                    null, newCp.SafetySnapshotId,
                    newCp.ForwardAttempts, newCp.RollbackAttempts,
                    null, true, true,
                    "RollbackStableIdMismatch",
                    $"StableId \"{moveResult.StableId}\" ≠ \"{step.StableId}\"。",
                    null, null, null);
            }

            if (moveResult.IsExact)
            {
                allRbAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromExactRollback(
                    attemptIndex, si, step, rollbackTarget, step.ToPosition, moveResult));
                rbIndexes.Add(si);
            }
            else
            {
                allRbAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromInexactRollback(
                    attemptIndex, si, step, rollbackTarget, step.ToPosition, moveResult));
                return await PersistRecoveryTerminalAsync(currentCp, allRbAttempts, rbIndexes,
                    new[] { $"Rollback 非精确: step {si}" }, 1, cancellationToken);
            }
            attemptIndex++;

            // ── 持久化 ExactSuccess checkpoint ──
            {
                var (ok, newCp) = await TryPersistCheckpointAsync(currentCp, allRbAttempts, rbIndexes,
                    RecoverySessionLifecycle.Executing, false, false, si,
                    currentCp.ForwardFailureType, currentCp.ForwardFailureMessage,
                    null, null, currentCp, cancellationToken);
                if (!ok) return PositionUncertain(currentCp, allRbAttempts, "CheckpointPersistFailed",
                    "恢复检查点持久化失败。");
                currentCp = newCp!;
            }
        }

        // ── 3. 独立验证 ──
        return await VerifyAndPersistRecoveryAsync(currentCp, allRbAttempts, rbIndexes,
            pendingSteps, execPlan, cancellationToken);
    }

    // ═══════════════ helpers ═══════════════

    private async Task<(bool Ok, RecoverySessionRecord? NewCp)> TryPersistCheckpointAsync(
        RecoverySessionRecord original,
        List<TargetDisplayLayoutExecutionAttemptResult> allRbAttempts,
        List<int> rbIndexes,
        RecoverySessionLifecycle lifecycle, bool manual, bool uncertain, int? failedIdx,
        string? fwdType, string? fwdMsg, string? recType, string? recMsg,
        RecoverySessionRecord currentCp,
        CancellationToken ct)
    {
        try
        {
            var mergedRb = original.RollbackAttempts
                .Concat(allRbAttempts.Where(a => !original.RollbackAttempts.Any(
                    o => o.AttemptIndex == a.AttemptIndex)))
                .OrderBy(a => a.AttemptIndex).ToList().AsReadOnly();
            var mergedRbIdx = original.CompletedRollbackSourceStepIndexes
                .Union(rbIndexes).OrderBy(x => x).ToList().AsReadOnly();

            var record = new RecoverySessionRecord
            {
                SessionId = original.SessionId, SafetySnapshotId = original.SafetySnapshotId,
                TargetDeviceName = original.TargetDeviceName, StartedAtUtc = original.StartedAtUtc,
                SourceRestorePlan = original.SourceRestorePlan, ExecutionPlan = original.ExecutionPlan,
                Lifecycle = lifecycle, RequiresManualRecovery = manual,
                IsPositionUncertain = uncertain, LastFailedStepIndex = failedIdx,
                ForwardAttempts = original.ForwardAttempts,
                RollbackAttempts = mergedRb,
                CompletedForwardStepIndexes = original.CompletedForwardStepIndexes,
                CompletedRollbackSourceStepIndexes = mergedRbIdx,
                ForwardFailureType = fwdType, ForwardFailureMessage = fwdMsg,
                RecoveryFailureType = recType, RecoveryFailureMessage = recMsg,
                Revision = currentCp.Revision,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            record.Validate();
            var updated = await _store.UpdateAsync(record);
            var fresh = await _store.GetAsync(original.SessionId);
            if (fresh is null || !TargetDisplayLayoutExecutionCoordinator.RecordsAreEquivalent(updated, fresh))
                return (false, null);
            return (true, fresh);
        }
        catch { return (false, null); }
    }

    /// <summary>Writer 前失败统一持久化：不创建 Attempt，直接写 PositionUncertain。</summary>
    private async Task<TargetDisplayLayoutExecutionRunResult> PersistPreWriteFailureAsync(
        RecoverySessionRecord currentCp,
        List<TargetDisplayLayoutExecutionAttemptResult> allRbAttempts,
        List<int> rbIndexes,
        string recoveryFailureType,
        string recoveryFailureMessage,
        CancellationToken ct)
    {
        var fwdType = currentCp.ForwardFailureType ?? "InterruptedExecution";
        var fwdMsg = currentCp.ForwardFailureMessage ?? "应用重启时发现未终结的持久化执行会话。";

        try
        {
            var mergedRb = currentCp.RollbackAttempts
                .Concat(allRbAttempts.Where(a => !currentCp.RollbackAttempts.Any(
                    o => o.AttemptIndex == a.AttemptIndex)))
                .OrderBy(a => a.AttemptIndex).ToList().AsReadOnly();
            var mergedRbIdx = currentCp.CompletedRollbackSourceStepIndexes
                .Union(rbIndexes).OrderBy(x => x).ToList().AsReadOnly();

            var record = new RecoverySessionRecord
            {
                SessionId = currentCp.SessionId, SafetySnapshotId = currentCp.SafetySnapshotId,
                TargetDeviceName = currentCp.TargetDeviceName, StartedAtUtc = currentCp.StartedAtUtc,
                SourceRestorePlan = currentCp.SourceRestorePlan, ExecutionPlan = currentCp.ExecutionPlan,
                Lifecycle = RecoverySessionLifecycle.PositionUncertain,
                RequiresManualRecovery = true, IsPositionUncertain = true,
                LastFailedStepIndex = null,
                ForwardAttempts = currentCp.ForwardAttempts,
                RollbackAttempts = mergedRb,
                CompletedForwardStepIndexes = currentCp.CompletedForwardStepIndexes,
                CompletedRollbackSourceStepIndexes = mergedRbIdx,
                ForwardFailureType = fwdType, ForwardFailureMessage = fwdMsg,
                RecoveryFailureType = recoveryFailureType, RecoveryFailureMessage = recoveryFailureMessage,
                Revision = currentCp.Revision,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            record.Validate();
            var updated = await _store.UpdateAsync(record);
            var fresh = await _store.GetAsync(currentCp.SessionId, ct);
            if (fresh is null || !TargetDisplayLayoutExecutionCoordinator.RecordsAreEquivalent(updated, fresh))
            {
                return PositionUncertain(currentCp, allRbAttempts,
                    "TerminalPersistFailed", $"{recoveryFailureType} 后终态持久化失败。");
            }
            return PositionUncertain(fresh, allRbAttempts, recoveryFailureType, recoveryFailureMessage);
        }
        catch
        {
            return PositionUncertain(currentCp, allRbAttempts,
                "TerminalPersistFailed", $"{recoveryFailureType} 后终态持久化异常。");
        }
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> VerifyAndPersistRecoveryAsync(
        RecoverySessionRecord currentCp,
        List<TargetDisplayLayoutExecutionAttemptResult> allRbAttempts,
        List<int> rbIndexes,
        List<int> pendingSteps,
        TargetDisplayLayoutExecutionPlan execPlan,
        CancellationToken ct)
    {
        TargetDisplayLayoutExecutionVerificationResult verification;
        try
        {
            var rawRead = await _staExecutor.RunAsync(() => _iconReader.ReadDesktopIcons());
            var issues = new List<string>();
            var notRestored = 0;
            foreach (var si in pendingSteps)
            {
                var step = execPlan.Steps[si];
                var currentIcon = rawRead.Icons.FirstOrDefault(
                    i => string.Equals(i.StableId, step.StableId, StringComparison.Ordinal));
                if (currentIcon is null)
                { issues.Add($"\"{step.StableId}\" 缺失。"); notRestored++; }
                else if (currentIcon.PixelPosition != step.FromPosition)
                { issues.Add($"\"{step.StableId}\" 位置未恢复。"); notRestored++; }
            }
            verification = notRestored > 0
                ? TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(notRestored, issues, DateTimeOffset.UtcNow)
                : TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return await PersistPreWriteFailureAsync(currentCp, allRbAttempts, rbIndexes,
                "RecoveryVerificationException", $"验证异常：{ex.Message}", ct);
        }

        return await PersistRecoveryTerminalAsync(currentCp, allRbAttempts, rbIndexes,
            verification.Issues, verification.FailedItemCount, ct);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> PersistRecoveryTerminalAsync(
        RecoverySessionRecord currentCp,
        List<TargetDisplayLayoutExecutionAttemptResult> allRbAttempts,
        List<int> rbIndexes,
        IReadOnlyList<string> issues, int failedItemCount,
        CancellationToken ct)
    {
        var isPassed = failedItemCount == 0 && issues.Count == 0;
        var fwdType = currentCp.ForwardFailureType ?? "InterruptedExecution";
        var fwdMsg = currentCp.ForwardFailureMessage ?? "应用重启时发现未终结的持久化执行会话。";

        if (isPassed)
        {
            var lifecycle = RecoverySessionLifecycle.FailedAndRolledBack;
            var verification = TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(DateTimeOffset.UtcNow);
            var mergedRb = currentCp.RollbackAttempts
                .Concat(allRbAttempts.Where(a => !currentCp.RollbackAttempts.Any(
                    o => o.AttemptIndex == a.AttemptIndex)))
                .OrderBy(a => a.AttemptIndex).ToList().AsReadOnly();
            var mergedRbIdx = currentCp.CompletedRollbackSourceStepIndexes
                .Union(rbIndexes).OrderBy(x => x).ToList().AsReadOnly();

            try
            {
                var final = new RecoverySessionRecord
                {
                    SessionId = currentCp.SessionId, SafetySnapshotId = currentCp.SafetySnapshotId,
                    TargetDeviceName = currentCp.TargetDeviceName, StartedAtUtc = currentCp.StartedAtUtc,
                    SourceRestorePlan = currentCp.SourceRestorePlan, ExecutionPlan = currentCp.ExecutionPlan,
                    Lifecycle = lifecycle, RequiresManualRecovery = false,
                    IsPositionUncertain = false, LastFailedStepIndex = null,
                    ForwardAttempts = currentCp.ForwardAttempts, RollbackAttempts = mergedRb,
                    CompletedForwardStepIndexes = currentCp.CompletedForwardStepIndexes,
                    CompletedRollbackSourceStepIndexes = mergedRbIdx,
                    VerificationResult = verification,
                    ForwardFailureType = fwdType, ForwardFailureMessage = fwdMsg,
                    Revision = currentCp.Revision, UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                final.Validate();
                var updated = await _store.UpdateAsync(final);
                var fresh = await _store.GetAsync(currentCp.SessionId, ct);
                if (fresh is null || !TargetDisplayLayoutExecutionCoordinator.RecordsAreEquivalent(updated, fresh))
                {
                    return PositionUncertain(currentCp, allRbAttempts,
                        "TerminalPersistFailed", "恢复终态持久化或 readback 不一致。");
                }
                var rbList = mergedRb as IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult>;
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, false, false,
                    null, currentCp.SafetySnapshotId, currentCp.ForwardAttempts, rbList,
                    null, false, false, null, null, null, null, verification);
            }
            catch
            {
                return PositionUncertain(currentCp, allRbAttempts,
                    "TerminalPersistFailed", "恢复终态持久化异常。");
            }
        }
        else
        {
            var lifecycle = RecoverySessionLifecycle.FailedRollbackIncomplete;
            var verification = TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(
                failedItemCount, issues, DateTimeOffset.UtcNow);
            var recType = "RecoveryVerificationFailed";
            var recMsg = "恢复后验证失败。";
            var mergedRb = currentCp.RollbackAttempts
                .Concat(allRbAttempts.Where(a => !currentCp.RollbackAttempts.Any(
                    o => o.AttemptIndex == a.AttemptIndex)))
                .OrderBy(a => a.AttemptIndex).ToList().AsReadOnly();
            var mergedRbIdx = currentCp.CompletedRollbackSourceStepIndexes
                .Union(rbIndexes).OrderBy(x => x).ToList().AsReadOnly();

            try
            {
                var final = new RecoverySessionRecord
                {
                    SessionId = currentCp.SessionId, SafetySnapshotId = currentCp.SafetySnapshotId,
                    TargetDeviceName = currentCp.TargetDeviceName, StartedAtUtc = currentCp.StartedAtUtc,
                    SourceRestorePlan = currentCp.SourceRestorePlan, ExecutionPlan = currentCp.ExecutionPlan,
                    Lifecycle = lifecycle, RequiresManualRecovery = true,
                    IsPositionUncertain = false, LastFailedStepIndex = null,
                    ForwardAttempts = currentCp.ForwardAttempts, RollbackAttempts = mergedRb,
                    CompletedForwardStepIndexes = currentCp.CompletedForwardStepIndexes,
                    CompletedRollbackSourceStepIndexes = mergedRbIdx,
                    VerificationResult = verification,
                    ForwardFailureType = fwdType, ForwardFailureMessage = fwdMsg,
                    RecoveryFailureType = recType, RecoveryFailureMessage = recMsg,
                    Revision = currentCp.Revision, UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                final.Validate();
                var updated = await _store.UpdateAsync(final);
                var fresh = await _store.GetAsync(currentCp.SessionId, ct);
                if (fresh is null || !TargetDisplayLayoutExecutionCoordinator.RecordsAreEquivalent(updated, fresh))
                {
                    return PositionUncertain(currentCp, allRbAttempts,
                        "TerminalPersistFailed", "恢复终态持久化或 readback 不一致。");
                }
                var rbList = mergedRb as IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult>;
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, false, false,
                    null, currentCp.SafetySnapshotId, currentCp.ForwardAttempts, rbList,
                    null, true, false, recType, recMsg, null, null, verification);
            }
            catch
            {
                return PositionUncertain(currentCp, allRbAttempts,
                    "TerminalPersistFailed", "恢复终态持久化异常。");
            }
        }
    }

    private static TargetDisplayLayoutExecutionRunResult PositionUncertain(
        RecoverySessionRecord record,
        List<TargetDisplayLayoutExecutionAttemptResult> allRbAttempts,
        string type, string message)
    {
        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.PositionUncertain, false, false,
            null, record.SafetySnapshotId,
            record.ForwardAttempts,
            allRbAttempts as IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult>,
            null, true, true, type, message, null, null);
    }
}
