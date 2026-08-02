using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Preview;

public sealed class DesktopLayoutPreviewPlannerTests
{
    private static DisplayDescriptor D(string n, PixelRect wa, bool primary = false) =>
        new(n, new PixelRect(wa.Left, wa.Top, wa.Right + wa.Left, wa.Bottom + wa.Top), wa, 96, 96, primary);

    private static DesktopLayoutZone Z(Guid id, string n, NormalizedRect b, int so, ZonePlacementFlow flow = ZonePlacementFlow.ColumnMajor, int? max = null) =>
        new(id, n, b, so, flow, max);

    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);

    private static DesktopIconSnapshotItem I(int ord, string sid, string? mon = "DISPLAY1", int px = 0, int py = 0) =>
        new(ord, sid, $"N_{sid}", null, ItemKind.File, new PixelPoint(px, py), mon, false, true);

    private static readonly DisplayDescriptor D1 = D("DISPLAY1", new PixelRect(0, 0, 800, 640), primary: true);
    private static readonly IReadOnlyList<DisplayDescriptor> OneDisplay = new[] { D1 };
    private static readonly PixelPoint SP = new(80, 80);

    [Fact] public void SelectedTarget_FullFlow()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 2) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, "DISPLAY1", SP, new[] { I(0, "a", px: 0, py: 0), I(1, "b", px: 0, py: 80) });
        Assert.True(plan.IsNoOp);
    }

    [Fact] public void PrimaryDisplay_FullFlow()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a") });
        Assert.Equal("DISPLAY1", plan.TargetDisplay.DeviceName);
    }

    [Fact] public void ExactDeviceName_FullFlow()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForExactDeviceName("DISPLAY1"),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a") });
        Assert.Equal("DISPLAY1", plan.TargetDisplay.DeviceName);
    }

    [Fact] public void SelectedTarget_CaseMismatch_Fails()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForSelectedTarget(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        Assert.Throws<InvalidOperationException>(() => DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, "display1", SP, new[] { I(0, "a") }));
    }

    [Fact] public void UsesRealWorkArea()
    {
        var wa = new PixelRect(100, 50, 900, 690);
        var dd = D("DISPLAY1", wa, primary: true);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, SP, new[] { I(0, "a", mon: "DISPLAY1", px: 100, py: 50) });
        Assert.Equal(wa, plan.ResolvedTemplate.WorkArea);
    }

    [Fact] public void IconSpacing_IsUsed()
    {
        var sp2 = new PixelPoint(100, 50);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, sp2, new[] { I(0, "a") });
        Assert.Equal(sp2, plan.ResolvedTemplate.IconSpacing);
    }

    [Fact] public void SpacingX_Zero_Fails()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, new PixelPoint(0, 80), new[] { I(0, "a") }));
    }

    [Fact] public void SpacingY_Zero_Fails()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, new PixelPoint(80, 0), new[] { I(0, "a") }));
    }

    [Fact] public void TargetDisplayIcons_OrdinalASC()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 3) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(2, "c"), I(0, "a"), I(1, "b") });
        Assert.Equal(0, plan.TargetDisplayIcons[0].Ordinal);
        Assert.Equal(1, plan.TargetDisplayIcons[1].Ordinal);
        Assert.Equal(2, plan.TargetDisplayIcons[2].Ordinal);
    }

    [Fact] public void InputPhysicalOrder_DoesNotAffectResult()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 3) });
        var a = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(2, "c"), I(1, "b"), I(0, "a") });
        var b = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a"), I(1, "b"), I(2, "c") });
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(a.TargetDisplayIcons[i].Ordinal, b.TargetDisplayIcons[i].Ordinal);
            Assert.Equal(a.TargetDisplayIcons[i].StableId, b.TargetDisplayIcons[i].StableId);
        }
    }

    [Fact] public void OtherDisplayIcons_GoToOutOfScope()
    {
        var displays = new[] { D("MAIN", new PixelRect(0, 0, 800, 640), primary: true), D("OTHER", new PixelRect(0, 0, 800, 640)) };
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, displays, null, SP, new[] { I(0, "a", mon: "MAIN"), I(1, "b", mon: "OTHER") });
        Assert.Single(plan.TargetDisplayIcons);
        Assert.Single(plan.OutOfScopeIcons);
        Assert.Equal("b", plan.OutOfScopeIcons[0].StableId);
    }

    [Fact] public void NullMonitorDeviceName_GoesToOutOfScope()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a", mon: null!) });
        Assert.Single(plan.OutOfScopeIcons);
    }

    [Fact] public void OutOfScope_NotConsumeCapacity()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 1) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a", mon: "DISPLAY1"), I(1, "b", mon: "OTHER") });
        Assert.Single(plan.TargetDisplayIcons);
        Assert.True(plan.IsComplete);
    }

    [Fact] public void OutOfScope_OrdinalASC()
    {
        var displays = new[] { D("MAIN", new PixelRect(0, 0, 800, 640), primary: true), D("OTHER", new PixelRect(0, 0, 800, 640)) };
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, displays, null, SP, new[] { I(3, "d", mon: "OTHER"), I(1, "b", mon: "OTHER"), I(0, "a", mon: "MAIN") });
        Assert.Equal(1, plan.OutOfScopeIcons[0].Ordinal);
        Assert.Equal(3, plan.OutOfScopeIcons[1].Ordinal);
    }

    [Fact] public void AlreadyAtTarget_WhenPositionMatches()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 2) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a", px: 0, py: 0) });
        Assert.Equal(DesktopLayoutPreviewItemState.AlreadyAtTarget, plan.Items[0].State);
    }

    [Fact] public void MoveRequired_WhenPositionDiffers()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 2) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a", px: 80, py: 0) });
        Assert.Equal(DesktopLayoutPreviewItemState.MoveRequired, plan.Items[0].State);
    }

    [Fact] public void DeltaXY_Correct()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 2) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a", px: 80, py: 80) });
        Assert.Equal(-80L, plan.Items[0].DeltaX);
        Assert.Equal(-80L, plan.Items[0].DeltaY);
    }

    [Fact] public void CapacityExceeded_Unassigned_NoException()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 1) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a"), I(1, "b") });
        Assert.False(plan.IsComplete);
        Assert.Equal(1, plan.UnassignedCount);
    }

    [Fact] public void IncompletePlan_AssignedStillHaveTargets()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 1) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a"), I(1, "b") });
        Assert.NotNull(plan.Items[0].TargetPosition);
    }

    [Fact] public void ZeroIcons_IsNoOp()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, Array.Empty<DesktopIconSnapshotItem>());
        Assert.True(plan.IsNoOp);
    }

    [Fact] public void AllAtTarget_IsNoOp()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 2) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a", px: 0, py: 0), I(1, "b", px: 0, py: 80) });
        Assert.True(plan.IsNoOp);
    }

    [Fact] public void HasChanges_WhenMoveRequired()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 2) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a", px: 80, py: 0) });
        Assert.True(plan.HasChanges);
    }

    [Fact] public void TargetEmpty_ButHasOutOfScope_StillIsNoOp()
    {
        var displays = new[] { D("MAIN", new PixelRect(0, 0, 800, 640), primary: true), D("OTHER", new PixelRect(0, 0, 800, 640)) };
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, displays, null, SP, new[] { I(0, "b", mon: "OTHER") });
        Assert.Equal(0, plan.TargetDisplayIconCount);
        Assert.True(plan.IsNoOp);
    }

    [Fact] public void DuplicateOrdinal_Fails()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        Assert.Throws<ArgumentException>(() => DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a"), I(0, "b") }));
    }

    [Fact] public void DuplicateStableId_Fails()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        Assert.Throws<ArgumentException>(() => DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a"), I(1, "a") }));
    }

    [Fact] public void StableIdCaseDifferent_Allowed()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 2) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "abc"), I(1, "ABC") });
        Assert.Equal(2, plan.TargetDisplayIconCount);
    }

    [Fact] public void NullIconElement_Fails()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        Assert.Throws<ArgumentException>(() => DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new DesktopIconSnapshotItem[] { null! }));
    }

    [Fact] public void ColumnMajor_2x2_Grid_AllPositions()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 4) });
        var wa2 = new PixelRect(0, 0, 160, 160);
        var dd = D("MAIN", wa2, primary: true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, SP,
            new[] { I(0, "a", mon: "MAIN"), I(1, "b", mon: "MAIN"), I(2, "c", mon: "MAIN"), I(3, "d", mon: "MAIN") });
        // slot0→(0,0) slot1→(0,80) slot2→(80,0) slot3→(80,80)
        Assert.Equal(new PixelPoint(0, 0), plan.Items[0].TargetPosition);
        Assert.Equal(new PixelPoint(0, 80), plan.Items[1].TargetPosition);
        Assert.Equal(new PixelPoint(80, 0), plan.Items[2].TargetPosition);
        Assert.Equal(new PixelPoint(80, 80), plan.Items[3].TargetPosition);
    }

    [Fact] public void RowMajor_2x2_Grid_AllPositions()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, ZonePlacementFlow.RowMajor, maxItemCount: 4) });
        var wa2 = new PixelRect(0, 0, 160, 160);
        var dd = D("MAIN", wa2, primary: true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, SP,
            new[] { I(0, "a", mon: "MAIN"), I(1, "b", mon: "MAIN"), I(2, "c", mon: "MAIN"), I(3, "d", mon: "MAIN") });
        // slot0→(0,0) slot1→(80,0) slot2→(0,80) slot3→(80,80)
        Assert.Equal(new PixelPoint(0, 0), plan.Items[0].TargetPosition);
        Assert.Equal(new PixelPoint(80, 0), plan.Items[1].TargetPosition);
        Assert.Equal(new PixelPoint(0, 80), plan.Items[2].TargetPosition);
        Assert.Equal(new PixelPoint(80, 80), plan.Items[3].TargetPosition);
    }

    [Fact] public void MaxItemCount_Effective()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 1) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a"), I(1, "b") });
        Assert.Equal(1, plan.AssignedCount);
        Assert.Equal(1, plan.UnassignedCount);
    }

    [Fact] public void ZoneSortOrder_DeterminesAssignment()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z2", R(5000, 0, 10000, 10000), 1, max: 1), Z(Guid.NewGuid(), "Z1", R(0, 0, 5000, 10000), 0, max: 1) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a") });
        Assert.Equal(0, plan.Items[0].Placement!.Zone.Zone.SortOrder);
    }

    [Fact] public void ZoneInputOrder_DoesNotOverrideSortOrder()
    {
        // 模板 Zone 输入顺序 Z2(SO=1) 在前，Z1(SO=0) 在后 → 应分配到 Z1(SO=0)
        var z1Id = Guid.NewGuid(); var z2Id = Guid.NewGuid();
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] {
                new DesktopLayoutZone(z2Id, "Z2", R(5000, 0, 10000, 10000), 1, ZonePlacementFlow.ColumnMajor, maxItemCount: 1),
                new DesktopLayoutZone(z1Id, "Z1", R(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 1)
            });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a") });
        Assert.Equal(z1Id, plan.Items[0].Placement!.Zone.Zone.ZoneId);
    }

    [Fact] public void NegativeWorkArea_Supported()
    {
        var dd = D("NEG", new PixelRect(-800, -640, 0, 0), primary: true);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, SP, new[] { I(0, "a", mon: "NEG", px: -800, py: -640) });
        Assert.NotNull(plan.Items[0].TargetPosition);
    }

    [Fact] public void ItemsAlways_OrdinalASC()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 5) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(9, "z"), I(0, "a"), I(5, "m") });
        Assert.Equal(0, plan.Items[0].Icon.Ordinal);
        Assert.Equal(5, plan.Items[1].Icon.Ordinal);
        Assert.Equal(9, plan.Items[2].Icon.Ordinal);
    }

    [Fact] public void Input_Equals_TargetPlusOutOfScope()
    {
        var displays = new[] { D("MAIN", new PixelRect(0, 0, 800, 640), primary: true), D("OTHER", new PixelRect(0, 0, 800, 640)) };
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, displays, null, SP, new[] { I(0, "a", mon: "MAIN"), I(1, "b", mon: "OTHER") });
        Assert.Equal(2, plan.InputIconCount);
        Assert.Equal(1, plan.TargetDisplayIconCount);
        Assert.Equal(1, plan.OutOfScopeIconCount);
    }

    [Fact] public void AllPublicCollections_ReadOnly()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a") });
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopLayoutPreviewItem>)plan.Items).Clear());
    }

    [Fact] public void Deterministic_RePlan()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 3) });
        var icons = new[] { I(0, "a"), I(1, "b"), I(2, "c") };
        var a = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, icons);
        var b = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, icons);
        for (var i = 0; i < a.Items.Count; i++)
        {
            Assert.Equal(a.Items[i].State, b.Items[i].State);
            Assert.Equal(a.Items[i].TargetPosition, b.Items[i].TargetPosition);
        }
    }

    [Fact] public void MutateInputList_AfterPlan_NoEffect()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var input = new List<DesktopIconSnapshotItem> { I(0, "a"), I(1, "b") };
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, input);
        input.Clear();
        Assert.Equal(2, plan.InputIconCount);
    }

    [Fact] public void TargetDisplayIcons_ReferenceEquals_AssignmentPlanInput()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a") });
        for (var i = 0; i < plan.TargetDisplayIcons.Count; i++)
            Assert.Same(plan.AssignmentPlan.InputIcons[i], plan.TargetDisplayIcons[i]);
    }

    [Fact] public void Items_ReferenceEquals_TargetDisplayIcons()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a") });
        for (var i = 0; i < plan.Items.Count; i++)
            Assert.Same(plan.TargetDisplayIcons[i], plan.Items[i].Icon);
    }

    [Fact] public void PlacementItem_UsesPlacementPlanPlacement()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a") });
        Assert.Same(plan.PlacementPlan.Placements[0], plan.Items[0].Placement);
    }

    [Fact] public void UnassignedItem_CorrespondsToPlacementPlanUnassigned()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { Z(Guid.NewGuid(), "Z", R(0, 0, 10000, 10000), 0, max: 1) });
        var plan = DesktopLayoutPreviewPlanner.Plan(t, OneDisplay, null, SP, new[] { I(0, "a"), I(1, "b") });
        Assert.Equal(DesktopLayoutPreviewItemState.Unassigned, plan.Items[1].State);
        Assert.Same(plan.PlacementPlan.UnassignedIcons[0], plan.Items[1].Icon);
    }
}
