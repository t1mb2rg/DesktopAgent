using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

/// <summary>原生定位锚点不变；网格分区与可视单元统一使用这套投影。</summary>
public static class ExplorerGridPresentation
{
    public const int LabelWidthDip = 132;
    public const int LabelHeightDip = 28;
    public const int LabelInsetDip = 10;
    public const int FrameInsetDip = 4;

    public static DesktopWorkspaceNativeRect Quantize(DesktopWorkspaceRelativeRect bounds, ExplorerDisplayGeometry display)
    {
        static int Boundary(double value, int count) => (int)Math.Round(value * count, MidpointRounding.AwayFromZero);
        var left = Math.Clamp(Boundary(bounds.Left, display.Columns), 0, display.Columns - 1);
        var top = Math.Clamp(Boundary(bounds.Top, display.Rows), 0, display.Rows - 1);
        var right = Math.Clamp(Boundary(bounds.Left + bounds.Width, display.Columns), left + 1, display.Columns);
        var bottom = Math.Clamp(Boundary(bounds.Top + bounds.Height, display.Rows), top + 1, display.Rows);
        return new(left, top, right - left, bottom - top);
    }

    public static DesktopWorkspaceRelativeRect Relative(DesktopWorkspaceNativeRect rect, ExplorerDisplayGeometry display)
        => new(rect.FirstColumn / (double)display.Columns, rect.FirstRow / (double)display.Rows,
            rect.ColumnCount / (double)display.Columns, rect.RowCount / (double)display.Rows);

    public static PixelRect PixelBounds(DesktopWorkspaceNativeRect rect, ExplorerDisplayGeometry display)
        => new(Math.Max(display.WorkArea.Left, checked(display.GridOrigin.X + rect.FirstColumn * display.Spacing.X)),
            Math.Max(display.WorkArea.Top, checked(display.GridOrigin.Y + rect.FirstRow * display.Spacing.Y)),
            Math.Min(display.WorkArea.Right, checked(display.GridOrigin.X + rect.ExclusiveColumn * display.Spacing.X)),
            Math.Min(display.WorkArea.Bottom, checked(display.GridOrigin.Y + rect.ExclusiveRow * display.Spacing.Y)));

    /// <summary>线框外沿吸收屏幕边缘的零碎留白，内部边界仍共用同一网格分界；不改变原生定位点。</summary>
    public static PixelRect FrameBounds(DesktopWorkspaceNativeRect rect, ExplorerDisplayGeometry display)
    {
        var cells = PixelBounds(rect, display);
        return new(rect.FirstColumn == 0 ? display.WorkArea.Left : cells.Left,
            rect.FirstRow == 0 ? display.WorkArea.Top : cells.Top,
            rect.ExclusiveColumn == display.Columns ? display.WorkArea.Right : cells.Right,
            rect.ExclusiveRow == display.Rows ? display.WorkArea.Bottom : cells.Bottom);
    }

    public static DesktopWorkspaceNativeRect LabelCells(DesktopWorkspaceNativeRect zone, ExplorerDisplayGeometry display)
    {
        var columns = Math.Clamp((int)Math.Ceiling((LabelWidthDip + 2 * LabelInsetDip + 2 * FrameInsetDip) * Math.Max(96, display.DpiX) / 96d / display.Spacing.X), 1, zone.ColumnCount);
        var rows = Math.Clamp((int)Math.Ceiling((LabelHeightDip + 2 * LabelInsetDip + 2 * FrameInsetDip) * Math.Max(96, display.DpiY) / 96d / display.Spacing.Y), 1, zone.RowCount);
        return new(zone.ExclusiveColumn - columns, zone.ExclusiveRow - rows, columns, rows);
    }

    public static (double X, double Y) CellCenter(PixelPoint nativePosition, PixelPoint spacing)
        => (nativePosition.X + spacing.X / 2d, nativePosition.Y + spacing.Y / 2d);

    public static DesktopWorkspaceRelativeRect Move(DesktopWorkspaceNativeRect origin, int deltaColumns, int deltaRows, ExplorerDisplayGeometry target)
    {
        var columns = Math.Min(origin.ColumnCount, target.Columns);
        var rows = Math.Min(origin.RowCount, target.Rows);
        return Relative(new(Math.Clamp(origin.FirstColumn + deltaColumns, 0, target.Columns - columns),
            Math.Clamp(origin.FirstRow + deltaRows, 0, target.Rows - rows), columns, rows), target);
    }

    public static DesktopLayoutZone SnapLegacyZone(DesktopLayoutZone zone, ExplorerDisplayGeometry display)
    {
        var work = display.WorkArea;
        int Col(int value) => (int)Math.Round((work.Left + value * work.Width / (double)NormalizedRect.Scale - display.GridOrigin.X) / display.Spacing.X, MidpointRounding.AwayFromZero);
        int Row(int value) => (int)Math.Round((work.Top + value * work.Height / (double)NormalizedRect.Scale - display.GridOrigin.Y) / display.Spacing.Y, MidpointRounding.AwayFromZero);
        var left = Math.Clamp(Col(zone.Bounds.Left), 0, display.Columns - 1);
        var top = Math.Clamp(Row(zone.Bounds.Top), 0, display.Rows - 1);
        var right = Math.Clamp(Col(zone.Bounds.Right), left + 1, display.Columns);
        var bottom = Math.Clamp(Row(zone.Bounds.Bottom), top + 1, display.Rows);
        var pixels = PixelBounds(new(left, top, right - left, bottom - top), display);
        int X(int pixel) => Math.Clamp((int)Math.Round((pixel - work.Left) * (double)NormalizedRect.Scale / work.Width), 0, NormalizedRect.Scale);
        int Y(int pixel) => Math.Clamp((int)Math.Round((pixel - work.Top) * (double)NormalizedRect.Scale / work.Height), 0, NormalizedRect.Scale);
        return new(zone.ZoneId, zone.Name, new(X(pixels.Left), Y(pixels.Top), X(pixels.Right), Y(pixels.Bottom)), zone.SortOrder, zone.PlacementFlow, zone.MaxItemCount);
    }
}
