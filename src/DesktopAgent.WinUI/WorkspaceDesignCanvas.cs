using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
namespace DesktopAgent.WinUI;

// Both surfaces render and edit the window-owned session; this controller owns no draft.
internal sealed class WorkspaceDesignCanvas
{
    private readonly ProductWorkspaceSession _workspaceSession;
    internal DesktopWorkspaceDesignDraft? DisplayDraft { get; set; }
    internal bool IsReadOnly { get; set; }
    internal bool DesktopCoordinates { get; set; }

    private readonly Canvas ChatTopologyCanvas;
    private readonly TextBlock AiTopologySummaryText;
    internal WorkspaceDesignCanvas(ProductWorkspaceSession session, Canvas canvas, TextBlock summary)
    {
        _workspaceSession = session; ChatTopologyCanvas = canvas; AiTopologySummaryText = summary;
        canvas.SizeChanged += ChatTopology_SizeChanged;
        canvas.PointerMoved += ChatTopology_PointerMoved;
        canvas.PointerReleased += ChatTopology_PointerReleased;
        canvas.PointerCanceled += ChatTopology_PointerCanceled;
        canvas.PointerCaptureLost += ChatTopology_PointerCanceled;
        canvas.Unloaded += (_, _) => CancelDrag();
    }
    internal void CancelDrag()
    {
        _topologyDragZone = null; _topologyPointer = null; _topologyCandidate = null;
        ChatTopologyCanvas.ReleasePointerCaptures();
    }
    private void ChatTopology_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _topologyDragZone = null; _topologyPointer = null; _topologyCandidate = null;
        ChatTopologyCanvas.ReleasePointerCaptures();
        RenderChatTopology();
    }

    internal void RenderChatTopology()
    {
        if (_topologyDragZone is not null)
        {
            if (_workspaceSession.DesignGeometry is not null &&
                _workspaceSession.Session.Snapshot().Revision == _topologyDragRevision) return;
            CancelDrag();
        }
        var observation = _workspaceSession.DesignObservation ?? _workspaceSession.LatestUnderstanding?.Observation;
        ChatTopologyCanvas.Children.Clear();
        if (observation is null || observation.Displays.Count == 0)
        {
            AiTopologySummaryText.Text = _workspaceSession.PersistenceMessage ??
                _workspaceSession.UnderstandingMessage ?? "等待桌面理解与网格校准，准备完成后可拖动分区。";
            return;
        }
        DesktopSemanticDisplayMap map;
        try { map = DesktopSemanticDisplayMap.Create(observation); }
        catch (Exception)
        {
            AiTopologySummaryText.Text = "当前显示器拓扑暂不可用，请检查显示环境。";
            return;
        }
        var minX = observation.Displays.Min(display => display.Bounds.Left);
        var minY = observation.Displays.Min(display => display.Bounds.Top);
        var maxX = observation.Displays.Max(display => display.Bounds.Right);
        var maxY = observation.Displays.Max(display => display.Bounds.Bottom);
        var width = Math.Max(1, (long)maxX - minX);
        var height = Math.Max(1, (long)maxY - minY);
        var canvasHeight = Math.Max(1, ChatTopologyCanvas.ActualHeight);
        var scale = Math.Min(Math.Max(1, ChatTopologyCanvas.ActualWidth - 32) / width, Math.Max(1, canvasHeight - 48) / height);
        if (DesktopCoordinates) scale = ChatTopologyCanvas.ActualWidth / width;
        var originX = DesktopCoordinates ? 0 : (ChatTopologyCanvas.ActualWidth - width * scale) / 2;
        var originY = DesktopCoordinates ? 0 : (canvasHeight - height * scale) / 2;
        _topologyScale = scale; _topologyOriginX = originX; _topologyOriginY = originY;
        _topologyMinX = minX; _topologyMinY = minY;
        var draft = DisplayDraft ?? _workspaceSession.Session.Snapshot().State.Draft;
        foreach (var entry in map.Entries)
        {
            var display = entry.Display;
            var left = originX + ((long)display.Bounds.Left - minX) * scale;
            var top = originY + ((long)display.Bounds.Top - minY) * scale;
            var block = new Border { Width = display.Bounds.Width * scale, Height = display.Bounds.Height * scale,
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(150, 130, 140, 160)),
                Child = new TextBlock { Text = display.IsPrimary ? "主屏" : entry.DisplayRef.Replace("secondary_", "副屏 "),
                    FontSize = 11, Margin = new Thickness(6), Opacity = 0.7 } };
            Canvas.SetLeft(block, left); Canvas.SetTop(block, top);
            ChatTopologyCanvas.Children.Add(block);
            foreach (var zone in draft.Zones.Where(zone => zone.DisplayRef == entry.DisplayRef))
            {
                var grid = _workspaceSession.DesignGeometry?.Displays.SingleOrDefault(item => item.DisplayRef == entry.DisplayRef);
                if (grid is null) continue;
                var pixels = ExplorerGridPresentation.FrameBounds(ExplorerGridPresentation.Quantize(zone.Bounds, grid), grid);
                var zoneContent = new Grid();
                zoneContent.Children.Add(new TextBlock { Text = zone.Name, FontSize = 11, Margin = new Thickness(5, 5, 16, 5),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis });
                var grip = new Border { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(220, 0, 120, 212)) };
                if (!IsReadOnly) zoneContent.Children.Add(grip);
                var zoneBlock = new Border { Width = pixels.Width * scale,
                    Height = pixels.Height * scale, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.FromArgb(60, 0, 120, 212)), BorderBrush = new SolidColorBrush(Color.FromArgb(220, 0, 120, 212)),
                    Child = zoneContent };
                zoneBlock.PointerPressed += (_, args) => BeginTopologyDrag(zone, zoneBlock, false, args);
                grip.PointerPressed += (_, args) => BeginTopologyDrag(zone, zoneBlock, true, args);
                Canvas.SetLeft(zoneBlock, originX + ((long)pixels.Left - minX) * scale);
                Canvas.SetTop(zoneBlock, originY + ((long)pixels.Top - minY) * scale);
                ChatTopologyCanvas.Children.Add(zoneBlock);
                var native = ExplorerGridPresentation.Quantize(zone.Bounds, grid);
                var labelCells = ExplorerGridPresentation.LabelCells(native, grid);
                var categoryKeys = draft.CategoryRules.Where(rule => rule.ZoneKey == zone.ZoneKey).Select(rule => rule.CategoryKey).ToHashSet(StringComparer.Ordinal);
                var itemCount = _workspaceSession.LatestUnderstanding?.Draft?.Assignments.Count(item => categoryKeys.Contains(item.CategoryKey)) ?? 0;
                var slots = grid.Slots.Where(slot => slot.Column >= native.FirstColumn && slot.Column < native.ExclusiveColumn &&
                    slot.Row >= native.FirstRow && slot.Row < native.ExclusiveRow &&
                    !(slot.Column >= labelCells.FirstColumn && slot.Column < labelCells.ExclusiveColumn && slot.Row >= labelCells.FirstRow && slot.Row < labelCells.ExclusiveRow))
                    .OrderBy(slot => slot.Row).ThenBy(slot => slot.Column).ToArray();
                ToolTipService.SetToolTip(zoneBlock, $"{zone.Name} · {itemCount} 项 / {slots.Length} 个网格位（已避让标题）");
                foreach (var slot in slots.Take(DesktopCoordinates ? 0 : Math.Min(itemCount, 300)))
                {
                    var center = ExplorerGridPresentation.CellCenter(slot.PixelPosition, grid.Spacing);
                    var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, IsHitTestVisible = false,
                        Fill = new SolidColorBrush(Color.FromArgb(200, 170, 220, 255)) };
                    Canvas.SetLeft(dot, originX + (center.X - minX) * scale - 2);
                    Canvas.SetTop(dot, originY + (center.Y - minY) * scale - 2);
                    ChatTopologyCanvas.Children.Add(dot);
                }
            }
        }
        if (IsReadOnly)
        {
            AiTopologySummaryText.Text = $"保存的设计 · {draft.Zones.Count} 个分区 · 点击编辑调整草稿";
            return;
        }
        AiTopologySummaryText.Text = _workspaceSession.PersistenceMessage ?? (draft.Zones.Count == 0 ? "当前设计 · 通过对话安排分区" : $"当前设计 · {draft.Zones.Count} 个分区 · 草稿自动保存 · 尚未应用到桌面");
    }
    private DesktopWorkspaceBlueprintZone? _topologyDragZone, _topologyCandidate;
    private Border? _topologyDragVisual;
    private DesktopWorkspaceNativeRect _topologyDragRect;
    private double _topologyScale, _topologyOriginX, _topologyOriginY, _topologyMinX, _topologyMinY, _topologyGrabX, _topologyGrabY;
    private bool _topologyResize;
    private long _topologyDragRevision;
    private uint? _topologyPointer;

    private void BeginTopologyDrag(DesktopWorkspaceBlueprintZone zone, Border visual, bool resize, PointerRoutedEventArgs args)
    {
        if (IsReadOnly || _topologyDragZone is not null || _workspaceSession.DesignGeometry is not { } geometry || _topologyScale <= 0 || !args.GetCurrentPoint(ChatTopologyCanvas).Properties.IsLeftButtonPressed) return;
        var display = geometry.Displays.Single(item => item.DisplayRef == zone.DisplayRef);
        _topologyDragRect = ExplorerGridPresentation.Quantize(zone.Bounds, display);
        var pixels = ExplorerGridPresentation.FrameBounds(_topologyDragRect, display);
        var point = args.GetCurrentPoint(ChatTopologyCanvas).Position;
        _topologyGrabX = (point.X - _topologyOriginX) / _topologyScale + _topologyMinX - pixels.Left;
        _topologyGrabY = (point.Y - _topologyOriginY) / _topologyScale + _topologyMinY - pixels.Top;
        _topologyDragZone = zone; _topologyCandidate = null; _topologyDragVisual = visual;
        _topologyResize = resize; _topologyDragRevision = _workspaceSession.Session.Snapshot().Revision;
        _topologyPointer = args.Pointer.PointerId;
        if (!ChatTopologyCanvas.CapturePointer(args.Pointer)) { _topologyDragZone = null; _topologyPointer = null; }
        args.Handled = true;
    }

    private void ChatTopology_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_topologyDragZone is not { } zone || _topologyPointer != args.Pointer.PointerId || _workspaceSession.DesignGeometry is not { } geometry) return;
        var point = args.GetCurrentPoint(ChatTopologyCanvas).Position;
        var x = (point.X - _topologyOriginX) / _topologyScale + _topologyMinX;
        var y = (point.Y - _topologyOriginY) / _topologyScale + _topologyMinY;
        var display = _topologyResize ? geometry.Displays.Single(item => item.DisplayRef == zone.DisplayRef)
            : geometry.Displays.FirstOrDefault(item => x >= item.Bounds.Left && x < item.Bounds.Right && y >= item.Bounds.Top && y < item.Bounds.Bottom);
        if (display is null) return;
        int Col(double value) => (int)Math.Round((value - display.GridOrigin.X) / display.Spacing.X, MidpointRounding.AwayFromZero);
        int Row(double value) => (int)Math.Round((value - display.GridOrigin.Y) / display.Spacing.Y, MidpointRounding.AwayFromZero);
        DesktopWorkspaceRelativeRect bounds;
        if (_topologyResize)
        {
            var right = Math.Clamp(Col(x), _topologyDragRect.FirstColumn + 1, display.Columns);
            var bottom = Math.Clamp(Row(y), _topologyDragRect.FirstRow + 1, display.Rows);
            bounds = ExplorerGridPresentation.Relative(new(_topologyDragRect.FirstColumn, _topologyDragRect.FirstRow,
                right - _topologyDragRect.FirstColumn, bottom - _topologyDragRect.FirstRow), display);
        }
        else bounds = ExplorerGridPresentation.Move(_topologyDragRect,
            Col(x - _topologyGrabX) - _topologyDragRect.FirstColumn, Row(y - _topologyGrabY) - _topologyDragRect.FirstRow, display);
        var rect = ExplorerGridPresentation.Quantize(bounds, display);
        if (_workspaceSession.Session.Snapshot().State.Draft.Zones.Where(item => item.ZoneKey != zone.ZoneKey && item.DisplayRef == display.DisplayRef)
            .Select(item => ExplorerGridPresentation.Quantize(item.Bounds, display)).Any(other =>
                rect.FirstColumn < other.ExclusiveColumn && rect.ExclusiveColumn > other.FirstColumn && rect.FirstRow < other.ExclusiveRow && rect.ExclusiveRow > other.FirstRow)) return;
        _topologyCandidate = new(zone.ZoneKey, zone.Name, display.DisplayRef, bounds, zone.Anchor, zone.PackingOrder);
        var pixels = ExplorerGridPresentation.FrameBounds(rect, display);
        _topologyDragVisual!.Width = pixels.Width * _topologyScale; _topologyDragVisual.Height = pixels.Height * _topologyScale;
        Canvas.SetLeft(_topologyDragVisual, _topologyOriginX + (pixels.Left - _topologyMinX) * _topologyScale);
        Canvas.SetTop(_topologyDragVisual, _topologyOriginY + (pixels.Top - _topologyMinY) * _topologyScale);
        AiTopologySummaryText.Text = $"{zone.Name} · {rect.ColumnCount} 列 × {rect.RowCount} 行 · 松开更新草稿";
        args.Handled = true;
    }

    private void ChatTopology_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_topologyPointer != args.Pointer.PointerId) return;
        var candidate = _topologyCandidate;
        if (candidate is not null && _topologyDragZone is { } original &&
            candidate.DisplayRef == original.DisplayRef && candidate.Bounds == original.Bounds)
            candidate = null;
        _topologyDragZone = null; _topologyPointer = null; _topologyCandidate = null;
        ChatTopologyCanvas.ReleasePointerCapture(args.Pointer);
        if (candidate is not null && _workspaceSession.DesignGeometry is not null)
        {
            var result = _workspaceSession.Session.TryApply(_topologyDragRevision, [new UpsertWorkspaceZone(candidate)]);
            if (result.Applied) _workspaceSession.Session.AppendMessage(DesktopWorkspaceMessageRole.LocalResult, $"已按实测网格调整“{candidate.Name}”，桌面图标尚未移动。");
            else AiTopologySummaryText.Text = result.Message ?? "设计已变化，本次拖动未覆盖当前草稿。";
        }
        RenderChatTopology();
    }

    private void ChatTopology_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_topologyPointer != args.Pointer.PointerId) return;
        _topologyDragZone = null; _topologyPointer = null; _topologyCandidate = null;
        RenderChatTopology();
    }
}
