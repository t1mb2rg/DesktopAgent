using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Workspaces.Execution;

/// <summary>
/// Workspace Frozen Preview 的产品级 Apply 边界。
/// WinUI 只提交显式确认对象和持久化接口；真实 Shell Writer、fresh Observation 与 Dynamic Coordinator
/// 都在 WindowsInterop 内部组装，避免 UI 获得 Writer authority。
/// </summary>
public sealed class DesktopWorkspaceApplyService
{
    private readonly IRecoverySessionStore _legacyRecoveryStore;
    private readonly Func<DesktopWorkspaceApplyConfirmation, CancellationToken,
        Task<DesktopWorkspaceDynamicExecutionRunResult>> _executeAsync;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public DesktopWorkspaceApplyService(
        IDesktopLayoutSnapshotStore snapshotStore,
        IDesktopWorkspaceExecutionJournalStore workspaceJournalStore,
        IDesktopWorkspaceDynamicExecutionJournalStore dynamicWorkspaceJournalStore,
        IRecoverySessionStore legacyRecoveryStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentNullException.ThrowIfNull(workspaceJournalStore);
        ArgumentNullException.ThrowIfNull(dynamicWorkspaceJournalStore);
        _legacyRecoveryStore = legacyRecoveryStore
            ?? throw new ArgumentNullException(nameof(legacyRecoveryStore));

        var clock = timeProvider ?? TimeProvider.System;
        var topologyReader = new NativeDisplayTopologyReader();
        var iconReader = new DesktopShellIconReader();
        var capabilitiesReader = new DesktopShellIconPositioningCapabilitiesReader();
        var positionWriter = new DesktopShellIconPositionWriter();
        var staExecutor = new StaThreadExecutor();
        var observationSource = new WindowsDesktopObservationSource(
            topologyReader,
            iconReader,
            capabilitiesReader,
            staExecutor,
            clock);
        var snapshotFactory = new DesktopLayoutSnapshotFactory(clock);
        var coordinator = new DesktopWorkspaceDynamicExecutionCoordinator(
            observationSource,
            positionWriter,
            staExecutor,
            new DefaultSafetySnapshotFactory(snapshotFactory),
            snapshotStore,
            workspaceJournalStore,
            dynamicWorkspaceJournalStore,
            clock);

        _executeAsync = coordinator.ExecuteAsync;
    }

    internal DesktopWorkspaceApplyService(
        IRecoverySessionStore legacyRecoveryStore,
        Func<DesktopWorkspaceApplyConfirmation, CancellationToken,
            Task<DesktopWorkspaceDynamicExecutionRunResult>> executeAsync)
    {
        _legacyRecoveryStore = legacyRecoveryStore
            ?? throw new ArgumentNullException(nameof(legacyRecoveryStore));
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }

    public async Task<DesktopWorkspaceDynamicExecutionRunResult> ApplyAsync(
        DesktopWorkspaceApplyConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        await _applyLock.WaitAsync(cancellationToken);
        try
        {
            // Workspace 与旧单屏执行共享同一真实桌面。旧 Recovery 未收敛时，新的 Workspace Writer
            // 必须 fail-closed，不能让多代 execution journal 同时争夺桌面事实。
            await _legacyRecoveryStore.InitializeAsync(cancellationToken);
            var unresolvedLegacy = (await _legacyRecoveryStore.ListUnresolvedAsync(50, cancellationToken))
                .Where(static record => record.GetDisposition().IsUnresolved)
                .Take(1)
                .ToArray();
            if (unresolvedLegacy.Length != 0)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "UnresolvedLegacyRecovery",
                    $"检测到未解决旧恢复会话 {unresolvedLegacy[0].SessionId:D}，Workspace 写入已暂停。");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 用户已经在产品层明确确认 Frozen Preview。进入 Coordinator 后不再接受 UI 取消，
            // 避免把窗口生命周期取消信号穿过 Writer boundary；Coordinator 自己负责失败收敛。
            return await _executeAsync(confirmation, CancellationToken.None);
        }
        finally
        {
            _applyLock.Release();
        }
    }
}
