using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Assignment;

public sealed class DesktopLayoutAssignmentModelsTests
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

    // ═══ ZoneAssignment ═══

    [Fact] public void ZA_NullZone_Throws() { Assert.Throws<ArgumentNullException>(() => new DesktopLayoutZoneAssignment(null!, Array.Empty<DesktopIconSnapshotItem>())); }
    [Fact] public void ZA_NullIcons_Throws() { Assert.Throws<ArgumentNullException>(() => new DesktopLayoutZoneAssignment(RZ(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 80, 80), null!)); }
    [Fact] public void ZA_IconsExceedCapacity_Throws() { Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopLayoutZoneAssignment(RZ(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 80, 3), new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") })); }
    [Fact] public void ZA_NonAscendingOrdinal_Throws() { Assert.Throws<ArgumentException>(() => new DesktopLayoutZoneAssignment(RZ(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 80, 80), new[] { I(1, "a"), I(0, "b") })); }
    [Fact] public void ZA_DuplicateOrdinal_Throws() { Assert.Throws<ArgumentException>(() => new DesktopLayoutZoneAssignment(RZ(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 80, 80), new[] { I(0, "a"), I(0, "b") })); }
    [Fact] public void ZA_DuplicateStableId_Throws() { Assert.Throws<ArgumentException>(() => new DesktopLayoutZoneAssignment(RZ(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 80, 80), new[] { I(0, "a"), I(1, "a") })); }
    [Fact] public void ZA_Empty_Valid() { var za = new DesktopLayoutZoneAssignment(RZ(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 80, 80), Array.Empty<DesktopIconSnapshotItem>()); Assert.True(za.IsEmpty); Assert.Equal(0, za.AssignedCount); Assert.Equal(80, za.RemainingCapacity); }
    [Fact] public void ZA_ReadOnly() { var za = new DesktopLayoutZoneAssignment(RZ(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 80, 80), new[] { I(0, "a") }); Assert.Throws<NotSupportedException>(() => ((IList<DesktopIconSnapshotItem>)za.Icons).Clear()); }

    // ═══ Plan: basic invariants ═══

    [Fact] public void Plan_NullRT_Throws() { Assert.Throws<ArgumentNullException>(() => new DesktopLayoutAssignmentPlan(null!, Array.Empty<DesktopIconSnapshotItem>(), Array.Empty<DesktopLayoutZoneAssignment>(), Array.Empty<DesktopIconSnapshotItem>())); }
    [Fact] public void Plan_MissingZone_Throws() { var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt, Array.Empty<DesktopIconSnapshotItem>(), Array.Empty<DesktopLayoutZoneAssignment>(), Array.Empty<DesktopIconSnapshotItem>())); }

    [Fact] public void Plan_DuplicateZone_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,4000,10000), 0), Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 1) });
        // Both ZoneAssignments reference the same resolved zone (A)
        var za = new DesktopLayoutZoneAssignment(rt.Zones[0], Array.Empty<DesktopIconSnapshotItem>());
        var ex = Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt, Array.Empty<DesktopIconSnapshotItem>(), new[] { za, za }, Array.Empty<DesktopIconSnapshotItem>()));
        Assert.Contains("重复", ex.Message);
    }

    [Fact] public void Plan_ZoneOrderWrong_Throws() { var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,2000,10000), 5), Z(Guid.NewGuid(), "B", R(8000,0,10000,10000), 1) }); Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt, new[] { I(0, "a"), I(1, "b") }, new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "a") }), new DesktopLayoutZoneAssignment(rt.Zones[1], new[] { I(1, "b") }) }, Array.Empty<DesktopIconSnapshotItem>())); }
    [Fact] public void Plan_ForeignZone_Throws() { var rt1 = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var rt2 = MakeRT(new[] { Z(Guid.NewGuid(), "B", R(0,0,10000,10000), 0) }); var za = new DesktopLayoutZoneAssignment(rt2.Zones[0], Array.Empty<DesktopIconSnapshotItem>()); Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt1, Array.Empty<DesktopIconSnapshotItem>(), new[] { za }, Array.Empty<DesktopIconSnapshotItem>())); }

    // ═══ Cross-identity rejection ═══

    [Fact] public void Plan_CrossStableId_Assigned_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        // Input: (0,"a"), (1,"b"); Assigned: (0,"b") — Ordinal matches but StableId doesn't
        var ex = Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt,
            new[] { I(0, "a"), I(1, "b") },
            new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "b") }) },
            new[] { I(1, "b") }));
        Assert.Contains("StableId", ex.Message);
    }

    // ═══ Unassigned validation ═══

    [Fact] public void Plan_UnassignedDuplicateOrdinal_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt,
            new[] { I(0, "a"), I(1, "b"), I(2, "c") },
            new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "a") }) },
            new[] { I(1, "b"), I(1, "c") }));
    }

    [Fact] public void Plan_UnassignedExtraIcon_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        var ex = Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt,
            new[] { I(0, "a") },
            new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], Array.Empty<DesktopIconSnapshotItem>()) },
            new[] { I(0, "a"), I(1, "x") }));
        Assert.Equal("unassignedIcons", ex.ParamName);
        Assert.Contains("不在 InputIcons 中", ex.Message);
    }

    [Fact] public void Plan_UnassignedNotAscending_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt,
            new[] { I(0, "a"), I(1, "b"), I(2, "c") },
            new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "a") }) },
            new[] { I(2, "c"), I(1, "b") }));
    }

    [Fact] public void Plan_AssignedAndUnassignedOverlap_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        var ex = Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt,
            new[] { I(0, "a"), I(1, "b") },
            new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "a") }) },
            new[] { I(0, "a"), I(1, "b") }));
        Assert.Equal("unassignedIcons", ex.ParamName);
        Assert.Contains("同时出现在 Assigned 和 Unassigned", ex.Message);
    }

    [Fact] public void Plan_CrossZoneIconOrderWrong_Throws()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,4000,10000), 0, max: 1), Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 1, max: 1) });
        // Input A(0), B(1); ZoneA gets B(1), ZoneB gets A(0) — wrong Ordinal order across zones
        Assert.Throws<ArgumentException>(() => new DesktopLayoutAssignmentPlan(rt,
            new[] { I(0, "a"), I(1, "b") },
            new[] {
                new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(1, "b") }),
                new DesktopLayoutZoneAssignment(rt.Zones[1], new[] { I(0, "a") })
            }, Array.Empty<DesktopIconSnapshotItem>()));
    }

    [Fact] public void Plan_CorrectConcat_EqualsInput_Accepted()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        var plan = new DesktopLayoutAssignmentPlan(rt,
            new[] { I(0, "a"), I(1, "b"), I(2, "c") },
            new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "a"), I(1, "b") }) },
            new[] { I(2, "c") });
        Assert.Equal(2, plan.AssignedCount); Assert.Equal(1, plan.UnassignedCount);
        Assert.Equal("a", plan.ZoneAssignments[0].Icons[0].StableId);
        Assert.Equal("b", plan.ZoneAssignments[0].Icons[1].StableId);
        Assert.Equal("c", plan.UnassignedIcons[0].StableId);
    }

    // ═══ Immutability ═══

    [Fact] public void Plan_UnassignedIcons_ReadOnly() { var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var plan = new DesktopLayoutAssignmentPlan(rt, new[] { I(0, "a"), I(1, "b") }, new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "a") }) }, new[] { I(1, "b") }); Assert.Throws<NotSupportedException>(() => ((IList<DesktopIconSnapshotItem>)plan.UnassignedIcons).Clear()); }

    [Fact] public void Plan_MutateInputList_NoEffect()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        var inputList = new List<DesktopIconSnapshotItem> { I(0, "a") };
        var plan = new DesktopLayoutAssignmentPlan(rt, inputList,
            new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "a") }) },
            Array.Empty<DesktopIconSnapshotItem>());
        inputList.Clear();
        Assert.Single(plan.InputIcons);
    }

    [Fact] public void Plan_MutateZAIconsList_NoEffect()
    {
        var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        var iconsList = new List<DesktopIconSnapshotItem> { I(0, "a") };
        var za = new DesktopLayoutZoneAssignment(rt.Zones[0], iconsList);
        iconsList.Clear();
        Assert.Single(za.Icons);
    }

    [Fact] public void Plan_ValidIncomplete() { var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: 1) }); var plan = new DesktopLayoutAssignmentPlan(rt, new[] { I(0, "a"), I(1, "b") }, new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "a") }) }, new[] { I(1, "b") }); Assert.False(plan.IsComplete); Assert.Equal(1, plan.UnassignedCount); }
    [Fact] public void Plan_ReadOnly() { var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var plan = new DesktopLayoutAssignmentPlan(rt, new[] { I(0, "a") }, new[] { new DesktopLayoutZoneAssignment(rt.Zones[0], new[] { I(0, "a") }) }, Array.Empty<DesktopIconSnapshotItem>()); Assert.Throws<NotSupportedException>(() => ((IList<DesktopLayoutZoneAssignment>)plan.ZoneAssignments).Clear()); Assert.Throws<NotSupportedException>(() => ((IList<DesktopIconSnapshotItem>)plan.InputIcons).Clear()); }
    [Fact] public void Plan_MutateInput_NoEffect() { var rt = MakeRT(new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var zList = new List<DesktopLayoutZoneAssignment> { new(rt.Zones[0], Array.Empty<DesktopIconSnapshotItem>()) }; var plan = new DesktopLayoutAssignmentPlan(rt, Array.Empty<DesktopIconSnapshotItem>(), zList, Array.Empty<DesktopIconSnapshotItem>()); zList.Clear(); Assert.Single(plan.ZoneAssignments); }
}
