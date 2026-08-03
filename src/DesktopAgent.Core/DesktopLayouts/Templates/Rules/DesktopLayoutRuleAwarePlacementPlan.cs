using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 规则感知的完整布局规划结果 — 不可变 sealed class。
/// 包含固定障碍、Override 分配/未分配、Default 分配/未分配，
/// 全部引用身份验证，确定性分区。
/// </summary>
public sealed class DesktopLayoutRuleAwarePlacementPlan
{
    // ═══ 输入引用 ═══
    public ResolvedDesktopLayoutTemplate ResolvedTemplate { get; }
    public DesktopLayoutIconRuleResolutionPlan ResolutionPlan { get; }

    // ═══ 固定障碍 ═══

    /// <summary>Exclude 固定项（按 Icon.Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopLayoutRuleAwareFixedItem> ExcludedItems { get; }

    /// <summary>Pin 固定项（按 Icon.Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopLayoutRuleAwareFixedItem> PinnedItems { get; }

    /// <summary>全部固定项（Excluded + Pinned）。</summary>
    public IReadOnlyList<DesktopLayoutRuleAwareFixedItem> AllFixedItems { get; }

    /// <summary>固定障碍矩形列表。</summary>
    public IReadOnlyList<PixelRect> FixedObstacleRects { get; }

    // ═══ Override 分配 ═══

    /// <summary>ZoneOverride 已分配项（按 Icon.Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopLayoutRuleAwareAssignedItem> OverrideAssignedItems { get; }

    /// <summary>ZoneOverride 未分配项（容量不足，按 Icon.Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopLayoutResolvedIconRule> OverrideUnassignedItems { get; }

    // ═══ Default 分配 ═══

    /// <summary>Default 已分配项（按 Icon.Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopLayoutRuleAwareAssignedItem> DefaultAssignedItems { get; }

    /// <summary>Default 未分配项（容量不足，按 Icon.Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopIconSnapshotItem> DefaultUnassignedItems { get; }

    // ═══ 组合视图 ═══

    /// <summary>全部已分配项（Override + Default）。</summary>
    public IReadOnlyList<DesktopLayoutRuleAwareAssignedItem> AllAssignedItems { get; }

    // ═══ 统计 ═══

    public int ExcludedCount { get; }
    public int PinnedCount { get; }
    public int FixedItemCount { get; }
    public int FixedObstacleRectCount { get; }
    public int OverrideAssignedCount { get; }
    public int OverrideUnassignedCount { get; }
    public int OverrideTotalCount { get; }
    public int DefaultAssignedCount { get; }
    public int DefaultUnassignedCount { get; }
    public int DefaultTotalCount { get; }
    public int TotalAssignedCount { get; }
    public int TotalTargetDisplayIconCount { get; }
    public bool IsComplete { get; }
    public bool HasFixedItems { get; }
    public bool HasOverrideUnassigned { get; }
    public bool HasDefaultUnassigned { get; }

    internal DesktopLayoutRuleAwarePlacementPlan(
        ResolvedDesktopLayoutTemplate resolvedTemplate,
        DesktopLayoutIconRuleResolutionPlan resolutionPlan,
        IReadOnlyList<DesktopLayoutRuleAwareFixedItem> excludedItems,
        IReadOnlyList<DesktopLayoutRuleAwareFixedItem> pinnedItems,
        IReadOnlyList<PixelRect> fixedObstacleRects,
        IReadOnlyList<DesktopLayoutRuleAwareAssignedItem> overrideAssignedItems,
        IReadOnlyList<DesktopLayoutResolvedIconRule> overrideUnassignedItems,
        IReadOnlyList<DesktopLayoutRuleAwareAssignedItem> defaultAssignedItems,
        IReadOnlyList<DesktopIconSnapshotItem> defaultUnassignedItems)
    {
        if (resolvedTemplate is null) throw new ArgumentNullException(nameof(resolvedTemplate));
        if (resolutionPlan is null) throw new ArgumentNullException(nameof(resolutionPlan));
        if (excludedItems is null) throw new ArgumentNullException(nameof(excludedItems));
        if (pinnedItems is null) throw new ArgumentNullException(nameof(pinnedItems));
        if (fixedObstacleRects is null) throw new ArgumentNullException(nameof(fixedObstacleRects));
        if (overrideAssignedItems is null) throw new ArgumentNullException(nameof(overrideAssignedItems));
        if (overrideUnassignedItems is null) throw new ArgumentNullException(nameof(overrideUnassignedItems));
        if (defaultAssignedItems is null) throw new ArgumentNullException(nameof(defaultAssignedItems));
        if (defaultUnassignedItems is null) throw new ArgumentNullException(nameof(defaultUnassignedItems));

        // 防御性复制
        var exclList = excludedItems.ToList();
        var pinList = pinnedItems.ToList();
        var fixedRectsList = fixedObstacleRects.ToList();
        var ovrAssignedList = overrideAssignedItems.ToList();
        var ovrUnassignedList = overrideUnassignedItems.ToList();
        var defAssignedList = defaultAssignedItems.ToList();
        var defUnassignedList = defaultUnassignedItems.ToList();

        // ═══ 规划链一致性 ═══
        if (!ReferenceEquals(resolutionPlan.Template, resolvedTemplate.Template))
            throw new ArgumentException(
                "ResolutionPlan.Template 与 ResolvedTemplate.Template 不是同一实例。", nameof(resolutionPlan));

        // ═══ Fixed 项验证 ═══
        ValidateFixedItems(exclList, DesktopLayoutIconRuleKind.Exclude,
            resolutionPlan.ExcludedMatches, "ExcludedItems");
        ValidateFixedItems(pinList, DesktopLayoutIconRuleKind.Pin,
            resolutionPlan.PinnedMatches, "PinnedItems");

        // FixedObstacleRects 数量与 AllFixedItems 一致
        var allFixed = new List<DesktopLayoutRuleAwareFixedItem>();
        allFixed.AddRange(exclList);
        allFixed.AddRange(pinList);

        if (fixedRectsList.Count != allFixed.Count)
            throw new ArgumentException(
                $"FixedObstacleRects 数量 ({fixedRectsList.Count}) 不等于 FixedItem 总数 ({allFixed.Count})。",
                nameof(fixedObstacleRects));

        for (var i = 0; i < fixedRectsList.Count; i++)
        {
            if (fixedRectsList[i] != allFixed[i].FixedRect)
                throw new ArgumentException(
                    $"FixedObstacleRects[{i}] 与 AllFixedItems[{i}].FixedRect 不一致。",
                    nameof(fixedObstacleRects));
        }

        // ═══ Override Assigned 验证 ═══
        // 构建 ResolvedTemplate Zones 引用集
        var rtZones = new HashSet<ResolvedDesktopLayoutZone>(ReferenceEqualityComparer.Instance);
        foreach (var rz in resolvedTemplate.Zones)
            rtZones.Add(rz);

        foreach (var item in ovrAssignedList)
        {
            if (item is null)
                throw new ArgumentException("OverrideAssignedItems 不能包含 null 元素。", nameof(overrideAssignedItems));
            if (item.Source != DesktopLayoutRuleAwareAssignmentSource.Override)
                throw new ArgumentException(
                    $"OverrideAssignedItems 中 Item Source 必须为 Override，实际 {item.Source}。",
                    nameof(overrideAssignedItems));
            if (item.Rule is null)
                throw new ArgumentException(
                    "OverrideAssignedItems 中 Item Rule 不能为 null。", nameof(overrideAssignedItems));

            // Zone 必须是 ResolvedTemplate.Zones 中的原对象引用
            if (!rtZones.Contains(item.Zone))
                throw new ArgumentException(
                    $"OverrideAssignedItem Zone (ZoneId=\"{item.Zone.Zone.ZoneId:D}\") " +
                    "不是 ResolvedTemplate.Zones 中的原对象引用。",
                    nameof(overrideAssignedItems));

            // Icon 和 Rule 必须对应 ResolutionPlan.ZoneOverrideMatches 中的同一个匹配
            var matchFound = false;
            foreach (var m in resolutionPlan.ZoneOverrideMatches)
            {
                if (ReferenceEquals(m.Rule, item.Rule))
                {
                    matchFound = true;
                    if (!ReferenceEquals(m.Icon, item.Icon))
                        throw new ArgumentException(
                            $"OverrideAssignedItem Icon 与 ZoneOverrideMatch Icon 不是同一实例。" +
                            $" Rule StableId=\"{item.Rule.StableId}\"。",
                            nameof(overrideAssignedItems));
                    // Item.Zone.Zone 必须是该匹配 TargetZone 的原始引用
                    if (!ReferenceEquals(item.Zone.Zone, m.TargetZone))
                        throw new ArgumentException(
                            $"OverrideAssignedItem Zone.Zone 与 ZoneOverrideMatch.TargetZone 不是同一实例。" +
                            $" Rule StableId=\"{item.Rule.StableId}\"。",
                            nameof(overrideAssignedItems));
                    // ZoneId 与 Rule.TargetZoneId 一致
                    if (item.Zone.Zone.ZoneId != item.Rule.TargetZoneId!.Value)
                        throw new ArgumentException(
                            $"OverrideAssignedItem Zone.ZoneId ({item.Zone.Zone.ZoneId:D}) " +
                            $"与 Rule.TargetZoneId ({item.Rule.TargetZoneId}) 不一致。" +
                            $" Rule StableId=\"{item.Rule.StableId}\"。",
                            nameof(overrideAssignedItems));
                    break;
                }
            }
            if (!matchFound)
                throw new ArgumentException(
                    $"OverrideAssignedItem Rule (StableId=\"{item.Rule.StableId}\") " +
                    "不在 ResolutionPlan.ZoneOverrideMatches 中。",
                    nameof(overrideAssignedItems));
        }

        // OverrideAssignedItems 按 Icon.Ordinal ASC
        for (var i = 1; i < ovrAssignedList.Count; i++)
        {
            if (ovrAssignedList[i].Icon.Ordinal <= ovrAssignedList[i - 1].Icon.Ordinal)
                throw new ArgumentException(
                    "OverrideAssignedItems 未按 Icon.Ordinal ASC。", nameof(overrideAssignedItems));
        }

        // ═══ Override Unassigned 验证 ═══
        foreach (var item in ovrUnassignedList)
        {
            if (item is null)
                throw new ArgumentException("OverrideUnassignedItems 不能包含 null 元素。", nameof(overrideUnassignedItems));

            var matchFound = false;
            foreach (var m in resolutionPlan.ZoneOverrideMatches)
            {
                if (ReferenceEquals(m.Rule, item.Rule) && ReferenceEquals(m.Icon, item.Icon))
                { matchFound = true; break; }
            }
            if (!matchFound)
                throw new ArgumentException(
                    $"OverrideUnassigned Rule (StableId=\"{item.Rule.StableId}\") " +
                    "不在 ResolutionPlan.ZoneOverrideMatches 中。",
                    nameof(overrideUnassignedItems));
        }

        // Override Unassigned 与 Assigned 无重叠
        {
            var assignedRuleSet = new HashSet<DesktopLayoutIconRule>(ReferenceEqualityComparer.Instance);
            foreach (var a in ovrAssignedList) assignedRuleSet.Add(a.Rule!);

            foreach (var u in ovrUnassignedList)
            {
                if (assignedRuleSet.Contains(u.Rule))
                    throw new ArgumentException(
                        $"OverrideUnassigned Rule (StableId=\"{u.Rule.StableId}\") " +
                        "同时出现在 OverrideAssigned 中。",
                        nameof(overrideUnassignedItems));
            }
        }

        // OverrideAssigned + OverrideUnassigned = ZoneOverrideMatches（引用完整覆盖）
        {
            var allOverride = new HashSet<DesktopLayoutIconRule>(ReferenceEqualityComparer.Instance);
            foreach (var a in ovrAssignedList) allOverride.Add(a.Rule!);
            foreach (var u in ovrUnassignedList) allOverride.Add(u.Rule);

            if (allOverride.Count != resolutionPlan.ZoneOverrideMatches.Count)
                throw new ArgumentException(
                    $"OverrideAssigned + OverrideUnassigned 规则数 ({allOverride.Count}) " +
                    $"不等于 ResolutionPlan.ZoneOverrideCount ({resolutionPlan.ZoneOverrideCount})。");
        }

        // ═══ Default Assigned 验证 ═══
        foreach (var item in defAssignedList)
        {
            if (item is null)
                throw new ArgumentException("DefaultAssignedItems 不能包含 null 元素。", nameof(defaultAssignedItems));
            if (item.Source != DesktopLayoutRuleAwareAssignmentSource.Default)
                throw new ArgumentException(
                    $"DefaultAssignedItems 中 Item Source 必须为 Default，实际 {item.Source}。",
                    nameof(defaultAssignedItems));
            if (item.Rule is not null)
                throw new ArgumentException(
                    "DefaultAssignedItems 中 Item Rule 必须为 null。", nameof(defaultAssignedItems));

            // Zone 必须是 ResolvedTemplate.Zones 中的原对象引用
            if (!rtZones.Contains(item.Zone))
                throw new ArgumentException(
                    $"DefaultAssignedItem Zone (ZoneId=\"{item.Zone.Zone.ZoneId:D}\") " +
                    "不是 ResolvedTemplate.Zones 中的原对象引用。",
                    nameof(defaultAssignedItems));

            // Icon 必须在 ResolutionPlan.DefaultManagedIcons 中
            var iconFound = false;
            foreach (var dmi in resolutionPlan.DefaultManagedIcons)
            {
                if (ReferenceEquals(dmi, item.Icon)) { iconFound = true; break; }
            }
            if (!iconFound)
                throw new ArgumentException(
                    $"DefaultAssignedItem Icon (Ordinal={item.Icon.Ordinal}, StableId=\"{item.Icon.StableId}\") " +
                    "不在 ResolutionPlan.DefaultManagedIcons 中。",
                    nameof(defaultAssignedItems));
        }

        // DefaultAssignedItems 按 Icon.Ordinal ASC
        for (var i = 1; i < defAssignedList.Count; i++)
        {
            if (defAssignedList[i].Icon.Ordinal <= defAssignedList[i - 1].Icon.Ordinal)
                throw new ArgumentException(
                    "DefaultAssignedItems 未按 Icon.Ordinal ASC。", nameof(defaultAssignedItems));
        }

        // ═══ Default Unassigned 验证 ═══
        foreach (var item in defUnassignedList)
        {
            if (item is null)
                throw new ArgumentException("DefaultUnassignedItems 不能包含 null 元素。", nameof(defaultUnassignedItems));

            var iconFound = false;
            foreach (var dmi in resolutionPlan.DefaultManagedIcons)
            {
                if (ReferenceEquals(dmi, item)) { iconFound = true; break; }
            }
            if (!iconFound)
                throw new ArgumentException(
                    $"DefaultUnassignedItem Icon (Ordinal={item.Ordinal}, StableId=\"{item.StableId}\") " +
                    "不在 ResolutionPlan.DefaultManagedIcons 中。",
                    nameof(defaultUnassignedItems));
        }

        // Default Unassigned 与 Assigned 图标无引用重叠
        {
            var assignedIcons = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
            foreach (var a in defAssignedList) assignedIcons.Add(a.Icon);

            foreach (var u in defUnassignedList)
            {
                if (assignedIcons.Contains(u))
                    throw new ArgumentException(
                        $"DefaultUnassigned Icon (Ordinal={u.Ordinal}) 同时出现在 DefaultAssigned 中。",
                        nameof(defaultUnassignedItems));
            }
        }

        // DefaultAssigned + DefaultUnassigned = DefaultManagedIcons（引用完整覆盖）
        {
            var allDefault = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
            foreach (var a in defAssignedList) allDefault.Add(a.Icon);
            foreach (var u in defUnassignedList) allDefault.Add(u);

            if (allDefault.Count != resolutionPlan.DefaultManagedIconCount)
                throw new ArgumentException(
                    $"DefaultAssigned + DefaultUnassigned 图标数 ({allDefault.Count}) " +
                    $"不等于 ResolutionPlan.DefaultManagedIconCount ({resolutionPlan.DefaultManagedIconCount})。");
        }

        // ═══ 全部 Assigned 中 TargetPosition 唯一 ═══
        var allAssigned = new List<DesktopLayoutRuleAwareAssignedItem>();
        allAssigned.AddRange(ovrAssignedList);
        allAssigned.AddRange(defAssignedList);

        var seenPositions = new HashSet<(int X, int Y)>();
        foreach (var item in allAssigned)
        {
            var posKey = (item.TargetPosition.X, item.TargetPosition.Y);
            if (!seenPositions.Add(posKey))
                throw new ArgumentException(
                    $"发现重复 TargetPosition ({item.TargetPosition.X},{item.TargetPosition.Y})。");
        }

        // AllAssigned 图标无引用重叠（Override 和 Default 之间也不重复）
        {
            var allAssignedIcons = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
            foreach (var a in allAssigned)
            {
                if (!allAssignedIcons.Add(a.Icon))
                    throw new ArgumentException(
                        $"图标 (Ordinal={a.Icon.Ordinal}, StableId=\"{a.Icon.StableId}\") 被多次分配。");
            }
        }

        // ═══ 被固定障碍过滤的槽位绝不出现在 Assigned 中 ═══
        foreach (var item in allAssigned)
        {
            foreach (var fr in fixedRectsList)
            {
                if (DesktopIconLayoutGeometry.Overlaps(item.TargetCellBounds, fr))
                    throw new ArgumentException(
                        $"Assigned Item (Ordinal={item.Icon.Ordinal}) TargetCellBounds 与固定障碍重叠。");
            }
        }

        // ═══ 统计计算 ═══
        int ovrTotal = ovrAssignedList.Count + ovrUnassignedList.Count;
        int defTotal = defAssignedList.Count + defUnassignedList.Count;
        int totalAssigned = ovrAssignedList.Count + defAssignedList.Count;
        int totalTarget = resolutionPlan.TargetDisplayIconCount;
        bool isComplete = ovrUnassignedList.Count == 0 && defUnassignedList.Count == 0;

        // 存储
        ResolvedTemplate = resolvedTemplate;
        ResolutionPlan = resolutionPlan;
        ExcludedItems = exclList.AsReadOnly();
        PinnedItems = pinList.AsReadOnly();
        AllFixedItems = allFixed.AsReadOnly();
        FixedObstacleRects = fixedRectsList.AsReadOnly();
        OverrideAssignedItems = ovrAssignedList.AsReadOnly();
        OverrideUnassignedItems = ovrUnassignedList.AsReadOnly();
        DefaultAssignedItems = defAssignedList.AsReadOnly();
        DefaultUnassignedItems = defUnassignedList.AsReadOnly();
        AllAssignedItems = allAssigned.AsReadOnly();

        ExcludedCount = exclList.Count;
        PinnedCount = pinList.Count;
        FixedItemCount = allFixed.Count;
        FixedObstacleRectCount = fixedRectsList.Count;
        OverrideAssignedCount = ovrAssignedList.Count;
        OverrideUnassignedCount = ovrUnassignedList.Count;
        OverrideTotalCount = ovrTotal;
        DefaultAssignedCount = defAssignedList.Count;
        DefaultUnassignedCount = defUnassignedList.Count;
        DefaultTotalCount = defTotal;
        TotalAssignedCount = totalAssigned;
        TotalTargetDisplayIconCount = totalTarget;
        IsComplete = isComplete;
        HasFixedItems = allFixed.Count > 0;
        HasOverrideUnassigned = ovrUnassignedList.Count > 0;
        HasDefaultUnassigned = defUnassignedList.Count > 0;
    }

    private static void ValidateFixedItems(
        List<DesktopLayoutRuleAwareFixedItem> items,
        DesktopLayoutIconRuleKind expectedKind,
        IReadOnlyList<DesktopLayoutResolvedIconRule> matches,
        string paramName)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null)
                throw new ArgumentException($"{paramName}[{i}] 为 null。", paramName);
            if (items[i].Kind != expectedKind)
                throw new ArgumentException(
                    $"{paramName}[{i}] Kind 应为 {expectedKind}，实际 {items[i].Kind}。", paramName);

            // 规则必须在 matches 中
            var found = false;
            foreach (var m in matches)
            {
                if (ReferenceEquals(m.Rule, items[i].Rule)
                    && ReferenceEquals(m.Icon, items[i].Icon))
                { found = true; break; }
            }
            if (!found)
                throw new ArgumentException(
                    $"{paramName}[{i}] 的 Rule/Icon 不在对应的 Matches 中。" +
                    $" Rule StableId=\"{items[i].Rule.StableId}\"。", paramName);
        }

        // FixedItems 数量等于 Matches 数量
        if (items.Count != matches.Count)
            throw new ArgumentException(
                $"{paramName} 数量 ({items.Count}) 不等于对应 Matches 数量 ({matches.Count})。",
                paramName);

        // 按 Icon.Ordinal ASC
        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].Icon.Ordinal <= items[i - 1].Icon.Ordinal)
                throw new ArgumentException(
                    $"{paramName} 未按 Icon.Ordinal ASC。", paramName);
        }
    }
}
