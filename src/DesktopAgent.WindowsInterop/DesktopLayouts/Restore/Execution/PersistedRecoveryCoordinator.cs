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
        var fresh = await _store.GetAsync(record.SessionId, cancellationToken);
        if (fresh is null)
            return PositionUncertain(record, new List<TargetDisplayLayoutExecutionAttemptResult>(),
                "SessionNotFound", "GetAsync 返回 null。");

        var execPlan = fresh.ExecutionPlan;
        // ── 拒绝 staging/cycle ──
        if (execPlan.UsesTemporaryStaging)
            return PositionUncertain(fresh, new List<TargetDisplayLayoutExecutionAttemptResult>(),
                "StagingNotSupported", "跨重启恢复不支持含临时中转的计划。");

        var allRbAttempts = new List<TargetDisplayLayoutExecutionAttemptResult>(fresh.RollbackAttempts);
        var attemptIndex = fresh.ForwardAttempts.Count + fresh.RollbackAttempts.Count;
        var rbIndexes = new List<int>(fresh.CompletedRollbackSourceStepIndexes);

        // 待回滚: CompletedFwd − CompletedRb, 降序
        var pendingSteps = fresh.CompletedForwardStepIndexes
            .Except(fresh.CompletedRollbackSourceStepIndexes)
            .OrderByDescending(x => x)
            .ToList();

        if (pendingSteps.Count == 0)
        {
            // 无需回滚 → 持久化终态
            return await PersistRecoveryTerminalAsync(fresh, allRbAttempts, rbIndexes,
                Array.Empty<string>(), 0, cancellationToken);
        }

        // 验证所有 StepIndex 在合法范围
        foreach (var si in pendingSteps)
        {
            if (si < 0 || si >= execPlan.Steps.Count)
                return PositionUncertain(fresh, allRbAttempts, "InvalidStepIndex",
                    $"Pending step index {si} 超出范围 [0, {execPlan.Steps.Count})。");
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
            return PositionUncertain(fresh, allRbAttempts, "TopologyReadFailed",
                $"环境读取失败：{ex.Message}");
        }

        if (currentRead.FailedItemCount != 0)
            return PositionUncertain(fresh, allRbAttempts, "CurrentReadIncomplete",
                $"FailedItemCount={currentRead.FailedItemCount}。");

        // ── 2. 逐步骤 Rollback ──
        var currentCp = fresh;
        foreach (var si in pendingSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = execPlan.Steps[si];
            var rollbackTarget = step.FromPosition;

            // 身份检查
            var currentIcon = currentRead.Icons.FirstOrDefault(
                i => string.Equals(i.StableId, step.StableId, StringComparison.Ordinal));
            if (currentIcon is null)
            {
                allRbAttempts.Add(new TargetDisplayLayoutExecutionAttemptResult(
                    attemptIndex, si, TargetDisplayLayoutExecutionAttemptPhase.Rollback,
                    step.StableId, step.ToPosition, rollbackTarget,
                    null, null, null, false, false,
                    TargetDisplayLayoutExecutionAttemptOutcome.WriterException,
                    null, "StableIdMissing", $"\"{step.StableId}\" 未找到。", null, null));
                attemptIndex++;
                // 持久化 + 停止
                return await PersistRecoveryTerminalAsync(fresh, allRbAttempts, rbIndexes,
                    new[] { $"\"{step.StableId}\" 缺失" }, 1, cancellationToken);
            }

            if (currentIcon.PixelPosition != step.ToPosition)
            {
                allRbAttempts.Add(new TargetDisplayLayoutExecutionAttemptResult(
                    attemptIndex, si, TargetDisplayLayoutExecutionAttemptPhase.Rollback,
                    step.StableId, step.ToPosition, rollbackTarget,
                    null, currentIcon.PixelPosition, null, false, false,
                    TargetDisplayLayoutExecutionAttemptOutcome.PreviousPositionMismatch,
                    currentRead.ConnectionMethod, null, null, null, null));
                attemptIndex++;
                return await PersistRecoveryTerminalAsync(fresh, allRbAttempts, rbIndexes,
                    new[] { $"\"{step.StableId}\" 当前位置={currentIcon.PixelPosition} ≠ 预期={step.ToPosition}" },
                    1, cancellationToken);
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
                var (ok, _) = await TryPersistCheckpointAsync(fresh, allRbAttempts, rbIndexes,
                    RecoverySessionLifecycle.PositionUncertain, true, true, si,
                    ex.GetType().Name, ex.Message, null, null, currentCp, cancellationToken);
                if (!ok) return PositionUncertain(fresh, allRbAttempts, "CheckpointPersistFailed",
                    "恢复检查点持久化失败。");
                return await PersistRecoveryTerminalAsync(fresh, allRbAttempts, rbIndexes,
                    new[] { $"Writer 异常：{ex.Message}" }, 1, cancellationToken);
            }

            // 验证 Writer 结果
            if (!string.Equals(moveResult.StableId, step.StableId, StringComparison.Ordinal))
            {
                allRbAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackStableIdMismatch(
                    attemptIndex, si, step, rollbackTarget, step.ToPosition, moveResult));
                attemptIndex++;
                var (ok, _) = await TryPersistCheckpointAsync(fresh, allRbAttempts, rbIndexes,
                    RecoverySessionLifecycle.PositionUncertain, true, true, si,
                    "RollbackStableIdMismatch",
                    $"StableId \"{moveResult.StableId}\" ≠ \"{step.StableId}\"。", null, null,
                    currentCp, cancellationToken);
                if (!ok) return PositionUncertain(fresh, allRbAttempts, "CheckpointPersistFailed",
                    "恢复检查点持久化失败。");
                return await PersistRecoveryTerminalAsync(fresh, allRbAttempts, rbIndexes,
                    new[] { $"Rollback StableId mismatch: {moveResult.StableId}" }, 1, cancellationToken);
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
                return await PersistRecoveryTerminalAsync(fresh, allRbAttempts, rbIndexes,
                    new[] { $"Rollback 非精确: step {si}" }, 1, cancellationToken);
            }
            attemptIndex++;

            // ── 持久化 checkpoint ──
            {
                var (ok, newCp) = await TryPersistCheckpointAsync(fresh, allRbAttempts, rbIndexes,
                    RecoverySessionLifecycle.Executing, false, false, si,
                    null, null, null, null, currentCp, cancellationToken);
                if (!ok) return PositionUncertain(fresh, allRbAttempts, "CheckpointPersistFailed",
                    "恢复检查点持久化失败。");
                currentCp = newCp!;
            }
        }

        // ── 3. 独立验证 ──
        return await VerifyAndPersistRecoveryAsync(fresh, allRbAttempts, rbIndexes,
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

    private async Task<TargetDisplayLayoutExecutionRunResult> VerifyAndPersistRecoveryAsync(
        RecoverySessionRecord record,
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
            return PositionUncertain(record, allRbAttempts, "VerificationException",
                $"验证异常：{ex.Message}");
        }

        return await PersistRecoveryTerminalAsync(record, allRbAttempts, rbIndexes,
            verification.Issues, verification.FailedItemCount, ct);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> PersistRecoveryTerminalAsync(
        RecoverySessionRecord record,
        List<TargetDisplayLayoutExecutionAttemptResult> allRbAttempts,
        List<int> rbIndexes,
        IReadOnlyList<string> issues, int failedItemCount,
        CancellationToken ct)
    {
        var isPassed = failedItemCount == 0 && issues.Count == 0;
        var lifecycle = isPassed ? RecoverySessionLifecycle.FailedAndRolledBack
            : RecoverySessionLifecycle.FailedRollbackIncomplete;
        var manual = !isPassed;
        var verification = isPassed
            ? TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(DateTimeOffset.UtcNow)
            : TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(failedItemCount, issues, DateTimeOffset.UtcNow);

        var mergedRb = record.RollbackAttempts
            .Concat(allRbAttempts.Where(a => !record.RollbackAttempts.Any(
                o => o.AttemptIndex == a.AttemptIndex)))
            .OrderBy(a => a.AttemptIndex).ToList().AsReadOnly();
        var mergedRbIdx = record.CompletedRollbackSourceStepIndexes
            .Union(rbIndexes).OrderBy(x => x).ToList().AsReadOnly();

        try
        {
            var final = new RecoverySessionRecord
            {
                SessionId = record.SessionId, SafetySnapshotId = record.SafetySnapshotId,
                TargetDeviceName = record.TargetDeviceName, StartedAtUtc = record.StartedAtUtc,
                SourceRestorePlan = record.SourceRestorePlan, ExecutionPlan = record.ExecutionPlan,
                Lifecycle = lifecycle, RequiresManualRecovery = manual,
                IsPositionUncertain = false, LastFailedStepIndex = null,
                ForwardAttempts = record.ForwardAttempts, RollbackAttempts = mergedRb,
                CompletedForwardStepIndexes = record.CompletedForwardStepIndexes,
                CompletedRollbackSourceStepIndexes = mergedRbIdx,
                VerificationResult = verification,
                Revision = record.Revision, UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            final.Validate();
            var updated = await _store.UpdateAsync(final);
            var fresh = await _store.GetAsync(record.SessionId);
            if (fresh is null || !TargetDisplayLayoutExecutionCoordinator.RecordsAreEquivalent(updated, fresh))
            {
                return PositionUncertain(record, allRbAttempts,
                    "TerminalPersistFailed", "恢复终态持久化或 readback 不一致。");
            }
        }
        catch
        {
            return PositionUncertain(record, allRbAttempts,
                "TerminalPersistFailed", "恢复终态持久化异常。");
        }

        var rbList = mergedRb as IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult>;
        return isPassed
            ? new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, false, false,
                null, record.SafetySnapshotId, record.ForwardAttempts, rbList,
                null, false, false, null, null, null, null, verification)
            : new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, false, false,
                null, record.SafetySnapshotId, record.ForwardAttempts, rbList,
                null, true, false, "RecoveryVerificationFailed",
                "恢复后验证失败。", null, null, verification);
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
