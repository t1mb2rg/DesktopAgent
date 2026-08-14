using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Resolution;

public sealed class DesktopExplorerGridPhaseResolverTests
{
    [Fact]
    public void TryInfer_StrongGridConsensus_ReturnsModalPhase()
    {
        var spacing = new PixelPoint(76, 83);
        var icons = new[]
        {
            Icon(0, "a", 62, 81),
            Icon(1, "b", 138, 164),
            Icon(2, "c", 214, 247),
            Icon(3, "d", -14, -2),
            Icon(4, "e", 290, 330),
        };

        var inference = DesktopExplorerGridPhaseResolver.TryInfer(icons, spacing);

        Assert.NotNull(inference);
        Assert.Equal(new PixelPoint(62, 81), inference!.Phase);
        Assert.Equal(5, inference.SampleCount);
        Assert.Equal(5, inference.ConsensusCount);
        Assert.Equal(1d, inference.ConsensusRatio);
    }

    [Fact]
    public void TryInfer_ScatteredFreePlacement_DoesNotGuessGridPhase()
    {
        var spacing = new PixelPoint(80, 80);
        var icons = new[]
        {
            Icon(0, "a", 3, 5),
            Icon(1, "b", 22, 17),
            Icon(2, "c", 41, 39),
            Icon(3, "d", 63, 71),
        };

        var inference = DesktopExplorerGridPhaseResolver.TryInfer(icons, spacing);

        Assert.Null(inference);
    }

    [Fact]
    public void TryInfer_IndependentAxisMajoritiesWithoutJointMajority_DoesNotInventPhase()
    {
        var spacing = new PixelPoint(80, 80);
        var icons = new[]
        {
            // X=10 has 3/5, Y=20 has 3/5, but pair (10,20) only 1/5.
            Icon(0, "a", 10, 20),
            Icon(1, "b", 90, 31),
            Icon(2, "c", 170, 42),
            Icon(3, "d", 23, 100),
            Icon(4, "e", 34, 180),
        };

        var inference = DesktopExplorerGridPhaseResolver.TryInfer(icons, spacing);

        Assert.Null(inference);
    }

    [Fact]
    public void Resolve_WithGridPhase_KeepsVisualBoundsAndMovesOnlyPlacementOrigin()
    {
        var zone = new DesktopLayoutZone(
            Guid.NewGuid(),
            "Z",
            new NormalizedRect(1000, 1000, 9000, 9000),
            0,
            ZonePlacementFlow.ColumnMajor,
            null);
        var template = new DesktopLayoutTemplate(
            Guid.NewGuid(),
            "T",
            1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { zone });
        var workArea = new PixelRect(0, 0, 1000, 1000);
        var spacing = new PixelPoint(80, 80);

        var legacy = DesktopLayoutTemplateResolver.Resolve(template, workArea, spacing);
        var aligned = DesktopLayoutTemplateResolver.Resolve(
            template,
            workArea,
            spacing,
            new PixelPoint(30, 50));

        Assert.Equal(legacy.Zones[0].PixelBounds, aligned.Zones[0].PixelBounds);
        Assert.Equal(new PixelPoint(110, 130), aligned.Zones[0].PlacementOrigin);
        Assert.Equal(30, PositiveModulo(aligned.Zones[0].PlacementOrigin.X, spacing.X));
        Assert.Equal(50, PositiveModulo(aligned.Zones[0].PlacementOrigin.Y, spacing.Y));
        Assert.True(aligned.Zones[0].ColumnCount <= legacy.Zones[0].ColumnCount);
        Assert.True(aligned.Zones[0].RowCount <= legacy.Zones[0].RowCount);
    }

    [Fact]
    public void PreviewPlanner_GridAlignedDesktop_ProducesTargetsOnSameExplorerPhase()
    {
        var templateId = Guid.NewGuid();
        var zone = new DesktopLayoutZone(
            Guid.NewGuid(),
            "Work",
            new NormalizedRect(0, 0, 10000, 10000),
            0,
            ZonePlacementFlow.ColumnMajor,
            null);
        var template = new DesktopLayoutTemplate(
            templateId,
            "T",
            1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { zone });
        var rules = DesktopLayoutIconRuleSet.Empty(templateId);
        var spacing = new PixelPoint(80, 80);
        var display = new DisplayDescriptor(
            "DISPLAY1",
            new PixelRect(0, 0, 800, 800),
            new PixelRect(0, 0, 800, 800),
            96,
            96,
            true);
        var icons = new[]
        {
            Icon(0, "a", 30, 50),
            Icon(1, "b", 110, 130),
            Icon(2, "c", 190, 210),
        };

        var preview = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template,
            rules,
            new[] { display },
            selectedTargetDeviceName: null,
            spacing,
            icons);

        Assert.Equal(new PixelPoint(30, 50), preview.ResolvedTemplate.Zones[0].PlacementOrigin);
        Assert.Same(preview.ResolvedTemplate, preview.PlacementPlan.ResolvedTemplate);
        Assert.All(preview.Items.Where(item => item.TargetPosition.HasValue), item =>
        {
            Assert.Equal(30, PositiveModulo(item.TargetPosition!.Value.X, spacing.X));
            Assert.Equal(50, PositiveModulo(item.TargetPosition!.Value.Y, spacing.Y));
        });
    }

    private static DesktopIconSnapshotItem Icon(int ordinal, string stableId, int x, int y)
        => new(
            ordinal,
            stableId,
            stableId,
            null,
            ItemKind.File,
            new PixelPoint(x, y),
            "DISPLAY1",
            false,
            true);

    private static int PositiveModulo(int value, int modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }
}
