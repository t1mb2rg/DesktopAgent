using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Placement;

public sealed class DesktopLayoutPlacementModelsTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);
    private static DesktopLayoutZone Z(Guid id, string n, NormalizedRect b, int so, int? max = null) =>
        new(id, n, b, so, ZonePlacementFlow.ColumnMajor, max);
    private static DesktopIconSnapshotItem I(int ord, string sid) =>
        new(ord, sid, $"N_{sid}", null, ItemKind.File, default, null, false, true);
    private static ResolvedDesktopLayoutZone RZ(DesktopLayoutZone z, PixelRect pb, int col, int row, long theo, long eff) =>
        new(z, pb, col, row, theo, eff);

    private static readonly PixelRect WA = new(0, 0, 800, 640);
    private static readonly PixelPoint SP = new(80, 80);

    private static ResolvedDesktopLayoutTemplate MakeRT(DesktopLayoutZone[] zs)
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(), zs);
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rzs = new List<ResolvedDesktopLayoutZone>();
        for (var i = 0; i < zs.Length; i++)
            rzs.Add(RZ(t.Zones[i], real.Zones[i].PixelBounds, real.Zones[i].ColumnCount, real.Zones[i].RowCount, real.Zones[i].TheoreticalCapacity, real.Zones[i].EffectiveCapacity));
        return new ResolvedDesktopLayoutTemplate(t, WA, SP, rzs);
    }

    private static DesktopLayoutAssignmentPlan MakeAP(ResolvedDesktopLayoutTemplate rt, DesktopIconSnapshotItem[] icons) =>
        DesktopLayoutAssignmentPlanner.Plan(rt, icons);

    // ═══ DesktopIconTargetPlacement: basic ═══

    [Fact] public void TP_NullIcon_Throws() { var z = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) }).Zones[0]; Assert.Throws<ArgumentNullException>(() => new DesktopIconTargetPlacement(null!, z, SP, 0)); }
    [Fact] public void TP_NullZone_Throws() { Assert.Throws<ArgumentNullException>(() => new DesktopIconTargetPlacement(I(0, "a"), null!, SP, 0)); }
    [Fact] public void TP_SpacingXZero_Throws() { var z = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) }).Zones[0]; Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopIconTargetPlacement(I(0, "a"), z, new PixelPoint(0, 80), 0)); }
    [Fact] public void TP_SpacingYZero_Throws() { var z = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) }).Zones[0]; Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopIconTargetPlacement(I(0, "a"), z, new PixelPoint(80, 0), 0)); }
    [Fact] public void TP_NegativeSlotIndex_Throws() { var z = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) }).Zones[0]; Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopIconTargetPlacement(I(0, "a"), z, SP, -1)); }

    [Fact] public void TP_SlotIndexExceedsEffectiveCapacity_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 2) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopIconTargetPlacement(I(0, "a"), rt.Zones[0], SP, 2));
    }

    [Fact] public void TP_ZeroCapacityZone_Throws()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var rz = RZ(t.Zones[0], new PixelRect(0, 0, 50, 50), 0, 0, 0, 0);
        Assert.Throws<ArgumentException>(() => new DesktopIconTargetPlacement(I(0, "a"), rz, SP, 0));
    }

    [Fact] public void TP_ColumnMajor_Allowed()
    {
        // ColumnMajor 是公开合法枚举，可正常构造并得到正确 SlotIndex 映射
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 2) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var tp0 = new DesktopIconTargetPlacement(I(0, "a"), rz, SP, 0);
        var tp1 = new DesktopIconTargetPlacement(I(1, "b"), rz, SP, 1);
        // ColMajor: slot0→col0,row0; slot1→col0,row1
        Assert.Equal(0, tp0.ColumnIndex); Assert.Equal(0, tp0.RowIndex);
        Assert.Equal(0, tp1.ColumnIndex); Assert.Equal(1, tp1.RowIndex);
    }

    [Fact] public void TP_RowMajor_Allowed()
    {
        // RowMajor 是公开合法枚举，可正常构造并得到正确 SlotIndex 映射
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.RowMajor, maxItemCount: 2) });
        var real = DesktopLayoutTemplateResolver.Resolve(t, WA, SP);
        var rz = RZ(t.Zones[0], real.Zones[0].PixelBounds, real.Zones[0].ColumnCount, real.Zones[0].RowCount, real.Zones[0].TheoreticalCapacity, real.Zones[0].EffectiveCapacity);
        var tp0 = new DesktopIconTargetPlacement(I(0, "a"), rz, SP, 0);
        var tp1 = new DesktopIconTargetPlacement(I(1, "b"), rz, SP, 1);
        // RowMajor: slot0→col0,row0; slot1→col1,row0
        Assert.Equal(0, tp0.ColumnIndex); Assert.Equal(0, tp0.RowIndex);
        Assert.Equal(1, tp1.ColumnIndex); Assert.Equal(0, tp1.RowIndex);
    }

    // ═══ DesktopLayoutZonePlacement: basic ═══

    [Fact] public void ZP_NullAssignment_Throws() { Assert.Throws<ArgumentNullException>(() => new DesktopLayoutZonePlacement(null!, SP, Array.Empty<DesktopIconTargetPlacement>())); }
    [Fact] public void ZP_NullPlacements_Throws() { var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) }); var ap = MakeAP(rt, Array.Empty<DesktopIconSnapshotItem>()); Assert.Throws<ArgumentNullException>(() => new DesktopLayoutZonePlacement(ap.ZoneAssignments[0], SP, null!)); }

    [Fact] public void ZP_NullPlacementElement_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 5) });
        var ap = MakeAP(rt, new[] { I(0, "a") });
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutZonePlacement(ap.ZoneAssignments[0], SP, new DesktopIconTargetPlacement[] { null! }));
    }

    [Fact] public void ZP_PlacementCount_Mismatches_IconCount_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 5) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(1, "b") });
        var za = ap.ZoneAssignments[0];
        var p = new DesktopIconTargetPlacement(za.Icons[0], za.Zone, SP, 0);
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutZonePlacement(za, SP, new[] { p }));
    }

    [Fact] public void ZP_PlacementForeignZone_Throws()
    {
        // 两个 Zone 均有正容量，Assignment 只含 1 个图标
        var rt = MakeRT(new[] {
            Z(Guid.NewGuid(), "A", R(0, 0, 4000, 10000), 0, max: 1),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, max: 1) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(1, "b") });
        var za = ap.ZoneAssignments[0]; // zone A, 1 icon
        // 用 zone B 创建 placement — Zone 不是同一实例
        var p = new DesktopIconTargetPlacement(za.Icons[0], ap.ZoneAssignments[1].Zone, SP, 0);
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutZonePlacement(za, SP, new[] { p }));
        Assert.Equal("placements", ex.ParamName);
        Assert.Contains("Zone 与 Assignment.Zone 不是同一实例", ex.Message);
    }

    [Fact] public void ZP_IconIdentityMismatch_Throws()
    {
        // Assignment 中仅 1 个图标 (Ordinal=0, StableId="a")，Placement 用身份错误的图标
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 1) });
        var ap = MakeAP(rt, new[] { I(0, "a") });
        var za = ap.ZoneAssignments[0];
        var wrongIcon = I(0, "wrong");
        var p = new DesktopIconTargetPlacement(wrongIcon, za.Zone, SP, 0);
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutZonePlacement(za, SP, new[] { p }));
        Assert.Equal("placements", ex.ParamName);
        Assert.Contains("StableId", ex.Message);
    }

    [Fact] public void ZP_IconOrdinalMismatch_Throws()
    {
        // 与 ZP_IconIdentityMismatch 对称：Ordinal 不匹配而 StableId 相同
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 1) });
        var ap = MakeAP(rt, new[] { I(0, "a") });
        var za = ap.ZoneAssignments[0];
        var wrongIcon = I(5, "a"); // Ordinal 不匹配
        var p = new DesktopIconTargetPlacement(wrongIcon, za.Zone, SP, 0);
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutZonePlacement(za, SP, new[] { p }));
        Assert.Equal("placements", ex.ParamName);
        Assert.Contains("Ordinal", ex.Message);
    }

    [Fact] public void ZP_SlotIndexNotContinuous_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 5) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(1, "b") });
        var za = ap.ZoneAssignments[0];
        var p0 = new DesktopIconTargetPlacement(za.Icons[0], za.Zone, SP, 0);
        var p1 = new DesktopIconTargetPlacement(za.Icons[1], za.Zone, SP, 2);
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutZonePlacement(za, SP, new[] { p0, p1 }));
    }

    [Fact] public void ZP_IconSpacingMismatch_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 5) });
        var ap = MakeAP(rt, new[] { I(0, "a") });
        var za = ap.ZoneAssignments[0];
        var p = new DesktopIconTargetPlacement(za.Icons[0], za.Zone, new PixelPoint(100, 100), 0);
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutZonePlacement(za, SP, new[] { p }));
    }

    [Fact] public void ZP_DuplicateSlotIndex_Throws()
    {
        // 两个 Placement 使用相同 SlotIndex=0 → SlotIndex 检查命中，不是 TargetPosition 检查
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 5) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(1, "b") });
        var za = ap.ZoneAssignments[0];
        var p0 = new DesktopIconTargetPlacement(za.Icons[0], za.Zone, SP, 0);
        var p1 = new DesktopIconTargetPlacement(za.Icons[1], za.Zone, SP, 0); // 重复 SlotIndex 0
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutZonePlacement(za, SP, new[] { p0, p1 }));
        Assert.Contains("SlotIndex", ex.Message);
        Assert.Contains("不等于列表索引", ex.Message);
    }

    [Fact] public void ZP_ValidEmpty_Allowed()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var ap = MakeAP(rt, Array.Empty<DesktopIconSnapshotItem>());
        var zp = new DesktopLayoutZonePlacement(ap.ZoneAssignments[0], SP, Array.Empty<DesktopIconTargetPlacement>());
        Assert.True(zp.IsEmpty);
        Assert.Equal(0, zp.PlacementCount);
    }

    [Fact] public void ZP_OutputReadOnly()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 5) });
        var ap = MakeAP(rt, new[] { I(0, "a") });
        var za = ap.ZoneAssignments[0];
        var p = new DesktopIconTargetPlacement(za.Icons[0], za.Zone, SP, 0);
        var zp = new DesktopLayoutZonePlacement(za, SP, new[] { p });
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopIconTargetPlacement>)zp.Placements).Clear());
    }

    [Fact] public void ZP_MutateInputList_NoEffect()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 5) });
        var ap = MakeAP(rt, new[] { I(0, "a") });
        var za = ap.ZoneAssignments[0];
        var p = new DesktopIconTargetPlacement(za.Icons[0], za.Zone, SP, 0);
        var input = new List<DesktopIconTargetPlacement> { p };
        var zp = new DesktopLayoutZonePlacement(za, SP, input);
        input.Clear();
        Assert.Single(zp.Placements);
    }

    // ═══ DesktopLayoutPlacementPlan: basic ═══

    [Fact] public void PP_NullAssignmentPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopLayoutPlacementPlan(null!, Array.Empty<DesktopLayoutZonePlacement>()));
    }

    [Fact] public void PP_NullZonePlacements_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var ap = MakeAP(rt, Array.Empty<DesktopIconSnapshotItem>());
        Assert.Throws<ArgumentNullException>(() => new DesktopLayoutPlacementPlan(ap, null!));
    }

    [Fact] public void PP_NullZonePlacementElement_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var ap = MakeAP(rt, Array.Empty<DesktopIconSnapshotItem>());
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutPlacementPlan(ap, new DesktopLayoutZonePlacement[] { null! }));
    }

    [Fact] public void PP_MissingZonePlacement_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var ap = MakeAP(rt, new[] { I(0, "a") });
        var pp = DesktopLayoutCoordinatePlanner.Plan(ap);
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutPlacementPlan(ap, Array.Empty<DesktopLayoutZonePlacement>()));
        _ = pp;
    }

    [Fact] public void PP_DuplicateZonePlacement_Throws()
    {
        // 两个 Zone 各 1 图标，传入 [zp0, zp0] → 第 2 个 Assignment 引用不匹配
        var rt = MakeRT(new[] {
            Z(Guid.NewGuid(), "A", R(0, 0, 4000, 10000), 0, max: 1),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, max: 1) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(1, "b") });
        var za0 = ap.ZoneAssignments[0];
        var za1 = ap.ZoneAssignments[1];
        var tp0 = new DesktopIconTargetPlacement(za0.Icons[0], za0.Zone, SP, 0);
        var tp1 = new DesktopIconTargetPlacement(za1.Icons[0], za1.Zone, SP, 0);
        var zp0 = new DesktopLayoutZonePlacement(za0, SP, new[] { tp0 });
        var zp1 = new DesktopLayoutZonePlacement(za1, SP, new[] { tp1 });
        // 传入 [zp0, zp0]：第二个位置期望 za1 但得到 za0
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutPlacementPlan(ap, new[] { zp0, zp0 }));
        Assert.Equal("zonePlacements", ex.ParamName);
        Assert.Contains("不是同一实例", ex.Message);
        _ = zp1;
    }

    [Fact] public void PP_WrongOrderZonePlacement_Throws()
    {
        // 两个 Zone，传入 [zp1, zp0] 而非 [zp0, zp1] → 顺序错误
        var rt = MakeRT(new[] {
            Z(Guid.NewGuid(), "A", R(0, 0, 4000, 10000), 0, max: 1),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, max: 1) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(1, "b") });
        var za0 = ap.ZoneAssignments[0];
        var za1 = ap.ZoneAssignments[1];
        var tp0 = new DesktopIconTargetPlacement(za0.Icons[0], za0.Zone, SP, 0);
        var tp1 = new DesktopIconTargetPlacement(za1.Icons[0], za1.Zone, SP, 0);
        var zp0 = new DesktopLayoutZonePlacement(za0, SP, new[] { tp0 });
        var zp1 = new DesktopLayoutZonePlacement(za1, SP, new[] { tp1 });
        // 传入 [zp1, zp0]：第一个位置期望 za0 但得到 za1
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutPlacementPlan(ap, new[] { zp1, zp0 }));
        Assert.Equal("zonePlacements", ex.ParamName);
        Assert.Contains("不是同一实例", ex.Message);
    }

    [Fact] public void PP_ForeignZonePlacement_Throws()
    {
        var rt1 = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var rt2 = MakeRT(new[] { Z(Guid.NewGuid(), "B", R(0, 0, 10000, 10000), 0) });
        var ap1 = MakeAP(rt1, Array.Empty<DesktopIconSnapshotItem>());
        var ap2 = MakeAP(rt2, Array.Empty<DesktopIconSnapshotItem>());
        var zp2 = new DesktopLayoutZonePlacement(ap2.ZoneAssignments[0], SP, Array.Empty<DesktopIconTargetPlacement>());
        Assert.Throws<ArgumentException>(() => new DesktopLayoutPlacementPlan(ap1, new[] { zp2 }));
    }

    [Fact] public void PP_FlattenedSequence_MatchesAssignmentPlanOrder()
    {
        var rt = MakeRT(new[] {
            Z(Guid.NewGuid(), "A", R(0, 0, 5000, 10000), 0, max: 2),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, max: 2) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });
        var pp = DesktopLayoutCoordinatePlanner.Plan(ap);
        Assert.Equal(4, pp.Placements.Count);
        Assert.Equal("a", pp.Placements[0].Icon.StableId);
        Assert.Equal("b", pp.Placements[1].Icon.StableId);
        Assert.Equal("c", pp.Placements[2].Icon.StableId);
        Assert.Equal("d", pp.Placements[3].Icon.StableId);
    }

    [Fact] public void PP_ValidComplete_Accepted()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 3) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(1, "b") });
        var pp = DesktopLayoutCoordinatePlanner.Plan(ap);
        Assert.True(pp.IsComplete);
        Assert.Equal(2, pp.PlacementCount);
        Assert.Equal(0, pp.UnassignedCount);
    }

    [Fact] public void PP_ValidIncomplete_Accepted()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 1) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(1, "b") });
        Assert.False(ap.IsComplete);
        var pp = DesktopLayoutCoordinatePlanner.Plan(ap);
        Assert.False(pp.IsComplete);
        Assert.Equal(1, pp.PlacementCount);
        Assert.Equal(1, pp.UnassignedCount);
    }

    [Fact] public void PP_ValidEmpty_Accepted()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var ap = MakeAP(rt, Array.Empty<DesktopIconSnapshotItem>());
        var pp = DesktopLayoutCoordinatePlanner.Plan(ap);
        Assert.True(pp.IsComplete);
        Assert.Equal(0, pp.PlacementCount);
    }

    [Fact] public void PP_OutputReadOnly()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 3) });
        var ap = MakeAP(rt, new[] { I(0, "a") });
        var pp = DesktopLayoutCoordinatePlanner.Plan(ap);
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopLayoutZonePlacement>)pp.ZonePlacements).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopIconTargetPlacement>)pp.Placements).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopIconSnapshotItem>)pp.UnassignedIcons).Clear());
    }

    [Fact] public void PP_MutateInput_NoEffect()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 3) });
        var ap = MakeAP(rt, new[] { I(0, "a") });
        var za = ap.ZoneAssignments[0];
        var tp = new DesktopIconTargetPlacement(za.Icons[0], za.Zone, SP, 0);
        var zp = new DesktopLayoutZonePlacement(za, SP, new[] { tp });
        var zpList = new List<DesktopLayoutZonePlacement> { zp };
        var pp = new DesktopLayoutPlacementPlan(ap, zpList);
        zpList.Clear();
        Assert.Single(pp.ZonePlacements);
    }

    [Fact] public void PP_UnassignedIcons_PreservedIdentityAndOrder()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 1) });
        var ap = MakeAP(rt, new[] { I(0, "a"), I(5, "b"), I(9, "c") });
        var pp = DesktopLayoutCoordinatePlanner.Plan(ap);
        Assert.Equal(2, pp.UnassignedCount);
        Assert.Equal(5, pp.UnassignedIcons[0].Ordinal);
        Assert.Equal("b", pp.UnassignedIcons[0].StableId);
        Assert.Equal(9, pp.UnassignedIcons[1].Ordinal);
        Assert.Equal("c", pp.UnassignedIcons[1].StableId);
    }
}
