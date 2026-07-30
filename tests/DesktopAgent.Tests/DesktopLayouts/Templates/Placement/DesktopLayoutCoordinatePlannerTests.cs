using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Placement;

public sealed class DesktopLayoutCoordinatePlannerTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);
    private static DesktopLayoutZone Z(Guid id, string n, NormalizedRect b, int so, ZonePlacementFlow flow = ZonePlacementFlow.ColumnMajor, int? max = null) =>
        new(id, n, b, so, flow, max);
    private static DesktopIconSnapshotItem I(int ord, string sid) =>
        new(ord, sid, $"N_{sid}", null, ItemKind.File, default, null, false, true);
    private static ResolvedDesktopLayoutZone RZ(DesktopLayoutZone z, PixelRect pb, int col, int row, long theo, long eff) =>
        new(z, pb, col, row, theo, eff);
    private static DesktopLayoutAssignmentPlan AP(ResolvedDesktopLayoutTemplate rt, DesktopIconSnapshotItem[] icons) =>
        DesktopLayoutAssignmentPlanner.Plan(rt, icons);
    private static DesktopLayoutPlacementPlan PP(DesktopLayoutAssignmentPlan ap) =>
        DesktopLayoutCoordinatePlanner.Plan(ap);

    private static ResolvedDesktopLayoutTemplate RT(PixelRect wa, PixelPoint sp, DesktopLayoutZone[] zones)
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(), zones);
        var real = DesktopLayoutTemplateResolver.Resolve(t, wa, sp);
        var rzs = new List<ResolvedDesktopLayoutZone>();
        for (var i = 0; i < zones.Length; i++)
            rzs.Add(RZ(t.Zones[i], real.Zones[i].PixelBounds, real.Zones[i].ColumnCount, real.Zones[i].RowCount, real.Zones[i].TheoreticalCapacity, real.Zones[i].EffectiveCapacity));
        return new ResolvedDesktopLayoutTemplate(t, wa, sp, rzs);
    }

    private static readonly PixelRect WA = new(0, 0, 800, 640);
    private static readonly PixelPoint SP = new(80, 80);

    // ═══ Basic coordinate generation ═══

    [Fact] public void SingleSlot_TargetPosition_IsZoneTopLeft()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var ap = AP(rt, new[] { I(0, "a") });
        var pp = PP(ap);
        var p = pp.Placements[0];
        Assert.Equal(rt.Zones[0].PixelBounds.Left, p.TargetPosition.X);
        Assert.Equal(rt.Zones[0].PixelBounds.Top, p.TargetPosition.Y);
    }

    [Fact] public void TargetPosition_IsCellTopLeft_NotCenter()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 1) });
        var ap = AP(rt, new[] { I(0, "a") });
        var pp = PP(ap);
        var p = pp.Placements[0];
        Assert.Equal(0, p.TargetPosition.X);
        Assert.Equal(0, p.TargetPosition.Y);
    }

    [Fact] public void TargetCellBounds_WidthHeight_EqualsSpacing()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var ap = AP(rt, new[] { I(0, "a") });
        var pp = PP(ap);
        var p = pp.Placements[0];
        Assert.Equal(SP.X, p.TargetCellBounds.Width);
        Assert.Equal(SP.Y, p.TargetCellBounds.Height);
    }

    // ═══ ColumnMajor ═══

    [Fact] public void ColumnMajor_FillsColumnFirst()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 6) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, WA, SP, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });
        var pp = PP(ap);
        // Zone: 10x8 grid, ColumnMajor → slot0=col0,row0; slot1=col0,row1; ... slot7=col0,row7; slot8=col1,row0
        Assert.Equal(0, pp.Placements[0].ColumnIndex); Assert.Equal(0, pp.Placements[0].RowIndex);
        Assert.Equal(0, pp.Placements[1].ColumnIndex); Assert.Equal(1, pp.Placements[1].RowIndex);
        Assert.Equal(0, pp.Placements[2].ColumnIndex); Assert.Equal(2, pp.Placements[2].RowIndex);
        Assert.Equal(0, pp.Placements[3].ColumnIndex); Assert.Equal(3, pp.Placements[3].RowIndex);
    }

    [Fact] public void ColumnMajor_FillsFullColumn_ThenNext()
    {
        // 3列×2行: slot0-col0,row0 slot1-col0,row1 slot2-col1,row0 slot3-col1,row1 slot4-col2,row0 slot5-col2,row1
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 6) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, new PixelRect(0, 0, 240, 160), SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 240, 160), SP, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d"), I(4, "e"), I(5, "f") });
        var pp = PP(ap);
        Assert.Equal(3, rz.ColumnCount); Assert.Equal(2, rz.RowCount);
        Assert.Equal((0, 0), (pp.Placements[0].ColumnIndex, pp.Placements[0].RowIndex));
        Assert.Equal((0, 1), (pp.Placements[1].ColumnIndex, pp.Placements[1].RowIndex));
        Assert.Equal((1, 0), (pp.Placements[2].ColumnIndex, pp.Placements[2].RowIndex));
        Assert.Equal((1, 1), (pp.Placements[3].ColumnIndex, pp.Placements[3].RowIndex));
        Assert.Equal((2, 0), (pp.Placements[4].ColumnIndex, pp.Placements[4].RowIndex));
        Assert.Equal((2, 1), (pp.Placements[5].ColumnIndex, pp.Placements[5].RowIndex));
    }

    [Fact] public void ColumnMajor_LastSlotCellBounds_TouchesZoneRightBottom()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 6) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, new PixelRect(0, 0, 240, 160), SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 240, 160), SP, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d"), I(4, "e"), I(5, "f") });
        var pp = PP(ap);
        var last = pp.Placements[5]; // col2,row1 → (160,80)-(240,160)
        Assert.Equal(160, last.TargetPosition.X);
        Assert.Equal(80, last.TargetPosition.Y);
        Assert.Equal(240, last.TargetCellBounds.Right);
        Assert.Equal(160, last.TargetCellBounds.Bottom);
    }

    [Fact] public void ColumnMajor_AfterFullColumn_MovesToNextColumn()
    {
        // 10×8 grid: 前 8 个 icon 填满 col0(row0→row7)，第 9 个进入 col1,row0
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 10) });
        var icons = new DesktopIconSnapshotItem[9];
        for (var i = 0; i < 9; i++) icons[i] = I(i, $"icon_{i}");
        var ap = AP(rt, icons);
        var pp = PP(ap);
        Assert.Equal(0, pp.Placements[7].ColumnIndex); Assert.Equal(7, pp.Placements[7].RowIndex); // col0,row7
        Assert.Equal(1, pp.Placements[8].ColumnIndex); Assert.Equal(0, pp.Placements[8].RowIndex); // col1,row0
        Assert.Equal(80, pp.Placements[8].TargetPosition.X);
        Assert.Equal(0, pp.Placements[8].TargetPosition.Y);
    }

    // ═══ RowMajor ═══

    [Fact] public void RowMajor_FillsRowFirst()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.RowMajor, maxItemCount: 6) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, new PixelRect(0, 0, 240, 160), SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 240, 160), SP, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d"), I(4, "e"), I(5, "f") });
        var pp = PP(ap);
        Assert.Equal((0, 0), (pp.Placements[0].ColumnIndex, pp.Placements[0].RowIndex));
        Assert.Equal((1, 0), (pp.Placements[1].ColumnIndex, pp.Placements[1].RowIndex));
        Assert.Equal((2, 0), (pp.Placements[2].ColumnIndex, pp.Placements[2].RowIndex));
        Assert.Equal((0, 1), (pp.Placements[3].ColumnIndex, pp.Placements[3].RowIndex));
        Assert.Equal((1, 1), (pp.Placements[4].ColumnIndex, pp.Placements[4].RowIndex));
        Assert.Equal((2, 1), (pp.Placements[5].ColumnIndex, pp.Placements[5].RowIndex));
    }

    [Fact] public void RowMajor_LastSlotBoundary_Correct()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.RowMajor, maxItemCount: 6) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, new PixelRect(0, 0, 240, 160), SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 240, 160), SP, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d"), I(4, "e"), I(5, "f") });
        var pp = PP(ap);
        var last = pp.Placements[5]; // col2,row1
        Assert.Equal(160, last.TargetPosition.X);
        Assert.Equal(80, last.TargetPosition.Y);
        Assert.Equal(240, last.TargetCellBounds.Right);
        Assert.Equal(160, last.TargetCellBounds.Bottom);
    }

    // ═══ Anchor = Zone top-left ═══

    [Fact] public void NonZeroZoneLeftTop_UsedAsAnchor()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(5000, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 1) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, WA, SP, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a") });
        var pp = PP(ap);
        Assert.Equal(400, pp.Placements[0].TargetPosition.X);
        Assert.Equal(0, pp.Placements[0].TargetPosition.Y);
    }

    [Fact] public void NegativeVirtualDesktopCoordinate_Correct()
    {
        var waWithNegative = new PixelRect(-800, -640, 0, 0);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 4) });
        var sp2 = new PixelPoint(80, 80);
        var real = DesktopLayoutTemplateResolver.Resolve(t, waWithNegative, sp2);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, waWithNegative, sp2, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") });
        var pp = PP(ap);
        Assert.Equal(-800, pp.Placements[0].TargetPosition.X);
        Assert.Equal(-640, pp.Placements[0].TargetPosition.Y);
    }

    [Fact] public void UnequalSpacingXY_Correct()
    {
        var sp3 = new PixelPoint(100, 50);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.RowMajor, maxItemCount: 4) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, new PixelRect(0, 0, 200, 100), sp3);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 200, 100), sp3, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") });
        var pp = PP(ap);
        Assert.Equal(100, pp.Placements[1].TargetCellBounds.Width);
        Assert.Equal(50, pp.Placements[1].TargetCellBounds.Height);
    }

    // ═══ Capacity ═══

    [Fact] public void MaxItemCount_LessThanTheoretical_OnlyEffectiveSlotsAllowed()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 3) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, WA, SP, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") });
        var pp = PP(ap);
        Assert.Equal(3, pp.PlacementCount);
    }

    [Fact] public void SlotIndex_EqualsEffectiveCapacity_Rejected()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 1) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopIconTargetPlacement(I(0, "a"), rz, SP, 1));
    }

    [Fact] public void ZeroCapacityZone_CannotCreatePlacement()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null) });
        var rz = RZ(t.Zones[0], new PixelRect(0, 0, 50, 50), 0, 0, 0, 0);
        Assert.Throws<ArgumentException>(
            () => new DesktopIconTargetPlacement(I(0, "a"), rz, SP, 0));
    }

    [Fact] public void EmptyAssignment_GeneratesEmptyZonePlacement()
    {
        var zA = Z(Guid.NewGuid(), "A", R(0, 0, 4000, 10000), 0);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(), new[] { zA });
        var rzA = RZ(t.Zones[0], new PixelRect(0, 0, 50, 640), 0, 8, 0, 0);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 50, 640), SP, new[] { rzA });
        var ap = AP(rt, Array.Empty<DesktopIconSnapshotItem>());
        var pp = PP(ap);
        Assert.Single(pp.ZonePlacements);
        Assert.True(pp.ZonePlacements[0].IsEmpty);
        Assert.Equal(0, pp.ZonePlacements[0].PlacementCount);
    }

    [Fact] public void AssignmentLessThanCapacity_OnlyGeneratesActualIcons()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 10) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b") });
        var pp = PP(ap);
        Assert.Equal(2, pp.PlacementCount);
    }

    [Fact] public void LargeEffectiveCapacity_LongSafe_PlannerDoesNotCrash()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        const long cap = 2_500_000_000L;
        var rz = RZ(t.Zones[0], new PixelRect(0, 0, 50_000, 50_000), 50_000, 50_000, cap, cap);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 50_000, 50_000), new PixelPoint(1, 1), new[] { rz });
        var icons = new DesktopIconSnapshotItem[100];
        for (var i = 0; i < 100; i++) icons[i] = I(i, $"icon_{i}");
        var ap = AP(rt, icons);
        var pp = PP(ap);
        Assert.Equal(100, pp.PlacementCount);
        Assert.True(pp.IsComplete);
    }

    // ═══ Multi-zone + PlacementFlow ═══

    [Fact] public void EachZone_StartsSlotIndexZero()
    {
        var rt = RT(WA, SP, new[] {
            Z(Guid.NewGuid(), "A", R(0, 0, 5000, 10000), 0, max: 2),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, max: 2) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });
        var pp = PP(ap);
        Assert.Equal(0, pp.ZonePlacements[0].Placements[0].SlotIndex);
        Assert.Equal(1, pp.ZonePlacements[0].Placements[1].SlotIndex);
        Assert.Equal(0, pp.ZonePlacements[1].Placements[0].SlotIndex);
        Assert.Equal(1, pp.ZonePlacements[1].Placements[1].SlotIndex);
    }

    [Fact] public void DifferentZones_DifferentPlacementFlow_Respected()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] {
                new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 4000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 4),
                new DesktopLayoutZone(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, ZonePlacementFlow.RowMajor, maxItemCount: 4)
            });
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rzs = new List<ResolvedDesktopLayoutZone>();
        for (var i = 0; i < 2; i++)
            rzs.Add(RZ(t.Zones[i], real.Zones[i].PixelBounds, real.Zones[i].ColumnCount, real.Zones[i].RowCount, real.Zones[i].TheoreticalCapacity, real.Zones[i].EffectiveCapacity));
        var rt = new ResolvedDesktopLayoutTemplate(t, WA, SP, rzs);
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d"), I(4, "e"), I(5, "f"), I(6, "g"), I(7, "h") });
        var pp = PP(ap);
        // Zone A (ColMajor, 4cols×8rows): slot0=col0,row0; slot1=col0,row1; slot2=col0,row2; slot3=col0,row3
        // Zone B (RowMajor, 5cols×8rows): slot0=col0,row0; slot1=col1,row0; slot2=col2,row0; slot3=col3,row0
        var za = pp.ZonePlacements[0];
        Assert.Equal((0, 0), (za.Placements[0].ColumnIndex, za.Placements[0].RowIndex));
        Assert.Equal((0, 1), (za.Placements[1].ColumnIndex, za.Placements[1].RowIndex));
        Assert.Equal((0, 2), (za.Placements[2].ColumnIndex, za.Placements[2].RowIndex));
        var zb = pp.ZonePlacements[1];
        Assert.Equal((0, 0), (zb.Placements[0].ColumnIndex, zb.Placements[0].RowIndex));
        Assert.Equal((1, 0), (zb.Placements[1].ColumnIndex, zb.Placements[1].RowIndex));
        Assert.Equal((2, 0), (zb.Placements[2].ColumnIndex, zb.Placements[2].RowIndex));
    }

    [Fact] public void ZonePlacementOrder_KeepsAssignmentPlanOrder()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] {
                new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 4000, 10000), 5, ZonePlacementFlow.ColumnMajor, maxItemCount: 1),
                new DesktopLayoutZone(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, ZonePlacementFlow.ColumnMajor, maxItemCount: 1)
            });
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rzs = new List<ResolvedDesktopLayoutZone>();
        for (var i = 0; i < 2; i++)
            rzs.Add(RZ(t.Zones[i], real.Zones[i].PixelBounds, real.Zones[i].ColumnCount, real.Zones[i].RowCount, real.Zones[i].TheoreticalCapacity, real.Zones[i].EffectiveCapacity));
        var rt = new ResolvedDesktopLayoutTemplate(t, WA, SP, rzs);
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b") });
        var pp = PP(ap);
        Assert.Equal("B", pp.ZonePlacements[0].Assignment.Zone.Zone.Name);
        Assert.Equal("A", pp.ZonePlacements[1].Assignment.Zone.Zone.Name);
    }

    [Fact] public void PlacementFlow_AffectsOnlyIntraZone_NotInterZone()
    {
        // 两套模板除 PlacementFlow 外完全一致：共享 TemplateId/ZoneId/Bounds/SortOrder/MaxItemCount。
        // 9 icons→总容量 8(4+4)，产生 Unassigned，验证 Flow 只影响 Zone 内坐标。
        var templateId = Guid.NewGuid();
        var z0Id = Guid.NewGuid();
        var z1Id = Guid.NewGuid();
        var wa = new PixelRect(0, 0, 320, 160);  // 320/80=4 cols, 160/80=2 rows total
        var sp = new PixelPoint(80, 80);

        // Template A: Zone0=ColMajor, Zone1=RowMajor
        var tA = new DesktopLayoutTemplate(templateId, "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] {
                new DesktopLayoutZone(z0Id, "Z0", R(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 4),
                new DesktopLayoutZone(z1Id, "Z1", R(5000, 0, 10000, 10000), 1, ZonePlacementFlow.RowMajor, maxItemCount: 4)
            });

        // Template B: 仅 PlacementFlow 互换
        var tB = new DesktopLayoutTemplate(templateId, "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] {
                new DesktopLayoutZone(z0Id, "Z0", R(0, 0, 5000, 10000), 0, ZonePlacementFlow.RowMajor, maxItemCount: 4),
                new DesktopLayoutZone(z1Id, "Z1", R(5000, 0, 10000, 10000), 1, ZonePlacementFlow.ColumnMajor, maxItemCount: 4)
            });

        var realA = DesktopLayoutTemplateResolver.Resolve(tA, wa, sp);
        var rzA = new List<ResolvedDesktopLayoutZone>();
        for (var i = 0; i < 2; i++)
            rzA.Add(RZ(tA.Zones[i], realA.Zones[i].PixelBounds, realA.Zones[i].ColumnCount, realA.Zones[i].RowCount, realA.Zones[i].TheoreticalCapacity, realA.Zones[i].EffectiveCapacity));
        var rtA = new ResolvedDesktopLayoutTemplate(tA, wa, sp, rzA);

        var realB = DesktopLayoutTemplateResolver.Resolve(tB, wa, sp);
        var rzB = new List<ResolvedDesktopLayoutZone>();
        for (var i = 0; i < 2; i++)
            rzB.Add(RZ(tB.Zones[i], realB.Zones[i].PixelBounds, realB.Zones[i].ColumnCount, realB.Zones[i].RowCount, realB.Zones[i].TheoreticalCapacity, realB.Zones[i].EffectiveCapacity));
        var rtB = new ResolvedDesktopLayoutTemplate(tB, wa, sp, rzB);

        // 2 cols × 2 rows per zone (160×160), 4+4=8 cap, 9 icons → 1 unassigned
        var icons = new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d"), I(4, "e"), I(5, "f"), I(6, "g"), I(7, "h"), I(8, "i") };
        var apA = AP(rtA, icons);
        var apB = AP(rtB, icons);
        Assert.False(apA.IsComplete);
        Assert.False(apB.IsComplete);
        var ppA = PP(apA);
        var ppB = PP(apB);

        // ═══ 结构一致性 ═══
        Assert.Equal(ppA.ZonePlacements.Count, ppB.ZonePlacements.Count);
        Assert.Equal(ppA.PlacementCount, ppB.PlacementCount);
        Assert.Equal(ppA.UnassignedCount, ppB.UnassignedCount);
        Assert.Equal(ppA.IsComplete, ppB.IsComplete);

        for (var zi = 0; zi < 2; zi++)
        {
            Assert.Equal(ppA.ZonePlacements[zi].Assignment.Zone.Zone.SortOrder,
                         ppB.ZonePlacements[zi].Assignment.Zone.Zone.SortOrder);
            Assert.Equal(ppA.ZonePlacements[zi].PlacementCount,
                         ppB.ZonePlacements[zi].PlacementCount);
            for (var pi = 0; pi < ppA.ZonePlacements[zi].PlacementCount; pi++)
            {
                Assert.Equal(ppA.ZonePlacements[zi].Placements[pi].Icon.Ordinal,
                             ppB.ZonePlacements[zi].Placements[pi].Icon.Ordinal);
                Assert.Equal(ppA.ZonePlacements[zi].Placements[pi].Icon.StableId,
                             ppB.ZonePlacements[zi].Placements[pi].Icon.StableId);
            }
        }

        // ═══ UnassignedIcons 完整身份 ═══
        Assert.Equal(1, ppA.UnassignedCount);
        Assert.Equal(8, ppA.UnassignedIcons[0].Ordinal);
        Assert.Equal("i", ppA.UnassignedIcons[0].StableId);
        Assert.Equal(ppA.UnassignedIcons[0].Ordinal, ppB.UnassignedIcons[0].Ordinal);
        Assert.Equal(ppA.UnassignedIcons[0].StableId, ppB.UnassignedIcons[0].StableId);

        // ═══ 坐标：Slot 0 相同，Slot 1 因 Flow 不同 ═══
        // Zone 0: slot0=(0,0)→(0,0) same; slot1 ColMajor=(col0,row1)→(0,80), RowMajor=(col1,row0)→(80,0)
        Assert.Equal(ppA.ZonePlacements[0].Placements[0].TargetPosition,
                     ppB.ZonePlacements[0].Placements[0].TargetPosition);
        Assert.NotEqual(ppA.ZonePlacements[0].Placements[1].TargetPosition,
                        ppB.ZonePlacements[0].Placements[1].TargetPosition);
        // ColMajor slot1: col0,row1
        Assert.Equal(0, ppA.ZonePlacements[0].Placements[1].ColumnIndex);
        Assert.Equal(1, ppA.ZonePlacements[0].Placements[1].RowIndex);
        // RowMajor slot1: col1,row0
        Assert.Equal(1, ppB.ZonePlacements[0].Placements[1].ColumnIndex);
        Assert.Equal(0, ppB.ZonePlacements[0].Placements[1].RowIndex);

        // Zone 1: slot0=(0,0)→(160,0) same; slot1 ColMajor=(col0,row1)→(160,80), RowMajor=(col1,row0)→(240,0)
        Assert.Equal(ppA.ZonePlacements[1].Placements[0].TargetPosition,
                     ppB.ZonePlacements[1].Placements[0].TargetPosition);
        Assert.NotEqual(ppA.ZonePlacements[1].Placements[1].TargetPosition,
                        ppB.ZonePlacements[1].Placements[1].TargetPosition);
        // RowMajor slot1 (A): col1,row0; ColMajor slot1 (B): col0,row1
        Assert.Equal(1, ppA.ZonePlacements[1].Placements[1].ColumnIndex);
        Assert.Equal(0, ppA.ZonePlacements[1].Placements[1].RowIndex);
        Assert.Equal(0, ppB.ZonePlacements[1].Placements[1].ColumnIndex);
        Assert.Equal(1, ppB.ZonePlacements[1].Placements[1].RowIndex);
    }

    [Fact] public void EachZone_UsesOwnPixelBoundsAnchor()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] {
                new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 4000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 1),
                new DesktopLayoutZone(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, ZonePlacementFlow.ColumnMajor, maxItemCount: 1)
            });
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rzs = new List<ResolvedDesktopLayoutZone>();
        for (var i = 0; i < 2; i++)
            rzs.Add(RZ(t.Zones[i], real.Zones[i].PixelBounds, real.Zones[i].ColumnCount, real.Zones[i].RowCount, real.Zones[i].TheoreticalCapacity, real.Zones[i].EffectiveCapacity));
        var rt = new ResolvedDesktopLayoutTemplate(t, WA, SP, rzs);
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b") });
        var pp = PP(ap);
        Assert.Equal(0, pp.ZonePlacements[0].Placements[0].TargetPosition.X);
        Assert.Equal(400, pp.ZonePlacements[1].Placements[0].TargetPosition.X);
    }

    // ═══ Incomplete plan ═══

    [Fact] public void IncompleteAssignmentPlan_Allowed()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 2) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") });
        Assert.False(ap.IsComplete);
        var pp = PP(ap);
        Assert.Equal(2, pp.PlacementCount);
        Assert.Equal(1, pp.UnassignedCount);
        Assert.False(pp.IsComplete);
    }

    [Fact] public void IncompletePlan_OnlyAssignedGetPlacements()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 2) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") });
        var pp = PP(ap);
        Assert.Equal("a", pp.Placements[0].Icon.StableId);
        Assert.Equal("b", pp.Placements[1].Icon.StableId);
        Assert.Equal("c", pp.UnassignedIcons[0].StableId);
    }

    [Fact] public void IncompletePlan_UnassignedOrderAndIdentity_Preserved()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 1) });
        var ap = AP(rt, new[] { I(0, "a"), I(5, "b"), I(9, "c") });
        var pp = PP(ap);
        Assert.Equal(2, pp.UnassignedCount);
        Assert.Equal(5, pp.UnassignedIcons[0].Ordinal);
        Assert.Equal("b", pp.UnassignedIcons[0].StableId);
        Assert.Equal(9, pp.UnassignedIcons[1].Ordinal);
        Assert.Equal("c", pp.UnassignedIcons[1].StableId);
    }

    [Fact] public void IncompletePlan_PlacementCount_Plus_UnassignedCount_Equals_InputCount()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 3) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d"), I(4, "e") });
        var pp = PP(ap);
        Assert.Equal(3, pp.PlacementCount);
        Assert.Equal(2, pp.UnassignedCount);
        Assert.Equal(5, ap.InputCount);
        Assert.Equal(pp.PlacementCount + pp.UnassignedCount, ap.InputCount);
    }

    // ═══ Determinism ═══

    [Fact] public void Deterministic_RePlan_SameCoordinates()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 3) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") });
        var p1 = PP(ap);
        var p2 = PP(ap);
        for (var i = 0; i < p1.Placements.Count; i++)
        {
            Assert.Equal(p1.Placements[i].TargetPosition, p2.Placements[i].TargetPosition);
            Assert.Equal(p1.Placements[i].TargetCellBounds, p2.Placements[i].TargetCellBounds);
            Assert.Equal(p1.Placements[i].ColumnIndex, p2.Placements[i].ColumnIndex);
            Assert.Equal(p1.Placements[i].RowIndex, p2.Placements[i].RowIndex);
        }
    }

    [Fact] public void Metadata_DoesNotAffectTargetCoordinate()
    {
        // 同一 ResolvedTemplate，两个 Plan 的 Ordinal/StableId 相同，
        // 仅改变 PixelPosition、DisplayName、ItemKind、ParsingName 等元数据。
        // 验证所有 Placement 属性完全相同。
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 3) });

        var iconA0 = I(0, "a");
        var iconA1 = I(1, "b");
        var iconA2 = I(2, "c");

        var iconB0 = new DesktopIconSnapshotItem(0, "a", "完全不同的名称", "parsing\\path", ItemKind.Folder, new PixelPoint(999, 888), "DISPLAY1", true, false);
        var iconB1 = new DesktopIconSnapshotItem(1, "b", "另类名", "other\\path", ItemKind.Shortcut, new PixelPoint(-100, -200), "DISPLAY2", false, false);
        var iconB2 = new DesktopIconSnapshotItem(2, "c", "第三个", null, ItemKind.File, new PixelPoint(0, 0), null, true, true);

        var ppA = PP(AP(rt, new[] { iconA0, iconA1, iconA2 }));
        var ppB = PP(AP(rt, new[] { iconB0, iconB1, iconB2 }));

        for (var i = 0; i < ppA.Placements.Count; i++)
        {
            Assert.Equal(ppA.Placements[i].SlotIndex, ppB.Placements[i].SlotIndex);
            Assert.Equal(ppA.Placements[i].ColumnIndex, ppB.Placements[i].ColumnIndex);
            Assert.Equal(ppA.Placements[i].RowIndex, ppB.Placements[i].RowIndex);
            Assert.Equal(ppA.Placements[i].TargetPosition, ppB.Placements[i].TargetPosition);
            Assert.Equal(ppA.Placements[i].TargetCellBounds, ppB.Placements[i].TargetCellBounds);
        }
    }

    [Fact] public void Planner_DoesNotReorder_AssignmentIcons()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 4) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });
        var pp = PP(ap);
        Assert.Equal("a", pp.Placements[0].Icon.StableId);
        Assert.Equal("b", pp.Placements[1].Icon.StableId);
        Assert.Equal("c", pp.Placements[2].Icon.StableId);
        Assert.Equal("d", pp.Placements[3].Icon.StableId);
    }

    [Fact] public void NoExtraEmptySlots_Generated()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 10) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b") });
        var pp = PP(ap);
        Assert.Equal(2, pp.PlacementCount);
    }

    // ═══ Tail remainder ═══

    [Fact] public void ZoneWidthNotDivisibleBySpacing_TailPixels_ProduceNoSlot()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.RowMajor, null) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, new PixelRect(0, 0, 205, 80), SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 205, 80), SP, new[] { rz });
        Assert.Equal(2, rz.ColumnCount);
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b") });
        var pp = PP(ap);
        Assert.Equal(2, pp.PlacementCount);
    }

    // ═══ Validation ═══

    [Fact] public void NullAssignmentPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DesktopLayoutCoordinatePlanner.Plan(null!));
    }

    [Fact] public void AllTargetCellBounds_FullyContained_InZone()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 6) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, new PixelRect(0, 0, 240, 160), SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 240, 160), SP, new[] { rz });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d"), I(4, "e"), I(5, "f") });
        var pp = PP(ap);
        foreach (var p in pp.Placements)
            Assert.True(DesktopIconLayoutGeometry.IsFullyContained(p.TargetCellBounds, p.Zone.PixelBounds));
    }

    [Fact] public void AllTargetCellBounds_FullyContained_InWorkArea()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 4) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });
        var pp = PP(ap);
        foreach (var p in pp.Placements)
            Assert.True(DesktopIconLayoutGeometry.IsFullyContained(p.TargetCellBounds, WA));
    }

    [Fact] public void DifferentSlots_NoOverlap()
    {
        var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 4) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });
        var pp = PP(ap);
        for (var i = 0; i < pp.Placements.Count; i++)
            for (var j = i + 1; j < pp.Placements.Count; j++)
                Assert.False(DesktopIconLayoutGeometry.Overlaps(pp.Placements[i].TargetCellBounds, pp.Placements[j].TargetCellBounds));
    }

    [Fact] public void DifferentZones_NoOverlap_AcrossZones()
    {
        var rt = RT(WA, SP, new[] {
            Z(Guid.NewGuid(), "A", R(0, 0, 5000, 10000), 0, max: 2),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, max: 2) });
        var ap = AP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });
        var pp = PP(ap);
        for (var i = 0; i < pp.Placements.Count; i++)
            for (var j = i + 1; j < pp.Placements.Count; j++)
                Assert.False(DesktopIconLayoutGeometry.Overlaps(pp.Placements[i].TargetCellBounds, pp.Placements[j].TargetCellBounds));
    }
}
