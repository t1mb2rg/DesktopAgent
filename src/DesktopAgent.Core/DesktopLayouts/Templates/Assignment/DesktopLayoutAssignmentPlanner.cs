using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Assignment;

/// <summary>
/// 纯 Core、无状态图标到分区确定性分配规划器。
/// 不生成坐标，不使用 PlacementFlow，不分类图标。
/// </summary>
public static class DesktopLayoutAssignmentPlanner
{
    /// <summary>
    /// 按 Ordinal ASC 顺序将图标依次填充到按 SortOrder ASC 排序的分区。
    /// 每个分区最多填充到其 EffectiveCapacity。
    /// 超过总容量的图标进入 UnassignedIcons。
    /// </summary>
    public static DesktopLayoutAssignmentPlan Plan(
        ResolvedDesktopLayoutTemplate resolvedTemplate,
        IReadOnlyList<DesktopIconSnapshotItem> icons)
    {
        if (resolvedTemplate is null) throw new ArgumentNullException(nameof(resolvedTemplate));
        if (icons is null) throw new ArgumentNullException(nameof(icons));

        // 图标验证并排序
        var canonicalIcons = new List<DesktopIconSnapshotItem>(icons.Count);
        var seenOrdinals = new HashSet<int>();
        var seenStableIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < icons.Count; i++)
        {
            if (icons[i] is null)
                throw new ArgumentException($"Icons[{i}] 为 null。", nameof(icons));
            if (icons[i].Ordinal < 0)
                throw new ArgumentOutOfRangeException(nameof(icons),
                    $"Icons[{i}] Ordinal 为负数 ({icons[i].Ordinal})。");

            if (!seenOrdinals.Add(icons[i].Ordinal))
                throw new ArgumentException(
                    $"Icons Ordinal {icons[i].Ordinal} 重复。", nameof(icons));
            if (!seenStableIds.Add(icons[i].StableId))
                throw new ArgumentException(
                    $"Icons StableId \"{icons[i].StableId}\" 重复。", nameof(icons));

            canonicalIcons.Add(icons[i]);
        }

        // 按 Ordinal ASC 确定性排序
        canonicalIcons.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        // Zone 按 SortOrder ASC 排序
        var zoneOrder = new List<(int Index, ResolvedDesktopLayoutZone Zone)>();
        for (var i = 0; i < resolvedTemplate.Zones.Count; i++)
        {
            zoneOrder.Add((i, resolvedTemplate.Zones[i]));
        }
        zoneOrder.Sort((a, b) => a.Zone.Zone.SortOrder.CompareTo(b.Zone.Zone.SortOrder));

        // 分配
        var cursor = 0;
        var assignments = new List<DesktopLayoutZoneAssignment>(zoneOrder.Count);

        foreach (var (_, zone) in zoneOrder)
        {
            var zoneIcons = new List<DesktopIconSnapshotItem>();
            long remaining = zone.EffectiveCapacity;

            while (cursor < canonicalIcons.Count && (long)zoneIcons.Count < remaining)
            {
                zoneIcons.Add(canonicalIcons[cursor]);
                cursor++;
            }

            assignments.Add(new DesktopLayoutZoneAssignment(zone, zoneIcons));
        }

        // Unassigned
        var unassigned = canonicalIcons.Skip(cursor).ToList();

        return new DesktopLayoutAssignmentPlan(
            resolvedTemplate, canonicalIcons, assignments, unassigned);
    }
}
