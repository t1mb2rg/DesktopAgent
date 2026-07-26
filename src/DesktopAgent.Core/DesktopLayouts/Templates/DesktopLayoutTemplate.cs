namespace DesktopAgent.Core.DesktopLayouts.Templates;

/// <summary>
/// 桌面布局模板 — 一组不重叠分区的完整定义。
/// 构造后不可变，对外集合为只读防御性副本。
/// </summary>
public sealed record DesktopLayoutTemplate
{
    public Guid TemplateId { get; }
    public string Name { get; }
    public int Version { get; }
    public TargetDisplayBinding TargetDisplayBinding { get; }
    public IReadOnlyList<DesktopLayoutZone> Zones { get; }

    public DesktopLayoutTemplate(
        Guid templateId,
        string name,
        int version,
        TargetDisplayBinding targetDisplayBinding,
        IEnumerable<DesktopLayoutZone> zones)
    {
        if (templateId == Guid.Empty)
            throw new ArgumentException(
                "TemplateId 不能为 Guid.Empty。", nameof(templateId));
        if (name is null)
            throw new ArgumentNullException(nameof(name));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Name 不能为 null、空字符串或空白。", nameof(name));
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version), version,
                "Version 必须大于 0。");
        if (targetDisplayBinding is null)
            throw new ArgumentNullException(nameof(targetDisplayBinding));
        if (zones is null)
            throw new ArgumentNullException(nameof(zones));

        // 立即枚举一次并复制
        var zoneList = zones.ToList();

        if (zoneList.Count == 0)
            throw new ArgumentException(
                "Zones 至少需要一个分区。", nameof(zones));

        // 检查 null 元素
        for (var i = 0; i < zoneList.Count; i++)
        {
            if (zoneList[i] is null)
                throw new ArgumentException(
                    $"Zones 索引 {i} 为 null。", nameof(zones));
        }

        // ZoneId 唯一性
        var seenIds = new HashSet<Guid>();
        for (var i = 0; i < zoneList.Count; i++)
        {
            if (!seenIds.Add(zoneList[i].ZoneId))
                throw new ArgumentException(
                    $"ZoneId \"{zoneList[i].ZoneId}\" 重复（索引 {i}）。", nameof(zones));
        }

        // SortOrder 唯一性
        var seenOrders = new HashSet<int>();
        for (var i = 0; i < zoneList.Count; i++)
        {
            if (!seenOrders.Add(zoneList[i].SortOrder))
                throw new ArgumentException(
                    $"SortOrder {zoneList[i].SortOrder} 重复（索引 {i}）。", nameof(zones));
        }

        // 分区不重叠检查（稳定顺序，每对检查一次）
        for (var i = 0; i < zoneList.Count; i++)
        {
            for (var j = i + 1; j < zoneList.Count; j++)
            {
                if (zoneList[i].Bounds.Overlaps(zoneList[j].Bounds))
                    throw new ArgumentException(
                        $"分区 \"{zoneList[i].ZoneId}\" 与 \"{zoneList[j].ZoneId}\" 的 Bounds 重叠。",
                        nameof(zones));
            }
        }

        TemplateId = templateId;
        Name = name;
        Version = version;
        TargetDisplayBinding = targetDisplayBinding;
        Zones = zoneList.AsReadOnly();
    }
}
