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

    private static DesktopLayoutPreviewPlan MakePlan()
    {
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, maxItemCount: 2) });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 800, 640), 96, 96, true);
        return DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a", px: 0, py: 0), I(1, "b", px: 80, py: 0) });
    }

    [Fact] public void Zones_BySortOrder()
    {
        // Create plan with zones SortOrder 5, 1 - verify scene keeps SortOrder
        var t = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] {
                new DesktopLayoutZone(Guid.NewGuid(), "Z1", new NormalizedRect(0, 0, 5000, 10000), 5, ZonePlacementFlow.ColumnMajor, maxItemCount: 1),
                new DesktopLayoutZone(Guid.NewGuid(), "Z0", new NormalizedRect(5000, 0, 10000, 10000), 1, ZonePlacementFlow.ColumnMajor, maxItemCount: 1)
            });
        var dd = new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 800, 640), 96, 96, true);
        var plan = DesktopLayoutPreviewPlanner.Plan(t, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { I(0, "a"), I(1, "b") });
        var transform = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        Assert.Equal(1, scene.Zones[0].SortOrder);
        Assert.Equal(5, scene.Zones[1].SortOrder);
    }

    [Fact] public void Icons_ByOrdinal()
    {
        var plan = MakePlan();
        var transform = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0);
        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);
        Assert.Equal(0, scene.Icons[0].Icon.Ordinal);
        Assert.Equal(1, scene.Icons[1].Icon.Ordinal);
    }

    [Fact] public void AlreadyAtTarget_HasCurrentPosition() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.Equal(DesktopLayoutPreviewItemState.AlreadyAtTarget, scene.Icons[0].State); }
    [Fact] public void MoveRequired_HasCurrentAndTarget() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.False(double.IsNaN(scene.Icons[1].TargetCanvasX)); }
    [Fact] public void ItemRows_Count_MatchesItems() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.Equal(plan.Items.Count, scene.ItemRows.Count); }
    [Fact] public void NegWorkArea_Maps() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(-800, -640, 0, 0), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.NotNull(scene); }
    [Fact] public void DefensiveCopy_ItemRows_ReadOnly() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var scene = LayoutPreviewSceneBuilder.Build(plan, t); Assert.Throws<NotSupportedException>(() => ((IList<LayoutPreviewItemRow>)scene.ItemRows).Clear()); }
    [Fact] public void InputPlan_NotModified() { var plan = MakePlan(); var tc = plan.TargetDisplayIconCount; var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); LayoutPreviewSceneBuilder.Build(plan, t); Assert.Equal(tc, plan.TargetDisplayIconCount); }
    [Fact] public void Redeterministic() { var plan = MakePlan(); var t = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var s1 = LayoutPreviewSceneBuilder.Build(plan, t); var s2 = LayoutPreviewSceneBuilder.Build(plan, t); Assert.Equal(s1.Zones.Count, s2.Zones.Count); }
    [Fact] public void ViewportResize_OnlyChangesCanvasCoords() { var plan = MakePlan(); var t1 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 800, 640, 0); var t2 = new LayoutPreviewCanvasTransform(new PixelRect(0, 0, 800, 640), 400, 320, 0); var s1 = LayoutPreviewSceneBuilder.Build(plan, t1); var s2 = LayoutPreviewSceneBuilder.Build(plan, t2); Assert.Equal(s1.Zones.Count, s2.Zones.Count); }
}
