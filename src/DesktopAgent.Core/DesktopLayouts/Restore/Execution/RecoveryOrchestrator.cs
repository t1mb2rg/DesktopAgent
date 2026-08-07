namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 跨重启恢复编排器。纯 Core 逻辑，不依赖 WPF/COM/PIDL/HWND。
/// 依赖 IRecoverySessionStore（持久化）和外部注入的 Writer/Reader/Verifier 委托。
/// </summary>
public sealed class RecoveryOrchestrator
{
    private const int UnresolvedCandidateLimit = 50;

    private readonly IRecoverySessionStore _store;

    public RecoveryOrchestrator(IRecoverySessionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// 检查是否存在未解决会话。损坏数据也算未解决。
    ///
    /// Store 返回的是持久化候选；真正的行为判定权属于 RecoverySessionDisposition。
    /// 旧兼容 bool 只能作为一致性校验位，不能独立把一个 resolved 生命周期升级为 unresolved。
    /// </summary>
    public async Task<bool> HasUnresolvedSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            var candidates = await _store.ListUnresolvedAsync(UnresolvedCandidateLimit, ct);
            var projection = RecoverySessionCandidateProjector.Project(candidates);

            // 兼容字段与 Lifecycle 投影不一致 = 持久化数据损坏。
            return projection.HasCorruption || projection.Unresolved.Count > 0;
        }
        catch (InvalidDataException)
        {
            // 损坏数据 → 视为未解决，执行锁定
            return true;
        }
    }

    /// <summary>
    /// 获取未解决会话列表（用于 UI 展示）。损坏时返回空列表 + 标记。
    /// 返回列表会再次按照 RecoverySessionDisposition 投影，Store 的历史兼容筛选没有最终解释权。
    /// </summary>
    public async Task<(IReadOnlyList<RecoverySessionRecord> Sessions, bool HasCorruption)>
        GetUnresolvedInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var candidates = await _store.ListUnresolvedAsync(UnresolvedCandidateLimit, ct);
            var projection = RecoverySessionCandidateProjector.Project(candidates);
            return projection.HasCorruption
                ? (Array.Empty<RecoverySessionRecord>(), true)
                : (projection.Unresolved, false);
        }
        catch (InvalidDataException)
        {
            return (Array.Empty<RecoverySessionRecord>(), true);
        }
    }

    /// <summary>
    /// 判断一个恢复会话在持久化语义上是否已经无需后续处理。
    ///
    /// 本方法不观察真实桌面，也不执行 Writer。Executing 仍需进入外部 observation-first
    /// 恢复流程；ManualIntervention 则保持锁定。只有 Handling=None 才视为已解决。
    /// </summary>
    public Task<bool> TryResolveAsync(
        RecoverySessionRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        var disposition = record.GetDisposition();
        return Task.FromResult(disposition.Handling == RecoverySessionHandling.None);
    }

    /// <summary>持久化恢复完成后的终态。</summary>
    public async Task<bool> PersistRecoveryResultAsync(
        RecoverySessionRecord record,
        RecoverySessionLifecycle finalLifecycle,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> forwardAttempts,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        IReadOnlyList<int> completedFwdIndexes,
        IReadOnlyList<int> completedRbIndexes,
        bool requiresManualRecovery,
        bool isPositionUncertain,
        string? forwardFailureType,
        string? forwardFailureMessage,
        string? recoveryFailureType,
        string? recoveryFailureMessage,
        TargetDisplayLayoutExecutionVerificationResult? verificationResult,
        CancellationToken ct = default)
    {
        try
        {
            var disposition = RecoverySessionDisposition.FromLifecycle(finalLifecycle);

            // 旧调用签名暂时保留两个 bool 以兼容现有调用方，但它们不再是独立真相。
            // 若调用方传入与 Lifecycle 投影矛盾的组合，fail-closed，不让三联状态继续漂移。
            if (requiresManualRecovery != disposition.RequiresManualRecovery
                || isPositionUncertain != disposition.IsPositionUncertain)
            {
                return false;
            }

            var updated = new RecoverySessionRecord
            {
                SessionId = record.SessionId,
                SafetySnapshotId = record.SafetySnapshotId,
                TargetDeviceName = record.TargetDeviceName,
                StartedAtUtc = record.StartedAtUtc,
                SourceRestorePlan = record.SourceRestorePlan,
                ExecutionPlan = record.ExecutionPlan,
                Lifecycle = finalLifecycle,
                RequiresManualRecovery = disposition.RequiresManualRecovery,
                IsPositionUncertain = disposition.IsPositionUncertain,
                LastFailedStepIndex = record.LastFailedStepIndex,
                ForwardAttempts = forwardAttempts,
                RollbackAttempts = rollbackAttempts,
                CompletedForwardStepIndexes = completedFwdIndexes,
                CompletedRollbackSourceStepIndexes = completedRbIndexes,
                ForwardFailureType = forwardFailureType,
                ForwardFailureMessage = forwardFailureMessage,
                RecoveryFailureType = recoveryFailureType,
                RecoveryFailureMessage = recoveryFailureMessage,
                VerificationResult = verificationResult,
                Revision = record.Revision,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            updated.Validate();
            await _store.UpdateAsync(updated, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
