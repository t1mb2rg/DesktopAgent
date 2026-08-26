using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopLayouts.Workspaces;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private DesktopLogicalWorkspacePlanningSession? _logicalWorkspaceSession;
    private int _activeLogicalWorkspaceDisplayIndex = -1;

    internal bool TryLoadAiLogicalWorkspacePreview(DesktopLogicalWorkspacePlanningSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_isLayoutEditing && (_draft is not null || _logicalWorkspaceSession is not null))
        {
            ShowInfo("已有布局正在编辑", "精确逻辑预览没有覆盖当前未保存内容。请先保存或取消当前编辑，再重新生成。", InfoBarSeverity.Warning);
            return false;
        }

        var preview = session.FrozenPreview;
        if (preview.Displays.Count == 0 || preview.Items.Count != session.Workspace.Items.Count)
            throw new InvalidOperationException("冻结逻辑预览没有完整覆盖当前工作区。");

        ClearAiMultiDisplayWorkspace();
        _draft = null;
        _latestPreview = null;
        _logicalWorkspaceSession = session;
        _activeLogicalWorkspaceDisplayIndex = 0;
        SetSelectedTemplateWithoutNotification(null);

        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.Items.Clear();
            foreach (var display in preview.Displays)
                AiWorkspaceDisplayCombo.Items.Add(new ComboBoxItem { Content = DisplayLabel(display) });
            AiWorkspaceDisplayCombo.SelectedIndex = 0;
            AiWorkspaceDisplayCombo.Visibility = preview.Displays.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _suppressAiWorkspaceSelectionChanged = false;
        }

        LayoutEditorPanel.Visibility = Visibility.Visible;
        LayoutEmptyEditorState.Visibility = Visibility.Collapsed;
        LayoutPreviewDetails.Visibility = Visibility.Visible;
        SetLayoutInteractionMode(isEditing: true);
        ApplyToDesktopButton.IsEnabled = false;
        FocusAiLogicalWorkspaceDisplay(0);

        ShowInfo(
            "AI 精确逻辑终态已冻结",
            $"{preview.Displays.Count} 块显示器 · {preview.Items.Count} 个项目 · 移动 {preview.MoveCount} · 跨屏 {preview.CrossDisplayAssignmentCount}。" +
            "所有显示器（包括 0 个目标项目的源屏）、保留空间和项目终点都直接来自 ValidatedPlan；当前不能写桌面。",
            InfoBarSeverity.Success);
        return true;
    }

    private void FocusAiLogicalWorkspaceDisplay(int index)
    {
        var preview = _logicalWorkspaceSession?.FrozenPreview;
        if (preview is null || index < 0 || index >= preview.Displays.Count)
            return;

        _activeLogicalWorkspaceDisplayIndex = index;
        var display = preview.Displays[index];
        LayoutNameTextBox.Text = $"AI 逻辑工作区 · {display.DisplayRef}";
        LayoutDeviceNameTextBox.Text = display.DisplayRef;
        LayoutTargetBindingCombo.SelectedIndex = display.IsPrimary ? 0 : 2;
        LayoutCanvasDraftNameText.Text = "AI 精确逻辑工作区";
        LayoutCanvasStatusText.Text =
            $"冻结 Preview · {preview.Displays.Count} 块显示器 · 当前 {display.DisplayRef}：" +
            $"源 {display.SourceItemCount} / 目标 {display.TargetPlacementCount} / 迁入 {display.InboundCount} / 迁出 {display.OutboundCount}";
        LayoutRuleSummaryText.Text = $"Plan {preview.PlanId:D} · Observation {preview.SourceObservationId:D}";

        _zoneItems.Clear();
        foreach (var zone in display.Zones)
            _zoneItems.Add(new LayoutZoneListItem(zone.Name, $"{zone.Bounds.Width} × {zone.Bounds.Height} 格", "终态"));
        foreach (var region in display.ReservedRegions)
            _zoneItems.Add(new LayoutZoneListItem($"留白：{region.Purpose}", $"{region.Bounds.Width} × {region.Bounds.Height} 格", "保留"));

        _previewItems.Clear();
        foreach (var item in preview.Items.Take(100))
        {
            var target = item.TargetCell.HasValue
                ? $"{item.TargetDisplayRef} ({item.TargetCell.Value.Column}, {item.TargetCell.Value.Row})"
                : "—";
            _previewItems.Add(new LayoutPreviewListItem(
                item.DisplayName,
                item.State.ToString(),
                $"{item.SourceDisplayRef} ({item.SourceCell.Column}, {item.SourceCell.Row}) → {target}"));
        }

        MoveCountText.Text = preview.MoveCount.ToString();
        StayCountText.Text = (preview.KeepCount + preview.Items.Count(static item =>
            item.State == DesktopDesiredItemState.Place && !item.IsPositionChange)).ToString();
        TargetDisplayText.Text = preview.Displays.Count == 1 ? display.DisplayRef : "多屏逻辑工作区";
        LayoutPreviewSummaryText.Text =
            $"输入 {preview.Items.Count} · 移动 {preview.MoveCount} · 跨屏 {preview.CrossDisplayAssignmentCount} · " +
            $"保持 {preview.KeepCount} · 排除 {preview.ExcludeCount} · 完整 是";
        LayoutPreviewObservationText.Text =
            $"Frozen Preview {preview.PreviewId:D}\nObservation {preview.SourceObservationId:D}\n只读逻辑终态；尚未生成物理 Candidate。";
        LayoutPreviewDetails.Visibility = Visibility.Visible;
        RenderLayoutCanvas();
    }

    private void RenderAiLogicalWorkspacePreview()
    {
        var preview = _logicalWorkspaceSession?.FrozenPreview;
        if (preview is null)
            return;

        var width = Math.Max(100d, LayoutCanvas.ActualWidth);
        var height = Math.Max(100d, LayoutCanvas.ActualHeight);
        const double inset = 20d;
        var drawableWidth = Math.Max(1d, width - inset * 2d);
        var drawableHeight = Math.Max(1d, height - inset * 2d);
        double X(int value) => inset + drawableWidth * value / DesktopLogicalTopologyRect.Scale;
        double Y(int value) => inset + drawableHeight * value / DesktopLogicalTopologyRect.Scale;

        var geometry = preview.Displays.ToDictionary(
            static display => display.DisplayRef,
            display => new LogicalDisplayCanvasGeometry(
                display,
                X(display.TopologyBounds.Left),
                Y(display.TopologyBounds.Top),
                Math.Max(1d, X(display.TopologyBounds.Right) - X(display.TopologyBounds.Left)),
                Math.Max(1d, Y(display.TopologyBounds.Bottom) - Y(display.TopologyBounds.Top))),
            StringComparer.Ordinal);

        foreach (var pair in preview.Displays.Select((display, index) => (display, index)))
        {
            var item = geometry[pair.display.DisplayRef];
            var selected = pair.index == _activeLogicalWorkspaceDisplayIndex;
            var surface = new Border
            {
                Width = item.Width,
                Height = item.Height,
                CornerRadius = new CornerRadius(7),
                Background = Brush(18, 0xFF, 0xFF, 0xFF),
                BorderBrush = Brush(selected ? (byte)230 : (byte)90, 0x00, 0x78, 0xD4),
                BorderThickness = new Thickness(selected ? 2.5 : 1.2),
            };
            Canvas.SetLeft(surface, item.Left);
            Canvas.SetTop(surface, item.Top);
            LayoutCanvas.Children.Add(surface);

            foreach (var zone in pair.display.Zones)
                AddLogicalRegion(item, zone.Bounds, zone.Name, isReserved: false);
            foreach (var region in pair.display.ReservedRegions)
                AddLogicalRegion(item, region.Bounds, $"留白 · {region.Purpose}", isReserved: true);
        }

        foreach (var item in preview.Items.Take(180))
        {
            if (!geometry.TryGetValue(item.SourceDisplayRef, out var source))
                continue;
            var sourcePoint = source.CellCenter(item.SourceCell);
            if (item.State == DesktopDesiredItemState.Place && item.TargetDisplayRef is not null &&
                item.TargetCell.HasValue && geometry.TryGetValue(item.TargetDisplayRef, out var target))
            {
                var targetPoint = target.CellCenter(item.TargetCell.Value);
                if (item.IsPositionChange)
                {
                    LayoutCanvas.Children.Add(new Line
                    {
                        X1 = sourcePoint.X,
                        Y1 = sourcePoint.Y,
                        X2 = targetPoint.X,
                        Y2 = targetPoint.Y,
                        StrokeThickness = item.IsCrossDisplayMove ? 1.7d : 1d,
                        Stroke = Brush(item.IsCrossDisplayMove ? (byte)145 : (byte)82, 0x00, 0x78, 0xD4),
                    });
                }
                AddLogicalItemDot(item, targetPoint.X, targetPoint.Y, 0x00, 0x78, 0xD4);
            }
            else
            {
                AddLogicalItemDot(item, sourcePoint.X, sourcePoint.Y,
                    item.State == DesktopDesiredItemState.Exclude ? (byte)0xD8 : (byte)0x72,
                    item.State == DesktopDesiredItemState.Exclude ? (byte)0x6B : (byte)0x72,
                    item.State == DesktopDesiredItemState.Exclude ? (byte)0x00 : (byte)0x72);
            }
        }

        foreach (var pair in preview.Displays.Select((display, index) => (display, index)))
        {
            var item = geometry[pair.display.DisplayRef];
            var index = pair.index;
            var hit = new Border
            {
                Width = item.Width,
                Height = item.Height,
                Background = Brush(1, 0xFF, 0xFF, 0xFF),
                Child = new TextBlock
                {
                    Text = DisplayLabel(pair.display),
                    Margin = new Thickness(8, 6, 8, 6),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
            };
            hit.PointerPressed += (_, _) =>
            {
                if (AiWorkspaceDisplayCombo.SelectedIndex != index)
                    AiWorkspaceDisplayCombo.SelectedIndex = index;
                else
                    FocusAiLogicalWorkspaceDisplay(index);
            };
            ToolTipService.SetToolTip(hit, "点击聚焦这块逻辑显示器；不会读取或修改真实桌面");
            Canvas.SetLeft(hit, item.Left);
            Canvas.SetTop(hit, item.Top);
            LayoutCanvas.Children.Add(hit);
        }
    }

    private void AddLogicalRegion(LogicalDisplayCanvasGeometry display, DesktopLogicalGridRect bounds, string label, bool isReserved)
    {
        var left = display.Left + display.Width * bounds.Column / display.Display.GridSize.Columns;
        var top = display.Top + display.Height * bounds.Row / display.Display.GridSize.Rows;
        var region = new Border
        {
            Width = Math.Max(1d, display.Width * bounds.Width / display.Display.GridSize.Columns),
            Height = Math.Max(1d, display.Height * bounds.Height / display.Display.GridSize.Rows),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(isReserved ? 1.5 : 1d),
            BorderBrush = isReserved ? Brush(190, 0xD8, 0x83, 0x00) : Brush(145, 0x00, 0x78, 0xD4),
            Background = isReserved ? Brush(45, 0xD8, 0x83, 0x00) : Brush(28, 0x00, 0x78, 0xD4),
            Child = new TextBlock { Text = label, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 4, 5, 4) },
        };
        Canvas.SetLeft(region, left);
        Canvas.SetTop(region, top);
        LayoutCanvas.Children.Add(region);
    }

    private void AddLogicalItemDot(DesktopLogicalWorkspacePreviewItem item, double x, double y, byte red, byte green, byte blue)
    {
        const double size = 7d;
        var dot = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = Brush(235, red, green, blue),
            Stroke = Brush(255, 0xFF, 0xFF, 0xFF),
            StrokeThickness = 1d,
        };
        ToolTipService.SetToolTip(dot, $"{item.DisplayName} · {item.State} · {item.SourceDisplayRef} → {item.TargetDisplayRef ?? "—"}");
        Canvas.SetLeft(dot, x - size / 2d);
        Canvas.SetTop(dot, y - size / 2d);
        LayoutCanvas.Children.Add(dot);
    }

    private void ClearAiLogicalWorkspacePreview()
    {
        _logicalWorkspaceSession = null;
        _activeLogicalWorkspaceDisplayIndex = -1;
    }

    private static string DisplayLabel(DesktopLogicalWorkspacePreviewDisplay display) =>
        $"{(display.IsPrimary ? "主屏" : "副屏")} · {display.DisplayRef} · 源 {display.SourceItemCount} / 目标 {display.TargetPlacementCount}";

    private sealed record LogicalDisplayCanvasGeometry(
        DesktopLogicalWorkspacePreviewDisplay Display,
        double Left,
        double Top,
        double Width,
        double Height)
    {
        internal (double X, double Y) CellCenter(DesktopLogicalGridCell cell) =>
            (Left + Width * (cell.Column + 0.5d) / Display.GridSize.Columns,
             Top + Height * (cell.Row + 0.5d) / Display.GridSize.Rows);
    }
}
