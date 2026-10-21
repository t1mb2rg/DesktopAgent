using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// Desktop Zone 编辑态的纯几何吸附。
/// 只调整当前用户正在拖动/缩放的 Zone，不移动其他 Zone，也不触碰 Shell/Writer。
/// </summary>
internal static class DesktopZoneSnapOperations
{
    internal const int Threshold = 80;

    internal static DesktopLayoutZone Snap(
        IReadOnlyList<DesktopLayoutZone> zones,
        int index,
        DesktopLayoutZone current,
        DesktopLayoutZone replacement)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);

        if (SameBounds(current.Bounds, replacement.Bounds))
            return replacement;

        if (current.Bounds.Width == replacement.Bounds.Width &&
            current.Bounds.Height == replacement.Bounds.Height)
        {
            return SnapMove(zones, index, replacement);
        }

        if (current.Bounds.Left == replacement.Bounds.Left &&
            current.Bounds.Top == replacement.Bounds.Top)
        {
            return SnapBottomRightResize(zones, index, replacement);
        }

        return replacement;
    }

    private static DesktopLayoutZone SnapMove(
        IReadOnlyList<DesktopLayoutZone> zones,
        int index,
        DesktopLayoutZone replacement)
    {
        var x = FindMoveSnapX(zones, index, replacement.Bounds);
        var y = FindMoveSnapY(zones, index, replacement.Bounds);

        foreach (var (dx, dy) in CandidatePairs(x, y))
        {
            if (dx == 0 && dy == 0)
                continue;

            var bounds = Offset(replacement.Bounds, dx, dy);
            if (bounds is not null && !OverlapsAny(zones, index, bounds))
                return WithBounds(replacement, bounds);
        }

        return replacement;
    }

    private static DesktopLayoutZone SnapBottomRightResize(
        IReadOnlyList<DesktopLayoutZone> zones,
        int index,
        DesktopLayoutZone replacement)
    {
        var right = FindResizeSnapRight(zones, index, replacement.Bounds);
        var bottom = FindResizeSnapBottom(zones, index, replacement.Bounds);

        foreach (var (dr, db) in CandidatePairs(right, bottom))
        {
            if (dr == 0 && db == 0)
                continue;

            var nextRight = replacement.Bounds.Right + dr;
            var nextBottom = replacement.Bounds.Bottom + db;
            if (nextRight <= replacement.Bounds.Left ||
                nextBottom <= replacement.Bounds.Top ||
                nextRight > NormalizedRect.Scale ||
                nextBottom > NormalizedRect.Scale)
            {
                continue;
            }

            var bounds = new NormalizedRect(
                replacement.Bounds.Left,
                replacement.Bounds.Top,
                nextRight,
                nextBottom);
            if (!OverlapsAny(zones, index, bounds))
                return WithBounds(replacement, bounds);
        }

        return replacement;
    }

    private static int FindMoveSnapX(
        IReadOnlyList<DesktopLayoutZone> zones,
        int index,
        NormalizedRect bounds)
    {
        var best = 0;
        var distance = Threshold + 1;
        Consider(-bounds.Left, ref best, ref distance);
        Consider(NormalizedRect.Scale - bounds.Right, ref best, ref distance);

        for (var i = 0; i < zones.Count; i++)
        {
            if (i == index || !RangesOverlap(
                    bounds.Top, bounds.Bottom,
                    zones[i].Bounds.Top, zones[i].Bounds.Bottom))
            {
                continue;
            }

            var other = zones[i].Bounds;
            Consider(other.Left - bounds.Left, ref best, ref distance);
            Consider(other.Right - bounds.Left, ref best, ref distance);
            Consider(other.Left - bounds.Right, ref best, ref distance);
            Consider(other.Right - bounds.Right, ref best, ref distance);
        }

        return best;
    }

    private static int FindMoveSnapY(
        IReadOnlyList<DesktopLayoutZone> zones,
        int index,
        NormalizedRect bounds)
    {
        var best = 0;
        var distance = Threshold + 1;
        Consider(-bounds.Top, ref best, ref distance);
        Consider(NormalizedRect.Scale - bounds.Bottom, ref best, ref distance);

        for (var i = 0; i < zones.Count; i++)
        {
            if (i == index || !RangesOverlap(
                    bounds.Left, bounds.Right,
                    zones[i].Bounds.Left, zones[i].Bounds.Right))
            {
                continue;
            }

            var other = zones[i].Bounds;
            Consider(other.Top - bounds.Top, ref best, ref distance);
            Consider(other.Bottom - bounds.Top, ref best, ref distance);
            Consider(other.Top - bounds.Bottom, ref best, ref distance);
            Consider(other.Bottom - bounds.Bottom, ref best, ref distance);
        }

        return best;
    }

    private static int FindResizeSnapRight(
        IReadOnlyList<DesktopLayoutZone> zones,
        int index,
        NormalizedRect bounds)
    {
        var best = 0;
        var distance = Threshold + 1;
        Consider(NormalizedRect.Scale - bounds.Right, ref best, ref distance);

        for (var i = 0; i < zones.Count; i++)
        {
            if (i == index || !RangesOverlap(
                    bounds.Top, bounds.Bottom,
                    zones[i].Bounds.Top, zones[i].Bounds.Bottom))
            {
                continue;
            }

            Consider(zones[i].Bounds.Left - bounds.Right, ref best, ref distance);
            Consider(zones[i].Bounds.Right - bounds.Right, ref best, ref distance);
        }

        return best;
    }

    private static int FindResizeSnapBottom(
        IReadOnlyList<DesktopLayoutZone> zones,
        int index,
        NormalizedRect bounds)
    {
        var best = 0;
        var distance = Threshold + 1;
        Consider(NormalizedRect.Scale - bounds.Bottom, ref best, ref distance);

        for (var i = 0; i < zones.Count; i++)
        {
            if (i == index || !RangesOverlap(
                    bounds.Left, bounds.Right,
                    zones[i].Bounds.Left, zones[i].Bounds.Right))
            {
                continue;
            }

            Consider(zones[i].Bounds.Top - bounds.Bottom, ref best, ref distance);
            Consider(zones[i].Bounds.Bottom - bounds.Bottom, ref best, ref distance);
        }

        return best;
    }

    private static void Consider(int offset, ref int best, ref int bestDistance)
    {
        var distance = Math.Abs(offset);
        if (distance > Threshold || distance >= bestDistance)
            return;

        best = offset;
        bestDistance = distance;
    }

    private static IEnumerable<(int X, int Y)> CandidatePairs(int x, int y)
    {
        if (x != 0 && y != 0)
            yield return (x, y);
        if (x != 0)
            yield return (x, 0);
        if (y != 0)
            yield return (0, y);
    }

    private static NormalizedRect? Offset(NormalizedRect bounds, int dx, int dy)
    {
        var left = bounds.Left + dx;
        var top = bounds.Top + dy;
        var right = bounds.Right + dx;
        var bottom = bounds.Bottom + dy;
        if (left < 0 || top < 0 || right > NormalizedRect.Scale || bottom > NormalizedRect.Scale)
            return null;

        return new NormalizedRect(left, top, right, bottom);
    }

    private static bool OverlapsAny(
        IReadOnlyList<DesktopLayoutZone> zones,
        int index,
        NormalizedRect bounds)
    {
        for (var i = 0; i < zones.Count; i++)
        {
            if (i != index && zones[i].Bounds.Overlaps(bounds))
                return true;
        }

        return false;
    }

    private static bool SameBounds(NormalizedRect first, NormalizedRect second)
        => first.Left == second.Left &&
           first.Top == second.Top &&
           first.Right == second.Right &&
           first.Bottom == second.Bottom;

    private static bool RangesOverlap(int a0, int a1, int b0, int b1)
        => Math.Max(a0, b0) < Math.Min(a1, b1);

    private static DesktopLayoutZone WithBounds(
        DesktopLayoutZone zone,
        NormalizedRect bounds)
        => new(
            zone.ZoneId,
            zone.Name,
            bounds,
            zone.SortOrder,
            zone.PlacementFlow,
            zone.MaxItemCount);
}
