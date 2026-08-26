using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopLogicalWorkspaceFrozenPreviewTests
{
    [Fact]
    public void Create_PrimaryHasZeroTargetsButReservedAndOutbound_PreservesBothDisplays()
    {
        var observationId = Guid.NewGuid();
        var workspace = new DesktopObservedLogicalWorkspace(
            observationId,
            "topology-v1:test",
            "grid-v1:test",
            [
                new DesktopObservedLogicalDisplay(
                    "primary",
                    isPrimary: true,
                    new DesktopLogicalTopologyRect(0, 0, 6000, 10000),
                    new DesktopLogicalGridSize(10, 8)),
                new DesktopObservedLogicalDisplay(
                    "secondary_1",
                    isPrimary: false,
                    new DesktopLogicalTopologyRect(6000, 0, 10000, 10000),
                    new DesktopLogicalGridSize(8, 8)),
            ],
            [
                new DesktopObservedLogicalItem(
                    "item-0001", "项目一", ItemKind.File, "primary",
                    new DesktopLogicalGridCell(0, 0), isMovable: true),
                new DesktopObservedLogicalItem(
                    "item-0002", "项目二", ItemKind.Folder, "primary",
                    new DesktopLogicalGridCell(1, 0), isMovable: true),
            ]);
        var plan = new DesktopDesiredWorkspacePlan(
            Guid.NewGuid(),
            observationId,
            workspace.TopologyFingerprint,
            workspace.GridFingerprint,
            zones:
            [
                new DesktopDesiredWorkspaceZone(
                    "secondary-work", "副屏工作区", "secondary_1",
                    new DesktopLogicalGridRect(0, 0, 8, 8)),
            ],
            reservedRegions:
            [
                new DesktopDesiredWorkspaceReservedRegion(
                    "primary-wallpaper", "展示壁纸", "primary",
                    new DesktopLogicalGridRect(2, 1, 6, 6)),
            ],
            items:
            [
                DesktopDesiredWorkspaceItem.Place(
                    "item-0001", "secondary_1", new DesktopLogicalGridCell(0, 0), "secondary-work"),
                DesktopDesiredWorkspaceItem.Place(
                    "item-0002", "secondary_1", new DesktopLogicalGridCell(1, 0), "secondary-work"),
            ]);

        var preview = DesktopLogicalWorkspaceFrozenPreviewProjector.Create(workspace, plan);

        Assert.Equal(2, preview.Displays.Count);
        Assert.Equal(2, preview.CrossDisplayAssignmentCount);
        Assert.Equal(2, preview.MoveCount);

        var primary = Assert.Single(preview.Displays, static display => display.IsPrimary);
        Assert.Equal("primary", primary.DisplayRef);
        Assert.Equal(2, primary.SourceItemCount);
        Assert.Equal(0, primary.TargetPlacementCount);
        Assert.Equal(2, primary.OutboundCount);
        Assert.Equal(0, primary.InboundCount);
        Assert.Single(primary.ReservedRegions);
        Assert.Equal("primary-wallpaper", primary.ReservedRegions[0].RegionKey);

        var secondary = Assert.Single(preview.Displays, static display => !display.IsPrimary);
        Assert.Equal("secondary_1", secondary.DisplayRef);
        Assert.Equal(0, secondary.SourceItemCount);
        Assert.Equal(2, secondary.TargetPlacementCount);
        Assert.Equal(2, secondary.InboundCount);
        Assert.Equal(0, secondary.OutboundCount);
    }

    [Fact]
    public void Create_InvalidPlan_IsRejectedInsteadOfBestEffortPreview()
    {
        var workspace = new DesktopObservedLogicalWorkspace(
            Guid.NewGuid(),
            "topology-v1:test",
            "grid-v1:test",
            [new DesktopObservedLogicalDisplay(
                "primary", true, new DesktopLogicalGridSize(4, 4))],
            [new DesktopObservedLogicalItem(
                "item-0001", "项目", ItemKind.File, "primary",
                new DesktopLogicalGridCell(0, 0), isMovable: true)]);
        var invalid = new DesktopDesiredWorkspacePlan(
            Guid.NewGuid(),
            workspace.ObservationId,
            workspace.TopologyFingerprint,
            workspace.GridFingerprint,
            items: []);

        var error = Assert.Throws<ArgumentException>(() =>
            DesktopLogicalWorkspaceFrozenPreviewProjector.Create(workspace, invalid));

        Assert.Contains("有效完整终态", error.Message, StringComparison.Ordinal);
    }
}
