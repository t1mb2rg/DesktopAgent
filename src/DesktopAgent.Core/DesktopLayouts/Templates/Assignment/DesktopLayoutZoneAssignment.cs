using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Assignment;

/// <summary>
/// 单个分区的图标分配结果。Icons 按 Ordinal ASC 排序。
/// </summary>
public sealed record DesktopLayoutZoneAssignment
{
    public ResolvedDesktopLayoutZone Zone { get; }
    public IReadOnlyList<DesktopIconSnapshotItem> Icons { get; }
    public int AssignedCount { get; }
    public long RemainingCapacity { get; }
    public bool IsEmpty { get; }

    public DesktopLayoutZoneAssignment(
        ResolvedDesktopLayoutZone zone,
        IReadOnlyList<DesktopIconSnapshotItem> icons)
    {
        if (zone is null) throw new ArgumentNullException(nameof(zone));
        if (icons is null) throw new ArgumentNullException(nameof(icons));

        var iconList = icons.ToList();

        if ((long)iconList.Count > zone.EffectiveCapacity)
            throw new ArgumentOutOfRangeException(nameof(icons),
                $"图标数 ({iconList.Count}) 超过 Zone.EffectiveCapacity ({zone.EffectiveCapacity})。ZoneId=\"{zone.Zone.ZoneId:D}\"。");

        // 验证无 null、Ordinal 唯一且升序、StableId 唯一
        int? prevOrdinal = null;
        var seenStableIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < iconList.Count; i++)
        {
            if (iconList[i] is null)
                throw new ArgumentException($"Icons[{i}] 为 null。", nameof(icons));
            if (iconList[i].Ordinal < 0)
                throw new ArgumentOutOfRangeException(nameof(icons),
                    $"Icons[{i}] Ordinal 为负数 ({iconList[i].Ordinal})。");

            if (prevOrdinal.HasValue)
            {
                if (iconList[i].Ordinal <= prevOrdinal.Value)
                    throw new ArgumentException(
                        $"Icons 未按 Ordinal 升序：索引 {i - 1} Ordinal={prevOrdinal}，索引 {i} Ordinal={iconList[i].Ordinal}。",
                        nameof(icons));
            }
            prevOrdinal = iconList[i].Ordinal;

            if (!seenStableIds.Add(iconList[i].StableId))
                throw new ArgumentException(
                    $"Icons StableId \"{iconList[i].StableId}\" 重复。", nameof(icons));
        }

        Zone = zone;
        Icons = iconList.AsReadOnly();
        AssignedCount = iconList.Count;
        RemainingCapacity = zone.EffectiveCapacity - iconList.Count;
        IsEmpty = iconList.Count == 0;
    }
}
