using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

/// <summary>
/// 跨重启恢复专用的桌面状态观察器。
/// 连续采样桌面图标与显示拓扑，并把稳定性判断交给 Core 的纯比较器。
/// </summary>
public sealed class RecoveryDesktopStateObserver
{
    private readonly IDisplayTopologyReader _topologyReader;
    private readonly IDesktopIconReader _iconReader;
    private readonly StaThreadExecutor _staExecutor;

    public RecoveryDesktopStateObserver(
        IDisplayTopologyReader topologyReader,
        IDesktopIconReader iconReader,
        StaThreadExecutor staExecutor)
    {
        _topologyReader = topologyReader ?? throw new ArgumentNullException(nameof(topologyReader));
        _iconReader = iconReader ?? throw new ArgumentNullException(nameof(iconReader));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
    }

    public async Task<RecoveryObservationStabilityResult> ObserveStableAsync(
        TargetDisplayLayoutExecutionPlan executionPlan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);
        ct.ThrowIfCancellationRequested();

        var first = await CaptureSampleAsync(ct);
        ct.ThrowIfCancellationRequested();
        var second = await CaptureSampleAsync(ct);

        return RecoveryObservationStabilityEvaluator.Evaluate(executionPlan, first, second);
    }

    /// <summary>
    /// Writer 紧邻调用前的 topology guard。
    /// 稳定观察之后若显示器集合、坐标范围、工作区、DPI 或主屏身份发生变化，则拒绝写入。
    /// </summary>
    public bool IsTopologyStillEquivalent(RecoveryObservationSample stableSample)
    {
        ArgumentNullException.ThrowIfNull(stableSample);
        var current = _topologyReader.GetDisplays();
        return RecoveryObservationStabilityEvaluator.AreTopologiesEquivalent(
            stableSample.Displays,
            current);
    }

    private async Task<RecoveryObservationSample> CaptureSampleAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var displays = _topologyReader.GetDisplays().ToArray();
        var rawRead = await _staExecutor.RunAsync(() => _iconReader.ReadDesktopIcons());

        var assignedIcons = rawRead.Icons.Select(icon =>
        {
            var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays);
            return icon with { MonitorDeviceName = monitor?.DeviceName };
        }).ToArray();

        var desktop = new DesktopIconReadResult(
            assignedIcons,
            rawRead.FailedItemCount,
            rawRead.ConnectionMethod,
            rawRead.ReadTime,
            rawRead.PrimaryPathException);

        return new RecoveryObservationSample(displays, desktop);
    }
}
