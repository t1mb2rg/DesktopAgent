using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopObservedLogicalWorkspaceSystemItemTests
{
    [Fact]
    public void SystemItem_CannotEnterProviderWorkspaceAsMovable()
    {
        var item = new DesktopObservedLogicalItem(
            "item-0001",
            "回收站",
            ItemKind.SystemItem,
            "primary",
            new DesktopLogicalGridCell(0, 0),
            isMovable: true);

        Assert.False(item.IsMovable);
    }

    [Fact]
    public void Validator_RejectsPlaceForSystemItemEvenWhenCallerRequestedMovable()
    {
        var observationId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var workspace = new DesktopObservedLogicalWorkspace(
            observationId,
            "topology-v1:test",
            "grid-v1:test",
            [
                new DesktopObservedLogicalDisplay(
                    "primary",
                    true,
                    new DesktopLogicalGridSize(5, 5)),
            ],
            [
                new DesktopObservedLogicalItem(
                    "item-0001",
                    "回收站",
                    ItemKind.SystemItem,
                    "primary",
                    new DesktopLogicalGridCell(0, 0),
                    isMovable: true),
            ]);
        var plan = new DesktopDesiredWorkspacePlan(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            observationId,
            workspace.TopologyFingerprint,
            workspace.GridFingerprint,
            zones:
            [
                new DesktopDesiredWorkspaceZone(
                    "zone-main",
                    "主区域",
                    "primary",
                    new DesktopLogicalGridRect(0, 0, 5, 5)),
            ],
            items:
            [
                DesktopDesiredWorkspaceItem.Place(
                    "item-0001",
                    "primary",
                    new DesktopLogicalGridCell(1, 0),
                    "zone-main"),
            ]);

        var validation = DesktopDesiredWorkspacePlanValidator.Validate(workspace, plan);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, static issue =>
            issue.Code == DesktopDesiredWorkspacePlanIssueCode.FixedItemMoveRequested &&
            issue.ItemRef == "item-0001");
    }
}
