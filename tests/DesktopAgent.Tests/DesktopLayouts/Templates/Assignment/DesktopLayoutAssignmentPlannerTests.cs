using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Assignment;

public sealed class DesktopLayoutAssignmentPlannerTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);
    private static DesktopLayoutZone Z(Guid id, string n, NormalizedRect b, int so, int? max = null) =>
        new(id, n, b, so, ZonePlacementFlow.ColumnMajor, max);

    private static DesktopIconSnapshotItem I(int ord, string sid) =>
        new(ord, sid, $"N_{sid}", null, ItemKind.File, default, null, false, true);

    private static ResolvedDesktopLayoutZone RZ(DesktopLayoutZone z, PixelRect pb, int col, int row, long theo, long eff) =>
        new(z, pb, col, row, theo, eff);

    private static DesktopLayoutAssignmentPlan Plan(ResolvedDesktopLayoutTemplate rt, DesktopIconSnapshotItem[] icons) =>
        DesktopLayoutAssignmentPlanner.Plan(rt, icons);

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

    // ═══ Basic ═══

    [Fact] public void EmptyIcons_ReturnsCompleteEmptyPlan() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var p = Plan(rt, Array.Empty<DesktopIconSnapshotItem>()); Assert.True(p.IsComplete); Assert.Equal(0, p.AssignedCount); Assert.Single(p.ZoneAssignments); Assert.True(p.ZoneAssignments[0].IsEmpty); }
    [Fact] public void SingleIcon_FillsFirstZone() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var p = Plan(rt, new[] { I(0, "a") }); Assert.True(p.IsComplete); Assert.Equal(1, p.AssignedCount); Assert.Single(p.ZoneAssignments[0].Icons); }
    [Fact] public void SingleZone_FillSequentially() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 3) }); var p = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") }); Assert.True(p.IsComplete); Assert.Equal(3, p.ZoneAssignments[0].Icons.Count); }
    [Fact] public void MultiZone_SequentialFill() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,5000,10000), 0, max: 2), Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 1, max: 2) }); var p = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") }); Assert.Equal(2, p.ZoneAssignments[0].Icons.Count); Assert.Single(p.ZoneAssignments[1].Icons); Assert.Equal(0, p.UnassignedCount); }
    [Fact] public void OverflowsToNextZone() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,5000,10000), 0, max: 2), Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 1, max: 5) }); var p = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") }); Assert.Equal(2, p.ZoneAssignments[0].AssignedCount); Assert.Equal(2, p.ZoneAssignments[1].AssignedCount); }
    [Fact] public void CapacityExactMatch() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 3) }); var p = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") }); Assert.True(p.IsComplete); Assert.Equal(0, p.UnassignedCount); }
    [Fact] public void CapacityExceeded_Unassigned() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 2) }); var p = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") }); Assert.False(p.IsComplete); Assert.Equal(1, p.UnassignedCount); Assert.Equal("c", p.UnassignedIcons[0].StableId); }
    [Fact] public void AllZeros_Capacity() { var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(), new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var rz = RZ(t.Zones[0], new PixelRect(0, 0, 50, 50), 0, 0, 0, 0); var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,50,50), SP, new[] { rz }); var p = Plan(rt, new[] { I(0, "a") }); Assert.False(p.IsComplete); Assert.Single(p.UnassignedIcons); Assert.True(p.ZoneAssignments[0].IsEmpty); }
    [Fact] public void ZeroCapacity_KeptAsEmpty() { var zA = Z(Guid.NewGuid(), "A", R(0,0,4000,10000), 0); var zB = Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 1, max: 2); var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(), new[] { zA, zB }); var rzA = RZ(t.Zones[0], new PixelRect(0, 0, 50, 640), 0, 8, 0, 0); var rzB = RZ(t.Zones[1], new PixelRect(100, 0, 260, 640), 2, 8, 16, 2); var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,260,640), SP, new[] { rzA, rzB }); var p = Plan(rt, new[] { I(0, "a"), I(1, "b") }); Assert.True(p.ZoneAssignments[0].IsEmpty); Assert.Equal(2, p.ZoneAssignments[1].AssignedCount); }

    // ═══ Deterministic ═══

    [Fact] public void InputOrder_Irrelevant() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 5) }); var p1 = Plan(rt, new[] { I(2, "c"), I(0, "a"), I(1, "b") }); var p2 = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") }); for (var i = 0; i < 3; i++) Assert.Equal(p1.ZoneAssignments[0].Icons[i].StableId, p2.ZoneAssignments[0].Icons[i].StableId); }
    [Fact] public void Icons_AlwaysOrdinalASC() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 5) }); var p = Plan(rt, new[] { I(5, "e"), I(0, "a"), I(3, "c") }); Assert.Equal(0, p.InputIcons[0].Ordinal); Assert.Equal(3, p.InputIcons[1].Ordinal); Assert.Equal(5, p.InputIcons[2].Ordinal); }
    [Fact] public void Zones_BySortOrder_NotInputOrder() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 5, max: 2), Z(Guid.NewGuid(), "A", R(0,0,4000,10000), 1, max: 2) }); var p = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") }); Assert.Equal(1, p.ZoneAssignments[0].Zone.Zone.SortOrder); Assert.Equal(5, p.ZoneAssignments[1].Zone.Zone.SortOrder); }
    [Fact] public void Deterministic_RePlan() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 2) }); var p1 = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") }); var p2 = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c") }); Assert.Equal(p1.AssignedCount, p2.AssignedCount); Assert.Equal(p1.UnassignedCount, p2.UnassignedCount); Assert.Equal(p1.ZoneAssignments[0].Icons[0].StableId, p2.ZoneAssignments[0].Icons[0].StableId); Assert.Equal(p1.ZoneAssignments[0].Icons[1].StableId, p2.ZoneAssignments[0].Icons[1].StableId); Assert.Equal(p1.UnassignedIcons[0].StableId, p2.UnassignedIcons[0].StableId); }

    // ═══ StableId spaces different ═══
    [Fact] public void StableIdSpacesDifferent_Allowed() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 3) }); var p = Plan(rt, new[] { I(0, " a"), I(1, "a "), I(2, "a") }); Assert.Equal(3, p.AssignedCount); }

    // ═══ PlacementFlow not used ═══
    [Fact] public void PlacementFlow_DoesNotAffectAssignment()
    {
        // Template A: SortOrder 0=ColMajor, SortOrder 1=RowMajor
        var zA0 = new DesktopLayoutZone(Guid.NewGuid(), "A0", R(0, 0, 4000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 2);
        var zA1 = new DesktopLayoutZone(Guid.NewGuid(), "A1", R(5000, 0, 10000, 10000), 1, ZonePlacementFlow.RowMajor, maxItemCount: 2);
        var tA = new DesktopLayoutTemplate(Guid.NewGuid(), "TA", 1, TargetDisplayBinding.ForSelectedTarget(), new[] { zA0, zA1 });
        var realA = DesktopLayoutTemplateResolver.Resolve(tA, WA, SP);
        var rzA = new List<ResolvedDesktopLayoutZone>();
        for (var i = 0; i < 2; i++)
            rzA.Add(RZ(tA.Zones[i], realA.Zones[i].PixelBounds, realA.Zones[i].ColumnCount, realA.Zones[i].RowCount, realA.Zones[i].TheoreticalCapacity, realA.Zones[i].EffectiveCapacity));
        var rtA = new ResolvedDesktopLayoutTemplate(tA, WA, SP, rzA);

        // Template B: SortOrder 0=RowMajor, SortOrder 1=ColMajor (PlacementFlow swapped)
        var zB0 = new DesktopLayoutZone(Guid.NewGuid(), "B0", R(0, 0, 4000, 10000), 0, ZonePlacementFlow.RowMajor, maxItemCount: 2);
        var zB1 = new DesktopLayoutZone(Guid.NewGuid(), "B1", R(5000, 0, 10000, 10000), 1, ZonePlacementFlow.ColumnMajor, maxItemCount: 2);
        var tB = new DesktopLayoutTemplate(Guid.NewGuid(), "TB", 1, TargetDisplayBinding.ForSelectedTarget(), new[] { zB0, zB1 });
        var realB = DesktopLayoutTemplateResolver.Resolve(tB, WA, SP);
        var rzB = new List<ResolvedDesktopLayoutZone>();
        for (var i = 0; i < 2; i++)
            rzB.Add(RZ(tB.Zones[i], realB.Zones[i].PixelBounds, realB.Zones[i].ColumnCount, realB.Zones[i].RowCount, realB.Zones[i].TheoreticalCapacity, realB.Zones[i].EffectiveCapacity));
        var rtB = new ResolvedDesktopLayoutTemplate(tB, WA, SP, rzB);

        var pA = Plan(rtA, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });
        var pB = Plan(rtB, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") });

        // PlacementFlow difference must not affect assignment
        Assert.Equal(pA.AssignedCount, pB.AssignedCount);
        Assert.Equal(pA.UnassignedCount, pB.UnassignedCount);
        Assert.Equal(pA.IsComplete, pB.IsComplete);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(pA.ZoneAssignments[i].Zone.Zone.SortOrder, pB.ZoneAssignments[i].Zone.Zone.SortOrder);
            for (var j = 0; j < pA.ZoneAssignments[i].Icons.Count; j++)
            {
                Assert.Equal(pA.ZoneAssignments[i].Icons[j].Ordinal, pB.ZoneAssignments[i].Icons[j].Ordinal);
                Assert.Equal(pA.ZoneAssignments[i].Icons[j].StableId, pB.ZoneAssignments[i].Icons[j].StableId);
            }
        }
    }

    // ═══ Large EffectiveCapacity — no unsafe int cast ═══
    [Fact] public void LargeEffectiveCapacity_LongSafe_PlannerDoesNotCrash()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(), new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        const long cap = 2_500_000_000L;
        var rz = RZ(t.Zones[0], new PixelRect(0, 0, 50_000, 50_000), 50_000, 50_000, cap, cap);
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, 0, 50_000, 50_000), new PixelPoint(1, 1), new[] { rz });
        Assert.True(rt.Zones[0].EffectiveCapacity > int.MaxValue);
        var icons = new DesktopIconSnapshotItem[100];
        for (var i = 0; i < 100; i++) icons[i] = I(i, $"icon_{i}");
        var p = Plan(rt, icons);
        Assert.Equal(100, p.AssignedCount);
        Assert.True(p.IsComplete);
        Assert.Equal(0, p.UnassignedCount);
        Assert.Equal(cap - 100, p.ZoneAssignments[0].RemainingCapacity);
    }

    // ═══ Capacity ═══

    [Fact] public void UsesEffectiveCapacity() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 3) }); var p = Plan(rt, new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") }); Assert.Equal(3, p.AssignedCount); Assert.Equal(1, p.UnassignedCount); }
    [Fact] public void AssignedRemainingCounts() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 3) }); var p = Plan(rt, new[] { I(0, "a"), I(1, "b") }); Assert.Equal(2, p.ZoneAssignments[0].AssignedCount); Assert.Equal(1, p.ZoneAssignments[0].RemainingCapacity); }

    // ═══ Validation ═══

    [Fact] public void NullRT_Throws() { Assert.Throws<ArgumentNullException>(() => Plan(null!, new[] { I(0, "a") })); }
    [Fact] public void NullIcons_Throws() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentNullException>(() => Plan(rt, null!)); }
    [Fact] public void NullIconElement_Throws() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentException>(() => Plan(rt, new DesktopIconSnapshotItem[] { null! })); }
    [Fact] public void DuplicateOrdinal_Throws() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentException>(() => Plan(rt, new[] { I(0, "a"), I(0, "b") })); }
    [Fact] public void DuplicateStableId_Throws() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentException>(() => Plan(rt, new[] { I(0, "a"), I(1, "a") })); }
    [Fact] public void CaseDifferentStableId_Allowed() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 2) }); var p = Plan(rt, new[] { I(0, "abc"), I(1, "ABC") }); Assert.Equal(2, p.AssignedCount); }
    [Fact] public void NonConsecutiveOrdinal_AllowedAndSorted() { var rt = RT(WA, SP, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 3) }); var p = Plan(rt, new[] { I(100, "z"), I(5, "a"), I(50, "m") }); Assert.Equal(5, p.InputIcons[0].Ordinal); Assert.Equal(50, p.InputIcons[1].Ordinal); Assert.Equal(100, p.InputIcons[2].Ordinal); }
}
