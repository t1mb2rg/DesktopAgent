using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

/// <summary>
/// 组装一次真机探测所需的真实依赖。
///
/// 存在的理由是边界：物理写入器与专用快捷方式存储不应在 UI 层被构造
/// （产品布局面板的契约测试明确禁止 WinUI partial 出现 <c>DesktopShellIconPositionWriter</c>）。
/// 调用方只提供它能合法知道的东西——观察源与 STA 执行器——其余由这一处决定。
/// </summary>
public static class ExplorerGridPhysicalProbeRunnerFactory
{
    /// <summary>与诊断工作台一致的稳定观察间隔：两次采样间隔 500ms。</summary>
    public static readonly TimeSpan StabilitySampleInterval = TimeSpan.FromMilliseconds(500);

    public static ExplorerGridPhysicalProbeRunner Create(
        IDesktopObservationSource observationSource,
        StaThreadExecutor staThreadExecutor,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(observationSource);
        ArgumentNullException.ThrowIfNull(staThreadExecutor);

        return new ExplorerGridPhysicalProbeRunner(
            new NativeDisplayTopologyReader(),
            observationSource,
            new StableDesktopObservationSource(
                observationSource,
                new DesktopObservationStabilitySettings(StabilitySampleInterval)),
            new DesktopShellIconPositionWriter(),
            staThreadExecutor,
            new DesktopInternetShortcutStore(),
            timeProvider ?? TimeProvider.System);
    }
}
