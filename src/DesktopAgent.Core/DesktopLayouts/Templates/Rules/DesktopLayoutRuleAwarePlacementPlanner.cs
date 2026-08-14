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
    public static DesktopLayoutRuleAwarePlacementPlan Plan(
        ResolvedDesktopLayoutTemplate resolvedTemplate,
        DesktopLayoutIconRuleResolutionPlan resolutionPlan,
        PixelPoint iconSpacing)
    {
        if (resolvedTemplate is null) throw new ArgumentNullException(nameof(resolvedTemplate));
        if (resolutionPlan is null) throw new ArgumentNullException(nameof(resolutionPlan));
        if (iconSpacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.X 必须大于 0，实际 {iconSpacing.X}。");
        if (iconSpacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.Y 必须大于 0，实际 {iconSpacing.Y}。");

        if (!ReferenceEquals(resolutionPlan.Template, resolvedTemplate.Template))
            throw new ArgumentException(
                "ResolutionPlan.Template 与 ResolvedTemplate.Template 不是同一实例。",
                nameof(resolutionPlan));
        if (resolutionPlan.RuleSet.TemplateId != resolvedTemplate.Template.TemplateId)
            throw new ArgumentException(
                $"ResolutionPlan.RuleSet.TemplateId ({resolutionPlan.RuleSet.TemplateId}) " +
                $"与 ResolvedTemplate.Template.TemplateId ({resolvedTemplate.Template.TemplateId}) 不一致。",
                nameof(resolutionPlan));

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

        var zoneOrder = new List<(int Index, ResolvedDesktopLayoutZone Zone)>();
        for (var i = 0; i < resolvedTemplate.Zones.Count; i++)
            zoneOrder.Add((i, resolvedTemplate.Zones[i]));
        zoneOrder.Sort((a, b) => a.Zone.Zone.SortOrder.CompareTo(b.Zone.Zone.SortOrder));

        var zoneOverrideCounts = new Dictionary<Guid, int>();
        foreach (var match in resolutionPlan.ZoneOverrideMatches)
        {
            var zid = match.TargetZone!.ZoneId;
            zoneOverrideCounts.TryGetValue(zid, out var c);
            zoneOverrideCounts[zid] = c + 1;
        }

        var zoneAvailableSlots = new Dictionary<Guid, List<AvailableSlot>>();
        foreach (var (_, zone) in zoneOrder)
        {
            var zid = zone.Zone.ZoneId;
            zoneOverrideCounts.TryGetValue(zid, out var ovrCount);

            long maxNeeded = (long)ovrCount + resolutionPlan.DefaultManagedIconCount;
            if (maxNeeded > zone.EffectiveCapacity)
                maxNeeded = zone.EffectiveCapacity;

            List<AvailableSlot> available;
            if (maxNeeded <= 0)
            {
                available = new List<AvailableSlot>(0);
            }
            else
            {
                available = ComputeAvailableSlots(
                    zone, iconSpacing, fixedObstacleRects, maxNeeded);
            }
            zoneAvailableSlots[zid] = available;
        }

        var zoneOverrideConsumed = new Dictionary<Guid, int>();
        foreach (var (_, zone) in zoneOrder)
            zoneOverrideConsumed[zone.Zone.ZoneId] = 0;

        var overrideAssigned = new List<DesktopLayoutRuleAwareAssignedItem>();
        var overrideUnassigned = new List<DesktopLayoutResolvedIconRule>();

        foreach (var match in resolutionPlan.ZoneOverrideMatches)
        {
            var targetZoneId = match.TargetZone!.ZoneId;
            var available = zoneAvailableSlots[targetZoneId];
            var consumed = zoneOverrideConsumed[targetZoneId];
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

        var zoneDefaultConsumed = new Dictionary<Guid, int>();
        foreach (var (_, zone) in zoneOrder)
            zoneDefaultConsumed[zone.Zone.ZoneId] = 0;

        var defaultAssigned = new List<DesktopLayoutRuleAwareAssignedItem>();
        var defaultUnassigned = new List<DesktopIconSnapshotItem>();

        var defaultIconIdx = 0;
        var defaultIcons = resolutionPlan.DefaultManagedIcons;

        foreach (var (_, zone) in zoneOrder)
        {
            var zoneId = zone.Zone.ZoneId;
            var available = zoneAvailableSlots[zoneId];
            var ovrConsumed = zoneOverrideConsumed[zoneId];
            long maxAssignable = zone.EffectiveCapacity;
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

        while (defaultIconIdx < defaultIcons.Count)
        {
            defaultUnassigned.Add(defaultIcons[defaultIconIdx]);
            defaultIconIdx++;
        }

        return new DesktopLayoutRuleAwarePlacementPlan(
            resolvedTemplate, resolutionPlan,
            excludedItems, pinnedItems, fixedObstacleRects,
            overrideAssigned, overrideUnassigned,
            defaultAssigned, defaultUnassigned);
    }

    private sealed class AvailableSlot
    {
        public int SlotIndex { get; init; }
        public int ColumnIndex { get; init; }
        public int RowIndex { get; init; }
        public PixelPoint TargetPosition { get; init; }
        public PixelRect TargetCellBounds { get; init; }
    }

    private static List<AvailableSlot> ComputeAvailableSlots(
        ResolvedDesktopLayoutZone zone,
        PixelPoint iconSpacing,
        IReadOnlyList<PixelRect> fixedObstacleRects,
        long maxNeeded)
    {
        var available = new List<AvailableSlot>();
        if (maxNeeded <= 0) return available;

        long total = zone.TheoreticalCapacity;
        var isColumnMajor = zone.Zone.PlacementFlow == ZonePlacementFlow.ColumnMajor;

        for (long slotIdx = 0; slotIdx < total; slotIdx++)
        {
            long col, row;
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

            if (col >= zone.ColumnCount || row >= zone.RowCount)
                continue;

            long targetX = (long)zone.PlacementOrigin.X + col * iconSpacing.X;
            long targetY = (long)zone.PlacementOrigin.Y + row * iconSpacing.Y;

            PixelPoint targetPos;
            PixelRect targetRect;
            try
            {
                targetPos = new PixelPoint(checked((int)targetX), checked((int)targetY));
                targetRect = DesktopIconLayoutGeometry.CreateRectChecked(targetPos, iconSpacing);
            }
            catch (OverflowException)
            {
                continue;
            }

            // PlacementOrigin 对齐后容量已经按剩余空间重算，这里继续做完整包含检查作为纵深防御。
            if (!DesktopIconLayoutGeometry.IsFullyContained(targetRect, zone.PixelBounds))
                continue;

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
                int intSlotIdx;
                try
                {
                    intSlotIdx = checked((int)slotIdx);
                }
                catch (OverflowException)
                {
                    throw new InvalidOperationException(
                        $"SlotIndex ({slotIdx}) 超出 int 范围，无法表示。ZoneId=\"{zone.Zone.ZoneId:D}\"。");
                }

                available.Add(new AvailableSlot
                {
                    SlotIndex = intSlotIdx,
                    ColumnIndex = checked((int)col),
                    RowIndex = checked((int)row),
                    TargetPosition = targetPos,
                    TargetCellBounds = targetRect,
                });

                if ((long)available.Count >= maxNeeded)
                    break;
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
