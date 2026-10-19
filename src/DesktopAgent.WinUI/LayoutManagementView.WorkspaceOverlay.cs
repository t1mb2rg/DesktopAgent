using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.WindowsInterop.DesktopOverlays;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private readonly List<DesktopZoneOverlayHost> _workspaceOverlayHosts = [];

    private async Task ShowFrozenWorkspaceOverlayAsync(DesktopWorkspaceFrozenPreview preview)
    {
        var capture = await _observationSource.CaptureAsync();
        if (!capture.IsCaptured || capture.Observation is null)
            throw new InvalidOperationException("当前显示器不可读取，无法安全定位线框。");

        var displays = capture.Observation.Displays;
        foreach (var expected in preview.Displays)
        {
            var current = displays.SingleOrDefault(display => display.DeviceName == expected.DeviceName);
            if (current is null || current.Bounds != expected.PixelBounds || current.WorkArea != expected.WorkArea)
                throw new InvalidOperationException("显示设置已变化，请重新生成精确预览后查看线框。");
        }

        await _settingsStore.InitializeAsync();
        _settings = await _settingsStore.GetAsync();
        HideOverlay();
        foreach (var expected in preview.Displays.Where(static display => display.Zones.Count > 0))
        {
            var host = new DesktopZoneOverlayHost();
            _workspaceOverlayHosts.Add(host);
            host.Show(displays.Single(display => display.DeviceName == expected.DeviceName),
                DesktopWorkspaceOverlayProjection.Project(expected.Zones), _settings.OverlayAppearance);
        }

        if (_workspaceOverlayHosts.Count == 0)
        {
            ShowInfo("没有可显示的分区", "当前工作区设计没有分区线框。", InfoBarSeverity.Informational);
            return;
        }

        OverlayToggleButton.Content = "▣ 隐藏线框";
        OverlayStateText.Text = $"{_workspaceOverlayHosts.Count} 块屏幕线框显示中 · 只读 · 鼠标穿透";
        ShowInfo("工作区线框已显示", "显示冻结预览的分区；不消费应用凭据，也不移动桌面图标。", InfoBarSeverity.Success);
    }
}
