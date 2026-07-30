using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Placement;

/// <summary>
/// 完整的分区坐标规划结果。
/// 包含所有已分配图标的目标坐标、Zone 划分、以及未分配图标。
/// 允许不完整计划（IsComplete == false），此时已分配图标仍生成合法坐标。
/// </summary>
public sealed record DesktopLayoutPlacementPlan
{
    public DesktopLayoutAssignmentPlan AssignmentPlan { get; }

    public IReadOnlyList<DesktopLayoutZonePlacement> ZonePlacements { get; }
    public IReadOnlyList<DesktopIconTargetPlacement> Placements { get; }
    public IReadOnlyList<DesktopIconSnapshotItem> UnassignedIcons { get; }

    public int PlacementCount { get; }
    public int UnassignedCount { get; }
    public bool IsComplete { get; }

    public DesktopLayoutPlacementPlan(
        DesktopLayoutAssignmentPlan assignmentPlan,
        IReadOnlyList<DesktopLayoutZonePlacement> zonePlacements)
    {
        if (assignmentPlan is null) throw new ArgumentNullException(nameof(assignmentPlan));
        if (zonePlacements is null) throw new ArgumentNullException(nameof(zonePlacements));

        var zpList = zonePlacements.ToList();

        // 无 null 元素
        for (var i = 0; i < zpList.Count; i++)
        {
            if (zpList[i] is null)
                throw new ArgumentException($"ZonePlacements[{i}] 为 null。", nameof(zonePlacements));
        }

        // ZonePlacements.Count == AssignmentPlan.ZoneAssignments.Count
        if (zpList.Count != assignmentPlan.ZoneAssignments.Count)
            throw new ArgumentException(
                $"ZonePlacements 数量 ({zpList.Count}) 不等于 " +
                $"AssignmentPlan.ZoneAssignments 数量 ({assignmentPlan.ZoneAssignments.Count})。",
                nameof(zonePlacements));

        var allPlacements = new List<DesktopIconTargetPlacement>();
        var seenPositions = new HashSet<(int X, int Y)>();

        for (var i = 0; i < zpList.Count; i++)
        {
            var zp = zpList[i];

            // 每个 ZonePlacement.Assignment 必须 ReferenceEquals AssignmentPlan.ZoneAssignments[i]
            if (!ReferenceEquals(zp.Assignment, assignmentPlan.ZoneAssignments[i]))
                throw new ArgumentException(
                    $"ZonePlacements[{i}].Assignment 与 " +
                    $"AssignmentPlan.ZoneAssignments[{i}] 不是同一实例。",
                    nameof(zonePlacements));

            // IconSpacing 必须等于 assignmentPlan.ResolvedTemplate.IconSpacing
            if (zp.IconSpacing != assignmentPlan.ResolvedTemplate.IconSpacing)
                throw new ArgumentException(
                    $"ZonePlacements[{i}] IconSpacing ({zp.IconSpacing.X},{zp.IconSpacing.Y}) " +
                    $"不等于 ResolvedTemplate.IconSpacing " +
                    $"({assignmentPlan.ResolvedTemplate.IconSpacing.X},{assignmentPlan.ResolvedTemplate.IconSpacing.Y})。",
                    nameof(zonePlacements));

            // 收集所有 Placement
            for (var j = 0; j < zp.Placements.Count; j++)
            {
                var p = zp.Placements[j];

                // TargetCellBounds 必须完整位于 WorkArea
                if (!DesktopIconLayoutGeometry.IsFullyContained(
                        p.TargetCellBounds, assignmentPlan.ResolvedTemplate.WorkArea))
                    throw new ArgumentException(
                        $"ZonePlacements[{i}].Placements[{j}] TargetCellBounds " +
                        $"({p.TargetCellBounds.Left},{p.TargetCellBounds.Top})-({p.TargetCellBounds.Right},{p.TargetCellBounds.Bottom}) " +
                        $"不完全位于 WorkArea " +
                        $"({assignmentPlan.ResolvedTemplate.WorkArea.Left},{assignmentPlan.ResolvedTemplate.WorkArea.Top})-({assignmentPlan.ResolvedTemplate.WorkArea.Right},{assignmentPlan.ResolvedTemplate.WorkArea.Bottom}) 内。",
                        nameof(zonePlacements));

                // TargetPosition 全局唯一
                var posKey = (p.TargetPosition.X, p.TargetPosition.Y);
                if (!seenPositions.Add(posKey))
                    throw new ArgumentException(
                        $"发现重复 TargetPosition ({p.TargetPosition.X},{p.TargetPosition.Y})。" +
                        $"来源：ZonePlacements[{i}].Placements[{j}]。",
                        nameof(zonePlacements));

                allPlacements.Add(p);
            }
        }

        // 拼接后的数量必须等于 AssignmentPlan.AssignedCount
        if (allPlacements.Count != assignmentPlan.AssignedCount)
            throw new ArgumentException(
                $"Flattened Placements 数量 ({allPlacements.Count}) 不等于 " +
                $"AssignmentPlan.AssignedCount ({assignmentPlan.AssignedCount})。",
                nameof(zonePlacements));

        // 拼接后的图标顺序逐项等于 AssignmentPlan 中所有 ZoneAssignments.Icons 的拼接顺序
        {
            var flatIdx = 0;
            for (var zi = 0; zi < assignmentPlan.ZoneAssignments.Count; zi++)
            {
                for (var ii = 0; ii < assignmentPlan.ZoneAssignments[zi].Icons.Count; ii++)
                {
                    var expected = assignmentPlan.ZoneAssignments[zi].Icons[ii];
                    var actual = allPlacements[flatIdx];

                    if (actual.Icon.Ordinal != expected.Ordinal)
                        throw new ArgumentException(
                            $"Flattened Placements[{flatIdx}] Icon Ordinal ({actual.Icon.Ordinal}) " +
                            $"不等于预期 ({expected.Ordinal})。预期来自 ZoneAssignments[{zi}].Icons[{ii}]。",
                            nameof(zonePlacements));

                    if (!StringComparer.Ordinal.Equals(actual.Icon.StableId, expected.StableId))
                        throw new ArgumentException(
                            $"Flattened Placements[{flatIdx}] Icon StableId \"{actual.Icon.StableId}\" " +
                            $"不等于预期 \"{expected.StableId}\"。预期来自 ZoneAssignments[{zi}].Icons[{ii}]。",
                            nameof(zonePlacements));

                    flatIdx++;
                }
            }
        }

        // UnassignedIcons 防御性复制自 AssignmentPlan
        var unassignedCopy = assignmentPlan.UnassignedIcons.ToList();

        AssignmentPlan = assignmentPlan;
        ZonePlacements = zpList.AsReadOnly();
        Placements = allPlacements.AsReadOnly();
        UnassignedIcons = unassignedCopy.AsReadOnly();
        PlacementCount = allPlacements.Count;
        UnassignedCount = unassignedCopy.Count;
        IsComplete = assignmentPlan.IsComplete;
    }
}
