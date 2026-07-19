namespace DesktopAgent.Core.Geometry;

/// <summary>
/// 虚拟桌面坐标系中的物理像素矩形。
/// 支持副屏位于主屏左侧或上方时出现的负坐标。
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public PixelPoint Center => new(
        Left + Width / 2,
        Top + Height / 2);

    public bool Contains(PixelPoint point) =>
        point.X >= Left &&
        point.X < Right &&
        point.Y >= Top &&
        point.Y < Bottom;
}
