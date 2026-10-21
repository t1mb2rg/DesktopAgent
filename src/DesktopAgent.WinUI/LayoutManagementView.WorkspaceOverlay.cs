using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.WindowsInterop.DesktopOverlays;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private readonly List<DesktopZoneOverlayHost> _workspaceOverlayHosts = [];

    private async Task ShowDraftWorkspaceOverlayAsync(
        DesktopAgent.Core.DesktopLayouts.Workspaces.Editing.DesktopWorkspaceDesignDraft draft)
    {
        await _designSession.RefreshDesignGeometryAsync();
        var geometry = _designSession.DesignGeometry ?? throw new InvalidOperationException("请先校准网格。");
        var observation = _designSession.DesignObservation ?? throw new InvalidOperationException("当前显示器不可读取。");
        if (draft.Zones.Any(zone => !geometry.Displays.Any(display => display.DisplayRef == zone.DisplayRef)))
            throw new InvalidOperationException("设计中的显示器与当前环境不一致，请先调整设计。");
        await _settingsStore.InitializeAsync();
        _settings = await _settingsStore.GetAsync();
        HideOverlay();
        foreach (var grid in geometry.Displays)
        {
            var zones = draft.Zones.Where(zone => zone.DisplayRef == grid.DisplayRef).Select((zone, index) =>
            {
                var rect = DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry.ExplorerGridPresentation.FrameBounds(
                    DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry.ExplorerGridPresentation.Quantize(zone.Bounds, grid), grid);
                var work = grid.WorkArea;
                int X(int x) => Math.Clamp((int)Math.Round((x - work.Left) * (double)NormalizedRect.Scale / work.Width), 0, NormalizedRect.Scale);
                int Y(int y) => Math.Clamp((int)Math.Round((y - work.Top) * (double)NormalizedRect.Scale / work.Height), 0, NormalizedRect.Scale);
                var id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(zone.DisplayRef + "/" + zone.ZoneKey)).AsSpan(0, 16));
                return new DesktopAgent.Core.DesktopLayouts.Templates.DesktopLayoutZone(id, zone.Name,
                    new NormalizedRect(X(rect.Left), Y(rect.Top), X(rect.Right), Y(rect.Bottom)), index,
                    DesktopAgent.Core.DesktopLayouts.Templates.ZonePlacementFlow.RowMajor, null);
            }).ToArray();
            if (zones.Length == 0) continue;
            var host = new DesktopZoneOverlayHost();
            _workspaceOverlayHosts.Add(host);
            host.Show(observation.Displays.Single(display => display.DeviceName == grid.DeviceName), zones, _settings.OverlayAppearance);
        }
        OverlayToggleButton.Content = "▣ 隐藏线框";
        OverlayStateText.Text = "当前设计线框 · 只读 · 鼠标穿透 · 尚未移动图标";
    }

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
                DesktopWorkspaceOverlayProjection.Project(expected.Zones, expected.GridGeometry), _settings.OverlayAppearance);
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
