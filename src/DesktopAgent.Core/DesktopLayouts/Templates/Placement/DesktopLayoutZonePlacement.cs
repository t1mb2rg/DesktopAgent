using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Placement;

/// <summary>
/// 单个分区的所有图标目标放置位置。
/// Placements 顺序保持 Assignment.Icons 的既有顺序，SlotIndex 严格等于列表索引。
/// </summary>
public sealed record DesktopLayoutZonePlacement
{
    public DesktopLayoutZoneAssignment Assignment { get; }
    public PixelPoint IconSpacing { get; }
    public IReadOnlyList<DesktopIconTargetPlacement> Placements { get; }
    public int PlacementCount { get; }
    public bool IsEmpty { get; }

    public DesktopLayoutZonePlacement(
        DesktopLayoutZoneAssignment assignment,
        PixelPoint iconSpacing,
        IReadOnlyList<DesktopIconTargetPlacement> placements)
    {
        if (assignment is null) throw new ArgumentNullException(nameof(assignment));
        if (iconSpacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.X 必须大于 0，实际 {iconSpacing.X}。");
        if (iconSpacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.Y 必须大于 0，实际 {iconSpacing.Y}。");
        if (placements is null) throw new ArgumentNullException(nameof(placements));

        var placementList = placements.ToList();

        // 无 null 元素
        for (var i = 0; i < placementList.Count; i++)
        {
            if (placementList[i] is null)
                throw new ArgumentException(
                    $"Placements[{i}] 为 null。", nameof(placements));
        }

        // 数量等于 Assignment.Icons.Count
        if (placementList.Count != assignment.Icons.Count)
            throw new ArgumentException(
                $"Placements 数量 ({placementList.Count}) 不等于 Assignment.Icons 数量 ({assignment.Icons.Count})。" +
                $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                nameof(placements));

        // 逐项验证
        var seenPositions = new HashSet<(int X, int Y)>();
        for (var i = 0; i < placementList.Count; i++)
        {
            var p = placementList[i];

            // Zone 必须通过 ReferenceEquals 等于 assignment.Zone
            if (!ReferenceEquals(p.Zone, assignment.Zone))
                throw new ArgumentException(
                    $"Placements[{i}] 的 Zone 与 Assignment.Zone 不是同一实例。" +
                    $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                    nameof(placements));

            // Icon 必须按完整身份逐项匹配 assignment.Icons[i]
            var expectedIcon = assignment.Icons[i];
            if (p.Icon.Ordinal != expectedIcon.Ordinal)
                throw new ArgumentException(
                    $"Placements[{i}] Icon Ordinal ({p.Icon.Ordinal}) 不等于 " +
                    $"Assignment.Icons[{i}] Ordinal ({expectedIcon.Ordinal})。" +
                    $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                    nameof(placements));

            if (!StringComparer.Ordinal.Equals(p.Icon.StableId, expectedIcon.StableId))
                throw new ArgumentException(
                    $"Placements[{i}] Icon StableId \"{p.Icon.StableId}\" 不等于 " +
                    $"Assignment.Icons[{i}] StableId \"{expectedIcon.StableId}\"。" +
                    $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                    nameof(placements));

            // SlotIndex 必须严格等于列表索引 i
            if (p.SlotIndex != i)
                throw new ArgumentException(
                    $"Placements[{i}] SlotIndex ({p.SlotIndex}) 不等于列表索引 ({i})。" +
                    $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                    nameof(placements));

            // IconSpacing 必须等于传入的 iconSpacing
            if (p.IconSpacing != iconSpacing)
                throw new ArgumentException(
                    $"Placements[{i}] IconSpacing ({p.IconSpacing.X},{p.IconSpacing.Y}) " +
                    $"不等于期望 ({iconSpacing.X},{iconSpacing.Y})。" +
                    $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                    nameof(placements));

            // TargetCellBounds 必须完全位于 Zone.PixelBounds
            if (!DesktopIconLayoutGeometry.IsFullyContained(p.TargetCellBounds, assignment.Zone.PixelBounds))
                throw new ArgumentException(
                    $"Placements[{i}] TargetCellBounds " +
                    $"({p.TargetCellBounds.Left},{p.TargetCellBounds.Top})-({p.TargetCellBounds.Right},{p.TargetCellBounds.Bottom}) " +
                    $"不完全位于 Zone.PixelBounds " +
                    $"({assignment.Zone.PixelBounds.Left},{assignment.Zone.PixelBounds.Top})-({assignment.Zone.PixelBounds.Right},{assignment.Zone.PixelBounds.Bottom}) 内。" +
                    $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                    nameof(placements));

            // TargetPosition 不允许重复
            var posKey = (p.TargetPosition.X, p.TargetPosition.Y);
            if (!seenPositions.Add(posKey))
                throw new ArgumentException(
                    $"Placements 中存在重复 TargetPosition ({p.TargetPosition.X},{p.TargetPosition.Y})。" +
                    $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                    nameof(placements));
        }

        // TargetCellBounds 不允许重叠（Zone 内）
        for (var i = 0; i < placementList.Count; i++)
        {
            for (var j = i + 1; j < placementList.Count; j++)
            {
                if (DesktopIconLayoutGeometry.Overlaps(
                        placementList[i].TargetCellBounds, placementList[j].TargetCellBounds))
                    throw new ArgumentException(
                        $"Placements[{i}] 和 Placements[{j}] TargetCellBounds 重叠。" +
                        $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                        nameof(placements));
            }
        }

        // PlacementCount == assignment.AssignedCount
        if (placementList.Count != assignment.AssignedCount)
            throw new ArgumentException(
                $"PlacementCount ({placementList.Count}) 不等于 Assignment.AssignedCount ({assignment.AssignedCount})。" +
                $"ZoneId=\"{assignment.Zone.Zone.ZoneId:D}\"。",
                nameof(placements));

        Assignment = assignment;
        IconSpacing = iconSpacing;
        Placements = placementList.AsReadOnly();
        PlacementCount = placementList.Count;
        IsEmpty = placementList.Count == 0;
    }
}
