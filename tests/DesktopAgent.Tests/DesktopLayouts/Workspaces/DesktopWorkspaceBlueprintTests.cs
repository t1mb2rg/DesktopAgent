using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopWorkspaceBlueprintTests
{
    [Fact]
    public void ValidBlueprint_PreservesResolutionIndependentWorkspaceDesign()
    {
        var blueprintId = Guid.NewGuid();
        var blueprint = new DesktopWorkspaceBlueprint(
            blueprintId,
            revision: 3,
            designSummary: "主屏中央保留壁纸，下方副屏承载工具与工作区，左侧副屏保持不变。",
            displayPolicies:
            [
                new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design),
                new DesktopWorkspaceBlueprintDisplayPolicy("secondary_lower", DesktopWorkspaceDisplayPolicyKind.Design),
                new DesktopWorkspaceBlueprintDisplayPolicy("secondary_left", DesktopWorkspaceDisplayPolicyKind.Preserve),
            ],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "工具",
                    "secondary_lower",
                    new DesktopWorkspaceRelativeRect(0d, 0d, 0.35d, 0.65d),
                    DesktopWorkspaceAnchorKind.TopLeft,
                    DesktopWorkspacePackingOrder.ColumnMajor),
                new DesktopWorkspaceBlueprintZone(
                    "work",
                    "工作",
                    "secondary_lower",
                    new DesktopWorkspaceRelativeRect(0.35d, 0d, 0.65d, 0.65d)),
                new DesktopWorkspaceBlueprintZone(
                    "games",
                    "娱乐",
                    "secondary_lower",
                    new DesktopWorkspaceRelativeRect(0d, 0.65d, 1d, 0.35d)),
            ],
            zoneRelations:
            [
                new DesktopWorkspaceBlueprintZoneRelation(
                    "tools",
                    DesktopWorkspaceZoneRelationKind.LeftOf,
                    "work"),
                new DesktopWorkspaceBlueprintZoneRelation(
                    "games",
                    DesktopWorkspaceZoneRelationKind.Below,
                    "tools"),
            ],
            categoryRules:
            [
                new DesktopWorkspaceBlueprintCategoryRule("development", "tools"),
                new DesktopWorkspaceBlueprintCategoryRule("study", "work"),
                new DesktopWorkspaceBlueprintCategoryRule("entertainment", "games"),
            ],
            itemOverrides:
            [
                new DesktopWorkspaceBlueprintItemOverride(
                    "item-0007",
                    DesktopWorkspaceItemOverrideKind.PlaceInZone,
                    "tools",
                    DesktopWorkspaceAnchorKind.TopLeft),
                new DesktopWorkspaceBlueprintItemOverride(
                    "item-0012",
                    DesktopWorkspaceItemOverrideKind.Keep),
            ],
            reservedRegions:
            [
                new DesktopWorkspaceBlueprintReservedRegion(
                    "primary-wallpaper-center",
                    "保持主屏中央壁纸可见",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0.2d, 0.2d, 0.6d, 0.6d)),
            ],
            parentRevision: 2);

        Assert.Equal(blueprintId, blueprint.BlueprintId);
        Assert.Equal(3, blueprint.Revision);
        Assert.Equal(2, blueprint.ParentRevision);
        Assert.Equal(DesktopWorkspaceBlueprint.CurrentSchemaVersion, blueprint.SchemaVersion);
        Assert.Equal(3, blueprint.Zones.Count);
        Assert.Equal(3, blueprint.CategoryRules.Count);
        Assert.Equal(2, blueprint.ItemOverrides.Count);
        Assert.Single(blueprint.ReservedRegions);
        Assert.Equal(
            DesktopWorkspaceDisplayPolicyKind.Preserve,
            blueprint.DisplayPolicies.Single(item => item.DisplayRef == "secondary_left").Policy);

        var tools = blueprint.Zones.Single(zone => zone.ZoneKey == "tools");
        Assert.Equal(0.35d, tools.Bounds.Width);
        Assert.Equal(DesktopWorkspaceAnchorKind.TopLeft, tools.Anchor);
        Assert.Equal(DesktopWorkspacePackingOrder.ColumnMajor, tools.PackingOrder);
    }

    [Theory]
    [InlineData(-0.01, 0, 0.5, 0.5)]
    [InlineData(0, -0.01, 0.5, 0.5)]
    [InlineData(0, 0, 0, 0.5)]
    [InlineData(0, 0, 0.5, 0)]
    [InlineData(0.7, 0, 0.4, 0.5)]
    [InlineData(0, 0.7, 0.5, 0.4)]
    public void RelativeRect_RejectsOutOfDesignSpace(
        double left,
        double top,
        double width,
        double height)
        => Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
            new DesktopWorkspaceRelativeRect(left, top, width, height));

    [Fact]
    public void Blueprint_RejectsCategoryRuleReferencingUnknownZone()
    {
        Assert.Throws<ArgumentException>(() => new DesktopWorkspaceBlueprint(
            Guid.NewGuid(),
            1,
            "design",
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    "secondary_1",
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1)),
            ],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule("dev", "missing")])) ;
    }

    [Fact]
    public void Blueprint_RejectsZoneOnPreservedDisplay()
    {
        Assert.Throws<ArgumentException>(() => new DesktopWorkspaceBlueprint(
            Guid.NewGuid(),
            1,
            "preserve left display",
            displayPolicies:
            [new DesktopWorkspaceBlueprintDisplayPolicy("secondary_left", DesktopWorkspaceDisplayPolicyKind.Preserve)],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    "secondary_left",
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1)),
            ]));
    }

    [Fact]
    public void Blueprint_RejectsCrossDisplayZoneRelation()
    {
        Assert.Throws<ArgumentException>(() => new DesktopWorkspaceBlueprint(
            Guid.NewGuid(),
            1,
            "invalid relation",
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "left",
                    "Left",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0, 0, 0.5, 1)),
                new DesktopWorkspaceBlueprintZone(
                    "right",
                    "Right",
                    "secondary_1",
                    new DesktopWorkspaceRelativeRect(0.5, 0, 0.5, 1)),
            ],
            zoneRelations:
            [
                new DesktopWorkspaceBlueprintZoneRelation(
                    "left",
                    DesktopWorkspaceZoneRelationKind.LeftOf,
                    "right"),
            ]));
    }

    [Fact]
    public void ItemOverride_KeepAndExcludeCannotSmuggleZoneOrAnchor()
    {
        Assert.Throws<ArgumentException>(() => new DesktopWorkspaceBlueprintItemOverride(
            "item-1",
            DesktopWorkspaceItemOverrideKind.Keep,
            zoneKey: "tools"));

        Assert.Throws<ArgumentException>(() => new DesktopWorkspaceBlueprintItemOverride(
            "item-2",
            DesktopWorkspaceItemOverrideKind.Exclude,
            anchor: DesktopWorkspaceAnchorKind.TopLeft));
    }

    [Fact]
    public void Revision_RequiresStrictlyOlderParent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopWorkspaceBlueprint(
            Guid.NewGuid(),
            revision: 2,
            designSummary: "design",
            parentRevision: 2));
    }

    [Fact]
    public void Contract_DoesNotExposeExplorerOrExecutionAuthority()
    {
        var contractTypes = new[]
        {
            typeof(DesktopWorkspaceBlueprint),
            typeof(DesktopWorkspaceBlueprintZone),
            typeof(DesktopWorkspaceBlueprintReservedRegion),
            typeof(DesktopWorkspaceBlueprintItemOverride),
        };
        var forbiddenNames = new[]
        {
            "Pixel",
            "Cell",
            "Grid",
            "StableId",
            "DeviceName",
            "ObservationId",
            "TopologyFingerprint",
            "Candidate",
            "Writer",
            "ExecutionPlan",
            "RecoverySession",
        };

        foreach (var type in contractTypes)
        {
            foreach (var property in type.GetProperties())
            {
                Assert.DoesNotContain(
                    forbiddenNames,
                    forbidden => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
