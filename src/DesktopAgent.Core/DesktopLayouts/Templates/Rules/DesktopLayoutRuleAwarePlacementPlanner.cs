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
        PixelPoint iconSpacing,
        bool preserveExistingGridSlots = false)
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

        // GetSpacing 只告诉我们网格步长，不告诉全局相位。若目标屏当前图标的位置余数
        // 对 X/Y 都形成强多数，则用这次 fresh 事实重新解析 PlacementOrigin；
        // 余数分散时不猜，保持调用方提供的 legacy Zone-local resolvedTemplate。
        var gridInference = DesktopExplorerGridPhaseResolver.TryInfer(
            resolutionPlan.TargetDisplayIcons,
            iconSpacing);
        if (gridInference is not null)
        {
            resolvedTemplate = DesktopLayoutTemplateResolver.Resolve(
                resolvedTemplate.Template,
                resolvedTemplate.WorkArea,
                iconSpacing,
                gridInference.Phase);
        }

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

        // P-1 可选的稳定槽位候选。默认仍保持 legacy 顺序语义，直到
        // 产品预览/自动建议共享路径与真实执行验收完成后再启用。
        if (preserveExistingGridSlots)
        {
            return PlanPreservingExistingGridSlots(
                resolvedTemplate, resolutionPlan, iconSpacing,
                excludedItems, pinnedItems, fixedObstacleRects,
                zoneOrder, zoneAvailableSlots);
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

    /// <summary>
    /// P-1 稳定槽位候选：先落实所有显式 ZoneOverride，再落实默认图标。
    /// 默认图标仍按原 Zone 顺序与容量分桶；仅在所属 Zone 内优先保留合法原生槽位。
    /// 不能让“少挪动”越过显式 Rule、固定障碍、跨屏归属或最大容量。
    /// </summary>
    private static DesktopLayoutRuleAwarePlacementPlan PlanPreservingExistingGridSlots(
        ResolvedDesktopLayoutTemplate resolvedTemplate,
        DesktopLayoutIconRuleResolutionPlan resolutionPlan,
        PixelPoint iconSpacing,
        IReadOnlyList<DesktopLayoutRuleAwareFixedItem> excludedItems,
        IReadOnlyList<DesktopLayoutRuleAwareFixedItem> pinnedItems,
        IReadOnlyList<PixelRect> fixedObstacleRects,
        IReadOnlyList<(int Index, ResolvedDesktopLayoutZone Zone)> zoneOrder,
        IReadOnlyDictionary<Guid, List<AvailableSlot>> zoneAvailableSlots)
    {
        var usedSlots = zoneOrder.ToDictionary(
            entry => entry.Zone.Zone.ZoneId,
            _ => new HashSet<int>());
        var overrideAssignedById = new Dictionary<string, DesktopLayoutRuleAwareAssignedItem>(
            StringComparer.Ordinal);
        var targetDisplayName = resolutionPlan.TargetDisplay.DeviceName;

        // 显式 ZoneOverride 在默认项目之前占位：同一格存在竞争时，Ordinal 小者优先。
        foreach (var match in resolutionPlan.ZoneOverrideMatches)
        {
            var zoneId = match.TargetZone!.ZoneId;
            var zone = FindResolvedZoneById(resolvedTemplate, zoneId);
            var used = usedSlots[zoneId];
            if (used.Count >= zone.EffectiveCapacity ||
                !TryGetLegalCurrentSlot(
                    match.Icon, zone, targetDisplayName, iconSpacing, fixedObstacleRects,
                    out var slot) ||
                !used.Add(slot))
            {
                continue;
            }

            overrideAssignedById.Add(match.Icon.StableId, new DesktopLayoutRuleAwareAssignedItem(
                match.Icon, zone, iconSpacing, slot,
                DesktopLayoutRuleAwareAssignmentSource.Override, match.Rule));
        }

        var overrideUnassigned = new List<DesktopLayoutResolvedIconRule>();
        foreach (var match in resolutionPlan.ZoneOverrideMatches)
        {
            if (overrideAssignedById.ContainsKey(match.Icon.StableId))
                continue;

            var zoneId = match.TargetZone!.ZoneId;
            var zone = FindResolvedZoneById(resolvedTemplate, zoneId);
            var used = usedSlots[zoneId];
            var next = used.Count < zone.EffectiveCapacity
                ? zoneAvailableSlots[zoneId].FirstOrDefault(slot => !used.Contains(slot.SlotIndex))
                : null;
            if (next is null)
            {
                overrideUnassigned.Add(match);
                continue;
            }

            used.Add(next.SlotIndex);
            overrideAssignedById.Add(match.Icon.StableId, new DesktopLayoutRuleAwareAssignedItem(
                match.Icon, zone, iconSpacing, next.SlotIndex,
                DesktopLayoutRuleAwareAssignmentSource.Override, match.Rule));
        }

        var overrideAssigned = resolutionPlan.ZoneOverrideMatches
            .Where(match => overrideAssignedById.ContainsKey(match.Icon.StableId))
            .Select(match => overrideAssignedById[match.Icon.StableId])
            .ToArray();

        // 分区归属仍与 legacy 一致：先考虑 ZoneOverride 占用，再按 Zone.SortOrder
        // 为默认图标分桶。保留原位只决定桶内槽位，不偷换用户规定的区顺序。
        var defaultsByZone = zoneOrder.ToDictionary(
            entry => entry.Zone.Zone.ZoneId,
            _ => new List<DesktopIconSnapshotItem>());
        var defaultIcons = resolutionPlan.DefaultManagedIcons;
        var nextDefault = 0;
        foreach (var (_, zone) in zoneOrder)
        {
            var id = zone.Zone.ZoneId;
            var occupied = usedSlots[id];
            var available = zoneAvailableSlots[id];
            var freeAvailable = available.Count(slot => !occupied.Contains(slot.SlotIndex));
            var zoneBudget = Math.Min(
                (long)freeAvailable,
                zone.EffectiveCapacity - occupied.Count);
            while (zoneBudget > 0 && nextDefault < defaultIcons.Count)
            {
                defaultsByZone[id].Add(defaultIcons[nextDefault++]);
                zoneBudget--;
            }
        }

        var assignedDefaultsById = new Dictionary<string, DesktopLayoutRuleAwareAssignedItem>(
            StringComparer.Ordinal);
        var defaultUnassigned = new List<DesktopIconSnapshotItem>();

        foreach (var (_, zone) in zoneOrder)
        {
            var id = zone.Zone.ZoneId;
            var group = defaultsByZone[id];
            var used = usedSlots[id];

            foreach (var icon in group)
            {
                if (!TryGetLegalCurrentSlot(
                        icon, zone, targetDisplayName, iconSpacing, fixedObstacleRects,
                        out var slot) ||
                    !used.Add(slot))
                {
                    continue;
                }

                assignedDefaultsById.Add(icon.StableId, new DesktopLayoutRuleAwareAssignedItem(
                    icon, zone, iconSpacing, slot,
                    DesktopLayoutRuleAwareAssignmentSource.Default, null));
            }

            foreach (var icon in group)
            {
                if (assignedDefaultsById.ContainsKey(icon.StableId))
                    continue;

                var next = zoneAvailableSlots[id].FirstOrDefault(
                    slot => !used.Contains(slot.SlotIndex));
                if (next is null)
                {
                    defaultUnassigned.Add(icon);
                    continue;
                }

                used.Add(next.SlotIndex);
                assignedDefaultsById.Add(icon.StableId, new DesktopLayoutRuleAwareAssignedItem(
                    icon, zone, iconSpacing, next.SlotIndex,
                    DesktopLayoutRuleAwareAssignmentSource.Default, null));
            }
        }

        for (; nextDefault < defaultIcons.Count; nextDefault++)
            defaultUnassigned.Add(defaultIcons[nextDefault]);

        var defaultAssigned = defaultIcons
            .Where(icon => assignedDefaultsById.ContainsKey(icon.StableId))
            .Select(icon => assignedDefaultsById[icon.StableId])
            .ToArray();
        defaultUnassigned.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        return new DesktopLayoutRuleAwarePlacementPlan(
            resolvedTemplate, resolutionPlan, excludedItems.ToArray(), pinnedItems.ToArray(),
            fixedObstacleRects.ToArray(), overrideAssigned, overrideUnassigned,
            defaultAssigned, defaultUnassigned);
    }

    private static bool TryGetLegalCurrentSlot(
        DesktopIconSnapshotItem icon,
        ResolvedDesktopLayoutZone zone,
        string targetDisplayName,
        PixelPoint spacing,
        IReadOnlyList<PixelRect> fixedObstacleRects,
        out int slotIndex)
    {
        slotIndex = -1;
        if (!string.Equals(icon.MonitorDeviceName, targetDisplayName, StringComparison.Ordinal))
            return false;

        var dx = (long)icon.PixelPosition.X - zone.PlacementOrigin.X;
        var dy = (long)icon.PixelPosition.Y - zone.PlacementOrigin.Y;
        if (dx < 0 || dy < 0 || dx % spacing.X != 0 || dy % spacing.Y != 0)
            return false;

        var col = dx / spacing.X;
        var row = dy / spacing.Y;
        if (col >= zone.ColumnCount || row >= zone.RowCount)
            return false;

        var index = zone.Zone.PlacementFlow == ZonePlacementFlow.ColumnMajor
            ? col * zone.RowCount + row
            : row * zone.ColumnCount + col;
        if (index < 0 || index > int.MaxValue)
            return false;

        PixelRect targetCell;
        try
        {
            targetCell = DesktopIconLayoutGeometry.CreateRectChecked(icon.PixelPosition, spacing);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (!DesktopIconLayoutGeometry.IsFullyContained(targetCell, zone.PixelBounds) ||
            fixedObstacleRects.Any(obstacle => DesktopIconLayoutGeometry.Overlaps(targetCell, obstacle)))
            return false;

        slotIndex = (int)index;
        return true;
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
