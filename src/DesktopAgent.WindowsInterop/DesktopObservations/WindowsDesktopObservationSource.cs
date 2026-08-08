using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopObservations;

/// <summary>
/// Windows 11 / Explorer 的 M3-A 只读桌面事实来源。
/// 事件监听不参与此类：每次调用都重新读取显示拓扑、定位能力和完整 Shell 桌面状态。
/// </summary>
public sealed class WindowsDesktopObservationSource : IDesktopObservationSource
{
    private readonly IDisplayTopologyReader _topologyReader;
    private readonly IDesktopIconReader _iconReader;
    private readonly IDesktopIconPositioningCapabilitiesReader _capabilitiesReader;
    private readonly StaThreadExecutor _staExecutor;
    private readonly TimeProvider _timeProvider;

    public WindowsDesktopObservationSource(
        IDisplayTopologyReader topologyReader,
        IDesktopIconReader iconReader,
        IDesktopIconPositioningCapabilitiesReader capabilitiesReader,
        StaThreadExecutor staExecutor,
        TimeProvider? timeProvider = null)
    {
        _topologyReader = topologyReader ?? throw new ArgumentNullException(nameof(topologyReader));
        _iconReader = iconReader ?? throw new ArgumentNullException(nameof(iconReader));
        _capabilitiesReader = capabilitiesReader ?? throw new ArgumentNullException(nameof(capabilitiesReader));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var firstTopology = TryReadTopology();
        if (firstTopology.Result is not null)
            return firstTopology.Result;
        var displays = firstTopology.Displays!;

        ShellObservation shell;
        try
        {
            shell = await _staExecutor.RunAsync(() =>
            {
                var capabilities = _capabilitiesReader.GetCapabilities();
                var desktop = _iconReader.ReadDesktopIcons();
                return new ShellObservation(capabilities, desktop);
            });
        }
        catch (Exception ex)
        {
            return DesktopObservationCaptureResult.Unavailable(
                new DesktopObservationCaptureIssue(
                    DesktopObservationCaptureIssueCode.ShellObservationFailed,
                    $"Shell 桌面观察失败：{ex.GetType().Name}: {ex.Message}"));
        }

        ct.ThrowIfCancellationRequested();

        // 一次 Observation 自己也必须描述同一套物理坐标系。
        // 若 topology 在 Shell 读取期间改变，这一帧直接作废，交给上层稍后重新观察。
        var secondTopology = TryReadTopology();
        if (secondTopology.Result is not null)
            return secondTopology.Result;
        if (!DisplayTopologyComparer.AreEquivalent(displays, secondTopology.Displays!))
        {
            return DesktopObservationCaptureResult.Unavailable(
                new DesktopObservationCaptureIssue(
                    DesktopObservationCaptureIssueCode.TopologyChangedDuringCapture,
                    "显示拓扑在一次桌面观察过程中发生变化，当前物理坐标事实不一致。"));
        }

        if (shell.Desktop.FailedItemCount != 0)
        {
            return DesktopObservationCaptureResult.Unavailable(
                new DesktopObservationCaptureIssue(
                    DesktopObservationCaptureIssueCode.IncompleteDesktopRead,
                    $"桌面读取不完整：FailedItemCount={shell.Desktop.FailedItemCount}。"));
        }

        var duplicateStableIds = shell.Desktop.Icons
            .GroupBy(icon => icon.StableId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicateStableIds.Length > 0)
        {
            return DesktopObservationCaptureResult.Unavailable(
                duplicateStableIds.Select(stableId =>
                    new DesktopObservationCaptureIssue(
                        DesktopObservationCaptureIssueCode.DuplicateStableId,
                        $"StableId “{stableId}”在当前完整读取中不唯一，拒绝建立可比较事实。",
                        stableId))
                    .ToArray());
        }

        var assignedIcons = new DesktopIconDescriptor[shell.Desktop.Icons.Count];
        var assignmentIssues = new List<DesktopObservationCaptureIssue>();
        for (var i = 0; i < shell.Desktop.Icons.Count; i++)
        {
            var icon = shell.Desktop.Icons[i];
            var display = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays);
            if (display is null)
            {
                assignmentIssues.Add(new DesktopObservationCaptureIssue(
                    DesktopObservationCaptureIssueCode.IconOutsideDisplayTopology,
                    $"StableId “{icon.StableId}”的物理坐标 {icon.PixelPosition} 不属于当前任何显示器。",
                    icon.StableId));
                continue;
            }

            assignedIcons[i] = icon with { MonitorDeviceName = display.DeviceName };
        }

        if (assignmentIssues.Count > 0)
            return DesktopObservationCaptureResult.Unavailable(assignmentIssues.ToArray());

        try
        {
            var observation = new DesktopObservation(
                Guid.NewGuid(),
                _timeProvider.GetUtcNow(),
                displays,
                shell.Capabilities,
                assignedIcons,
                shell.Desktop.ConnectionMethod,
                shell.Desktop.ReadTime);

            return DesktopObservationCaptureResult.Captured(observation);
        }
        catch (Exception ex)
        {
            return DesktopObservationCaptureResult.Unavailable(
                new DesktopObservationCaptureIssue(
                    DesktopObservationCaptureIssueCode.ObservationBuildFailed,
                    $"桌面观察模型构建失败：{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private TopologyRead TryReadTopology()
    {
        DisplayDescriptor[] displays;
        try
        {
            displays = _topologyReader.GetDisplays().ToArray();
        }
        catch (Exception ex)
        {
            return new TopologyRead(
                null,
                DesktopObservationCaptureResult.Unavailable(
                    new DesktopObservationCaptureIssue(
                        DesktopObservationCaptureIssueCode.TopologyReadFailed,
                        $"显示拓扑读取失败：{ex.GetType().Name}: {ex.Message}")));
        }

        if (displays.Length == 0)
        {
            return new TopologyRead(
                null,
                DesktopObservationCaptureResult.Unavailable(
                    new DesktopObservationCaptureIssue(
                        DesktopObservationCaptureIssueCode.NoDisplays,
                        "当前未读取到任何显示器，无法建立桌面物理坐标事实。")));
        }

        var duplicateDisplayNames = displays
            .GroupBy(display => display.DeviceName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (duplicateDisplayNames.Length > 0)
        {
            return new TopologyRead(
                null,
                DesktopObservationCaptureResult.Unavailable(
                    duplicateDisplayNames.Select(name =>
                        new DesktopObservationCaptureIssue(
                            DesktopObservationCaptureIssueCode.DuplicateDisplayDeviceName,
                            $"显示拓扑中 DeviceName “{name}”重复，无法建立唯一显示器身份。"))
                        .ToArray()));
        }

        return new TopologyRead(displays, null);
    }

    private sealed record ShellObservation(
        DesktopIconPositioningCapabilities Capabilities,
        DesktopIconReadResult Desktop);

    private sealed record TopologyRead(
        DisplayDescriptor[]? Displays,
        DesktopObservationCaptureResult? Result);
}
