using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 纯逻辑显示器归属判断。
/// 不依赖 WPF、Win32 或 WindowsInterop。
/// </summary>
public static class DisplayAssignmentService
{
    /// <summary>
    /// 为图标坐标分配所属显示器。
    /// </summary>
    /// <param name="iconPosition">图标在虚拟桌面物理像素坐标系中的位置。</param>
    /// <param name="displays">当前显示器拓扑。</param>
    /// <returns>
    /// 包含图标的显示器；若坐标不在任何显示器内，
    /// 返回距离最近的显示器；显示器列表为空时返回 null。
    /// </returns>
    public static DisplayDescriptor? AssignMonitor(
        PixelPoint iconPosition,
        IReadOnlyList<DisplayDescriptor> displays)
    {
        if (displays.Count == 0)
        {
            return null;
        }

        // 规则 1：坐标落在某显示器 Bounds 内时选择该显示器。
        var containing = displays.FirstOrDefault(d => d.Bounds.Contains(iconPosition));
        if (containing is not null)
        {
            return containing;
        }

        // 规则 2：选择距离图标坐标最近的显示器。
        // 使用中心距离（欧几里得距离的平方，避免浮点）。
        DisplayDescriptor? nearest = null;
        long nearestDistanceSq = long.MaxValue;

        foreach (var display in displays)
        {
            var distanceSq = SquaredDistanceToRect(iconPosition, display.Bounds);
            if (distanceSq < nearestDistanceSq)
            {
                nearestDistanceSq = distanceSq;
                nearest = display;
            }
        }

        return nearest;
    }

    /// <summary>
    /// 计算点到矩形的最短欧几里得距离的平方。
    /// 点在矩形内部时距离为 0。
    /// </summary>
    internal static long SquaredDistanceToRect(PixelPoint point, PixelRect rect)
    {
        // 计算 X 方向超出范围的距离
        var dx = point.X < rect.Left ? (long)(rect.Left - point.X)
               : point.X >= rect.Right ? (long)(point.X - rect.Right + 1)
               : 0;

        // 计算 Y 方向超出范围的距离
        var dy = point.Y < rect.Top ? (long)(rect.Top - point.Y)
               : point.Y >= rect.Bottom ? (long)(point.Y - rect.Bottom + 1)
               : 0;

        return dx * dx + dy * dy;
    }
}
