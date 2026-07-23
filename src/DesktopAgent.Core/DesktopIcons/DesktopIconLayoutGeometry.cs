using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 桌面图标布局的共享半开矩形语义。
/// 纯计算，不访问 Shell COM、WPF、SQLite、显示器 API。
/// </summary>
public static class DesktopIconLayoutGeometry
{
    /// <summary>checked 创建图标占用矩形 [pos, pos+spacing)。</summary>
    public static PixelRect CreateRectChecked(PixelPoint position, PixelPoint spacing) =>
        checked(new PixelRect(position.X, position.Y, position.X + spacing.X, position.Y + spacing.Y));

    /// <summary>安全尝试创建图标占用矩形，溢出时返回 false。</summary>
    public static bool TryCreateRect(PixelPoint position, PixelPoint spacing, out PixelRect rect)
    {
        try
        {
            rect = CreateRectChecked(position, spacing);
            return true;
        }
        catch (OverflowException)
        {
            rect = default;
            return false;
        }
    }

    /// <summary>半开矩形重叠检测。</summary>
    public static bool Overlaps(PixelRect a, PixelRect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    /// <summary>inner 是否完整包含在 outer 内。</summary>
    public static bool IsFullyContained(PixelRect inner, PixelRect outer) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top
        && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}
