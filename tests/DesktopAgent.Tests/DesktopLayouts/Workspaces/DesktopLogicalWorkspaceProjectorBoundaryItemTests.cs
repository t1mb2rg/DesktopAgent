using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopLogicalWorkspaceProjectorBoundaryItemTests
{
    private static readonly PixelPoint Spacing = new(100, 100);

    [Fact]
    public void Project_ExistingIconInPhysicalTailOutsideFullGrid_IsConservativelyMappedToBoundaryCell()
    {
        var result = DesktopLogicalWorkspaceProjector.Project(CreateObservation(
        [
            Icon("a", "A", new PixelPoint(10, 20)),
            Icon("b", "B", new PixelPoint(110, 120)),
            Icon("tail", "Tail", new PixelPoint(910, 20)),
        ]));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Issues);

        var tail = Assert.Single(result.Workspace!.Items, static item => item.DisplayName == "Tail");
        Assert.Equal("primary", tail.CurrentDisplayRef);
        Assert.Equal(new DesktopLogicalGridCell(8, 0), tail.CurrentCell);
        Assert.False(tail.IsGridAligned);
        Assert.Equal(new[] { new DesktopLogicalGridCell(8, 0) }, tail.OccupiedCells);
    }

    [Fact]
    public void Project_IconOutsideAssignedPhysicalDisplayBounds_RemainsFailClosed()
    {
        var result = DesktopLogicalWorkspaceProjector.Project(CreateObservation(
        [
            Icon("a", "A", new PixelPoint(10, 20)),
            Icon("b", "B", new PixelPoint(110, 120)),
            Icon("outside", "Outside", new PixelPoint(1_010, 20)),
        ]));

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Contains(result.Issues, static issue =>
            issue.Code == DesktopLogicalWorkspaceProjectionIssueCode.IconOutsideDisplayGrid &&
            issue.ItemDisplayName == "Outside" &&
            issue.DisplayRef == "primary");
    }

    private static DesktopObservation CreateObservation(IReadOnlyList<DesktopIconDescriptor> icons)
        => new(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
            [
                new DisplayDescriptor(
                    @"\\.\DISPLAY1",
                    new PixelRect(0, 0, 1_000, 600),
                    new PixelRect(0, 0, 1_000, 600),
                    96,
                    96,
                    true),
            ],
            new DesktopIconPositioningCapabilities(
                false,
                Spacing,
                ConnectionMethod.FindWindowSW,
                null),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(10));

    private static DesktopIconDescriptor Icon(
        string stableId,
        string displayName,
        PixelPoint position)
        => new(
            stableId,
            displayName,
            null,
            ItemKind.File,
            position,
            @"\\.\DISPLAY1",
            false,
            true);
}
