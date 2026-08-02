using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

/// <summary>
/// 将 WorkArea 像素坐标等比例映射到 Canvas viewport。
/// 保持真实纵横比，letterbox 留白。
/// </summary>
public sealed class LayoutPreviewCanvasTransform
{
    public PixelRect WorkArea { get; }
    public double ViewportWidth { get; }
    public double ViewportHeight { get; }
    public double Padding { get; }
    public double Scale { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }
    public double AvailableWidth { get; }
    public double AvailableHeight { get; }

    public LayoutPreviewCanvasTransform(
        PixelRect workArea,
        double viewportWidth,
        double viewportHeight,
        double padding = 4.0)
    {
        if (workArea.Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea.Width 必须 > 0，实际 {workArea.Width}。");
        if (workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea.Height 必须 > 0，实际 {workArea.Height}。");
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth),
                "viewportWidth 必须为有限正数。");
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportHeight),
                "viewportHeight 必须为有限正数。");
        if (!double.IsFinite(padding) || padding < 0)
            throw new ArgumentOutOfRangeException(nameof(padding),
                "padding 必须为有限且 >= 0。");

        AvailableWidth = viewportWidth - padding * 2;
        AvailableHeight = viewportHeight - padding * 2;

        if (AvailableWidth <= 0 || AvailableHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(padding),
                $"padding ({padding}) 导致无可用区域：" +
                $"AvailableWidth={AvailableWidth}, AvailableHeight={AvailableHeight}。");

        Scale = Math.Min(
            AvailableWidth / workArea.Width,
            AvailableHeight / workArea.Height);

        double contentWidth = workArea.Width * Scale;
        double contentHeight = workArea.Height * Scale;

        OffsetX = padding + (AvailableWidth - contentWidth) / 2.0;
        OffsetY = padding + (AvailableHeight - contentHeight) / 2.0;

        WorkArea = workArea;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
        Padding = padding;
    }

    /// <summary>将 WorkArea 像素坐标映射到 Canvas 坐标。</summary>
    public (double canvasX, double canvasY) Map(double pixelX, double pixelY)
    {
        double cx = OffsetX + (pixelX - (double)WorkArea.Left) * Scale;
        double cy = OffsetY + (pixelY - (double)WorkArea.Top) * Scale;
        return (cx, cy);
    }

    /// <summary>将 PixelPoint 映射到 Canvas 坐标。</summary>
    public (double canvasX, double canvasY) Map(PixelPoint point) =>
        Map(point.X, point.Y);

    /// <summary>将 PixelRect 映射到 Canvas 坐标。</summary>
    public (double left, double top, double width, double height) Map(PixelRect rect)
    {
        long w = (long)rect.Right - rect.Left;
        long h = (long)rect.Bottom - rect.Top;
        var (cx, cy) = Map(rect.Left, rect.Top);
        double cw = w * Scale;
        double ch = h * Scale;
        return (cx, cy, cw, ch);
    }
}
