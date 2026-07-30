using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Placement;

/// <summary>
/// 纯 Core、无状态图标目标坐标规划器。
/// 输入 M2-02B 的 AssignmentPlan，为每个已分配图标生成网格单元左上角坐标。
/// 不处理碰撞、不分类图标、不读取原 PixelPosition。
/// </summary>
public static class DesktopLayoutCoordinatePlanner
{
    /// <summary>
    /// 为 AssignmentPlan 中每个已分配图标生成目标放置坐标。
    /// 每个 Zone 从 SlotIndex 0 开始，按 Icons 顺序依次排列。
    /// 只枚举实际分配的图标，不预生成空槽位或理论容量。
    /// </summary>
    public static DesktopLayoutPlacementPlan Plan(
        DesktopLayoutAssignmentPlan assignmentPlan)
    {
        if (assignmentPlan is null)
            throw new ArgumentNullException(nameof(assignmentPlan));

        var spacing = assignmentPlan.ResolvedTemplate.IconSpacing;
        var zonePlacements = new List<DesktopLayoutZonePlacement>(
            assignmentPlan.ZoneAssignments.Count);

        foreach (var assignment in assignmentPlan.ZoneAssignments)
        {
            var placements = new List<DesktopIconTargetPlacement>(
                assignment.Icons.Count);

            for (var i = 0; i < assignment.Icons.Count; i++)
            {
                var placement = new DesktopIconTargetPlacement(
                    assignment.Icons[i],
                    assignment.Zone,
                    spacing,
                    i);

                placements.Add(placement);
            }

            zonePlacements.Add(
                new DesktopLayoutZonePlacement(assignment, spacing, placements));
        }

        return new DesktopLayoutPlacementPlan(assignmentPlan, zonePlacements);
    }
}
