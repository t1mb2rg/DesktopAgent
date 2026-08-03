using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 规则感知的布局规划器 — 纯 Core，无副作用。
/// 消费 RuleResolutionPlan 和 ResolvedTemplate，生成包含固定障碍、
/// ZoneOverride 优先分配和默认图标剩余槽位分配的完整 PlacementPlan。
/// </summary>
public static class DesktopLayoutRuleAwarePlacementPlanner
{
    /// <summary>
    /// 执行规则感知的布局规划。
    /// </summary>
    /// <param name="resolvedTemplate">解析后的模板。</param>
    /// <param name="resolutionPlan">M2-03C-A 规则解析结果。</param>
    /// <param name="iconSpacing">图标间距。</param>
    /// <returns>规则感知的完整布局规划。</returns>
    public static DesktopLayoutRuleAwarePlacementPlan Plan(
        ResolvedDesktopLayoutTemplate resolvedTemplate,
        DesktopLayoutIconRuleResolutionPlan resolutionPlan,
        PixelPoint iconSpacing)
    {
        // ═══ 输入验证 ═══
        if (resolvedTemplate is null) throw new ArgumentNullException(nameof(resolvedTemplate));
        if (resolutionPlan is null) throw new ArgumentNullException(nameof(resolutionPlan));
        if (iconSpacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.X 必须大于 0，实际 {iconSpacing.X}。");
        if (iconSpacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.Y 必须大于 0，实际 {iconSpacing.Y}。");

        // 规划链一致性
        if (!ReferenceEquals(resolutionPlan.Template, resolvedTemplate.Template))
            throw new ArgumentException(
                "ResolutionPlan.Template 与 ResolvedTemplate.Template 不是同一实例。",
                nameof(resolutionPlan));
        if (resolutionPlan.RuleSet.TemplateId != resolvedTemplate.Template.TemplateId)
            throw new ArgumentException(
                $"ResolutionPlan.RuleSet.TemplateId ({resolutionPlan.RuleSet.TemplateId}) " +
                $"与 ResolvedTemplate.Template.TemplateId ({resolvedTemplate.Template.TemplateId}) 不一致。",
                nameof(resolutionPlan));

        // ═══ 构建固定障碍 ═══
        var excludedItems = new List<DesktopLayoutRuleAwareFixedItem>();
        var pinnedItems = new List<DesktopLayoutRuleAwareFixedItem>();
        var fixedObstacleRects = new List<PixelRect>();

        foreach (var match in resolutionPlan.ExcludedMatches)
        {
            var item = new DesktopLayoutRuleAwareFixedItem(match.Icon, match.Rule, iconSpacing);
            excludedItems.Add(item);
            fixedObstacleRects.Add(item.FixedRect);
        }

        foreach (var match in resolutionPlan.PinnedMatches)
        {
            var item = new DesktopLayoutRuleAwareFixedItem(match.Icon, match.Rule, iconSpacing);
            pinnedItems.Add(item);
            fixedObstacleRects.Add(item.FixedRect);
        }

        // ═══ Zone 按 SortOrder ASC 排序 ═══
        var zoneOrder = new List<(int Index, ResolvedDesktopLayoutZone Zone)>();
        for (var i = 0; i < resolvedTemplate.Zones.Count; i++)
            zoneOrder.Add((i, resolvedTemplate.Zones[i]));
        zoneOrder.Sort((a, b) => a.Zone.Zone.SortOrder.CompareTo(b.Zone.Zone.SortOrder));

        // ═══ 为每个 Zone 计算可用槽位（过滤固定障碍） ═══
        // zoneAvailableSlots[zoneId] = list of (slotIndex, columnIndex, rowIndex, targetPos, targetCellBounds)
        var zoneAvailableSlots = new Dictionary<Guid, List<AvailableSlot>>();
        foreach (var (_, zone) in zoneOrder)
        {
            var available = ComputeAvailableSlots(zone, iconSpacing, fixedObstacleRects);
            zoneAvailableSlots[zone.Zone.ZoneId] = available;
        }

        // ═══ ZoneOverride 优先分配 ═══
        // 跟踪每个 Zone 已被 Override 消费的槽位
        var zoneOverrideConsumed = new Dictionary<Guid, int>(); // zoneId → consumed count
        foreach (var (_, zone) in zoneOrder)
            zoneOverrideConsumed[zone.Zone.ZoneId] = 0;

        var overrideAssigned = new List<DesktopLayoutRuleAwareAssignedItem>();
        var overrideUnassigned = new List<DesktopLayoutResolvedIconRule>();

        foreach (var match in resolutionPlan.ZoneOverrideMatches)
        {
            var targetZoneId = match.TargetZone!.ZoneId;
            var available = zoneAvailableSlots[targetZoneId];
            var consumed = zoneOverrideConsumed[targetZoneId];

            // 查找该 Zone 的 ResolvedDesktopLayoutZone
            var resolvedZone = FindResolvedZoneById(resolvedTemplate, targetZoneId);

            if (consumed < available.Count && consumed < resolvedZone.EffectiveCapacity)
            {
                var slot = available[consumed];
                var assignedItem = new DesktopLayoutRuleAwareAssignedItem(
                    match.Icon, resolvedZone, iconSpacing,
                    slot.SlotIndex,
                    DesktopLayoutRuleAwareAssignmentSource.Override,
                    match.Rule);
                overrideAssigned.Add(assignedItem);
                zoneOverrideConsumed[targetZoneId] = consumed + 1;
            }
            else
            {
                overrideUnassigned.Add(match);
            }
        }

        // ═══ Default 剩余槽位分配 ═══
        // 跟踪每个 Zone 已被 Default 消费的槽位
        var zoneDefaultConsumed = new Dictionary<Guid, int>(); // zoneId → consumed count
        foreach (var (_, zone) in zoneOrder)
            zoneDefaultConsumed[zone.Zone.ZoneId] = 0;

        var defaultAssigned = new List<DesktopLayoutRuleAwareAssignedItem>();
        var defaultUnassigned = new List<DesktopIconSnapshotItem>();

        var defaultIconIdx = 0;
        var defaultIcons = resolutionPlan.DefaultManagedIcons;

        // 按 Zone SortOrder ASC 遍历
        foreach (var (_, zone) in zoneOrder)
        {
            var zoneId = zone.Zone.ZoneId;
            var available = zoneAvailableSlots[zoneId];

            // Override 已占用的槽位起始索引
            var ovrConsumed = zoneOverrideConsumed[zoneId];

            // 该 Zone 最多可分配的有效槽位（EffectiveCapacity 上限）
            long maxAssignable = zone.EffectiveCapacity;

            // Default 从 Override 后的槽位开始消费
            var defStartSlot = ovrConsumed;

            while (defaultIconIdx < defaultIcons.Count
                   && defStartSlot + zoneDefaultConsumed[zoneId] < available.Count
                   && ovrConsumed + zoneDefaultConsumed[zoneId] < maxAssignable)
            {
                var slot = available[defStartSlot + zoneDefaultConsumed[zoneId]];
                var assignedItem = new DesktopLayoutRuleAwareAssignedItem(
                    defaultIcons[defaultIconIdx], zone, iconSpacing,
                    slot.SlotIndex,
                    DesktopLayoutRuleAwareAssignmentSource.Default,
                    null);
                defaultAssigned.Add(assignedItem);
                zoneDefaultConsumed[zoneId] = zoneDefaultConsumed[zoneId] + 1;
                defaultIconIdx++;
            }
        }

        // 剩余的 DefaultManagedIcons → DefaultUnassigned
        while (defaultIconIdx < defaultIcons.Count)
        {
            defaultUnassigned.Add(defaultIcons[defaultIconIdx]);
            defaultIconIdx++;
        }

        // ═══ 构建结果 ═══
        return new DesktopLayoutRuleAwarePlacementPlan(
            resolvedTemplate, resolutionPlan,
            excludedItems, pinnedItems, fixedObstacleRects,
            overrideAssigned, overrideUnassigned,
            defaultAssigned, defaultUnassigned);
    }

    /// <summary>槽位描述 — 内部结构。</summary>
    private sealed class AvailableSlot
    {
        public int SlotIndex { get; init; }
        public int ColumnIndex { get; init; }
        public int RowIndex { get; init; }
        public PixelPoint TargetPosition { get; init; }
        public PixelRect TargetCellBounds { get; init; }
    }

    /// <summary>
    /// 计算指定 Zone 中所有不被固定障碍阻挡的可用槽位。
    /// 按 SlotIndex ASC（即 PlacementFlow 定义的遍历顺序）枚举。
    /// </summary>
    private static List<AvailableSlot> ComputeAvailableSlots(
        ResolvedDesktopLayoutZone zone,
        PixelPoint iconSpacing,
        IReadOnlyList<PixelRect> fixedObstacleRects)
    {
        var available = new List<AvailableSlot>();
        long total = zone.TheoreticalCapacity;
        var isColumnMajor = zone.Zone.PlacementFlow == ZonePlacementFlow.ColumnMajor;

        for (var slotIdx = 0; (long)slotIdx < total; slotIdx++)
        {
            int col, row;
            if (isColumnMajor)
            {
                col = slotIdx / zone.RowCount;
                row = slotIdx % zone.RowCount;
            }
            else
            {
                row = slotIdx / zone.ColumnCount;
                col = slotIdx % zone.ColumnCount;
            }

            // 计算 TargetPosition（long 中间运算，checked 转换）
            long targetX = (long)zone.PixelBounds.Left + (long)col * iconSpacing.X;
            long targetY = (long)zone.PixelBounds.Top + (long)row * iconSpacing.Y;

            PixelPoint targetPos;
            PixelRect targetRect;
            try
            {
                targetPos = new PixelPoint(checked((int)targetX), checked((int)targetY));
                targetRect = DesktopIconLayoutGeometry.CreateRectChecked(targetPos, iconSpacing);
            }
            catch (OverflowException)
            {
                // 极端坐标溢出 — 跳过该槽位
                continue;
            }

            // 检查是否与固定障碍重叠
            var blocked = false;
            foreach (var fr in fixedObstacleRects)
            {
                if (DesktopIconLayoutGeometry.Overlaps(targetRect, fr))
                {
                    blocked = true;
                    break;
                }
            }

            if (!blocked)
            {
                available.Add(new AvailableSlot
                {
                    SlotIndex = slotIdx,
                    ColumnIndex = col,
                    RowIndex = row,
                    TargetPosition = targetPos,
                    TargetCellBounds = targetRect,
                });
            }
        }

        return available;
    }

    private static ResolvedDesktopLayoutZone FindResolvedZoneById(
        ResolvedDesktopLayoutTemplate resolvedTemplate, Guid zoneId)
    {
        foreach (var z in resolvedTemplate.Zones)
        {
            if (z.Zone.ZoneId == zoneId) return z;
        }
        throw new ArgumentException(
            $"ZoneId \"{zoneId:D}\" 不在 ResolvedTemplate 中。");
    }

}
