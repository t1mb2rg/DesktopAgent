using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private const string AiTopologyCanvasMarker = "Tessel.AiMultiDisplayTopology";
    private bool _aiTopologyRendering;

    private bool TryRenderAiMultiDisplayTopology()
    {
        if (_aiTopologyRendering
            || !IsAiMultiDisplayWorkspaceActive
            || _aiWorkspaceDrafts.Count <= 1)
        {
            return false;
        }

        _aiTopologyRendering = true;
        try
        {
            var workspaceDisplays = new List<(int Index, AiWorkspaceDraftState State, DisplayDescriptor Display)>();
            for (var index = 0; index < _aiWorkspaceDrafts.Count; index++)
            {
                var state = _aiWorkspaceDrafts[index];
                workspaceDisplays.Add((index, state, state.Display));
            }

            if (workspaceDisplays.Count <= 1)
                return false;

            var virtualLeft = workspaceDisplays.Min(static item => item.Display.Bounds.Left);
            var virtualTop = workspaceDisplays.Min(static item => item.Display.Bounds.Top);
            var virtualRight = workspaceDisplays.Max(static item => item.Display.Bounds.Right);
            var virtualBottom = workspaceDisplays.Max(static item => item.Display.Bounds.Bottom);
            var virtualWidth = (long)virtualRight - virtualLeft;
            var virtualHeight = (long)virtualBottom - virtualTop;
            if (virtualWidth <= 0 || virtualHeight <= 0)
                return false;

            var canvasWidth = Math.Max(100d, LayoutCanvas.ActualWidth);
            var canvasHeight = Math.Max(100d, LayoutCanvas.ActualHeight);
            const double inset = 20d;
            var drawableWidth = Math.Max(1d, canvasWidth - inset * 2);
            var drawableHeight = Math.Max(1d, canvasHeight - inset * 2);
            var scale = Math.Min(drawableWidth / virtualWidth, drawableHeight / virtualHeight);
            if (!double.IsFinite(scale) || scale <= 0d)
                return false;

            var renderedWidth = virtualWidth * scale;
            var renderedHeight = virtualHeight * scale;
            var originX = (canvasWidth - renderedWidth) / 2d;
            var originY = (canvasHeight - renderedHeight) / 2d;

            double X(int globalX) => originX + ((long)globalX - virtualLeft) * scale;
            double Y(int globalY) => originY + ((long)globalY - virtualTop) * scale;

            LayoutCanvas.Children.Clear();
            LayoutCanvasEmptyState.Visibility = Visibility.Collapsed;
            LayoutCanvas.Children.Add(new Border
            {
                Width = 0,
                Height = 0,
                Tag = AiTopologyCanvasMarker,
            });

            foreach (var (index, state, display) in workspaceDisplays)
            {
                var bounds = display.Bounds;
                var left = X(bounds.Left);
                var top = Y(bounds.Top);
                var width = Math.Max(1d, bounds.Width * scale);
                var height = Math.Max(1d, bounds.Height * scale);

                var surface = new Border
                {
                    Width = width,
                    Height = height,
                    CornerRadius = new CornerRadius(7),
                    Background = Brush(16, 0xFF, 0xFF, 0xFF),
                    BorderBrush = Brush(
                        index == _activeAiWorkspaceIndex ? (byte)225 : (byte)82,
                        0x00,
                        0x78,
                        0xD4),
                    BorderThickness = new Thickness(index == _activeAiWorkspaceIndex ? 2.5 : 1.2),
                };
                Canvas.SetLeft(surface, left);
                Canvas.SetTop(surface, top);
                LayoutCanvas.Children.Add(surface);

                var work = display.WorkArea;
                foreach (var zone in state.Draft.Zones.OrderBy(static zone => zone.SortOrder))
                {
                    var zoneLeftGlobal = work.Left + (long)work.Width * zone.Bounds.Left / NormalizedRect.Scale;
                    var zoneTopGlobal = work.Top + (long)work.Height * zone.Bounds.Top / NormalizedRect.Scale;
                    var zoneRightGlobal = work.Left + (long)work.Width * zone.Bounds.Right / NormalizedRect.Scale;
                    var zoneBottomGlobal = work.Top + (long)work.Height * zone.Bounds.Bottom / NormalizedRect.Scale;

                    var color = ResolveZoneCanvasColor(zone.ZoneId);
                    var zoneBlock = new Border
                    {
                        Width = Math.Max(1d, (zoneRightGlobal - zoneLeftGlobal) * scale),
                        Height = Math.Max(1d, (zoneBottomGlobal - zoneTopGlobal) * scale),
                        CornerRadius = new CornerRadius(5),
                        BorderThickness = new Thickness(1),
                        BorderBrush = Brush(145, color.R, color.G, color.B),
                        Background = Brush(30, color.R, color.G, color.B),
                        Child = new TextBlock
                        {
                            Text = zone.Name,
                            FontSize = 10,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Thickness(6, 4, 5, 4),
                        },
                    };
                    Canvas.SetLeft(zoneBlock, X(checked((int)zoneLeftGlobal)));
                    Canvas.SetTop(zoneBlock, Y(checked((int)zoneTopGlobal)));
                    LayoutCanvas.Children.Add(zoneBlock);
                }

                var displayIndex = index;
                var hitSurface = new Border
                {
                    Width = width,
                    Height = height,
                    CornerRadius = new CornerRadius(7),
                    Background = Brush(1, 0xFF, 0xFF, 0xFF),
                    BorderThickness = new Thickness(0),
                    Child = new TextBlock
                    {
                        Text = $"{(state.IsPrimary ? "主屏" : "副屏")} · {ShortDeviceName(state.DeviceName)} · {state.SourceAssignmentCount} 项",
                        Margin = new Thickness(8, 6, 8, 6),
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                    },
                };
                hitSurface.PointerPressed += (_, _) =>
                {
                    if (AiWorkspaceDisplayCombo.SelectedIndex != displayIndex)
                        AiWorkspaceDisplayCombo.SelectedIndex = displayIndex;
                };
                ToolTipService.SetToolTip(hitSurface, "点击聚焦这块显示器进行精细编辑");
                Canvas.SetLeft(hitSurface, left);
                Canvas.SetTop(hitSurface, top);
                LayoutCanvas.Children.Add(hitSurface);
            }

            RenderActivePreviewAcrossTopology(
                X,
                Y,
                scale,
                virtualLeft,
                virtualTop,
                virtualRight,
                virtualBottom);

            LayoutCanvasStatusText.Text =
                $"多屏拓扑 · {_aiWorkspaceDrafts.Count} 块显示器 · 点击屏幕聚焦编辑" +
                (_latestPreview is null ? " · 尚未预览当前聚焦屏" : " · 蓝线/蓝点为当前聚焦屏 Preview");
            return true;
        }
        catch
        {
            // 这里只是产品可视化增强。失败时保留原单屏 Canvas，不改变任何 Preview / Writer 权限。
            return false;
        }
        finally
        {
            _aiTopologyRendering = false;
        }
    }

    private void RenderActivePreviewAcrossTopology(
        Func<int, double> x,
        Func<int, double> y,
        double scale,
        int virtualLeft,
        int virtualTop,
        int virtualRight,
        int virtualBottom)
    {
        if (_latestPreview is null)
            return;

        foreach (var item in _latestPreview.Plan.Items.Take(120))
        {
            if (!item.TargetPosition.HasValue)
                continue;

            var target = item.TargetPosition.Value;
            if (!InsideVirtualDesktop(target.X, target.Y, virtualLeft, virtualTop, virtualRight, virtualBottom))
                continue;

            var current = item.CurrentPosition;
            var currentVisible = InsideVirtualDesktop(
                current.X,
                current.Y,
                virtualLeft,
                virtualTop,
                virtualRight,
                virtualBottom);
            var targetX = x(target.X);
            var targetY = y(target.Y);

            if (currentVisible)
            {
                var currentX = x(current.X);
                var currentY = y(current.Y);
                if (Math.Abs(currentX - targetX) > 1d || Math.Abs(currentY - targetY) > 1d)
                {
                    LayoutCanvas.Children.Add(new Line
                    {
                        X1 = currentX,
                        Y1 = currentY,
                        X2 = targetX,
                        Y2 = targetY,
                        StrokeThickness = item.Icon.MonitorDeviceName == _latestPreview.Plan.TargetDisplay.DeviceName
                            ? 1d
                            : 1.8d,
                        Stroke = Brush(125, 0x00, 0x78, 0xD4),
                    });
                }
            }

            var dotSize = Math.Clamp(7d * Math.Sqrt(Math.Max(scale, 0.01d)), 6d, 9d);
            var dot = new Ellipse
            {
                Width = dotSize,
                Height = dotSize,
                Fill = Brush(230, 0x00, 0x78, 0xD4),
                Stroke = Brush(255, 0xFF, 0xFF, 0xFF),
                StrokeThickness = 1,
            };
            ToolTipService.SetToolTip(dot, item.Icon.DisplayName);
            Canvas.SetLeft(dot, targetX - dotSize / 2d);
            Canvas.SetTop(dot, targetY - dotSize / 2d);
            LayoutCanvas.Children.Add(dot);
        }
    }

    private static bool InsideVirtualDesktop(
        int x,
        int y,
        int left,
        int top,
        int right,
        int bottom)
        => x >= left && x < right && y >= top && y < bottom;
}
