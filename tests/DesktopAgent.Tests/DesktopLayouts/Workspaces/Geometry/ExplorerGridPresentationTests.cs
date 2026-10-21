using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class ExplorerGridPresentationTests
{
    private static ExplorerDisplayGeometry Display(uint dpi = 96) => new("primary", "DISPLAY1",
        new(-400, 0, 30, 290), new(-400, 0, 30, 290), true, dpi, dpi, new(80, 80), new(30, 50), new(-370, 50), 5, 3,
        Enumerable.Range(0, 15).Select(index => new ExplorerNativeSlot($"s{index}", index % 5, index / 5,
            new(-370 + index % 5 * 80, 50 + index / 5 * 80))));

    [Fact]
    public void BoundariesUseMeasuredOriginAndSpacingInsteadOfWorkAreaPercentages()
    {
        var display = Display();
        var rect = ExplorerGridPresentation.Quantize(new(0, 0, 1, 1), display);
        Assert.Equal(new PixelRect(-370, 50, 30, 290), ExplorerGridPresentation.PixelBounds(rect, display));
        Assert.Equal(new PixelRect(-290, 130, -130, 210), ExplorerGridPresentation.PixelBounds(new(1, 1, 2, 1), display));
    }

    [Fact]
    public void SampleCenterDoesNotChangeNativeAnchor()
    {
        var native = new PixelPoint(-370, 50);
        Assert.Equal((-330d, 90d), ExplorerGridPresentation.CellCenter(native, new(80, 80)));
        Assert.Equal(new PixelPoint(-370, 50), native);
    }

    [Fact]
    public void DesktopOverlayUsesPhysicalGridOriginForItsNormalizedBounds()
    {
        var display = Display();
        var zone = new CompiledWorkspaceZone("work", "工作", "primary", new(0, 0, 1, 1), new(0, 0, 1, 1),
            new(0, 0, 5, 3), DesktopWorkspaceAnchorKind.TopLeft, DesktopWorkspacePackingOrder.RowMajor, 15);
        var projected = Assert.Single(DesktopWorkspaceOverlayProjection.Project([zone], display));
        Assert.Equal(0, projected.Bounds.Left);
        Assert.Equal(0, projected.Bounds.Top);
        Assert.Equal(10000, projected.Bounds.Right);
        Assert.Equal(10000, projected.Bounds.Bottom);
    }

    [Fact]
    public void FrameReachesWorkAreaEdgesWithoutChangingNativeCells()
    {
        var display = Display();
        var rect = new DesktopWorkspaceNativeRect(0, 0, 5, 3);
        Assert.Equal(display.WorkArea, ExplorerGridPresentation.FrameBounds(rect, display));
        Assert.Equal(new PixelRect(-370, 50, 30, 290), ExplorerGridPresentation.PixelBounds(rect, display));
        Assert.Equal(new PixelPoint(-370, 50), display.Slots[0].PixelPosition);
    }

    [Fact]
    public void AdjacentFramesShareEdgesWithoutGapsOrOverlaps()
    {
        var display = Display();
        var top = ExplorerGridPresentation.FrameBounds(new(0, 0, 5, 1), display);
        var bottom = ExplorerGridPresentation.FrameBounds(new(0, 1, 5, 2), display);
        Assert.Equal(top.Bottom, bottom.Top);
        Assert.Equal(display.WorkArea.Top, top.Top);
        Assert.Equal(display.WorkArea.Bottom, bottom.Bottom);
        var left = ExplorerGridPresentation.FrameBounds(new(0, 0, 2, 3), display);
        var right = ExplorerGridPresentation.FrameBounds(new(2, 0, 3, 3), display);
        Assert.Equal(left.Right, right.Left);
        Assert.Equal(display.WorkArea.Left, left.Left);
        Assert.Equal(display.WorkArea.Right, right.Right);
    }

    [Fact]
    public void InteriorFrameKeepsGridBoundaryInsteadOfFillingIntentionalEmptySpace()
    {
        var display = Display();
        var rect = new DesktopWorkspaceNativeRect(1, 1, 2, 1);
        Assert.Equal(ExplorerGridPresentation.PixelBounds(rect, display), ExplorerGridPresentation.FrameBounds(rect, display));
    }

    [Fact]
    public void SnappedBoundsRoundTripWithoutDrift()
    {
        var display = Display();
        var rect = ExplorerGridPresentation.Quantize(new(0.19, 0.34, 0.41, 0.31), display);
        Assert.Equal(new DesktopWorkspaceNativeRect(1, 1, 2, 1), rect);
        for (var index = 0; index < 100; index++)
            Assert.Equal(rect, ExplorerGridPresentation.Quantize(ExplorerGridPresentation.Relative(rect, display), display));
    }

    [Fact]
    public void MoveClampsToNativeSurfaceAndKeepsCellSize()
    {
        var display = Display();
        var bounds = ExplorerGridPresentation.Move(new(0, 0, 2, 1), 99, -99, display);
        Assert.Equal(new DesktopWorkspaceNativeRect(3, 0, 2, 1), ExplorerGridPresentation.Quantize(bounds, display));
    }

    [Fact]
    public void LegacyEditorSnapsToSamePhysicalBoundaries()
    {
        var display = Display();
        var zone = new DesktopLayoutZone(Guid.NewGuid(), "工作", new(0, 0, 10000, 10000), 0, ZonePlacementFlow.RowMajor, null);
        var snapped = ExplorerGridPresentation.SnapLegacyZone(zone, display);
        Assert.Equal(698, snapped.Bounds.Left);
        Assert.Equal(1724, snapped.Bounds.Top);
        Assert.Equal(snapped.Bounds, ExplorerGridPresentation.SnapLegacyZone(snapped, display).Bounds);
    }

    [Theory]
    [InlineData(96, 2, 1)]
    [InlineData(192, 4, 2)]
    public void LabelOccupancyUsesDpiAndBottomRightCells(uint dpi, int columns, int rows)
    {
        var label = ExplorerGridPresentation.LabelCells(new(0, 0, 5, 3), Display(dpi));
        Assert.Equal(columns, label.ColumnCount);
        Assert.Equal(rows, label.RowCount);
        Assert.Equal(5, label.ExclusiveColumn);
        Assert.Equal(3, label.ExclusiveRow);
    }

    [Theory]
    [InlineData(13, true)]
    [InlineData(14, false)]
    public void LabelReservationReducesRealCapacityAndIsIndependentlyChecked(int count, bool success)
    {
        var display = Display();
        var geometry = new ExplorerWorkspaceGeometry(Guid.NewGuid(), "measured-test", false, display.Spacing, [display]);
        var draft = new DesktopWorkspaceDesignDraft(Guid.NewGuid(), "工作",
            displayPolicies: [new("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones: [new("work", "工作", "primary", new(0, 0, 1, 1))], categoryRules: [new("docs", "work")]);
        var prepared = DesktopWorkspaceZonePresentation.WithLabelReservations(draft, geometry);
        Assert.Single(prepared.ReservedRegions);
        Assert.Single(DesktopWorkspaceZonePresentation.WithLabelReservations(prepared, geometry).ReservedRegions);
        var blueprint = prepared.ToBlueprint();
        var facts = Enumerable.Range(0, count).Select(index => new DesktopWorkspaceCompilationItemFact($"i{index}", "primary",
            display.Slots[index].PixelPosition, true)).ToArray();
        var semantic = new DesktopSemanticUnderstanding("文档", [new("docs", "文档", "文档")],
            facts.Select(fact => new DesktopSemanticAssignment(fact.StableId, "docs", 1, "文档")));
        var result = DesktopWorkspaceCompiler.Compile(blueprint, semantic, geometry, facts);
        Assert.Equal(success, result.Succeeded);
        if (success)
        {
            Assert.Equal(13, Assert.Single(result.Plan!.Zones).AvailableSlotCount);
            Assert.True(DesktopWorkspaceSemanticEquivalenceChecker.Check(blueprint, geometry, result.Plan, facts).Equivalent);
            Assert.DoesNotContain(result.Plan.Items, item => item.TargetPixelPosition is { } position && position.Y == 210 && position.X >= -130);
        }
        else Assert.Contains(result.Issues, issue => issue.Code == DesktopWorkspaceCompileIssueCode.ZoneCapacityInsufficient && issue.AvailableSlots == 13);
    }
}
