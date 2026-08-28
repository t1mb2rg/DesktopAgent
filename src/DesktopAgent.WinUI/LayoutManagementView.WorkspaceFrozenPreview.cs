using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private const int MaxWorkspaceFrozenPreviewMovementLines = 32;
    private const int MaxWorkspaceFrozenPreviewItemDots = 180;

    private DesktopWorkspaceFrozenPreview? _workspaceFrozenPreview;
    private int _activeWorkspaceFrozenPreviewDisplayIndex = -1;

    internal bool TryLoadAiWorkspaceFrozenPreview(DesktopWorkspaceFrozenPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (_isLayoutEditing && _draft is not null)
        {
            ShowInfo(
                "已有布局正在编辑",
                "新的 Workspace Frozen Preview 没有覆盖当前未保存布局。请先保存或取消当前编辑。",
                InfoBarSeverity.Warning);
            return false;
        }

        if (_zoneColorPreferencesDirty)
        {
            ShowInfo(
                "分区颜色尚未应用",
                "新的 Workspace Frozen Preview 没有覆盖当前颜色修改。请先应用或还原颜色。",
                InfoBarSeverity.Warning);
            return false;
        }

        if (preview.Displays.Count == 0)
            throw new InvalidOperationException("Workspace Frozen Preview 没有显示器。 ");
        if (preview.Items.Count != preview.ExactPlan.Items.Count)
            throw new InvalidOperationException("Workspace Frozen Preview 没有完整覆盖 Exact Plan 项目。 ");

        ClearAiMultiDisplayWorkspace();
        ClearAiLogicalWorkspacePreview();
        _draft = null;
        _latestPreview = null;
        _workspaceFrozenPreview = preview;
        _activeWorkspaceFrozenPreviewDisplayIndex = 0;
        SetSelectedTemplateWithoutNotification(null);

        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.Items.Clear();
            foreach (var display in preview.Displays)
            {
                AiWorkspaceDisplayCombo.Items.Add(new ComboBoxItem
                {
                    Content = WorkspaceFrozenPreviewDisplayLabel(display),
                });
            }
            AiWorkspaceDisplayCombo.SelectedIndex = 0;
            AiWorkspaceDisplayCombo.Visibility = preview.Displays.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _suppressAiWorkspaceSelectionChanged = false;
        }

        LayoutPreviewList.SelectionMode = ListViewSelectionMode.None;
        LayoutPreviewList.ContextFlyout = null;

        LayoutEditorPanel.Visibility = Visibility.Visible;
        LayoutEmptyEditorState.Visibility = Visibility.Collapsed;
        LayoutPreviewDetails.Visibility = Visibility.Visible;
        SetLayoutInteractionMode(isEditing: true);
        ApplyToDesktopButton.IsEnabled = false;
        FocusAiWorkspaceFrozenPreviewDisplay(0);

        ShowInfo(
            "AI Workspace Preview 已冻结",
            $"Blueprint v{preview.Blueprint.Revision} · {preview.Displays.Count} 块显示器 · " +
            $"{preview.Items.Count} 个项目 · 移动 {preview.MoveCount} · 跨屏 {preview.CrossDisplayAssignmentCount}。" +
            "当前只展示已经通过 Realization Certificate 的精确结果，Writer authority 仍为 0。",
            InfoBarSeverity.Success);
        return true;
    }

    private void FocusAiWorkspaceFrozenPreviewDisplay(int index)
    {
        var preview = _workspaceFrozenPreview;
        if (preview is null || index < 0 || index >= preview.Displays.Count)
            return;

        _activeWorkspaceFrozenPreviewDisplayIndex = index;
        var display = preview.Displays[index];
        LayoutNameTextBox.Text = $"AI Workspace · {display.DisplayRef}";
        LayoutDeviceNameTextBox.Text = display.DisplayRef;
        LayoutTargetBindingCombo.SelectedIndex = display.IsPrimary ? 0 : 2;
        LayoutCanvasDraftNameText.Text = $"AI Workspace Blueprint v{preview.Blueprint.Revision}";
        LayoutCanvasStatusText.Text =
            $"Frozen Preview · 当前 {display.DisplayRef} · {display.Policy} · " +
            $"源 {display.SourceItemCount} / 目标 {display.TargetPlacementCount} / " +
            $"迁入 {display.InboundCount} / 迁出 {display.OutboundCount}";
        LayoutRuleSummaryText.Text =
            $"Blueprint v{preview.Blueprint.Revision} · Plan {ShortFingerprint(preview.ExactPlan.PlanFingerprint)} · " +
            $"Certificate {ShortFingerprint(preview.RealizationCertificate.CertificateFingerprint)}";

        _zoneItems.Clear();
        foreach (var zone in display.Zones.OrderBy(static zone => zone.ZoneKey, StringComparer.Ordinal))
        {
            _zoneItems.Add(new LayoutZoneListItem(
                zone.Name,
                RelativeBoundsText(zone.RealizedBounds),
                $"{zone.Anchor} / {zone.PackingOrder}"));
        }
        foreach (var region in display.ReservedRegions.OrderBy(static region => region.RegionKey, StringComparer.Ordinal))
        {
            _zoneItems.Add(new LayoutZoneListItem(
                $"留白：{region.Purpose}",
                RelativeBoundsText(region.RealizedBounds),
                "Reserved"));
        }

        _previewItems.Clear();
        foreach (var item in preview.Items
                     .Where(item =>
                         string.Equals(item.SourceDisplayRef, display.DisplayRef, StringComparison.Ordinal) ||
                         string.Equals(item.TargetDisplayRef, display.DisplayRef, StringComparison.Ordinal))
                     .Take(100))
        {
            var target = item.TargetPixelPosition.HasValue
                ? $"{item.TargetDisplayRef} ({item.TargetPixelPosition.Value.X}, {item.TargetPixelPosition.Value.Y})"
                : "—";
            _previewItems.Add(new LayoutPreviewListItem(
                item.DisplayName,
                $"{item.Disposition} / {item.CategoryKey}",
                $"{item.SourceDisplayRef} ({item.SourcePixelPosition.X}, {item.SourcePixelPosition.Y}) → {target}"));
        }

        MoveCountText.Text = preview.MoveCount.ToString();
        StayCountText.Text = (preview.KeepCount + preview.Items.Count(static item =>
            item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place && !item.IsPositionChange)).ToString();
        TargetDisplayText.Text = preview.Displays.Count == 1 ? display.DisplayRef : "多屏 Workspace";
        LayoutPreviewSummaryText.Text =
            $"输入 {preview.Items.Count} · 移动 {preview.MoveCount} · 跨屏 {preview.CrossDisplayAssignmentCount} · " +
            $"保持 {preview.KeepCount} · 排除 {preview.ExcludeCount} · Certificate 已验证";
        LayoutPreviewObservationText.Text =
            $"Frozen Preview {preview.PreviewId:D}\n" +
            $"Observation {preview.SourceObservationId:D}\n" +
            $"Preview {ShortFingerprint(preview.PreviewFingerprint)} · Source {ShortFingerprint(preview.SourceStateFingerprint)}";
        LayoutPreviewDetails.Visibility = Visibility.Visible;
        RenderLayoutCanvas();
    }

    private void RenderAiWorkspaceFrozenPreview()
    {
        var preview = _workspaceFrozenPreview;
        if (preview is null)
            return;

        var virtualLeft = preview.Displays.Min(static display => display.PixelBounds.Left);
        var virtualTop = preview.Displays.Min(static display => display.PixelBounds.Top);
        var virtualRight = preview.Displays.Max(static display => display.PixelBounds.Right);
        var virtualBottom = preview.Displays.Max(static display => display.PixelBounds.Bottom);
        var virtualWidth = (long)virtualRight - virtualLeft;
        var virtualHeight = (long)virtualBottom - virtualTop;
        if (virtualWidth <= 0 || virtualHeight <= 0)
            return;

        var canvasWidth = Math.Max(100d, LayoutCanvas.ActualWidth);
        var canvasHeight = Math.Max(100d, LayoutCanvas.ActualHeight);
        const double inset = 20d;
        var drawableWidth = Math.Max(1d, canvasWidth - inset * 2d);
        var drawableHeight = Math.Max(1d, canvasHeight - inset * 2d);
        var scale = Math.Min(drawableWidth / virtualWidth, drawableHeight / virtualHeight);
        if (!double.IsFinite(scale) || scale <= 0d)
            return;

        var renderedWidth = virtualWidth * scale;
        var renderedHeight = virtualHeight * scale;
        var originX = (canvasWidth - renderedWidth) / 2d;
        var originY = (canvasHeight - renderedHeight) / 2d;

        double X(double globalX) => originX + (globalX - virtualLeft) * scale;
        double Y(double globalY) => originY + (globalY - virtualTop) * scale;

        var geometry = preview.Displays.ToDictionary(
            static display => display.DisplayRef,
            display => new WorkspaceFrozenDisplayCanvasGeometry(
                display,
                X(display.PixelBounds.Left),
                Y(display.PixelBounds.Top),
                Math.Max(1d, display.PixelBounds.Width * scale),
                Math.Max(1d, display.PixelBounds.Height * scale),
                scale),
            StringComparer.Ordinal);

        foreach (var pair in preview.Displays.Select((display, index) => (display, index)))
        {
            var canvasDisplay = geometry[pair.display.DisplayRef];
            var selected = pair.index == _activeWorkspaceFrozenPreviewDisplayIndex;
            var surface = new Border
            {
                Width = canvasDisplay.Width,
                Height = canvasDisplay.Height,
                CornerRadius = new CornerRadius(7),
                Background = Brush(18, 0xFF, 0xFF, 0xFF),
                BorderBrush = Brush(selected ? (byte)230 : (byte)90, 0x00, 0x78, 0xD4),
                BorderThickness = new Thickness(selected ? 2.5 : 1.2),
            };
            Canvas.SetLeft(surface, canvasDisplay.Left);
            Canvas.SetTop(surface, canvasDisplay.Top);
            LayoutCanvas.Children.Add(surface);

            foreach (var zone in pair.display.Zones)
            {
                AddWorkspaceFrozenRegion(
                    canvasDisplay,
                    zone.RealizedBounds,
                    zone.Name,
                    isReserved: false,
                    outlineOnly: false);
            }
            foreach (var region in pair.display.ReservedRegions)
            {
                AddWorkspaceFrozenRegion(
                    canvasDisplay,
                    region.RealizedBounds,
                    $"留白 · {region.Purpose}",
                    isReserved: true,
                    outlineOnly: false);
            }
        }

        var selectedDisplayRef = _activeWorkspaceFrozenPreviewDisplayIndex >= 0 &&
                                 _activeWorkspaceFrozenPreviewDisplayIndex < preview.Displays.Count
            ? preview.Displays[_activeWorkspaceFrozenPreviewDisplayIndex].DisplayRef
            : null;

        var movementLinesDrawn = 0;
        foreach (var item in preview.Items)
        {
            if (movementLinesDrawn >= MaxWorkspaceFrozenPreviewMovementLines)
                break;
            if (!item.IsPositionChange ||
                item.Disposition != DesktopWorkspaceCompiledItemDisposition.Place ||
                !item.TargetPixelPosition.HasValue ||
                item.TargetDisplayRef is null)
            {
                continue;
            }
            if (selectedDisplayRef is not null &&
                !string.Equals(item.SourceDisplayRef, selectedDisplayRef, StringComparison.Ordinal) &&
                !string.Equals(item.TargetDisplayRef, selectedDisplayRef, StringComparison.Ordinal))
            {
                continue;
            }

            LayoutCanvas.Children.Add(new Line
            {
                X1 = X(item.SourcePixelPosition.X),
                Y1 = Y(item.SourcePixelPosition.Y),
                X2 = X(item.TargetPixelPosition.Value.X),
                Y2 = Y(item.TargetPixelPosition.Value.Y),
                StrokeThickness = item.IsCrossDisplayMove ? 1.45d : 0.85d,
                Stroke = Brush(item.IsCrossDisplayMove ? (byte)105 : (byte)48, 0x00, 0x78, 0xD4),
            });
            movementLinesDrawn++;
        }

        foreach (var item in preview.Items.Take(MaxWorkspaceFrozenPreviewItemDots))
        {
            var point = item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
                        item.TargetPixelPosition.HasValue
                ? item.TargetPixelPosition.Value
                : item.SourcePixelPosition;
            var red = item.Disposition == DesktopWorkspaceCompiledItemDisposition.Exclude ? (byte)0xD8 : (byte)0x00;
            var green = item.Disposition == DesktopWorkspaceCompiledItemDisposition.Exclude ? (byte)0x6B : (byte)0x78;
            var blue = item.Disposition == DesktopWorkspaceCompiledItemDisposition.Exclude ? (byte)0x00 : (byte)0xD4;
            AddWorkspaceFrozenItemDot(item, X(point.X), Y(point.Y), red, green, blue);
        }

        // 项目点与迁移线之后重描结构边界，让用户首先能审阅 AI 的空间设计，而不是被 100+ 项细节淹没。
        foreach (var display in preview.Displays)
        {
            var canvasDisplay = geometry[display.DisplayRef];
            foreach (var zone in display.Zones)
            {
                AddWorkspaceFrozenRegion(
                    canvasDisplay,
                    zone.RealizedBounds,
                    zone.Name,
                    isReserved: false,
                    outlineOnly: true);
            }
            foreach (var region in display.ReservedRegions)
            {
                AddWorkspaceFrozenRegion(
                    canvasDisplay,
                    region.RealizedBounds,
                    $"留白 · {region.Purpose}",
                    isReserved: true,
                    outlineOnly: true);
            }
        }

        foreach (var pair in preview.Displays.Select((display, index) => (display, index)))
        {
            var canvasDisplay = geometry[pair.display.DisplayRef];
            var displayIndex = pair.index;
            var hit = new Border
            {
                Width = canvasDisplay.Width,
                Height = canvasDisplay.Height,
                Background = Brush(1, 0xFF, 0xFF, 0xFF),
                Child = new TextBlock
                {
                    Text = WorkspaceFrozenPreviewDisplayLabel(pair.display),
                    Margin = new Thickness(8, 6, 8, 6),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
            };
            hit.PointerPressed += (_, _) =>
            {
                if (AiWorkspaceDisplayCombo.SelectedIndex != displayIndex)
                    AiWorkspaceDisplayCombo.SelectedIndex = displayIndex;
                else
                    FocusAiWorkspaceFrozenPreviewDisplay(displayIndex);
            };
            ToolTipService.SetToolTip(hit, "点击聚焦这块 Workspace Preview 显示器；当前仍为只读状态");
            Canvas.SetLeft(hit, canvasDisplay.Left);
            Canvas.SetTop(hit, canvasDisplay.Top);
            LayoutCanvas.Children.Add(hit);
        }
    }

    private void AddWorkspaceFrozenRegion(
        WorkspaceFrozenDisplayCanvasGeometry display,
        DesktopWorkspaceRelativeRect bounds,
        string label,
        bool isReserved,
        bool outlineOnly)
    {
        var work = display.Display.WorkArea;
        var leftGlobal = work.Left + work.Width * bounds.Left;
        var topGlobal = work.Top + work.Height * bounds.Top;
        var width = Math.Max(1d, work.Width * bounds.Width * display.Scale);
        var height = Math.Max(1d, work.Height * bounds.Height * display.Scale);
        var left = display.Left + (leftGlobal - display.Display.PixelBounds.Left) * display.Scale;
        var top = display.Top + (topGlobal - display.Display.PixelBounds.Top) * display.Scale;

        var block = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(outlineOnly ? (isReserved ? 2.2d : 1.8d) : (isReserved ? 1.5d : 1d)),
            BorderBrush = isReserved
                ? Brush(outlineOnly ? (byte)235 : (byte)190, 0xD8, 0x83, 0x00)
                : Brush(outlineOnly ? (byte)225 : (byte)145, 0x00, 0x78, 0xD4),
            Background = outlineOnly
                ? Brush(0, 0x00, 0x00, 0x00)
                : isReserved
                    ? Brush(45, 0xD8, 0x83, 0x00)
                    : Brush(28, 0x00, 0x78, 0xD4),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                Margin = new Thickness(6, 4, 5, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = Math.Max(32d, width - 12d),
            },
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        LayoutCanvas.Children.Add(block);
    }

    private void AddWorkspaceFrozenItemDot(
        DesktopWorkspaceFrozenPreviewItem item,
        double x,
        double y,
        byte red,
        byte green,
        byte blue)
    {
        const double size = 5d;
        var dot = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = Brush(215, red, green, blue),
            Stroke = Brush(235, 0xFF, 0xFF, 0xFF),
            StrokeThickness = 0.8d,
        };
        ToolTipService.SetToolTip(
            dot,
            $"{item.DisplayName} · {item.Disposition} · {item.SourceDisplayRef} → {item.TargetDisplayRef ?? "—"}");
        Canvas.SetLeft(dot, x - size / 2d);
        Canvas.SetTop(dot, y - size / 2d);
        LayoutCanvas.Children.Add(dot);
    }

    private void ClearAiWorkspaceFrozenPreview()
    {
        _workspaceFrozenPreview = null;
        _activeWorkspaceFrozenPreviewDisplayIndex = -1;
    }

    private static string WorkspaceFrozenPreviewDisplayLabel(DesktopWorkspaceFrozenPreviewDisplay display)
        => $"{(display.IsPrimary ? "主屏" : "副屏")} · {display.DisplayRef} · {display.Policy} · " +
           $"源 {display.SourceItemCount} / 目标 {display.TargetPlacementCount}";

    private static string RelativeBoundsText(DesktopWorkspaceRelativeRect bounds)
        => $"{bounds.Width * 100d:0.#}% × {bounds.Height * 100d:0.#}%";

    private static string ShortFingerprint(string value)
        => value.Length <= 12 ? value : value[..12];

    private sealed record WorkspaceFrozenDisplayCanvasGeometry(
        DesktopWorkspaceFrozenPreviewDisplay Display,
        double Left,
        double Top,
        double Width,
        double Height,
        double Scale);
}
