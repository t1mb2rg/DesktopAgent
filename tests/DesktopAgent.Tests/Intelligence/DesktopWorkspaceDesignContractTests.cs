using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopWorkspaceDesignContractTests
{
    [Fact]
    public void Request_UsesCategorySummaryAndOnlyDesignCriticalItems()
    {
        var request = CreateInitialRequest(
            itemCount: 130,
            criticalItems:
            [
                new DesktopWorkspaceDesignCriticalItem(
                    "item-0001",
                    "Visual Studio Code",
                    "dev",
                    "primary",
                    isMovable: true),
            ]);

        Assert.Single(request.Categories);
        Assert.Equal(130, request.Categories[0].ItemCount);
        Assert.Single(request.CriticalItems);
        Assert.Equal("item-0001", request.CriticalItems[0].ItemRef);
        Assert.Equal(3, request.Displays.Count);
    }

    [Fact]
    public void RequestContract_DoesNotExposeWindowsOrExecutionAuthority()
    {
        var providerContractTypes = new[]
        {
            typeof(DesktopWorkspaceDesignRevisionTarget),
            typeof(DesktopWorkspaceDesignDisplay),
            typeof(DesktopWorkspaceDesignCategorySummary),
            typeof(DesktopWorkspaceDesignCriticalItem),
            typeof(DesktopWorkspaceDesignRequest),
        };

        var forbiddenTokens = new[]
        {
            "StableId",
            "DeviceName",
            "Pixel",
            "Cell",
            "Grid",
            "ObservationId",
            "Candidate",
            "ExecutionPlan",
            "RecoverySession",
            "Writer",
        };

        foreach (var type in providerContractTypes)
        {
            var propertyNames = type.GetProperties().Select(static property => property.Name).ToArray();
            foreach (var token in forbiddenTokens)
            {
                Assert.DoesNotContain(
                    propertyNames,
                    name => name.Contains(token, StringComparison.OrdinalIgnoreCase));
            }
        }

        Assert.Null(typeof(DesktopWorkspaceDesignRequest).GetProperty("SemanticUnderstanding"));
        Assert.Null(typeof(DesktopWorkspaceDesignCriticalItem).GetProperty("StableId"));
    }

    [Fact]
    public void Request_RequiresExactlyOnePrimaryDisplay()
    {
        var target = new DesktopWorkspaceDesignRevisionTarget(Guid.NewGuid(), 1);
        var categories = new[]
        {
            new DesktopWorkspaceDesignCategorySummary("dev", "Development", "developer tools", 10),
        };

        Assert.Throws<ArgumentException>(() => new DesktopWorkspaceDesignRequest(
            target,
            "summary",
            "design it",
            [
                new DesktopWorkspaceDesignDisplay(
                    "display-a",
                    "A",
                    isPrimary: false,
                    new DesktopWorkspaceRelativeRect(0, 0, 0.5, 1),
                    16d / 9d),
                new DesktopWorkspaceDesignDisplay(
                    "display-b",
                    "B",
                    isPrimary: false,
                    new DesktopWorkspaceRelativeRect(0.5, 0, 0.5, 1),
                    16d / 9d),
            ],
            categories));
    }

    [Fact]
    public void Request_RejectsCriticalItemReferencingUnknownDisplayOrCategory()
    {
        Assert.Throws<ArgumentException>(() => CreateInitialRequest(
            criticalItems:
            [
                new DesktopWorkspaceDesignCriticalItem(
                    "item-1",
                    "Tool",
                    "missing-category",
                    "primary",
                    isMovable: true),
            ]));

        Assert.Throws<ArgumentException>(() => CreateInitialRequest(
            criticalItems:
            [
                new DesktopWorkspaceDesignCriticalItem(
                    "item-1",
                    "Tool",
                    "dev",
                    "missing-display",
                    isMovable: true),
            ]));
    }

    [Fact]
    public void RevisionRequest_BindsPreviousBlueprintToLocalRevisionChain()
    {
        var blueprintId = Guid.NewGuid();
        var previous = CreateBlueprint(blueprintId, revision: 1, parentRevision: null);
        var request = CreateRequest(
            new DesktopWorkspaceDesignRevisionTarget(blueprintId, revision: 2, parentRevision: 1),
            previousBlueprint: previous);

        Assert.Same(previous, request.PreviousBlueprint);
        Assert.Equal(2, request.RevisionTarget.Revision);
        Assert.Equal(1, request.RevisionTarget.ParentRevision);

        var wrongBlueprint = CreateBlueprint(Guid.NewGuid(), revision: 1, parentRevision: null);
        Assert.Throws<ArgumentException>(() => CreateRequest(
            new DesktopWorkspaceDesignRevisionTarget(blueprintId, revision: 2, parentRevision: 1),
            previousBlueprint: wrongBlueprint));
    }

    [Fact]
    public void ProviderSuccess_MustReturnRequestedRevisionIdentity()
    {
        var request = CreateInitialRequest();
        var good = CreateBlueprint(
            request.RevisionTarget.BlueprintId,
            request.RevisionTarget.Revision,
            request.RevisionTarget.ParentRevision);

        var result = DesktopWorkspaceDesignProviderResult.Success(request, good);

        Assert.True(result.Succeeded);
        Assert.Same(good, result.Blueprint);

        var wrong = CreateBlueprint(Guid.NewGuid(), revision: 1, parentRevision: null);
        Assert.Throws<ArgumentException>(() =>
            DesktopWorkspaceDesignProviderResult.Success(request, wrong));
    }

    [Fact]
    public void RevisionTarget_RequiresExplicitParentAfterRevisionOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopWorkspaceDesignRevisionTarget(Guid.NewGuid(), revision: 2));
        Assert.Throws<ArgumentException>(() =>
            new DesktopWorkspaceDesignRevisionTarget(Guid.NewGuid(), revision: 1, parentRevision: 1));
    }

    private static DesktopWorkspaceDesignRequest CreateInitialRequest(
        int itemCount = 12,
        IEnumerable<DesktopWorkspaceDesignCriticalItem>? criticalItems = null)
    {
        var target = new DesktopWorkspaceDesignRevisionTarget(Guid.NewGuid(), 1);
        return CreateRequest(target, itemCount, criticalItems);
    }

    private static DesktopWorkspaceDesignRequest CreateRequest(
        DesktopWorkspaceDesignRevisionTarget target,
        int itemCount = 12,
        IEnumerable<DesktopWorkspaceDesignCriticalItem>? criticalItems = null,
        DesktopWorkspaceBlueprint? previousBlueprint = null)
        => new(
            target,
            "桌面主要是开发与学习内容。",
            "主屏中间留空，所有可移动项目整理到下方副屏，左侧副屏保持不动。",
            [
                new DesktopWorkspaceDesignDisplay(
                    "left-secondary",
                    "左侧副屏",
                    isPrimary: false,
                    new DesktopWorkspaceRelativeRect(0.00, 0.00, 0.25, 0.65),
                    16d / 9d),
                new DesktopWorkspaceDesignDisplay(
                    "primary",
                    "主屏",
                    isPrimary: true,
                    new DesktopWorkspaceRelativeRect(0.25, 0.00, 0.50, 0.65),
                    16d / 9d),
                new DesktopWorkspaceDesignDisplay(
                    "lower-secondary",
                    "下方副屏",
                    isPrimary: false,
                    new DesktopWorkspaceRelativeRect(0.25, 0.65, 0.50, 0.35),
                    16d / 9d),
            ],
            [new DesktopWorkspaceDesignCategorySummary("dev", "开发工具", "IDE、终端和工程工具", itemCount)],
            criticalItems,
            previousBlueprint);

    private static DesktopWorkspaceBlueprint CreateBlueprint(
        Guid blueprintId,
        int revision,
        int? parentRevision)
        => new(
            blueprintId,
            revision,
            "工具放在下方副屏，主屏保留中央壁纸区域，左侧副屏保持不动。",
            displayPolicies:
            [
                new DesktopWorkspaceBlueprintDisplayPolicy(
                    "left-secondary",
                    DesktopWorkspaceDisplayPolicyKind.Preserve),
                new DesktopWorkspaceBlueprintDisplayPolicy(
                    "primary",
                    DesktopWorkspaceDisplayPolicyKind.Design),
                new DesktopWorkspaceBlueprintDisplayPolicy(
                    "lower-secondary",
                    DesktopWorkspaceDisplayPolicyKind.Design),
            ],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    "lower-secondary",
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1)),
            ],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule("dev", "tools")],
            reservedRegions:
            [
                new DesktopWorkspaceBlueprintReservedRegion(
                    "wallpaper-center",
                    "保持主屏中间壁纸可见",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0.25, 0.20, 0.50, 0.60)),
            ],
            parentRevision: parentRevision);
}
