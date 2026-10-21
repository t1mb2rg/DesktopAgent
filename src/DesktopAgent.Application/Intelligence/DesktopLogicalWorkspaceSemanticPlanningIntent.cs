namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 第一阶段 fresh 桌面理解对第二阶段精确逻辑规划保留下来的 Provider-safe Zone 意图。
/// 只包含请求内语义 key / name / category key，不包含 StableId、设备名、像素或 Writer authority。
/// </summary>
public sealed record DesktopLogicalWorkspaceSemanticZoneIntent
{
    private readonly IReadOnlyList<string> _categoryKeys;

    public string ZoneKey { get; }
    public string Name { get; }
    public IReadOnlyList<string> CategoryKeys => _categoryKeys;

    public DesktopLogicalWorkspaceSemanticZoneIntent(
        string zoneKey,
        string name,
        IEnumerable<string>? categoryKeys = null)
    {
        if (string.IsNullOrWhiteSpace(zoneKey))
            throw new ArgumentException("ZoneKey 不能为空。", nameof(zoneKey));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));

        var keys = categoryKeys?.Select(static key => key.Trim()).ToArray() ?? [];
        if (keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("CategoryKeys 不能包含空白。", nameof(categoryKeys));
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new ArgumentException("CategoryKeys 不能包含重复值。", nameof(categoryKeys));

        ZoneKey = zoneKey.Trim();
        Name = name.Trim();
        _categoryKeys = Array.AsReadOnly(keys);
    }
}

/// <summary>
/// 一个请求内 item_ref 在第一阶段理解中已经得到的语义 Zone 归属。
/// </summary>
public sealed record DesktopLogicalWorkspaceSemanticItemIntent
{
    public string ItemRef { get; }
    public string ZoneKey { get; }

    public DesktopLogicalWorkspaceSemanticItemIntent(string itemRef, string zoneKey)
    {
        if (string.IsNullOrWhiteSpace(itemRef))
            throw new ArgumentException("ItemRef 不能为空。", nameof(itemRef));
        if (string.IsNullOrWhiteSpace(zoneKey))
            throw new ArgumentException("ZoneKey 不能为空。", nameof(zoneKey));

        ItemRef = itemRef.Trim();
        ZoneKey = zoneKey.Trim();
    }
}

/// <summary>
/// 同一次 fresh Semantic Understanding 投影出的精确规划约束。
/// 第二阶段 AI 仍决定 Zone 的显示器、几何与每个项目最终 Cell，
/// 但不能静默丢弃第一阶段已经建立的语义分区结构。
/// </summary>
public sealed record DesktopLogicalWorkspaceSemanticPlanningIntent
{
    private readonly IReadOnlyList<DesktopLogicalWorkspaceSemanticZoneIntent> _zones;
    private readonly IReadOnlyList<DesktopLogicalWorkspaceSemanticItemIntent> _items;

    public IReadOnlyList<DesktopLogicalWorkspaceSemanticZoneIntent> Zones => _zones;
    public IReadOnlyList<DesktopLogicalWorkspaceSemanticItemIntent> Items => _items;

    public DesktopLogicalWorkspaceSemanticPlanningIntent(
        IEnumerable<DesktopLogicalWorkspaceSemanticZoneIntent>? zones,
        IEnumerable<DesktopLogicalWorkspaceSemanticItemIntent>? items)
    {
        var zoneArray = zones?.ToArray() ?? [];
        var itemArray = items?.ToArray() ?? [];
        if (zoneArray.Any(static zone => zone is null))
            throw new ArgumentException("Zones 不能包含 null。", nameof(zones));
        if (itemArray.Any(static item => item is null))
            throw new ArgumentException("Items 不能包含 null。", nameof(items));
        if (zoneArray.Select(static zone => zone.ZoneKey).Distinct(StringComparer.Ordinal).Count() != zoneArray.Length)
            throw new ArgumentException("Zones 不能包含重复 ZoneKey。", nameof(zones));
        if (itemArray.Select(static item => item.ItemRef).Distinct(StringComparer.Ordinal).Count() != itemArray.Length)
            throw new ArgumentException("Items 不能包含重复 ItemRef。", nameof(items));

        var zoneKeys = zoneArray.Select(static zone => zone.ZoneKey).ToHashSet(StringComparer.Ordinal);
        if (itemArray.Any(item => !zoneKeys.Contains(item.ZoneKey)))
            throw new ArgumentException("Item intent 引用了不存在的 ZoneKey。", nameof(items));

        _zones = Array.AsReadOnly(zoneArray);
        _items = Array.AsReadOnly(itemArray);
    }
}
