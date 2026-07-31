using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Poc.LayoutTemplates.Editor;

/// <summary>
/// 四角缩放句柄。
/// </summary>
public enum ZoneResizeHandle
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// 归一化 Zone 编辑器的纯逻辑几何辅助类。
/// 不依赖 WPF 控件。
/// </summary>
public static class NormalizedZoneEditorGeometry
{
    public const int DefaultSnapStep = 100;
    public const int DefaultMinimumSize = 100;
    public const int NormalizedMax = 10000;

    private static readonly int[] CandidateSizes = [2500, 2000, 1000, 500];
    private const int ScanStep = 250;

    /// <summary>将 delta 吸附到最近的 snapStep 倍数，中点 AwayFromZero。</summary>
    public static int SnapDelta(int delta, int snapStep)
    {
        if (snapStep <= 0)
            throw new ArgumentOutOfRangeException(nameof(snapStep), "snapStep 必须大于 0。");

        var sign = delta >= 0 ? 1 : -1;
        var abs = Math.Abs((long)delta);
        var snapped = RoundToNearest(abs, (long)snapStep);
        return checked((int)(snapped * sign));
    }

    private static long RoundToNearest(long value, long step)
    {
        var remainder = value % step;
        var lower = value - remainder;
        if (checked(remainder * 2) < step)
            return lower;
        else
            return checked(lower + step);
    }

    /// <summary>在 0..10000 范围内移动矩形，保持宽高不变。</summary>
    public static NormalizedRect Move(NormalizedRect source, int deltaX, int deltaY)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        long newLeft = (long)source.Left + deltaX;
        long newTop = (long)source.Top + deltaY;
        long width = (long)source.Right - source.Left;
        long height = (long)source.Bottom - source.Top;

        // Clamp
        if (newLeft < 0) newLeft = 0;
        if (newTop < 0) newTop = 0;
        if (newLeft + width > NormalizedMax) newLeft = NormalizedMax - width;
        if (newTop + height > NormalizedMax) newTop = NormalizedMax - height;

        return new NormalizedRect(
            checked((int)newLeft),
            checked((int)newTop),
            checked((int)(newLeft + width)),
            checked((int)(newTop + height)));
    }

    /// <summary>四角缩放，保持对角固定。</summary>
    public static NormalizedRect Resize(
        NormalizedRect source,
        ZoneResizeHandle handle,
        int deltaX,
        int deltaY,
        int minimumSize = DefaultMinimumSize)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (source.Width < minimumSize || source.Height < minimumSize)
            throw new ArgumentException(
                $"source 的宽高必须不小于 minimumSize ({minimumSize})。" +
                $"实际 Width={source.Width}, Height={source.Height}。", nameof(source));
        if (minimumSize <= 0 || minimumSize > NormalizedMax)
            throw new ArgumentOutOfRangeException(nameof(minimumSize),
                $"minimumSize 必须在 1..{NormalizedMax} 之间，实际 {minimumSize}。");

        long left = source.Left, top = source.Top, right = source.Right, bottom = source.Bottom;

        switch (handle)
        {
            case ZoneResizeHandle.TopLeft:
                left = (long)source.Left + deltaX;
                top = (long)source.Top + deltaY;
                break;
            case ZoneResizeHandle.TopRight:
                right = (long)source.Right + deltaX;
                top = (long)source.Top + deltaY;
                break;
            case ZoneResizeHandle.BottomLeft:
                left = (long)source.Left + deltaX;
                bottom = (long)source.Bottom + deltaY;
                break;
            case ZoneResizeHandle.BottomRight:
                right = (long)source.Right + deltaX;
                bottom = (long)source.Bottom + deltaY;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(handle),
                    $"未知的 ZoneResizeHandle: {handle}。");
        }

        // 边界 clamp
        if (left < 0) left = 0;
        if (top < 0) top = 0;
        if (right > NormalizedMax) right = NormalizedMax;
        if (bottom > NormalizedMax) bottom = NormalizedMax;

        // 最小尺寸
        if (right - left < minimumSize)
        {
            if (handle is ZoneResizeHandle.TopLeft or ZoneResizeHandle.BottomLeft)
                left = right - minimumSize;
            else
                right = left + minimumSize;
        }
        if (bottom - top < minimumSize)
        {
            if (handle is ZoneResizeHandle.TopLeft or ZoneResizeHandle.TopRight)
                top = bottom - minimumSize;
            else
                bottom = top + minimumSize;
        }

        // 再次 clamp
        if (left < 0) { right -= left; left = 0; }
        if (top < 0) { bottom -= top; top = 0; }

        return new NormalizedRect(
            checked((int)left), checked((int)top),
            checked((int)right), checked((int)bottom));
    }

    /// <summary>检查 candidate 是否与 otherBounds 中的任一矩形重叠（相邻不算重叠）。</summary>
    public static bool OverlapsAny(
        NormalizedRect candidate,
        IEnumerable<NormalizedRect> otherBounds)
    {
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));
        if (otherBounds is null) throw new ArgumentNullException(nameof(otherBounds));

        foreach (var other in otherBounds)
        {
            if (other is null)
                throw new ArgumentException("otherBounds 包含 null 元素。", nameof(otherBounds));
            if (candidate.Overlaps(other))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 确定性扫描空闲矩形。从大到小尝试候选尺寸，在 0..10000 平面上以 250 步长扫描，
    /// 返回第一个不与 existing 重叠的矩形。
    /// </summary>
    public static bool TryFindFreeRect(
        IEnumerable<NormalizedRect> existing,
        out NormalizedRect? result)
    {
        if (existing is null) throw new ArgumentNullException(nameof(existing));

        var existingList = new List<NormalizedRect>();
        foreach (var r in existing)
        {
            if (r is null)
                throw new ArgumentException("existing 包含 null 元素。", nameof(existing));
            existingList.Add(r);
        }

        foreach (var size in CandidateSizes)
        {
            for (var top = 0; top <= NormalizedMax - size; top += ScanStep)
            {
                for (var left = 0; left <= NormalizedMax - size; left += ScanStep)
                {
                    var candidate = new NormalizedRect(left, top, left + size, top + size);
                    if (!OverlapsAny(candidate, existingList))
                    {
                        result = candidate;
                        return true;
                    }
                }
            }
        }

        result = null;
        return false;
    }
}
