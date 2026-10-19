using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private const int MaxWorkspaceFrozenPreviewItemDots = 180;
    private const int MaxWorkspaceFrozenPreviewAggregateFlows = 8;

    private DesktopWorkspaceFrozenPreview? _workspaceFrozenPreview;
    // -1 表示整体拓扑；>= 0 表示聚焦某块显示器。
    private int _activeWorkspaceFrozenPreviewDisplayIndex = -1;
    private bool _workspaceFrozenPreviewHandlersAttached;

    internal bool TryLoadAiWorkspaceFrozenPreview(DesktopWorkspaceFrozenPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (_isLayoutEditing && _draft is not null)
        {
            ShowInfo(
                "已有布局正在编辑",
                "新的工作区精确预览没有覆盖当前未保存布局。请先保存或取消当前编辑。",
                InfoBarSeverity.Warning);
            return false;
        }

        if (_zoneColorPreferencesDirty)
        {
            ShowInfo(
                "分区颜色尚未应用",
                "新的工作区精确预览没有覆盖当前颜色修改。请先应用或还原颜色。",
                InfoBarSeverity.Warning);
            return false;
        }

        if (preview.Displays.Count == 0)
            throw new InvalidOperationException("工作区精确预览没有显示器。 ");
        if (preview.Items.Count != preview.ExactPlan.Items.Count)
            throw new InvalidOperationException("工作区精确预览没有完整覆盖精确方案项目。 ");

        ClearAiMultiDisplayWorkspace();
        ClearAiLogicalWorkspacePreview();
        _draft = null;
        _latestPreview = null;
        _workspaceFrozenPreview = preview;
        _activeWorkspaceFrozenPreviewDisplayIndex = preview.Displays.Count > 1 ? -1 : 0;
        SetSelectedTemplateWithoutNotification(null);
        EnsureWorkspaceFrozenPreviewUiHandlers();

        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.Items.Clear();
            if (preview.Displays.Count > 1)
            {
                AiWorkspaceDisplayCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"整体拓扑 · {preview.Displays.Count} 块显示器 · 跨屏 {preview.CrossDisplayAssignmentCount}",
                });
            }

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
        SetLayoutStage(DesktopAgent.Application.DesktopLayouts.Workspaces.DesktopLayoutStage.Execute);
        RefreshApplyButtonFromCurrentEvidence();
        FocusAiWorkspaceFrozenPreviewDisplay(0);

        ShowInfo(
            "AI 工作区精确预览已冻结",
            $"设计版本 v{preview.Blueprint.Revision} · {preview.Displays.Count} 块显示器 · " +
            $"{preview.Items.Count} 个项目 · 移动 {preview.MoveCount} · 跨屏 {preview.CrossDisplayAssignmentCount}。" +
            "当前只展示已经通过实现证书验证的精确结果，写入权限仍为 0。",
            InfoBarSeverity.Success);
        return true;
    }

    private void EnsureWorkspaceFrozenPreviewUiHandlers()
    {
        if (_workspaceFrozenPreviewHandlersAttached)
            return;

        AiWorkspaceDisplayCombo.SelectionChanged += AiWorkspaceFrozenPreviewDisplayCombo_SelectionChanged;
        LayoutCanvas.SizeChanged += WorkspaceFrozenPreviewCanvas_SizeChanged;
        _workspaceFrozenPreviewHandlersAttached = true;
    }

    private void AiWorkspaceFrozenPreviewDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAiWorkspaceSelectionChanged || _workspaceFrozenPreview is null)
            return;

        FocusAiWorkspaceFrozenPreviewDisplay(AiWorkspaceDisplayCombo.SelectedIndex);
    }

    private void WorkspaceFrozenPreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_workspaceFrozenPreview is not null)
            RenderWorkspaceFrozenPreviewCanvas();
    }

    private void FocusAiWorkspaceFrozenPreviewDisplay(int selectorIndex)
    {
        var preview = _workspaceFrozenPreview;
        if (preview is null)
            return;

        var hasTopologyChoice = preview.Displays.Count > 1;
        if (hasTopologyChoice && selectorIndex == 0)
        {
            RenderWorkspaceFrozenPreviewTopologySummary(preview);
            return;
        }

        var displayIndex = hasTopologyChoice ? selectorIndex - 1 : selectorIndex;
        if (displayIndex < 0 || displayIndex >= preview.Displays.Count)
            return;

        _activeWorkspaceFrozenPreviewDisplayIndex = displayIndex;
        var display = preview.Displays[displayIndex];
        var displayName = FormatWorkspacePreviewDisplayRef(display.DisplayRef, display.IsPrimary);
        LayoutNameTextBox.Text = $"AI 工作区 · {displayName}";
        LayoutDeviceNameTextBox.Text = displayName;
        LayoutTargetBindingCombo.SelectedIndex = display.IsPrimary ? 0 : 2;
        LayoutCanvasDraftNameText.Text = $"AI 工作区设计 v{preview.Blueprint.Revision}";
        LayoutCanvasStatusText.Text =
            $"精确预览 · {displayName} · {FormatWorkspacePreviewPolicy(display.Policy)} · " +
            $"源 {display.SourceItemCount} / 目标 {display.TargetPlacementCount} / " +
            $"迁入 {display.InboundCount} / 迁出 {display.OutboundCount}";
        LayoutRuleSummaryText.Text =
            $"设计版本 v{preview.Blueprint.Revision} · 精确方案已验证 · 实现证书已通过";

        _zoneItems.Clear();
        foreach (var zone in display.Zones.OrderBy(static zone => zone.ZoneKey, StringComparer.Ordinal))
        {
            _zoneItems.Add(new LayoutZoneListItem(
                LocalizeWorkspacePreviewLabel(zone.Name),
                RelativeBoundsText(zone.RealizedBounds),
                $"{FormatWorkspacePreviewAnchor(zone.Anchor)} / {FormatWorkspacePreviewPacking(zone.PackingOrder)}"));
        }
        foreach (var region in display.ReservedRegions.OrderBy(static region => region.RegionKey, StringComparer.Ordinal))
        {
            _zoneItems.Add(new LayoutZoneListItem(
                $"留白：{LocalizeWorkspacePreviewLabel(region.Purpose)}",
                RelativeBoundsText(region.RealizedBounds),
                "保留区域"));
        }

        _previewItems.Clear();
        foreach (var item in preview.Items
                     .Where(item =>
                         string.Equals(item.SourceDisplayRef, display.DisplayRef, StringComparison.Ordinal) ||
                         string.Equals(item.TargetDisplayRef, display.DisplayRef, StringComparison.Ordinal))
                     .Take(100))
        {
            _previewItems.Add(CreateWorkspacePreviewListItem(item));
        }

        UpdateWorkspaceFrozenPreviewCommonSummary(preview, displayName);
        RenderWorkspaceFrozenPreviewCanvas();
    }

    private void RenderWorkspaceFrozenPreviewTopologySummary(DesktopWorkspaceFrozenPreview preview)
    {
        _activeWorkspaceFrozenPreviewDisplayIndex = -1;
        LayoutNameTextBox.Text = "AI 工作区 · 整体拓扑";
        LayoutDeviceNameTextBox.Text = "多屏";
        LayoutTargetBindingCombo.SelectedIndex = 0;
        LayoutCanvasDraftNameText.Text = $"AI 工作区设计 v{preview.Blueprint.Revision}";
        LayoutCanvasStatusText.Text =
            $"精确预览 · 整体拓扑 · {preview.Displays.Count} 块显示器 · " +
            $"移动 {preview.MoveCount} · 跨屏 {preview.CrossDisplayAssignmentCount}";
        LayoutRuleSummaryText.Text =
            $"设计版本 v{preview.Blueprint.Revision} · 精确方案已验证 · 实现证书已通过";

        _zoneItems.Clear();
        foreach (var display in preview.Displays)
        {
            var displayName = FormatWorkspacePreviewDisplayRef(display.DisplayRef, display.IsPrimary);
            foreach (var zone in display.Zones.OrderBy(static zone => zone.ZoneKey, StringComparer.Ordinal))
            {
                _zoneItems.Add(new LayoutZoneListItem(
                    $"{displayName} · {LocalizeWorkspacePreviewLabel(zone.Name)}",
                    RelativeBoundsText(zone.RealizedBounds),
                    $"{FormatWorkspacePreviewAnchor(zone.Anchor)} / {FormatWorkspacePreviewPacking(zone.PackingOrder)}"));
            }
            foreach (var region in display.ReservedRegions.OrderBy(static region => region.RegionKey, StringComparer.Ordinal))
            {
                _zoneItems.Add(new LayoutZoneListItem(
                    $"{displayName} · 留白：{LocalizeWorkspacePreviewLabel(region.Purpose)}",
                    RelativeBoundsText(region.RealizedBounds),
                    "保留区域"));
            }
        }

        _previewItems.Clear();
        foreach (var item in preview.Items
                     .Where(static item => item.IsCrossDisplayMove)
                     .Take(100))
        {
            _previewItems.Add(CreateWorkspacePreviewListItem(item));
        }

        UpdateWorkspaceFrozenPreviewCommonSummary(preview, "多屏工作区");
        RenderWorkspaceFrozenPreviewCanvas();
    }

    private void UpdateWorkspaceFrozenPreviewCommonSummary(
        DesktopWorkspaceFrozenPreview preview,
        string targetDisplayText)
    {
        MoveCountText.Text = preview.MoveCount.ToString();
        StayCountText.Text = (preview.KeepCount + preview.Items.Count(static item =>
            item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place && !item.IsPositionChange)).ToString();
        TargetDisplayText.Text = targetDisplayText;
        LayoutPreviewSummaryText.Text =
            $"输入 {preview.Items.Count} · 移动 {preview.MoveCount} · 跨屏 {preview.CrossDisplayAssignmentCount} · " +
            $"保持 {preview.KeepCount} · 排除 {preview.ExcludeCount} · 实现证书已验证";
        LayoutPreviewObservationText.Text =
            $"精确预览 {preview.PreviewId:D}\n" +
            $"来源观察 {preview.SourceObservationId:D}\n" +
            $"预览 {ShortFingerprint(preview.PreviewFingerprint)} · 来源 {ShortFingerprint(preview.SourceStateFingerprint)}";
        LayoutPreviewDetails.Visibility = Visibility.Visible;
    }

    private LayoutPreviewListItem CreateWorkspacePreviewListItem(DesktopWorkspaceFrozenPreviewItem item)
    {
        var sourceDisplay = FormatWorkspacePreviewDisplayRef(item.SourceDisplayRef, isPrimary: false);
        var targetDisplay = item.TargetDisplayRef is null
            ? "—"
            : FormatWorkspacePreviewDisplayRef(item.TargetDisplayRef, isPrimary: false);
        var target = item.TargetPixelPosition.HasValue
            ? $"{targetDisplay} ({item.TargetPixelPosition.Value.X}, {item.TargetPixelPosition.Value.Y})"
            : "—";
        return new LayoutPreviewListItem(
            item.DisplayName,
            $"{FormatWorkspacePreviewDisposition(item.Disposition)} / {FormatWorkspacePreviewCategory(item.CategoryKey)}",
            $"{sourceDisplay} ({item.SourcePixelPosition.X}, {item.SourcePixelPosition.Y}) → {target}");
    }

    private void RenderWorkspaceFrozenPreviewCanvas()
    {
        var preview = _workspaceFrozenPreview;
        if (preview is null)
            return;

        LayoutCanvas.Children.Clear();
        LayoutCanvasEmptyState.Visibility = Visibility.Collapsed;

        var isTopology = _activeWorkspaceFrozenPreviewDisplayIndex < 0;
        IReadOnlyList<DesktopWorkspaceFrozenPreviewDisplay> renderDisplays = isTopology
            ? preview.Displays
            : [preview.Displays[_activeWorkspaceFrozenPreviewDisplayIndex]];

        var virtualLeft = renderDisplays.Min(static display => display.PixelBounds.Left);
        var virtualTop = renderDisplays.Min(static display => display.PixelBounds.Top);
        var virtualRight = renderDisplays.Max(static display => display.PixelBounds.Right);
        var virtualBottom = renderDisplays.Max(static display => display.PixelBounds.Bottom);
        var virtualWidth = (long)virtualRight - virtualLeft;
        var virtualHeight = (long)virtualBottom - virtualTop;
        if (virtualWidth <= 0 || virtualHeight <= 0)
            return;

        var canvasWidth = Math.Max(100d, LayoutCanvas.ActualWidth);
        var canvasHeight = Math.Max(100d, LayoutCanvas.ActualHeight);
        var inset = isTopology ? 28d : 20d;
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

        var geometry = renderDisplays.ToDictionary(
            static display => display.DisplayRef,
            display => new WorkspaceFrozenDisplayCanvasGeometry(
                display,
                X(display.PixelBounds.Left),
                Y(display.PixelBounds.Top),
                Math.Max(1d, display.PixelBounds.Width * scale),
                Math.Max(1d, display.PixelBounds.Height * scale),
                scale),
            StringComparer.Ordinal);

        foreach (var display in renderDisplays)
        {
            var canvasDisplay = geometry[display.DisplayRef];
            var surface = new Border
            {
                Width = canvasDisplay.Width,
                Height = canvasDisplay.Height,
                CornerRadius = new CornerRadius(7),
                Background = Brush(18, 0xFF, 0xFF, 0xFF),
                BorderBrush = Brush(isTopology ? (byte)120 : (byte)230, 0x00, 0x78, 0xD4),
                BorderThickness = new Thickness(isTopology ? 1.2d : 2.2d),
            };
            Canvas.SetLeft(surface, canvasDisplay.Left);
            Canvas.SetTop(surface, canvasDisplay.Top);
            LayoutCanvas.Children.Add(surface);

            foreach (var zone in display.Zones)
            {
                AddWorkspaceFrozenRegion(
                    canvasDisplay,
                    zone.RealizedBounds,
                    LocalizeWorkspacePreviewLabel(zone.Name),
                    isReserved: false,
                    outlineOnly: false,
                    showLabel: !isTopology);
            }
            foreach (var region in display.ReservedRegions)
            {
                AddWorkspaceFrozenRegion(
                    canvasDisplay,
                    region.RealizedBounds,
                    $"留白 · {LocalizeWorkspacePreviewLabel(region.Purpose)}",
                    isReserved: true,
                    outlineOnly: false,
                    showLabel: true);
            }
        }

        if (isTopology)
        {
            RenderWorkspaceFrozenPreviewAggregateFlows(preview, geometry);
        }
        else
        {
            var selectedDisplayRef = renderDisplays[0].DisplayRef;
            foreach (var item in preview.Items
                         .Where(item => ShouldRenderFocusedWorkspacePreviewItem(item, selectedDisplayRef))
                         .Take(MaxWorkspaceFrozenPreviewItemDots))
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
        }

        // 最后重描结构边界。整体拓扑审阅显示器与跨屏流向，单屏聚焦审阅分区与精确落点。
        foreach (var display in renderDisplays)
        {
            var canvasDisplay = geometry[display.DisplayRef];
            foreach (var zone in display.Zones)
            {
                AddWorkspaceFrozenRegion(
                    canvasDisplay,
                    zone.RealizedBounds,
                    LocalizeWorkspacePreviewLabel(zone.Name),
                    isReserved: false,
                    outlineOnly: true,
                    showLabel: !isTopology);
            }
            foreach (var region in display.ReservedRegions)
            {
                AddWorkspaceFrozenRegion(
                    canvasDisplay,
                    region.RealizedBounds,
                    $"留白 · {LocalizeWorkspacePreviewLabel(region.Purpose)}",
                    isReserved: true,
                    outlineOnly: true,
                    showLabel: true);
            }
        }

        if (!isTopology)
            return;

        foreach (var pair in preview.Displays.Select((display, index) => (display, index)))
        {
            if (!geometry.TryGetValue(pair.display.DisplayRef, out var canvasDisplay))
                continue;

            var displayIndex = pair.index;
            var hit = new Border
            {
                Width = canvasDisplay.Width,
                Height = canvasDisplay.Height,
                Background = Brush(1, 0xFF, 0xFF, 0xFF),
            };
            hit.PointerPressed += (_, _) =>
            {
                var selectorIndex = displayIndex + 1;
                if (AiWorkspaceDisplayCombo.SelectedIndex != selectorIndex)
                    AiWorkspaceDisplayCombo.SelectedIndex = selectorIndex;
                else
                    FocusAiWorkspaceFrozenPreviewDisplay(selectorIndex);
            };
            ToolTipService.SetToolTip(hit, "点击聚焦这块显示器；当前仍为只读精确预览");
            Canvas.SetLeft(hit, canvasDisplay.Left);
            Canvas.SetTop(hit, canvasDisplay.Top);
            LayoutCanvas.Children.Add(hit);

            AddWorkspaceFrozenDisplayBadge(canvasDisplay);
        }
    }

    private void RenderWorkspaceFrozenPreviewAggregateFlows(
        DesktopWorkspaceFrozenPreview preview,
        IReadOnlyDictionary<string, WorkspaceFrozenDisplayCanvasGeometry> geometry)
    {
        var flows = preview.Items
            .Where(static item =>
                item.IsCrossDisplayMove &&
                item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
                item.TargetDisplayRef is not null)
            .GroupBy(item => new
            {
                item.SourceDisplayRef,
                TargetDisplayRef = item.TargetDisplayRef!,
            })
            .Select(group => new WorkspaceFrozenAggregateFlow(
                group.Key.SourceDisplayRef,
                group.Key.TargetDisplayRef,
                group.Count()))
            .OrderByDescending(static flow => flow.Count)
            .ThenBy(static flow => flow.SourceDisplayRef, StringComparer.Ordinal)
            .ThenBy(static flow => flow.TargetDisplayRef, StringComparer.Ordinal)
            .Take(MaxWorkspaceFrozenPreviewAggregateFlows)
            .ToArray();

        foreach (var flow in flows)
        {
            if (!geometry.TryGetValue(flow.SourceDisplayRef, out var source) ||
                !geometry.TryGetValue(flow.TargetDisplayRef, out var target))
            {
                continue;
            }

            var x1 = source.Left + source.Width / 2d;
            var y1 = source.Top + source.Height / 2d;
            var x2 = target.Left + target.Width / 2d;
            var y2 = target.Top + target.Height / 2d;
            var thickness = Math.Clamp(
                1.8d + Math.Log2(flow.Count + 1d) * 0.55d,
                2d,
                5d);

            LayoutCanvas.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                StrokeThickness = thickness,
                Stroke = Brush(150, 0x00, 0x78, 0xD4),
                IsHitTestVisible = false,
            });

            var label = new Border
            {
                Width = 144,
                Height = 24,
                Padding = new Thickness(6, 3, 6, 3),
                CornerRadius = new CornerRadius(8),
                Background = Brush(225, 0x20, 0x20, 0x20),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"{FormatWorkspacePreviewDisplayRef(flow.SourceDisplayRef, false)} → " +
                           $"{FormatWorkspacePreviewDisplayRef(flow.TargetDisplayRef, false)} · {flow.Count}",
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Canvas.SetLeft(label, (x1 + x2) / 2d - 72d);
            Canvas.SetTop(label, (y1 + y2) / 2d - 12d);
            LayoutCanvas.Children.Add(label);
        }
    }

    private void AddWorkspaceFrozenDisplayBadge(WorkspaceFrozenDisplayCanvasGeometry display)
    {
        var badge = new Border
        {
            Padding = new Thickness(7, 4, 7, 4),
            CornerRadius = new CornerRadius(7),
            Background = Brush(220, 0x20, 0x20, 0x20),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = WorkspaceFrozenPreviewDisplayLabel(display.Display),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = Math.Max(100d, display.Width - 16d),
            },
        };
        Canvas.SetLeft(badge, display.Left + 6d);
        Canvas.SetTop(badge, display.Top + 6d);
        LayoutCanvas.Children.Add(badge);
    }

    private static bool ShouldRenderFocusedWorkspacePreviewItem(
        DesktopWorkspaceFrozenPreviewItem item,
        string selectedDisplayRef)
    {
        if (item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
            item.TargetPixelPosition.HasValue)
        {
            return string.Equals(item.TargetDisplayRef, selectedDisplayRef, StringComparison.Ordinal);
        }

        return string.Equals(item.SourceDisplayRef, selectedDisplayRef, StringComparison.Ordinal);
    }

    private void AddWorkspaceFrozenRegion(
        WorkspaceFrozenDisplayCanvasGeometry display,
        DesktopWorkspaceRelativeRect bounds,
        string label,
        bool isReserved,
        bool outlineOnly,
        bool showLabel)
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
            Child = showLabel
                ? new TextBlock
                {
                    Text = label,
                    FontSize = 10,
                    Margin = new Thickness(6, 4, 5, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = Math.Max(32d, width - 12d),
                }
                : null,
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
            $"{item.DisplayName} · {FormatWorkspacePreviewDisposition(item.Disposition)} · " +
            $"{FormatWorkspacePreviewDisplayRef(item.SourceDisplayRef, false)} → " +
            $"{(item.TargetDisplayRef is null ? "—" : FormatWorkspacePreviewDisplayRef(item.TargetDisplayRef, false))}");
        Canvas.SetLeft(dot, x - size / 2d);
        Canvas.SetTop(dot, y - size / 2d);
        LayoutCanvas.Children.Add(dot);
    }

    private void ClearAiWorkspaceFrozenPreview()
    {
        HideOverlay();
        _workspaceFrozenPreview = null;
        _activeWorkspaceFrozenPreviewDisplayIndex = -1;
    }

    private static string WorkspaceFrozenPreviewDisplayLabel(DesktopWorkspaceFrozenPreviewDisplay display)
        => $"{FormatWorkspacePreviewDisplayRef(display.DisplayRef, display.IsPrimary)} · " +
           $"{FormatWorkspacePreviewPolicy(display.Policy)} · " +
           $"源 {display.SourceItemCount} / 目标 {display.TargetPlacementCount}";

    private static string FormatWorkspacePreviewDisplayRef(string displayRef, bool isPrimary)
    {
        if (isPrimary || string.Equals(displayRef, "primary", StringComparison.OrdinalIgnoreCase))
            return "主屏";
        if (displayRef.StartsWith("secondary_", StringComparison.OrdinalIgnoreCase))
            return $"副屏 {displayRef["secondary_".Length..]}";
        return displayRef;
    }

    private static string FormatWorkspacePreviewPolicy(DesktopWorkspaceDisplayPolicyKind policy) => policy switch
    {
        DesktopWorkspaceDisplayPolicyKind.Design => "设计",
        DesktopWorkspaceDisplayPolicyKind.Preserve => "保持现状",
        _ => policy.ToString(),
    };

    private static string FormatWorkspacePreviewAnchor(DesktopWorkspaceAnchorKind anchor) => anchor switch
    {
        DesktopWorkspaceAnchorKind.TopLeft => "左上",
        DesktopWorkspaceAnchorKind.TopRight => "右上",
        DesktopWorkspaceAnchorKind.BottomLeft => "左下",
        DesktopWorkspaceAnchorKind.BottomRight => "右下",
        DesktopWorkspaceAnchorKind.Center => "居中",
        _ => "未指定",
    };

    private static string FormatWorkspacePreviewPacking(DesktopWorkspacePackingOrder packing) => packing switch
    {
        DesktopWorkspacePackingOrder.RowMajor => "行优先",
        DesktopWorkspacePackingOrder.ColumnMajor => "列优先",
        _ => packing.ToString(),
    };

    private static string FormatWorkspacePreviewDisposition(DesktopWorkspaceCompiledItemDisposition disposition) => disposition switch
    {
        DesktopWorkspaceCompiledItemDisposition.Place => "放置",
        DesktopWorkspaceCompiledItemDisposition.Keep => "保持",
        DesktopWorkspaceCompiledItemDisposition.Exclude => "排除",
        _ => disposition.ToString(),
    };

    private static string FormatWorkspacePreviewCategory(string categoryKey)
        => categoryKey.Trim().ToLowerInvariant() switch
        {
            "dev-tools" => "开发工具",
            "office-communication" => "办公沟通",
            "network-cloud" => "网络与云服务",
            "system-utilities" => "系统工具",
            "ai-assistants" => "AI 助手",
            "game-platforms" => "游戏平台",
            "games" => "游戏",
            "media-entertainment" => "影音娱乐",
            "study-work" => "学习工作",
            "competition-project" => "竞赛项目",
            "personal-private" => "私人资料",
            "config-data" => "配置数据",
            "inbox-misc" => "待整理",
            _ => categoryKey,
        };

    private static string LocalizeWorkspacePreviewLabel(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "application tools" or "tools" => "应用工具",
            "entertainment & games" or "entertainment and games" or "games & entertainment" => "娱乐与游戏",
            "study & private" or "study and private" => "学习与私人",
            "study & work" or "study and work" => "学习与工作",
            "inbox & config" or "inbox and config" => "收件箱与配置",
            "private" => "私人资料",
            "archive" => "归档",
            "entertainment" => "娱乐",
            "keep the wallpaper visible" => "保留壁纸可见区域",
            _ => value,
        };
    }

    private static string RelativeBoundsText(DesktopWorkspaceRelativeRect bounds)
        => $"{bounds.Width * 100d:0.#}% × {bounds.Height * 100d:0.#}%";

    private static string ShortFingerprint(string value)
        => value.Length <= 12 ? value : value[..12];

    private sealed record WorkspaceFrozenAggregateFlow(
        string SourceDisplayRef,
        string TargetDisplayRef,
        int Count);

    private sealed record WorkspaceFrozenDisplayCanvasGeometry(
        DesktopWorkspaceFrozenPreviewDisplay Display,
        double Left,
        double Top,
        double Width,
        double Height,
        double Scale);
}
