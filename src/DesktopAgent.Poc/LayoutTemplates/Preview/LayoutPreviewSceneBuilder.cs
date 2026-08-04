using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

public static class LayoutPreviewSceneBuilder
{
    public static LayoutPreviewScene Build(
        DesktopLayoutPreviewPlan plan,
        LayoutPreviewCanvasTransform transform)
        => BuildLegacy(plan, transform);

    private static LayoutPreviewScene BuildLegacy(
        DesktopLayoutPreviewPlan plan,
        LayoutPreviewCanvasTransform transform)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (transform is null) throw new ArgumentNullException(nameof(transform));
        if (transform.WorkArea != plan.TargetDisplay.WorkArea)
            throw new ArgumentException(
                "Transform.WorkArea 必须与 Plan.TargetDisplay.WorkArea 完全一致。" +
                $" Transform=({transform.WorkArea.Left},{transform.WorkArea.Top})-({transform.WorkArea.Right},{transform.WorkArea.Bottom})" +
                $" Plan=({plan.TargetDisplay.WorkArea.Left},{plan.TargetDisplay.WorkArea.Top})-({plan.TargetDisplay.WorkArea.Right},{plan.TargetDisplay.WorkArea.Bottom})。",
                nameof(transform));

        var zoneVisuals = new List<LayoutPreviewZoneVisual>();
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
            zoneVisuals.Add(new LayoutPreviewZoneVisual(
                rz.Zone.ZoneId, rz.Zone.Name, rz.Zone.SortOrder,
                pb.Left, pb.Top, pb.Right, pb.Bottom,
                cl, ct, cw, ch,
                rz.EffectiveCapacity, assigned,
                rz.Zone.PlacementFlow.ToString()));
        }

        var iconVisuals = new List<LayoutPreviewIconVisual>();
        foreach (var item in plan.Items)
        {
            var (cx, cy) = transform.Map(item.CurrentPosition);
            double? tx = null, ty = null;
            Guid? zoneId = null;
            string zoneName = "";

            if (item.IsAssigned && item.Placement is not null)
            {
                var (ttx, tty) = transform.Map(item.TargetPosition!.Value);
                tx = ttx; ty = tty;
                zoneId = item.Placement.Zone.Zone.ZoneId;
                zoneName = item.Placement.Zone.Zone.Name;

                // 验证映射结果有限
                if (!double.IsFinite(tx.Value) || !double.IsFinite(ty.Value))
                    throw new InvalidOperationException(
                        $"图标 (Ordinal={item.Icon.Ordinal}) TargetPosition 映射结果不是有限数值。");
            }
            if (!double.IsFinite(cx) || !double.IsFinite(cy))
                throw new InvalidOperationException(
                    $"图标 (Ordinal={item.Icon.Ordinal}) CurrentPosition 映射结果不是有限数值。");

            iconVisuals.Add(new LayoutPreviewIconVisual(
                item.Icon, item.State, cx, cy, tx, ty, zoneId, zoneName,
                BuildToolTip(item)));
        }

        var itemRows = plan.Items
            .Select(LayoutPreviewItemRow.From)
            .ToList()
            .AsReadOnly();

        var oosRows = plan.OutOfScopeIcons
            .Select(LayoutPreviewOutOfScopeRow.From)
            .ToList()
            .AsReadOnly();

        return new LayoutPreviewScene(
            plan, transform,
            zoneVisuals.AsReadOnly(),
            iconVisuals.AsReadOnly(),
            itemRows, oosRows);
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

    /// <summary>构建规则感知场景。</summary>
    public static LayoutPreviewScene Build(
        DesktopLayoutRuleAwarePreviewPlan plan,
        LayoutPreviewCanvasTransform transform)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (transform is null) throw new ArgumentNullException(nameof(transform));

        var zoneVisuals = new List<LayoutPreviewZoneVisual>();
        var zones = plan.ResolvedTemplate.Zones
            .OrderBy(z => z.Zone.SortOrder).ToList();

        var zoneAssignedCounts = new Dictionary<Guid, int>();
        foreach (var item in plan.Items)
        {
            if (item.IsAssigned && item.ZoneId.HasValue)
            {
                zoneAssignedCounts.TryGetValue(item.ZoneId.Value, out var c);
                zoneAssignedCounts[item.ZoneId.Value] = c + 1;
            }
        }

        foreach (var rz in zones)
        {
            var pb = rz.PixelBounds;
            var (cl, ct, cw, ch) = transform.Map(pb);
            zoneAssignedCounts.TryGetValue(rz.Zone.ZoneId, out var assigned);
            zoneVisuals.Add(new LayoutPreviewZoneVisual(
                rz.Zone.ZoneId, rz.Zone.Name, rz.Zone.SortOrder,
                pb.Left, pb.Top, pb.Right, pb.Bottom,
                cl, ct, cw, ch,
                rz.EffectiveCapacity, assigned,
                rz.Zone.PlacementFlow.ToString()));
        }

        var iconVisuals = new List<LayoutPreviewIconVisual>();
        foreach (var item in plan.Items)
        {
            var (cx, cy) = transform.Map(item.CurrentPosition);
            double? tx = null, ty = null;
            Guid? zoneId = item.ZoneId;
            string zoneName = item.TargetZone?.Name ?? "";

            var oldState = item.PlacementState switch
            {
                DesktopLayoutRuleAwarePreviewPlacementState.Fixed =>
                    DesktopLayoutPreviewItemState.AlreadyAtTarget,
                DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget =>
                    DesktopLayoutPreviewItemState.AlreadyAtTarget,
                DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired =>
                    DesktopLayoutPreviewItemState.MoveRequired,
                _ => DesktopLayoutPreviewItemState.Unassigned,
            };

            if (item.IsAssigned && item.TargetPosition.HasValue)
            {
                var (ttx, tty) = transform.Map(item.TargetPosition.Value);
                tx = ttx; ty = tty;
            }

            iconVisuals.Add(new LayoutPreviewIconVisual(
                item.Icon, oldState, cx, cy, tx, ty, zoneId, zoneName,
                BuildRuleAwareToolTip(item)));
        }

        var itemRows = plan.Items
            .Select(LayoutPreviewItemRow.FromRuleAware)
            .ToList().AsReadOnly();

        var oosRows = plan.OutOfScopeIcons
            .Select(LayoutPreviewOutOfScopeRow.From)
            .ToList().AsReadOnly();

        return new LayoutPreviewScene(
            plan, transform,
            zoneVisuals.AsReadOnly(),
            iconVisuals.AsReadOnly(),
            itemRows, oosRows);
    }

    private static string BuildRuleAwareToolTip(DesktopLayoutRuleAwarePreviewItem item)
    {
        var parts = new List<string>
        {
            item.Icon.DisplayName,
            $"StableId: {item.Icon.StableId}",
            $"Disposition: {item.Disposition}",
            $"State: {item.PlacementState}",
            $"Current: ({item.CurrentPosition.X},{item.CurrentPosition.Y})"
        };
        if (item.IsAssigned && item.TargetPosition.HasValue)
        {
            parts.Add($"Target: ({item.TargetPosition.Value.X},{item.TargetPosition.Value.Y})");
            parts.Add($"Zone: {item.TargetZone?.Name ?? "—"}");
            parts.Add($"SlotIndex: {item.SlotIndex}");
        }
        if (item.Rule is not null)
            parts.Add($"Rule: {item.Rule.Kind} → {item.Rule.StableId}");
        return string.Join("\n", parts);
    }
}
