namespace DesktopAgent.Core.DesktopLayouts.Workspaces;

public enum DesktopDesiredWorkspacePlanIssueCode
{
    UnsupportedSchemaVersion = 0,
    SourceObservationMismatch = 1,
    TopologyFingerprintMismatch = 2,
    GridFingerprintMismatch = 3,
    DuplicateZoneKey = 4,
    UnknownZoneDisplay = 5,
    ZoneOutOfBounds = 6,
    ZoneOverlap = 7,
    DuplicateReservedRegionKey = 8,
    UnknownReservedRegionDisplay = 9,
    ReservedRegionOutOfBounds = 10,
    ReservedRegionOverlap = 11,
    ZoneReservedRegionOverlap = 12,
    DuplicateItemRef = 13,
    MissingItemRef = 14,
    UnknownItemRef = 15,
    FixedItemMoveRequested = 16,
    UnknownTargetDisplay = 17,
    TargetCellOutOfBounds = 18,
    UnknownTargetZone = 19,
    TargetZoneDisplayMismatch = 20,
    TargetCellOutsideZone = 21,
    TargetCellInsideReservedRegion = 22,
    TargetCellCollision = 23,
    ZoneCapacityExceeded = 24,
}

public sealed record DesktopDesiredWorkspacePlanIssue(
    DesktopDesiredWorkspacePlanIssueCode Code,
    string Message,
    string? ItemRef = null,
    string? DisplayRef = null,
    string? ZoneKey = null);

public sealed record DesktopDesiredWorkspacePlanValidationResult
{
    public DesktopDesiredWorkspacePlan Plan { get; }
    public IReadOnlyList<DesktopDesiredWorkspacePlanIssue> Issues { get; }
    public bool IsValid => Issues.Count == 0;

    public DesktopDesiredWorkspacePlanValidationResult(
        DesktopDesiredWorkspacePlan plan,
        IEnumerable<DesktopDesiredWorkspacePlanIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(issues);
        var issueArray = issues.ToArray();
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("Issues 不能包含 null。", nameof(issues));

        Plan = plan;
        Issues = Array.AsReadOnly(issueArray);
    }
}

/// <summary>
/// Desired Workspace Plan 的严格本地校验器。它只返回问题，不修复、移动、补齐或重写 Plan。
/// </summary>
public static class DesktopDesiredWorkspacePlanValidator
{
    public static DesktopDesiredWorkspacePlanValidationResult Validate(
        DesktopObservedLogicalWorkspace observed,
        DesktopDesiredWorkspacePlan plan)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(plan);

        var issues = new List<DesktopDesiredWorkspacePlanIssue>();
        AddPlanIdentityIssues(observed, plan, issues);

        var displaysByRef = observed.Displays.ToDictionary(
            static display => display.DisplayRef,
            StringComparer.Ordinal);
        var observedItemsByRef = observed.Items.ToDictionary(
            static item => item.ItemRef,
            StringComparer.Ordinal);

        var zoneGroups = plan.Zones
            .GroupBy(static zone => zone.ZoneKey, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in zoneGroups.Where(static group => group.Count() > 1))
        {
            Add(
                DesktopDesiredWorkspacePlanIssueCode.DuplicateZoneKey,
                $"ZoneKey \"{group.Key}\" 在 Plan 中出现了 {group.Count()} 次。",
                zoneKey: group.Key);
        }
        var zonesByKey = zoneGroups.ToDictionary(
            static group => group.Key,
            static group => group.First(),
            StringComparer.Ordinal);

        foreach (var zone in plan.Zones)
        {
            if (!displaysByRef.TryGetValue(zone.DisplayRef, out var display))
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.UnknownZoneDisplay,
                    $"Zone \"{zone.ZoneKey}\" 引用了未知显示器 \"{zone.DisplayRef}\"。",
                    displayRef: zone.DisplayRef,
                    zoneKey: zone.ZoneKey);
            }
            else if (!display.GridSize.Contains(zone.Bounds))
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.ZoneOutOfBounds,
                    $"Zone \"{zone.ZoneKey}\" 超出显示器 \"{zone.DisplayRef}\" 的逻辑网格。",
                    displayRef: zone.DisplayRef,
                    zoneKey: zone.ZoneKey);
            }
        }
        AddPairwiseRegionIssues(
            plan.Zones,
            static zone => zone.DisplayRef,
            static zone => zone.Bounds,
            static zone => zone.ZoneKey,
            DesktopDesiredWorkspacePlanIssueCode.ZoneOverlap,
            "Zone",
            issues);

        var reservedGroups = plan.ReservedRegions
            .GroupBy(static region => region.RegionKey, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in reservedGroups.Where(static group => group.Count() > 1))
        {
            Add(
                DesktopDesiredWorkspacePlanIssueCode.DuplicateReservedRegionKey,
                $"RegionKey \"{group.Key}\" 在 Plan 中出现了 {group.Count()} 次。",
                zoneKey: group.Key);
        }
        foreach (var region in plan.ReservedRegions)
        {
            if (!displaysByRef.TryGetValue(region.DisplayRef, out var display))
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.UnknownReservedRegionDisplay,
                    $"保留区域 \"{region.RegionKey}\" 引用了未知显示器 \"{region.DisplayRef}\"。",
                    displayRef: region.DisplayRef,
                    zoneKey: region.RegionKey);
            }
            else if (!display.GridSize.Contains(region.Bounds))
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.ReservedRegionOutOfBounds,
                    $"保留区域 \"{region.RegionKey}\" 超出显示器 \"{region.DisplayRef}\" 的逻辑网格。",
                    displayRef: region.DisplayRef,
                    zoneKey: region.RegionKey);
            }
        }
        AddPairwiseRegionIssues(
            plan.ReservedRegions,
            static region => region.DisplayRef,
            static region => region.Bounds,
            static region => region.RegionKey,
            DesktopDesiredWorkspacePlanIssueCode.ReservedRegionOverlap,
            "保留区域",
            issues);

        foreach (var zone in plan.Zones)
        {
            foreach (var region in plan.ReservedRegions)
            {
                if (string.Equals(zone.DisplayRef, region.DisplayRef, StringComparison.Ordinal) &&
                    zone.Bounds.Overlaps(region.Bounds))
                {
                    Add(
                        DesktopDesiredWorkspacePlanIssueCode.ZoneReservedRegionOverlap,
                        $"Zone \"{zone.ZoneKey}\" 与保留区域 \"{region.RegionKey}\" 重叠。",
                        displayRef: zone.DisplayRef,
                        zoneKey: zone.ZoneKey);
                }
            }
        }

        var itemGroups = plan.Items
            .GroupBy(static item => item.ItemRef, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in itemGroups.Where(static group => group.Count() > 1))
        {
            Add(
                DesktopDesiredWorkspacePlanIssueCode.DuplicateItemRef,
                $"ItemRef \"{group.Key}\" 在 Plan 中出现了 {group.Count()} 次。",
                itemRef: group.Key);
        }
        var desiredItemRefs = itemGroups
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var observedItem in observed.Items.Where(item => !desiredItemRefs.Contains(item.ItemRef)))
        {
            Add(
                DesktopDesiredWorkspacePlanIssueCode.MissingItemRef,
                $"Plan 缺少 ItemRef \"{observedItem.ItemRef}\" 的显式最终状态。",
                itemRef: observedItem.ItemRef);
        }
        foreach (var group in itemGroups.Where(group => !observedItemsByRef.ContainsKey(group.Key)))
        {
            Add(
                DesktopDesiredWorkspacePlanIssueCode.UnknownItemRef,
                $"Plan 引用了未知 ItemRef \"{group.Key}\"。",
                itemRef: group.Key);
        }

        var occupancy = new Dictionary<(string DisplayRef, DesktopLogicalGridCell Cell), List<Occupant>>();
        var placeCountByZone = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in itemGroups.Where(static group => group.Count() == 1))
        {
            var desired = group.Single();
            if (!observedItemsByRef.TryGetValue(desired.ItemRef, out var current))
                continue;

            if (desired.State != DesktopDesiredItemState.Place)
            {
                AddOccupant(current.CurrentDisplayRef, current.CurrentCell, desired.ItemRef, false);
                continue;
            }

            if (!current.IsMovable)
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.FixedItemMoveRequested,
                    $"固定项目 \"{desired.ItemRef}\" 不能进入 Place 状态。",
                    desired.ItemRef,
                    desired.TargetDisplayRef,
                    desired.ZoneKey);
                AddOccupant(current.CurrentDisplayRef, current.CurrentCell, desired.ItemRef, false);
                continue;
            }

            var targetDisplayRef = desired.TargetDisplayRef!;
            var targetCell = desired.TargetCell!.Value;
            var zoneKey = desired.ZoneKey!;
            if (!displaysByRef.TryGetValue(targetDisplayRef, out var targetDisplay))
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.UnknownTargetDisplay,
                    $"ItemRef \"{desired.ItemRef}\" 引用了未知目标显示器 \"{targetDisplayRef}\"。",
                    desired.ItemRef,
                    targetDisplayRef,
                    zoneKey);
            }
            else if (!targetDisplay.GridSize.Contains(targetCell))
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.TargetCellOutOfBounds,
                    $"ItemRef \"{desired.ItemRef}\" 的目标格超出显示器 \"{targetDisplayRef}\"。",
                    desired.ItemRef,
                    targetDisplayRef,
                    zoneKey);
            }

            if (!zonesByKey.TryGetValue(zoneKey, out var zone))
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.UnknownTargetZone,
                    $"ItemRef \"{desired.ItemRef}\" 引用了未知 Zone \"{zoneKey}\"。",
                    desired.ItemRef,
                    targetDisplayRef,
                    zoneKey);
            }
            else
            {
                if (!string.Equals(zone.DisplayRef, targetDisplayRef, StringComparison.Ordinal))
                {
                    Add(
                        DesktopDesiredWorkspacePlanIssueCode.TargetZoneDisplayMismatch,
                        $"ItemRef \"{desired.ItemRef}\" 的目标显示器与 Zone \"{zoneKey}\" 所属显示器不一致。",
                        desired.ItemRef,
                        targetDisplayRef,
                        zoneKey);
                }
                if (!zone.Bounds.Contains(targetCell))
                {
                    Add(
                        DesktopDesiredWorkspacePlanIssueCode.TargetCellOutsideZone,
                        $"ItemRef \"{desired.ItemRef}\" 的目标格不在 Zone \"{zoneKey}\" 内。",
                        desired.ItemRef,
                        targetDisplayRef,
                        zoneKey);
                }
            }

            if (plan.ReservedRegions.Any(region =>
                    string.Equals(region.DisplayRef, targetDisplayRef, StringComparison.Ordinal) &&
                    region.Bounds.Contains(targetCell)))
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.TargetCellInsideReservedRegion,
                    $"ItemRef \"{desired.ItemRef}\" 的目标格位于保留区域内。",
                    desired.ItemRef,
                    targetDisplayRef,
                    zoneKey);
            }

            AddOccupant(targetDisplayRef, targetCell, desired.ItemRef, true);
            placeCountByZone.TryGetValue(zoneKey, out var count);
            placeCountByZone[zoneKey] = count + 1;
        }

        foreach (var pair in occupancy.Where(static pair =>
                     pair.Value.Count > 1 && pair.Value.Any(static occupant => occupant.IsPlaced)))
        {
            var refs = pair.Value
                .Select(static occupant => occupant.ItemRef)
                .OrderBy(static itemRef => itemRef, StringComparer.Ordinal);
            Add(
                DesktopDesiredWorkspacePlanIssueCode.TargetCellCollision,
                $"显示器 \"{pair.Key.DisplayRef}\" 的格位 ({pair.Key.Cell.Column}, {pair.Key.Cell.Row}) 被多个项目占用：{string.Join(", ", refs)}。",
                displayRef: pair.Key.DisplayRef);
        }

        foreach (var pair in placeCountByZone)
        {
            if (zonesByKey.TryGetValue(pair.Key, out var zone) && pair.Value > zone.Bounds.CellCount)
            {
                Add(
                    DesktopDesiredWorkspacePlanIssueCode.ZoneCapacityExceeded,
                    $"Zone \"{pair.Key}\" 只有 {zone.Bounds.CellCount} 个格位，但 Plan 放入了 {pair.Value} 个项目。",
                    displayRef: zone.DisplayRef,
                    zoneKey: pair.Key);
            }
        }

        return new DesktopDesiredWorkspacePlanValidationResult(plan, issues);

        void Add(
            DesktopDesiredWorkspacePlanIssueCode code,
            string message,
            string? itemRef = null,
            string? displayRef = null,
            string? zoneKey = null)
            => issues.Add(new DesktopDesiredWorkspacePlanIssue(code, message, itemRef, displayRef, zoneKey));

        void AddOccupant(
            string displayRef,
            DesktopLogicalGridCell cell,
            string itemRef,
            bool isPlaced)
        {
            var key = (displayRef, cell);
            if (!occupancy.TryGetValue(key, out var occupants))
            {
                occupants = [];
                occupancy.Add(key, occupants);
            }
            occupants.Add(new Occupant(itemRef, isPlaced));
        }
    }

    private static void AddPlanIdentityIssues(
        DesktopObservedLogicalWorkspace observed,
        DesktopDesiredWorkspacePlan plan,
        ICollection<DesktopDesiredWorkspacePlanIssue> issues)
    {
        if (plan.SchemaVersion != DesktopDesiredWorkspacePlan.CurrentSchemaVersion)
            Add(DesktopDesiredWorkspacePlanIssueCode.UnsupportedSchemaVersion,
                $"Plan schema_version={plan.SchemaVersion}，当前仅支持 {DesktopDesiredWorkspacePlan.CurrentSchemaVersion}。");
        if (plan.SourceObservationId != observed.ObservationId)
            Add(DesktopDesiredWorkspacePlanIssueCode.SourceObservationMismatch,
                "Plan 绑定的 Observation 与当前逻辑工作区不一致。");
        if (!string.Equals(plan.TopologyFingerprint, observed.TopologyFingerprint, StringComparison.Ordinal))
            Add(DesktopDesiredWorkspacePlanIssueCode.TopologyFingerprintMismatch,
                "Plan 绑定的显示器拓扑指纹与当前逻辑工作区不一致。");
        if (!string.Equals(plan.GridFingerprint, observed.GridFingerprint, StringComparison.Ordinal))
            Add(DesktopDesiredWorkspacePlanIssueCode.GridFingerprintMismatch,
                "Plan 绑定的网格指纹与当前逻辑工作区不一致。");

        void Add(DesktopDesiredWorkspacePlanIssueCode code, string message)
            => issues.Add(new DesktopDesiredWorkspacePlanIssue(code, message));
    }

    private static void AddPairwiseRegionIssues<T>(
        IReadOnlyList<T> regions,
        Func<T, string> displayRef,
        Func<T, DesktopLogicalGridRect> bounds,
        Func<T, string> key,
        DesktopDesiredWorkspacePlanIssueCode code,
        string label,
        ICollection<DesktopDesiredWorkspacePlanIssue> issues)
    {
        for (var left = 0; left < regions.Count; left++)
        {
            for (var right = left + 1; right < regions.Count; right++)
            {
                if (!string.Equals(displayRef(regions[left]), displayRef(regions[right]), StringComparison.Ordinal) ||
                    !bounds(regions[left]).Overlaps(bounds(regions[right])))
                {
                    continue;
                }

                issues.Add(new DesktopDesiredWorkspacePlanIssue(
                    code,
                    $"{label} \"{key(regions[left])}\" 与 \"{key(regions[right])}\" 重叠。",
                    DisplayRef: displayRef(regions[left]),
                    ZoneKey: key(regions[left])));
            }
        }
    }

    private sealed record Occupant(string ItemRef, bool IsPlaced);
}
