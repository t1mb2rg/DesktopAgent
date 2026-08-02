using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Poc.LayoutTemplates.Preview;

namespace DesktopAgent.Tests.LayoutTemplates.Preview;

public sealed class LayoutPreviewSceneBuilderTests
{
    private static DesktopIconSnapshotItem I(int ord, string sid, string? mon = "DISPLAY1", int px = 0, int py = 0) =>
        new(ord, sid, $"N_{sid}", null, ItemKind.File, new PixelPoint(px, py), mon, false, true);

    private static DesktopLayoutPreviewPlan MakePlan(PixelRect? wa = null)
    {
        var wa2 = wa ?? new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 2) });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa2, 96, 96, true);
        return DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a", px: wa2.Left, py: wa2.Top), I(1, "b", px: wa2.Left + 80, py: wa2.Top) });
    }

    [Fact] public void Zones_BySortOrder()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] {
                new DesktopLayoutZone(Guid.NewGuid(), "Z1", new NormalizedRect(0, 0, 5000, 10000), 5, ZonePlacementFlow.ColumnMajor, maxItemCount: 1),
                new DesktopLayoutZone(Guid.NewGuid(), "Z0", new NormalizedRect(5000, 0, 10000, 10000), 1, ZonePlacementFlow.ColumnMajor, maxItemCount: 1)
            });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a"), I(1, "b") });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        Assert.Equal(1, scene.Zones[0].SortOrder);
        Assert.Equal(5, scene.Zones[1].SortOrder);
    }

    [Fact] public void Icons_ByOrdinal() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.Equal(0, scene.Icons[0].Icon.Ordinal); Assert.Equal(1, scene.Icons[1].Icon.Ordinal); }
    [Fact] public void AlreadyAtTarget_SinglePosition() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.Equal(DesktopLayoutPreviewItemState.AlreadyAtTarget, scene.Icons[0].State); }
    [Fact] public void MoveRequired_HasTarget() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.True(scene.Icons[1].TargetCanvasX.HasValue); }
    [Fact] public void ItemRowsCount_Matches() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.Equal(plan.Items.Count, scene.ItemRows.Count); }
    [Fact] public void NegWorkArea_MatchesPlan()
    {
        var wa = new PixelRect(-800, -640, 0, 0);
        var plan = MakePlan(wa);
        var t = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, t);
        Assert.NotNull(scene);
    }
    [Fact] public void WorkAreaMismatch_Fails()
    {
        var plan = MakePlan(new PixelRect(0, 0, 800, 640));
        var t = new LayoutPreviewCanvasTransform(new PixelRect(100, 200, 900, 840), 800, 640, 0);
        Assert.Throws<ArgumentException>(() => LayoutPreviewSceneBuilder.Build(plan, t));
    }
    [Fact] public void Rows_ReadOnly() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.Throws<NotSupportedException>(() => ((IList<LayoutPreviewItemRow>)scene.ItemRows).Clear()); }
    [Fact] public void InputPlan_NotModified() { var plan = MakePlan(); var tc = plan.TargetDisplayIconCount; var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); LayoutPreviewSceneBuilder.Build(plan, t); Assert.Equal(tc, plan.TargetDisplayIconCount); }
    [Fact] public void Redeterministic() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var s1 = LayoutPreviewSceneBuilder.Build(plan, t); var s2 = LayoutPreviewSceneBuilder.Build(plan, t); Assert.Equal(s1.Zones.Count, s2.Zones.Count); Assert.Equal(s1.Icons.Count, s2.Icons.Count); Assert.Equal(s1.Icons[0].CurrentCanvasX, s2.Icons[0].CurrentCanvasX); }
    [Fact] public void ViewportResize_OnlyCanvasChanges()
    {
        var plan = MakePlan();
        var t1 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0);
        var t2 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 400, 320, 0);
        var s1 = LayoutPreviewSceneBuilder.Build(plan, t1);
        var s2 = LayoutPreviewSceneBuilder.Build(plan, t2);
        Assert.Equal(s1.Zones.Count, s2.Zones.Count);
        Assert.Equal(s1.Icons.Count, s2.Icons.Count);
        // Core pixel values unchanged
        Assert.Equal(s1.ItemRows[0].CurrentX, s2.ItemRows[0].CurrentX);
        Assert.Equal(s1.ItemRows[0].TargetX, s2.ItemRows[0].TargetX);
    }
    [Fact] public void Unassigned_NoTarget()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 1) });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a"), I(1, "b") });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        Assert.False(scene.Icons[1].TargetCanvasX.HasValue);
        Assert.Equal("—", scene.ItemRows[1].TargetX);
    }
    [Fact] public void Oos_ByOrdinal()
    {
        var wa = new PixelRect(0, 0, 800, 640);
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null) });
        var displays = new[] {
            new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, true),
            new DisplayDescriptor("OTHER", new PixelRect(0, 0, 1920, 1080), wa, 96, 96, false) };
        var plan = DesktopLayoutPreviewPlanner.Plan(t, displays, null, new PixelPoint(80, 80),
            new[] { I(3, "d", mon: "OTHER"), I(1, "b", mon: "OTHER"), I(0, "a", mon: "DISPLAY1") });
        var transform = new LayoutPreviewCanvasTransform(wa, 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        Assert.Equal(1, scene.OutOfScopeRows[0].Ordinal);
        Assert.Equal(3, scene.OutOfScopeRows[1].Ordinal);
    }
}
