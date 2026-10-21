using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// Desktop Zone 编辑器使用的纯内存几何操作。
///
/// 这里不读取 Shell、不持久化模板，也不产生任何 WriterCommand；
/// 它只把显式的用户拖动/缩放/新增/删除转换成新的不可变 Zone 列表。
/// </summary>
public static class DesktopZoneEditOperations
{
    private static readonly int[] CreateZoneSizes = [3000, 2500, 2000, 1500, 1000];
    private const int CreateScanStep = 250;

    public static DesktopLayoutZone Move(
        DesktopLayoutZone zone,
        int deltaX,
        int deltaY)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var width = zone.Bounds.Width;
        var height = zone.Bounds.Height;
        var left = Math.Clamp(zone.Bounds.Left + deltaX, 0, NormalizedRect.Scale - width);
        var top = Math.Clamp(zone.Bounds.Top + deltaY, 0, NormalizedRect.Scale - height);

        return WithBounds(
            zone,
            new NormalizedRect(left, top, left + width, top + height));
    }

    public static DesktopLayoutZone ResizeBottomRight(
        DesktopLayoutZone zone,
        int deltaX,
        int deltaY,
        int minimumWidth,
        int minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (minimumWidth <= 0 || minimumWidth > NormalizedRect.Scale)
            throw new ArgumentOutOfRangeException(nameof(minimumWidth));
        if (minimumHeight <= 0 || minimumHeight > NormalizedRect.Scale)
            throw new ArgumentOutOfRangeException(nameof(minimumHeight));

        var minimumRight = Math.Min(
            NormalizedRect.Scale,
            zone.Bounds.Left + minimumWidth);
        var minimumBottom = Math.Min(
            NormalizedRect.Scale,
            zone.Bounds.Top + minimumHeight);

        var right = Math.Clamp(
            zone.Bounds.Right + deltaX,
            minimumRight,
            NormalizedRect.Scale);
        var bottom = Math.Clamp(
            zone.Bounds.Bottom + deltaY,
            minimumBottom,
            NormalizedRect.Scale);

        return WithBounds(
            zone,
            new NormalizedRect(zone.Bounds.Left, zone.Bounds.Top, right, bottom));
    }

    public static IReadOnlyList<DesktopLayoutZone> ReplaceZone(
        IReadOnlyList<DesktopLayoutZone> zones,
        DesktopLayoutZone replacement,
        bool snapToAdjacentZones = true)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(replacement);

        var index = -1;
        for (var i = 0; i < zones.Count; i++)
        {
            if (zones[i].ZoneId == replacement.ZoneId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            throw new InvalidOperationException("要更新的 Zone 不在当前草稿中。 ");

        var adjusted = snapToAdjacentZones ? DesktopZoneSnapOperations.Snap(
            zones,
            index,
            zones[index],
            replacement) : replacement;

        for (var i = 0; i < zones.Count; i++)
        {
            if (i == index)
                continue;
            if (zones[i].Bounds.Overlaps(adjusted.Bounds))
            {
                throw new InvalidOperationException(
                    $"分区“{adjusted.Name}”不能与“{zones[i].Name}”重叠。 ");
            }
        }

        var updated = zones.ToArray();
        updated[index] = adjusted;
        return Array.AsReadOnly(updated);
    }

    public static IReadOnlyList<DesktopLayoutZone> AddZone(
        IReadOnlyList<DesktopLayoutZone> zones,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(zones);
        if (zones.Count == 0)
            throw new ArgumentException("现有 Zone 列表不能为空。", nameof(zones));

        foreach (var size in CreateZoneSizes)
        {
            for (var top = 0; top <= NormalizedRect.Scale - size; top += CreateScanStep)
            {
                for (var left = 0; left <= NormalizedRect.Scale - size; left += CreateScanStep)
                {
                    var candidateBounds = new NormalizedRect(
                        left,
                        top,
                        left + size,
                        top + size);
                    if (zones.Any(zone => zone.Bounds.Overlaps(candidateBounds)))
                        continue;

                    var sortOrder = zones.Max(zone => zone.SortOrder) + 1;
                    var candidate = new DesktopLayoutZone(
                        Guid.NewGuid(),
                        string.IsNullOrWhiteSpace(name) ? $"分区 {zones.Count + 1}" : name.Trim(),
                        candidateBounds,
                        sortOrder,
                        ZonePlacementFlow.RowMajor,
                        maxItemCount: null);

                    return Array.AsReadOnly(zones.Append(candidate).ToArray());
                }
            }
        }

        throw new InvalidOperationException(
            "当前显示器没有足够的空闲区域创建新分区。可以先缩小或删除现有分区。 ");
    }

    public static IReadOnlyList<DesktopLayoutZone> DeleteZone(
        IReadOnlyList<DesktopLayoutZone> zones,
        DesktopLayoutIconRuleSet ruleSet,
        Guid zoneId)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(ruleSet);
        if (zoneId == Guid.Empty)
            throw new ArgumentException("ZoneId 不能为 Guid.Empty。", nameof(zoneId));

        if (zones.Count <= 1)
            throw new InvalidOperationException("布局至少需要保留一个分区。 ");

        var existing = zones.FirstOrDefault(zone => zone.ZoneId == zoneId)
            ?? throw new InvalidOperationException("要删除的 Zone 不在当前草稿中。 ");

        if (ruleSet.Rules.Any(rule =>
                rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride &&
                rule.TargetZoneId == existing.ZoneId))
        {
            throw new InvalidOperationException(
                $"分区“{existing.Name}”仍被 ZoneOverride 规则引用，不能删除。 ");
        }

        return Array.AsReadOnly(zones
            .Where(zone => zone.ZoneId != zoneId)
            .ToArray());
    }

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
