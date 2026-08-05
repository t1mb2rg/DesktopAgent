using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

/// <summary>
/// 跨重启恢复协调器 — 使用持久化 RecoverySessionRecord 执行保守 Rollback。
/// 只回滚持久化确认完成的 Forward 步骤，不重放 Forward，不使用当前模板/规则。
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

    /// <summary>查询未解决会话列表。</summary>
    public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(CancellationToken ct = default)
        => _store.ListUnresolvedAsync(limit: 50, ct);

    /// <summary>检查是否存在未解决会话（损坏数据也视为未解决）。</summary>
    public async Task<bool> HasUnresolvedAsync(CancellationToken ct = default)
    {
        try { return (await _store.ListUnresolvedAsync(limit: 1, ct)).Count > 0; }
        catch (InvalidDataException) { return true; }
    }

    /// <summary>
    /// 对持久化会话执行跨重启恢复。
    /// 1. 重新读取当前桌面状态和身份
    /// 2. 对持久化确认完成的 Forward 步骤执行保守 Rollback
    /// 3. 独立验证
    /// 4. 持久化终态
    /// </summary>
    public async Task<TargetDisplayLayoutExecutionRunResult> RecoverAsync(
        RecoverySessionRecord record,
        CancellationToken cancellationToken = default)
    {
        var execPlan = record.ExecutionPlan;
        var allAttempts = new List<TargetDisplayLayoutExecutionAttemptResult>();
        var attemptIndex = record.ForwardAttempts.Count + record.RollbackAttempts.Count;
        var rbSourceIndexes = new List<int>(record.CompletedRollbackSourceStepIndexes);

        // 计算待回滚步骤：CompletedForward − CompletedRollback，降序
        var pendingSteps = record.CompletedForwardStepIndexes
            .Except(record.CompletedRollbackSourceStepIndexes)
            .OrderByDescending(x => x)
            .ToList();

        if (pendingSteps.Count == 0)
        {
            // 无需回滚 → 已经是终态
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack,
                false, false, null, record.SafetySnapshotId,
                record.ForwardAttempts, record.RollbackAttempts,
                null, false, false, null, null, null, null);
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
            currentRead = new DesktopIconReadResult(
                rawRead.Icons, rawRead.FailedItemCount,
                rawRead.ConnectionMethod, rawRead.ReadTime,
                rawRead.PrimaryPathException);
        }
        catch
        {
            return UncertainResult(record, allAttempts, "TopologyReadFailed",
                "跨重启恢复前环境读取失败。");
        }

        if (currentRead.FailedItemCount != 0)
            return UncertainResult(record, allAttempts, "CurrentReadIncomplete",
                $"FailedItemCount={currentRead.FailedItemCount}。");

        // ── 2. 逐步骤 Rollback ──
        foreach (var si in pendingSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (si < 0 || si >= execPlan.Steps.Count) continue;

            var step = execPlan.Steps[si];
            // Rollback 参数固定：Requested = Step.From, ExpectedPrevious = Step.To
            var rollbackTarget = step.FromPosition;
            string stableId = step.StableId;

            // 身份检查：StableId 存在于当前桌面且位置匹配
            var currentIcon = currentRead.Icons.FirstOrDefault(
                i => string.Equals(i.StableId, stableId, StringComparison.Ordinal));
            if (currentIcon is null)
            {
                allAttempts.Add(new TargetDisplayLayoutExecutionAttemptResult(
                    attemptIndex, si, TargetDisplayLayoutExecutionAttemptPhase.Rollback,
                    stableId, step.ToPosition, rollbackTarget,
                    null, null, null,
                    false, false, TargetDisplayLayoutExecutionAttemptOutcome.WriterException,
                    null, "StableIdMissing", $"StableId \"{stableId}\" 在当前桌面缺失。",
                    null, null));
                attemptIndex++;
                continue;
            }

            if (currentIcon.PixelPosition != step.ToPosition)
            {
                allAttempts.Add(new TargetDisplayLayoutExecutionAttemptResult(
                    attemptIndex, si, TargetDisplayLayoutExecutionAttemptPhase.Rollback,
                    stableId, step.ToPosition, rollbackTarget,
                    null, new PixelPoint(currentIcon.PixelPosition.X, currentIcon.PixelPosition.Y), null,
                    false, false, TargetDisplayLayoutExecutionAttemptOutcome.PreviousPositionMismatch,
                    currentRead.ConnectionMethod,
                    null, null, null, null));
                attemptIndex++;
                continue;
            }

            // 调用 Writer
            DesktopIconMoveResult moveResult;
            try
            {
                moveResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(stableId, rollbackTarget));
            }
            catch (Exception ex)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackException(
                    attemptIndex, si, step, rollbackTarget, step.ToPosition, ex));
                attemptIndex++;
                continue;
            }

            if (!string.Equals(moveResult.StableId, stableId, StringComparison.Ordinal))
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackStableIdMismatch(
                    attemptIndex, si, step, rollbackTarget, step.ToPosition, moveResult));
                attemptIndex++;
                continue;
            }

            if (moveResult.IsExact)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromExactRollback(
                    attemptIndex, si, step, rollbackTarget, step.ToPosition, moveResult));
                rbSourceIndexes.Add(si);
            }
            else
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromInexactRollback(
                    attemptIndex, si, step, rollbackTarget, step.ToPosition, moveResult));
            }
            attemptIndex++;

            // 每步持久化
            if (record.Revision > 0)
            {
                try
                {
                    var cp = new RecoverySessionRecord
                    {
                        SessionId = record.SessionId, SafetySnapshotId = record.SafetySnapshotId,
                        TargetDeviceName = record.TargetDeviceName, StartedAtUtc = record.StartedAtUtc,
                        SourceRestorePlan = record.SourceRestorePlan, ExecutionPlan = execPlan,
                        Lifecycle = RecoverySessionLifecycle.Executing,
                        ForwardAttempts = record.ForwardAttempts,
                        RollbackAttempts = allAttempts
                            .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList().AsReadOnly(),
                        CompletedForwardStepIndexes = record.CompletedForwardStepIndexes,
                        CompletedRollbackSourceStepIndexes = rbSourceIndexes.AsReadOnly(),
                        Revision = record.Revision, UpdatedAtUtc = DateTimeOffset.UtcNow,
                    };
                    cp.Validate();
                    var updated = await _store.UpdateAsync(cp);
                    var fresh = await _store.GetAsync(record.SessionId);
                    if (fresh is not null) record = fresh;
                }
                catch { /* 持久化失败不中止恢复 */ }
            }
        }

        // ── 3. 独立验证 ──
        TargetDisplayLayoutExecutionVerificationResult verification;
        try
        {
            var verifyRead = await _staExecutor.RunAsync(() => _iconReader.ReadDesktopIcons());
            var issues = new List<string>();
            var notRestored = 0;

            foreach (var si in pendingSteps)
            {
                var step = execPlan.Steps[si];
                var currentIcon = verifyRead.Icons.FirstOrDefault(
                    i => string.Equals(i.StableId, step.StableId, StringComparison.Ordinal));
                if (currentIcon is null)
                { issues.Add($"StableId \"{step.StableId}\" 在验证读取中缺失。"); notRestored++; }
                else if (currentIcon.PixelPosition != step.FromPosition)
                { issues.Add($"\"{step.StableId}\" 未恢复到原位置。"); notRestored++; }
            }

            verification = notRestored > 0
                ? TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(
                    notRestored, issues, DateTimeOffset.UtcNow)
                : TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return UncertainResult(record, allAttempts, "RecoveryVerificationException",
                $"恢复后验证异常：{ex.Message}");
        }

        // ── 4. 持久化终态 ──
        var finalLifecycle = verification.Status == ExecutionVerificationStatus.Passed
            ? RecoverySessionLifecycle.FailedAndRolledBack
            : RecoverySessionLifecycle.FailedRollbackIncomplete;
        var needsManual = verification.Status != ExecutionVerificationStatus.Passed;
        var isUncertain = verification.Status == ExecutionVerificationStatus.Uncertain;

        try
        {
            var final = new RecoverySessionRecord
            {
                SessionId = record.SessionId, SafetySnapshotId = record.SafetySnapshotId,
                TargetDeviceName = record.TargetDeviceName, StartedAtUtc = record.StartedAtUtc,
                SourceRestorePlan = record.SourceRestorePlan, ExecutionPlan = execPlan,
                Lifecycle = finalLifecycle,
                RequiresManualRecovery = needsManual, IsPositionUncertain = isUncertain,
                ForwardAttempts = record.ForwardAttempts,
                RollbackAttempts = allAttempts
                    .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList().AsReadOnly(),
                CompletedForwardStepIndexes = record.CompletedForwardStepIndexes,
                CompletedRollbackSourceStepIndexes = rbSourceIndexes.AsReadOnly(),
                VerificationResult = verification,
                Revision = record.Revision, UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            final.Validate();
            await _store.UpdateAsync(final);
        }
        catch { /* best-effort */ }

        var fwdAtt = record.ForwardAttempts;
        var rbAtt = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList()
            as IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult>;

        return verification.Status switch
        {
            ExecutionVerificationStatus.Passed => new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, false, false,
                null, record.SafetySnapshotId, fwdAtt, rbAtt,
                null, false, false, null, null, null, null, verification),

            ExecutionVerificationStatus.Failed => new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, false, false,
                null, record.SafetySnapshotId, fwdAtt, rbAtt,
                null, true, false, "RecoveryVerificationFailed",
                "恢复后验证失败。", null, null, verification),

            _ => new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.PositionUncertain, false, false,
                null, record.SafetySnapshotId, fwdAtt, rbAtt,
                null, true, true, "RecoveryVerificationUncertain",
                "恢复后验证不确定。", null, null, verification),
        };
    }

    private static TargetDisplayLayoutExecutionRunResult UncertainResult(
        RecoverySessionRecord record,
        List<TargetDisplayLayoutExecutionAttemptResult> allAttempts,
        string type, string message)
    {
        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.PositionUncertain, false, false,
            null, record.SafetySnapshotId,
            record.ForwardAttempts,
            allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList(),
            null, true, true, type, message, null, null);
    }
}
