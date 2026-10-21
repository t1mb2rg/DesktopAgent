using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

public enum DesktopLayoutRecoveryProductOutcome
{
    NoPendingRecovery = 0,
    MultiplePendingRecoveries = 1,
    Reconciled = 2,
    StillNeedsAttention = 3,
    Failed = 4,
    AcceptedCurrentState = 5,
}

/// <summary>
/// 产品壳恢复结果。它不把“恢复”误写成“强制回滚”：
/// PersistedRecoveryCoordinator 仍以 fresh 当前桌面事实为权威，决定当前是
/// Committed / Restored / MixedKnown / NeedsAttention。
/// </summary>
public sealed record DesktopLayoutRecoveryProductResult
{
    public DesktopLayoutRecoveryProductOutcome Outcome { get; }
    public int PendingCount { get; }
    public Guid? SessionId { get; }
    public TargetDisplayLayoutExecutionRunResult? RecoveryResult { get; }
    public IReadOnlyList<string> Issues { get; }

    public bool IsResolved => Outcome is DesktopLayoutRecoveryProductOutcome.Reconciled
        or DesktopLayoutRecoveryProductOutcome.AcceptedCurrentState;

    public DesktopLayoutRecoveryProductResult(
        DesktopLayoutRecoveryProductOutcome outcome,
        int pendingCount,
        Guid? sessionId,
        TargetDisplayLayoutExecutionRunResult? recoveryResult,
        IReadOnlyList<string>? issues = null)
    {
        Outcome = outcome;
        PendingCount = pendingCount;
        SessionId = sessionId;
        RecoveryResult = recoveryResult;
        Issues = Array.AsReadOnly((issues ?? Array.Empty<string>()).ToArray());
    }
}

/// <summary>
/// WinUI 可调用的产品恢复 façade。真实 Writer、Shell Reader、STA 与
/// PersistedRecoveryCoordinator 都保留在 WindowsInterop 内部。
///
/// v1 产品入口只自动处理“恰好一个” unresolved session；多笔恢复不猜顺序，fail-closed。
/// </summary>
public sealed class DesktopLayoutRecoveryProductService
{
    private readonly IRecoverySessionStore? _recoveryStore;
    private readonly PersistedRecoveryCoordinator _coordinator;
    private readonly SemaphoreSlim _recoveryLock = new(1, 1);

    public DesktopLayoutRecoveryProductService(IRecoverySessionStore recoveryStore)
    {
        ArgumentNullException.ThrowIfNull(recoveryStore);
        _recoveryStore = recoveryStore;

        var topologyReader = new NativeDisplayTopologyReader();
        var iconReader = new DesktopShellIconReader();
        var writer = new DesktopShellIconPositionWriter();
        var staExecutor = new StaThreadExecutor();

        _coordinator = new PersistedRecoveryCoordinator(
            recoveryStore,
            topologyReader,
            iconReader,
            writer,
            staExecutor);
    }

    internal DesktopLayoutRecoveryProductService(PersistedRecoveryCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public async Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _coordinator.ListUnresolvedAsync(cancellationToken);
        return pending.Count;
    }

    public async Task<DesktopLayoutRecoveryProductResult> ReconcileSingleAsync(
        CancellationToken cancellationToken = default)
    {
        await _recoveryLock.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<RecoverySessionRecord> pending;
            try
            {
                pending = await _coordinator.ListUnresolvedAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new DesktopLayoutRecoveryProductResult(
                    DesktopLayoutRecoveryProductOutcome.Failed,
                    0,
                    null,
                    null,
                    new[] { $"读取恢复状态失败：{ex.GetType().Name}: {ex.Message}" });
            }

            if (pending.Count == 0)
            {
                return new DesktopLayoutRecoveryProductResult(
                    DesktopLayoutRecoveryProductOutcome.NoPendingRecovery,
                    0,
                    null,
                    null);
            }

            if (pending.Count != 1)
            {
                return new DesktopLayoutRecoveryProductResult(
                    DesktopLayoutRecoveryProductOutcome.MultiplePendingRecoveries,
                    pending.Count,
                    null,
                    null,
                    new[] { $"检测到 {pending.Count} 笔未解决恢复会话。v1 不猜测处理顺序。" });
            }

            var record = pending[0];
            TargetDisplayLayoutExecutionRunResult recovery;
            try
            {
                recovery = await _coordinator.RecoverAsync(record, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DesktopLayoutRecoveryProductResult(
                    DesktopLayoutRecoveryProductOutcome.Failed,
                    1,
                    record.SessionId,
                    null,
                    new[] { $"恢复协调器异常：{ex.GetType().Name}: {ex.Message}" });
            }

            var issues = new List<string>();
            if (!string.IsNullOrWhiteSpace(recovery.ForwardFailureMessage))
                issues.Add(recovery.ForwardFailureMessage);
            if (!string.IsNullOrWhiteSpace(recovery.RecoveryFailureMessage))
                issues.Add(recovery.RecoveryFailureMessage);
            if (recovery.VerificationResult is { Issues.Count: > 0 })
                issues.AddRange(recovery.VerificationResult.Issues.Take(3));

            var remaining = await _coordinator.ListUnresolvedAsync(cancellationToken);
            var stillPending = remaining.Any(candidate => candidate.SessionId == record.SessionId);
            var outcome = !stillPending
                ? DesktopLayoutRecoveryProductOutcome.Reconciled
                : DesktopLayoutRecoveryProductOutcome.StillNeedsAttention;

            return new DesktopLayoutRecoveryProductResult(
                outcome,
                remaining.Count,
                record.SessionId,
                recovery,
                issues);
        }
        finally
        {
            _recoveryLock.Release();
        }
    }

    /// <summary>
    /// 用户显式接受当前真实桌面，并结束恰好一笔需要人工介入的恢复会话。
    /// 本路径不读取/调用 Writer，不尝试恢复 Original 或 Desired，也不删除历史证据；
    /// 仅以 CAS 更新 Lifecycle=AcceptedCurrentState，使该会话不再阻断新的用户执行。
    /// </summary>
    public async Task<DesktopLayoutRecoveryProductResult> AcceptCurrentStateSingleAsync(
        CancellationToken cancellationToken = default)
    {
        if (_recoveryStore is null)
            throw new InvalidOperationException("当前 RecoveryProductService 未提供可更新的 Recovery Store。");

        await _recoveryLock.WaitAsync(cancellationToken);
        try
        {
            await _recoveryStore.InitializeAsync(cancellationToken);
            var pending = await _recoveryStore.ListUnresolvedAsync(limit: 2, cancellationToken);

            if (pending.Count == 0)
            {
                return new DesktopLayoutRecoveryProductResult(
                    DesktopLayoutRecoveryProductOutcome.NoPendingRecovery,
                    0,
                    null,
                    null);
            }

            if (pending.Count != 1)
            {
                return new DesktopLayoutRecoveryProductResult(
                    DesktopLayoutRecoveryProductOutcome.MultiplePendingRecoveries,
                    pending.Count,
                    null,
                    null,
                    new[] { $"检测到至少 {pending.Count} 笔未解决恢复会话。v1 不允许批量放弃恢复。" });
            }

            var record = pending[0];
            var disposition = record.GetDisposition();
            if (!disposition.RequiresManualRecovery)
            {
                return new DesktopLayoutRecoveryProductResult(
                    DesktopLayoutRecoveryProductOutcome.Failed,
                    1,
                    record.SessionId,
                    null,
                    new[] { "当前会话仍属于自动对账范围，不能直接接受当前状态。请先执行恢复协调。" });
            }

            var accepted = record.WithProjectedLifecycle(RecoverySessionLifecycle.AcceptedCurrentState);
            var updated = await _recoveryStore.UpdateAsync(accepted, cancellationToken);
            if (updated.GetDisposition().IsUnresolved)
            {
                return new DesktopLayoutRecoveryProductResult(
                    DesktopLayoutRecoveryProductOutcome.Failed,
                    1,
                    record.SessionId,
                    null,
                    new[] { "接受当前桌面后会话仍被投影为 unresolved，已停止后续操作。" });
            }

            var remaining = await _recoveryStore.ListUnresolvedAsync(limit: 2, cancellationToken);
            return new DesktopLayoutRecoveryProductResult(
                DesktopLayoutRecoveryProductOutcome.AcceptedCurrentState,
                remaining.Count,
                updated.SessionId,
                null,
                new[] { "用户已显式接受当前真实桌面；未执行任何恢复 Writer，历史恢复证据继续保留。" });
        }
        finally
        {
            _recoveryLock.Release();
        }
    }
}
