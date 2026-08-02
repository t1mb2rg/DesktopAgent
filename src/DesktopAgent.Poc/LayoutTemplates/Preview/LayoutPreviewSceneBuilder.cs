using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

/// <summary>
/// 从 PreviewPlan 和 CanvasTransform 构建只读 Scene 模型。
/// </summary>
public static class LayoutPreviewSceneBuilder
{
    public static LayoutPreviewScene Build(
        DesktopLayoutPreviewPlan plan,
        LayoutPreviewCanvasTransform transform)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (transform is null) throw new ArgumentNullException(nameof(transform));

        var zoneVisuals = new List<LayoutPreviewZoneVisual>();
        // 按 SortOrder ASC
        var zones = plan.ResolvedTemplate.Zones
            .OrderBy(z => z.Zone.SortOrder)
            .ToList();

        var zoneAssignedCounts = new Dictionary<Guid, int>();
        foreach (var item in plan.Items)
        {
            if (item.IsAssigned && item.Placement is not null)
            {
                var zid = item.Placement.Zone.Zone.ZoneId;
                zoneAssignedCounts.TryGetValue(zid, out var c);
                zoneAssignedCounts[zid] = c + 1;
            }
        }

        foreach (var rz in zones)
        {
            var pb = rz.PixelBounds;
            var (cl, ct, cw, ch) = transform.Map(pb);
            zoneAssignedCounts.TryGetValue(rz.Zone.ZoneId, out var assigned);
            zoneVisuals.Add(new LayoutPreviewZoneVisual
            {
                ZoneId = rz.Zone.ZoneId,
                Name = rz.Zone.Name,
                SortOrder = rz.Zone.SortOrder,
                PixelLeft = pb.Left, PixelTop = pb.Top,
                PixelRight = pb.Right, PixelBottom = pb.Bottom,
                CanvasLeft = cl, CanvasTop = ct,
                CanvasWidth = cw, CanvasHeight = ch,
                EffectiveCapacity = rz.EffectiveCapacity,
                AssignedCount = assigned,
                PlacementFlow = rz.Zone.PlacementFlow.ToString()
            });
        }

        var iconVisuals = new List<LayoutPreviewIconVisual>();
        foreach (var item in plan.Items)
        {
            var (cx, cy) = transform.Map(item.CurrentPosition);
            var visual = new LayoutPreviewIconVisual
            {
                Icon = item.Icon,
                State = item.State,
                CurrentCanvasX = cx,
                CurrentCanvasY = cy,
                ToolTip = BuildToolTip(item)
            };

            if (item.IsAssigned && item.Placement is not null)
            {
                var (tx, ty) = transform.Map(item.TargetPosition!.Value);
                visual = visual with
                {
                    TargetCanvasX = tx,
                    TargetCanvasY = ty,
                    ZoneId = item.Placement.Zone.Zone.ZoneId,
                    ZoneName = item.Placement.Zone.Zone.Name
                };
            }
            else
            {
                visual = visual with
                {
                    TargetCanvasX = double.NaN,
                    TargetCanvasY = double.NaN
                };
            }

            iconVisuals.Add(visual);
        }

        var itemRows = plan.Items
            .Select(LayoutPreviewItemRow.From)
            .ToList()
            .AsReadOnly();

        var oosRows = plan.OutOfScopeIcons
            .Select(LayoutPreviewOutOfScopeRow.From)
            .ToList()
            .AsReadOnly();

        return new LayoutPreviewScene
        {
            Plan = plan,
            Transform = transform,
            Zones = zoneVisuals.AsReadOnly(),
            Icons = iconVisuals.AsReadOnly(),
            ItemRows = itemRows,
            OutOfScopeRows = oosRows
        };
    }

    private static string BuildToolTip(DesktopLayoutPreviewItem item)
    {
        var parts = new List<string>
        {
            item.Icon.DisplayName,
            $"StableId: {item.Icon.StableId}",
            $"State: {StateToString(item.State)}",
            $"Current: ({item.CurrentPosition.X},{item.CurrentPosition.Y})"
        };

        if (item.IsAssigned && item.Placement is not null)
        {
            parts.Add($"Target: ({item.TargetPosition!.Value.X},{item.TargetPosition!.Value.Y})");
            parts.Add($"Delta: ({item.DeltaX},{item.DeltaY})");
            parts.Add($"Zone: {item.Placement.Zone.Zone.Name}");
            parts.Add($"Slot: {item.Placement.SlotIndex} Col:{item.Placement.ColumnIndex} Row:{item.Placement.RowIndex}");
        }

        return string.Join("\n", parts);
    }

    private static string StateToString(DesktopLayoutPreviewItemState state) => state switch
    {
        DesktopLayoutPreviewItemState.AlreadyAtTarget => "已就位",
        DesktopLayoutPreviewItemState.MoveRequired => "需要移动",
        DesktopLayoutPreviewItemState.Unassigned => "未分配",
        _ => state.ToString()
    };
}
