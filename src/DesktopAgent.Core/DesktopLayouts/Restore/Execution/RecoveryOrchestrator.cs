namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 跨重启恢复编排器。纯 Core 逻辑，不依赖 WPF/COM/PIDL/HWND。
/// 依赖 IRecoverySessionStore（持久化）和外部注入的 Writer/Reader/Verifier 委托。
/// </summary>
public sealed class RecoveryOrchestrator
{
    private readonly IRecoverySessionStore _store;

    public RecoveryOrchestrator(IRecoverySessionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>检查是否存在未解决会话。损坏数据也算未解决。</summary>
    public async Task<bool> HasUnresolvedSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            var unresolved = await _store.ListUnresolvedAsync(limit: 1, ct);
            return unresolved.Count > 0;
        }
        catch (InvalidDataException)
        {
            // 损坏数据 → 视为未解决，执行锁定
            return true;
        }
    }

    /// <summary>获取未解决会话列表（用于 UI 展示）。损坏时返回空列表 + 标记。</summary>
    public async Task<(IReadOnlyList<RecoverySessionRecord> Sessions, bool HasCorruption)>
        GetUnresolvedInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var unresolved = await _store.ListUnresolvedAsync(limit: 50, ct);
            return (unresolved, false);
        }
        catch (InvalidDataException)
        {
            return (Array.Empty<RecoverySessionRecord>(), true);
        }
    }

    /// <summary>
    /// 尝试解决（resolve）一个恢复会话。
    /// 返回 true 表示已成功解决（持久化为终态），false 表示仍需人工处理。
    /// </summary>
    public async Task<bool> TryResolveAsync(
        RecoverySessionRecord record,
        CancellationToken ct = default)
    {
        // 检查是否可以自动解决
        if (record.Lifecycle == RecoverySessionLifecycle.PositionUncertain
            || record.Lifecycle == RecoverySessionLifecycle.FailedRollbackIncomplete)
        {
            // 这些状态需要人工处理，不能自动关闭
            return false;
        }

        if (record.Lifecycle == RecoverySessionLifecycle.Executing)
        {
            // Executing 状态表示崩溃残留，需要恢复流程（由外部 Writer 调用方处理）
            // 此处只标记为仍需处理
            return false;
        }

        // Completed 或 FailedAndRolledBack 已经是终态，不需要额外操作
        // 但如果还出现在 unresolved 列表中，说明有问题要手动处理
        return false;
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
            var updated = new RecoverySessionRecord
            {
                SessionId = record.SessionId,
                SafetySnapshotId = record.SafetySnapshotId,
                TargetDeviceName = record.TargetDeviceName,
                StartedAtUtc = record.StartedAtUtc,
                SourceRestorePlan = record.SourceRestorePlan,
                ExecutionPlan = record.ExecutionPlan,
                Lifecycle = finalLifecycle,
                RequiresManualRecovery = requiresManualRecovery,
                IsPositionUncertain = isPositionUncertain,
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
            await _store.UpdateAsync(updated);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
