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
        ApplyToDesktopButton.IsEnabled = false;
        FocusAiWorkspaceFrozenPreviewDisplay(0);

        ShowInfo(
            "AI 工作区精确预览已冻结",
            $"设计版本 v{preview.Blueprint.Revision} · {preview.Displays.Count} 块显示器 · " +
            $"{preview.Items.Count} 个项目 · 移动 {preview.MoveCount} · 跨屏 {preview.CrossDisplayAssignmentCount}。" +
            "当前只展示已经通过实现证书验证的精确结果；只有产品层成功加载后才会开放一次显式 Apply 入口。",
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
        foreach (var item in preview.Items.Take(100))
            _previewItems.Add(CreateWorkspacePreviewListItem(item));

        UpdateWorkspaceFrozenPreviewCommonSummary(preview, "整体拓扑");
        RenderWorkspaceFrozenPreviewCanvas();
    }

    private void UpdateWorkspaceFrozenPreviewCommonSummary(
        DesktopWorkspaceFrozenPreview preview,
        string selectedLabel)
    {
        LayoutRuleTotalCountText.Text = $"{preview.Items.Count} 项";
        LayoutRuleMatchedCountText.Text = $"移动 {preview.MoveCount}";
        LayoutRuleFallbackCountText.Text = $"保持 {preview.KeepCount}";
        LayoutRuleUnassignedCountText.Text = $"跨屏 {preview.CrossDisplayAssignmentCount}";
        LayoutPreviewSummaryText.Text =
            $"精确预览 · {selectedLabel} · 移动 {preview.MoveCount} / 保持 {preview.KeepCount} / " +
            $"排除 {preview.ExcludeCount} / 跨屏 {preview.CrossDisplayAssignmentCount}";
        LayoutPreviewLegendText.Text =
            $"源点 / 精确目标点 · 分区 = Compiler 实现结果 · 保留区域 = 不可放置 · 实现证书 {ShortFingerprint(preview.RealizationCertificate.CertificateFingerprint)}";
    }

    private LayoutPreviewListItem CreateWorkspacePreviewListItem(DesktopWorkspaceFrozenPreviewItem item)
    {
        var target = item.TargetPixelPosition.HasValue
            ? $"({item.TargetPixelPosition.Value.X}, {item.TargetPixelPosition.Value.Y})"
            : "—";
        var source = $"({item.SourcePixelPosition.X}, {item.SourcePixelPosition.Y})";
        return new LayoutPreviewListItem(
            FormatWorkspacePreviewDisposition(item.Disposition),
            item.DisplayName,
            FormatWorkspacePreviewCategory(item.CategoryKey),
            item.ZoneKey is null ? "—" : LocalizeWorkspacePreviewLabel(item.ZoneKey),
            source,
            target,
            item.TargetDisplayRef is null
                ? "—"
                : FormatWorkspacePreviewDisplayRef(item.TargetDisplayRef, false));
    }

    private void RenderWorkspaceFrozenPreviewCanvas()
    {
        var preview = _workspaceFrozenPreview;
        if (preview is null)
            return;

        LayoutCanvas.Children.Clear();
        var width = LayoutCanvas.ActualWidth;
        var height = LayoutCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
            return;

        if (_activeWorkspaceFrozenPreviewDisplayIndex < 0 && preview.Displays.Count > 1)
        {
            RenderWorkspaceFrozenPreviewTopologyCanvas(preview, width, height);
            return;
        }

        var displayIndex = Math.Clamp(
            _activeWorkspaceFrozenPreviewDisplayIndex,
            0,
            preview.Displays.Count - 1);
        RenderWorkspaceFrozenPreviewFocusedCanvas(preview, displayIndex, width, height);
    }

    private void RenderWorkspaceFrozenPreviewTopologyCanvas(
        DesktopWorkspaceFrozenPreview preview,
        double canvasWidth,
        double canvasHeight)
    {
        const double padding = 20d;
        var usableWidth = Math.Max(1d, canvasWidth - padding * 2d);
        var usableHeight = Math.Max(1d, canvasHeight - padding * 2d);
        var displayByRef = preview.Displays.ToDictionary(
            static display => display.DisplayRef,
            StringComparer.Ordinal);
        var geometries = new Dictionary<string, WorkspaceFrozenDisplayCanvasGeometry>(StringComparer.Ordinal);

        foreach (var display in preview.Displays)
        {
            var left = padding + display.TopologyBounds.Left * usableWidth;
            var top = padding + display.TopologyBounds.Top * usableHeight;
            var width = Math.Max(24d, display.TopologyBounds.Width * usableWidth);
            var height = Math.Max(24d, display.TopologyBounds.Height * usableHeight);
            var scaleX = width / Math.Max(1d, display.PixelBounds.Width);
            var scaleY = height / Math.Max(1d, display.PixelBounds.Height);
            var geometry = new WorkspaceFrozenDisplayCanvasGeometry(
                display,
                left,
                top,
                width,
                height,
                Math.Min(scaleX, scaleY));
            geometries[display.DisplayRef] = geometry;

            AddWorkspaceFrozenPreviewDisplaySurface(
                preview,
                geometry,
                drawLabels: false,
                drawItemDots: false,
                allowClickFocus: true);
        }

        RenderWorkspaceFrozenPreviewAggregateFlows(preview, displayByRef, geometries);
    }

    private void RenderWorkspaceFrozenPreviewFocusedCanvas(
        DesktopWorkspaceFrozenPreview preview,
        int displayIndex,
        double canvasWidth,
        double canvasHeight)
    {
        const double padding = 22d;
        var display = preview.Displays[displayIndex];
        var pixelWidth = Math.Max(1d, display.PixelBounds.Width);
        var pixelHeight = Math.Max(1d, display.PixelBounds.Height);
        var availableWidth = Math.Max(1d, canvasWidth - padding * 2d);
        var availableHeight = Math.Max(1d, canvasHeight - padding * 2d);
        var scale = Math.Min(availableWidth / pixelWidth, availableHeight / pixelHeight);
        var width = pixelWidth * scale;
        var height = pixelHeight * scale;
        var left = (canvasWidth - width) / 2d;
        var top = (canvasHeight - height) / 2d;

        AddWorkspaceFrozenPreviewDisplaySurface(
            preview,
            new WorkspaceFrozenDisplayCanvasGeometry(
                display,
                left,
                top,
                width,
                height,
                scale),
            drawLabels: true,
            drawItemDots: true,
            allowClickFocus: false);
    }

    private void AddWorkspaceFrozenPreviewDisplaySurface(
        DesktopWorkspaceFrozenPreview preview,
        WorkspaceFrozenDisplayCanvasGeometry geometry,
        bool drawLabels,
        bool drawItemDots,
        bool allowClickFocus)
    {
        var display = geometry.Display;
        var surface = new Border
        {
            Width = geometry.Width,
            Height = geometry.Height,
            Background = Brush(20, 0x58, 0x80, 0x9A),
            BorderBrush = display.Policy == DesktopWorkspaceDisplayPolicyKind.Preserve
                ? Brush(210, 0x7A, 0x8A, 0x99)
                : Brush(220, 0x33, 0xA6, 0xFF),
            BorderThickness = new Thickness(display.IsPrimary ? 2d : 1.4d),
            CornerRadius = new CornerRadius(6d),
        };
        if (allowClickFocus)
        {
            surface.Tapped += (_, _) =>
            {
                var index = preview.Displays
                    .Select((candidate, index) => (candidate, index))
                    .First(pair => string.Equals(
                        pair.candidate.DisplayRef,
                        display.DisplayRef,
                        StringComparison.Ordinal)).index;
                AiWorkspaceDisplayCombo.SelectedIndex = preview.Displays.Count > 1 ? index + 1 : index;
                FocusAiWorkspaceFrozenPreviewDisplay(AiWorkspaceDisplayCombo.SelectedIndex);
            };
        }
        ToolTipService.SetToolTip(
            surface,
            $"{FormatWorkspacePreviewDisplayRef(display.DisplayRef, display.IsPrimary)} · " +
            $"{FormatWorkspacePreviewPolicy(display.Policy)} · 点击聚焦这块显示器");
        Canvas.SetLeft(surface, geometry.Left);
        Canvas.SetTop(surface, geometry.Top);
        LayoutCanvas.Children.Add(surface);

        foreach (var zone in display.Zones)
        {
            var rect = MapRelativeRect(zone.RealizedBounds, geometry);
            var zoneBorder = new Border
            {
                Width = rect.Width,
                Height = rect.Height,
                Background = Brush(26, 0x33, 0xA6, 0xFF),
                BorderBrush = Brush(170, 0x33, 0xA6, 0xFF),
                BorderThickness = new Thickness(1d),
                CornerRadius = new CornerRadius(4d),
            };
            if (drawLabels)
            {
                zoneBorder.Child = new TextBlock
                {
                    Text = LocalizeWorkspacePreviewLabel(zone.Name),
                    FontSize = 11,
                    Margin = new Thickness(5),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                };
            }
            Canvas.SetLeft(zoneBorder, rect.Left);
            Canvas.SetTop(zoneBorder, rect.Top);
            LayoutCanvas.Children.Add(zoneBorder);
        }

        foreach (var region in display.ReservedRegions)
        {
            var rect = MapRelativeRect(region.RealizedBounds, geometry);
            var reserved = new Border
            {
                Width = rect.Width,
                Height = rect.Height,
                Background = Brush(34, 0xFF, 0xA0, 0x00),
                BorderBrush = Brush(210, 0xFF, 0xA0, 0x00),
                BorderThickness = new Thickness(1.4d),
                CornerRadius = new CornerRadius(4d),
            };
            if (drawLabels || display.IsPrimary)
            {
                reserved.Child = new TextBlock
                {
                    Text = $"留白 · {LocalizeWorkspacePreviewLabel(region.Purpose)}",
                    FontSize = 11,
                    Margin = new Thickness(5),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                };
            }
            Canvas.SetLeft(reserved, rect.Left);
            Canvas.SetTop(reserved, rect.Top);
            LayoutCanvas.Children.Add(reserved);
        }

        AddWorkspaceFrozenDisplayHeader(display, geometry);

        if (!drawItemDots)
            return;

        var dots = preview.Items
            .Where(item => ShouldRenderFocusedWorkspacePreviewItem(item, display.DisplayRef))
            .Take(MaxWorkspaceFrozenPreviewItemDots)
            .ToArray();
        foreach (var item in dots)
        {
            if (item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
                item.TargetPixelPosition.HasValue &&
                string.Equals(item.TargetDisplayRef, display.DisplayRef, StringComparison.Ordinal))
            {
                AddWorkspaceFrozenPreviewItemDot(
                    item,
                    MapPixelX(item.TargetPixelPosition.Value.X, geometry),
                    MapPixelY(item.TargetPixelPosition.Value.Y, geometry),
                    0x33,
                    0xA6,
                    0xFF);
            }
            else if (string.Equals(item.SourceDisplayRef, display.DisplayRef, StringComparison.Ordinal))
            {
                AddWorkspaceFrozenPreviewItemDot(
                    item,
                    MapPixelX(item.SourcePixelPosition.X, geometry),
                    MapPixelY(item.SourcePixelPosition.Y, geometry),
                    0x8A,
                    0x9A,
                    0xA8);
            }
        }
    }

    private void RenderWorkspaceFrozenPreviewAggregateFlows(
        DesktopWorkspaceFrozenPreview preview,
        IReadOnlyDictionary<string, DesktopWorkspaceFrozenPreviewDisplay> displayByRef,
        IReadOnlyDictionary<string, WorkspaceFrozenDisplayCanvasGeometry> geometries)
    {
        var flows = preview.Items
            .Where(static item => item.IsCrossDisplayMove)
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
            if (!geometries.TryGetValue(flow.SourceDisplayRef, out var source) ||
                !geometries.TryGetValue(flow.TargetDisplayRef, out var target))
            {
                continue;
            }

            var x1 = source.Left + source.Width / 2d;
            var y1 = source.Top + source.Height / 2d;
            var x2 = target.Left + target.Width / 2d;
            var y2 = target.Top + target.Height / 2d;
            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = Brush(190, 0x2F, 0x80, 0xED),
                StrokeThickness = Math.Clamp(1.4d + Math.Log2(flow.Count + 1d) * 0.45d, 1.6d, 5d),
                IsHitTestVisible = false,
            };
            LayoutCanvas.Children.Add(line);

            var sourceName = displayByRef.TryGetValue(flow.SourceDisplayRef, out var sourceDisplay)
                ? FormatWorkspacePreviewDisplayRef(sourceDisplay.DisplayRef, sourceDisplay.IsPrimary)
                : FormatWorkspacePreviewDisplayRef(flow.SourceDisplayRef, false);
            var targetName = displayByRef.TryGetValue(flow.TargetDisplayRef, out var targetDisplay)
                ? FormatWorkspacePreviewDisplayRef(targetDisplay.DisplayRef, targetDisplay.IsPrimary)
                : FormatWorkspacePreviewDisplayRef(flow.TargetDisplayRef, false);
            var label = new Border
            {
                Background = Brush(232, 0x0D, 0x16, 0x22),
                BorderBrush = Brush(175, 0x2F, 0x80, 0xED),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 6, 3),
                Child = new TextBlock
                {
                    Text = $"{sourceName} → {targetName} · {flow.Count}",
                    FontSize = 10.5,
                },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, (x1 + x2) / 2d + 6d);
            Canvas.SetTop(label, (y1 + y2) / 2d - 10d);
            LayoutCanvas.Children.Add(label);
        }
    }

    private void AddWorkspaceFrozenDisplayHeader(
        DesktopWorkspaceFrozenPreviewDisplay display,
        WorkspaceFrozenDisplayCanvasGeometry geometry)
    {
        var header = new Border
        {
            Background = Brush(224, 0x0D, 0x16, 0x22),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 4, 7, 4),
            Child = new TextBlock
            {
                Text = $"{FormatWorkspacePreviewDisplayRef(display.DisplayRef, display.IsPrimary)} · " +
                       $"{FormatWorkspacePreviewPolicy(display.Policy)} · " +
                       $"源 {display.SourceItemCount} / 目标 {display.TargetPlacementCount}",
                FontSize = 10.5,
                MaxWidth = Math.Max(80d, geometry.Width - 12d),
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        Canvas.SetLeft(header, geometry.Left + 5d);
        Canvas.SetTop(header, geometry.Top + 5d);
        LayoutCanvas.Children.Add(header);
    }

    private static bool ShouldRenderFocusedWorkspacePreviewItem(
        DesktopWorkspaceFrozenPreviewItem item,
        string focusedDisplayRef)
    {
        if (item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
            item.TargetPixelPosition.HasValue &&
            string.Equals(item.TargetDisplayRef, focusedDisplayRef, StringComparison.Ordinal))
        {
            return true;
        }

        return item.Disposition is DesktopWorkspaceCompiledItemDisposition.Keep
                   or DesktopWorkspaceCompiledItemDisposition.Exclude &&
               string.Equals(item.SourceDisplayRef, focusedDisplayRef, StringComparison.Ordinal);
    }

    private static PixelRect MapRelativeRect(
        DesktopWorkspaceRelativeRect rect,
        WorkspaceFrozenDisplayCanvasGeometry geometry)
    {
        var left = geometry.Left + rect.Left * geometry.Width;
        var top = geometry.Top + rect.Top * geometry.Height;
        var right = geometry.Left + (rect.Left + rect.Width) * geometry.Width;
        var bottom = geometry.Top + (rect.Top + rect.Height) * geometry.Height;
        return new PixelRect(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(right),
            (int)Math.Round(bottom));
    }

    private static double MapPixelX(int pixelX, WorkspaceFrozenDisplayCanvasGeometry geometry)
        => geometry.Left + (pixelX - geometry.Display.PixelBounds.Left) * geometry.Scale;

    private static double MapPixelY(int pixelY, WorkspaceFrozenDisplayCanvasGeometry geometry)
        => geometry.Top + (pixelY - geometry.Display.PixelBounds.Top) * geometry.Scale;

    private static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(
        byte alpha,
        byte red,
        byte green,
        byte blue)
        => new(Windows.UI.Color.FromArgb(alpha, red, green, blue));

    private static void AddWorkspaceFrozenPreviewItemDot(
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
        _workspaceFrozenPreview = null;
        _activeWorkspaceFrozenPreviewDisplayIndex = -1;
        _workspaceFrozenPreviewApplyAvailable = false;
        RefreshApplyButtonFromCurrentEvidence();
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
