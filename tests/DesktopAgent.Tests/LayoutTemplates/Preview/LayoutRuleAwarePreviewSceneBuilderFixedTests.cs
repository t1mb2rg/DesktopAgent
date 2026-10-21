using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Poc.LayoutTemplates.Preview;

namespace DesktopAgent.Tests.LayoutTemplates.Preview;

public sealed class LayoutRuleAwarePreviewSceneBuilderFixedTests
{
    [Fact]
    public void ExcludeAndPin_FixedItemsBuildSceneWithoutTargetCoordinates()
    {
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(
            templateId,
            "T",
            1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[]
            {
                new DesktopLayoutZone(
                    zoneId,
                    "Z",
                    new NormalizedRect(0, 0, 10000, 10000),
                    0,
                    ZonePlacementFlow.ColumnMajor,
                    maxItemCount: 2)
            });
        var display = new DisplayDescriptor(
            "DISPLAY1",
            new PixelRect(0, 0, 800, 640),
            new PixelRect(0, 0, 800, 640),
            96,
            96,
            true);
        var icons = new[]
        {
            new DesktopIconSnapshotItem(
                0, "a", "A", null, ItemKind.File,
                new PixelPoint(0, 0), "DISPLAY1", false, true),
            new DesktopIconSnapshotItem(
                1, "b", "B", null, ItemKind.File,
                new PixelPoint(80, 0), "DISPLAY1", false, true)
        };
        var rules = new DesktopLayoutIconRuleSet(
            templateId,
            new[]
            {
                DesktopLayoutIconRule.ForExclude("a"),
                DesktopLayoutIconRule.ForPin("b")
            });

        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template,
            rules,
            new[] { display },
            null,
            new PixelPoint(80, 80),
            icons);
        var transform = new LayoutPreviewCanvasTransform(
            display.WorkArea,
            800,
            640,
            0);

        var scene = LayoutPreviewSceneBuilder.Build(plan, transform);

        Assert.Equal(2, scene.Icons.Count);
        Assert.All(scene.Icons, icon =>
        {
            Assert.Equal(DesktopLayoutPreviewItemState.Fixed, icon.State);
            Assert.Null(icon.TargetCanvasX);
            Assert.Null(icon.TargetCanvasY);
            Assert.Null(icon.ZoneId);
            Assert.Equal(string.Empty, icon.ZoneName);
        });
        Assert.Contains(scene.ItemRows, row => row.DispositionText == "Excluded" && row.PlacementStateText == "Fixed");
        Assert.Contains(scene.ItemRows, row => row.DispositionText == "Pinned" && row.PlacementStateText == "Fixed");
    }
}
