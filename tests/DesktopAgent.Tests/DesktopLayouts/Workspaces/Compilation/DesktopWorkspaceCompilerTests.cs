using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Compilation;

public sealed class DesktopWorkspaceCompilerTests
{
    [Fact]
    public void Compile_SameFactsInDifferentOrder_ProducesSameExactPlan()
    {
        var geometry = Geometry(Display("primary", "DISPLAY1", isPrimary: true, columns: 8, rows: 4));
        var blueprint = new DesktopWorkspaceBlueprint(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            1,
            "开发工具放左侧，学习内容放右侧。",
            displayPolicies:
            [new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "work",
                    "Work",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0.375, 0, 0.625, 1)),
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0, 0, 0.375, 1)),
            ],
            zoneRelations:
            [new DesktopWorkspaceBlueprintZoneRelation("tools", DesktopWorkspaceZoneRelationKind.LeftOf, "work")],
            categoryRules:
            [
                new DesktopWorkspaceBlueprintCategoryRule("study", "work"),
                new DesktopWorkspaceBlueprintCategoryRule("dev", "tools"),
            ]);
        var semanticA = Understanding(
            ("b", "dev"),
            ("a", "dev"),
            ("d", "study"),
            ("c", "study"));
        var semanticB = Understanding(
            ("c", "study"),
            ("d", "study"),
            ("a", "dev"),
            ("b", "dev"));
        var factsA = new[]
        {
            Fact("d", 350, 290),
            Fact("a", 110, 130),
            Fact("c", 270, 210),
            Fact("b", 190, 130),
        };
        var factsB = factsA.Reverse().ToArray();

        var first = DesktopWorkspaceCompiler.Compile(blueprint, semanticA, geometry, factsA);
        var second = DesktopWorkspaceCompiler.Compile(blueprint, semanticB, geometry, factsB);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(first.Plan!.PlanFingerprint, second.Plan!.PlanFingerprint);
        Assert.Equal(
            first.Plan.Items.Select(ItemSignature),
            second.Plan.Items.Select(ItemSignature));
        Assert.Equal("tools", first.Plan.Items.Single(item => item.StableId == "a").ZoneKey);
        Assert.Equal("work", first.Plan.Items.Single(item => item.StableId == "c").ZoneKey);
    }

    [Fact]
    public void Compile_QuantizesRelativeBoundsToNearestNativeBoundaryOnly()
    {
        var geometry = Geometry(Display("primary", "DISPLAY1", true, columns: 8, rows: 4));
        var blueprint = BlueprintForSingleZone(
            new DesktopWorkspaceRelativeRect(0, 0, 0.35, 1),
            categoryKey: "dev");
        var semantic = Understanding(("a", "dev"));

        var result = DesktopWorkspaceCompiler.Compile(
            blueprint,
            semantic,
            geometry,
            [Fact("a", 430, 210)]);

        Assert.True(result.Succeeded);
        var zone = Assert.Single(result.Plan!.Zones);
        // 8 × 35% = 2.8，因此 nearest Explorer boundary 是 3/8 = 37.5%。
        Assert.Equal(3, zone.NativeRect.ColumnCount);
        Assert.Equal(0.375d, zone.RealizedBounds.Width, precision: 6);
        Assert.Equal(0.35d, zone.RequestedBounds.Width, precision: 6);
    }

    [Fact]
    public void Compile_InsufficientCapacity_FailsInsteadOfResizingZone()
    {
        var geometry = Geometry(Display("primary", "DISPLAY1", true, columns: 4, rows: 2));
        var blueprint = BlueprintForSingleZone(
            new DesktopWorkspaceRelativeRect(0, 0, 0.25, 1),
            categoryKey: "dev");
        var semantic = Understanding(("a", "dev"), ("b", "dev"), ("c", "dev"));

        var result = DesktopWorkspaceCompiler.Compile(
            blueprint,
            semantic,
            geometry,
            [Fact("a", 110, 130), Fact("b", 190, 130), Fact("c", 270, 130)]);

        Assert.False(result.Succeeded);
        Assert.Null(result.Plan);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DesktopWorkspaceCompileIssueCode.ZoneCapacityInsufficient, issue.Code);
        Assert.Equal(3, issue.RequiredSlots);
        Assert.Equal(2, issue.AvailableSlots);
    }

    [Fact]
    public void Compile_PreserveDisplay_KeepsCurrentItemsWithoutInventingCategoryRule()
    {
        var geometry = Geometry(
            Display("secondary_1", "DISPLAY2", false, columns: 4, rows: 2, originX: -290),
            Display("primary", "DISPLAY1", true, columns: 4, rows: 2));
        var blueprint = new DesktopWorkspaceBlueprint(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            1,
            "左侧副屏保持不动，主屏设计学习区。",
            displayPolicies:
            [
                new DesktopWorkspaceBlueprintDisplayPolicy("secondary_1", DesktopWorkspaceDisplayPolicyKind.Preserve),
                new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design),
            ],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "work",
                    "Work",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1)),
            ],
            // 故意没有 dev→Zone。Preserve 屏上的 dev 不属于设计施工范围。
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule("study", "work")]);
        var semantic = Understanding(("left-dev", "dev"), ("notes", "study"));
        var facts = new[]
        {
            new DesktopWorkspaceCompilationItemFact("left-dev", "secondary_1", new PixelPoint(-290, 50), true),
            Fact("notes", 110, 130),
        };

        var result = DesktopWorkspaceCompiler.Compile(blueprint, semantic, geometry, facts);

        Assert.True(result.Succeeded);
        var preserved = result.Plan!.Items.Single(item => item.StableId == "left-dev");
        Assert.Equal(DesktopWorkspaceCompiledItemDisposition.Keep, preserved.Disposition);
        Assert.Null(preserved.TargetSlotId);
        Assert.Equal("work", result.Plan.Items.Single(item => item.StableId == "notes").ZoneKey);
    }

    [Fact]
    public void Compile_NonMovableItemOnDesignDisplay_RequiresExplicitKeepOrExclude()
    {
        var display = Display(
            "primary",
            "DISPLAY1",
            true,
            columns: 3,
            rows: 1,
            fixedObstacles:
            [new ExplorerFixedObstacle("system", "Recycle Bin", new PixelPoint(30, 50), ["primary:slot:0000:0000"])]);
        var geometry = Geometry(display);
        var semantic = Understanding(("system", "system"), ("tool", "dev"));
        var withoutPolicy = new DesktopWorkspaceBlueprint(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            1,
            "系统项不动，工具进入 Tools。",
            displayPolicies:
            [new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1)),
            ],
            categoryRules:
            [
                new DesktopWorkspaceBlueprintCategoryRule("system", "tools"),
                new DesktopWorkspaceBlueprintCategoryRule("dev", "tools"),
            ]);
        var facts = new[]
        {
            new DesktopWorkspaceCompilationItemFact("system", "primary", new PixelPoint(30, 50), false),
            Fact("tool", 110, 50),
        };

        var rejected = DesktopWorkspaceCompiler.Compile(withoutPolicy, semantic, geometry, facts);

        Assert.False(rejected.Succeeded);
        Assert.Contains(
            rejected.Issues,
            issue => issue.Code == DesktopWorkspaceCompileIssueCode.NonMovableItemRequiresExplicitPolicy &&
                     issue.StableId == "system");

        var withKeep = new DesktopWorkspaceBlueprint(
            withoutPolicy.BlueprintId,
            2,
            "显式保持系统项，工具进入 Tools。",
            displayPolicies: withoutPolicy.DisplayPolicies,
            zones: withoutPolicy.Zones,
            categoryRules: withoutPolicy.CategoryRules,
            itemOverrides:
            [new DesktopWorkspaceBlueprintItemOverride("item-system", DesktopWorkspaceItemOverrideKind.Keep)],
            parentRevision: 1);

        var accepted = DesktopWorkspaceCompiler.Compile(
            withKeep,
            semantic,
            geometry,
            facts,
            [new DesktopWorkspaceCompilationItemRefBinding("item-system", "system")]);

        Assert.True(accepted.Succeeded);
        Assert.Equal(
            DesktopWorkspaceCompiledItemDisposition.Keep,
            accepted.Plan!.Items.Single(item => item.StableId == "system").Disposition);
        Assert.Equal(
            "primary:slot:0001:0000",
            accepted.Plan.Items.Single(item => item.StableId == "tool").TargetSlotId);
    }

    private static string ItemSignature(CompiledWorkspaceItem item)
        => string.Join(
            "|",
            item.StableId,
            item.CategoryKey,
            item.SourceDisplayRef,
            item.Disposition,
            item.ZoneKey ?? string.Empty,
            item.TargetDisplayRef ?? string.Empty,
            item.TargetSlotId ?? string.Empty,
            item.TargetPixelPosition?.ToString() ?? string.Empty);

    private static DesktopWorkspaceBlueprint BlueprintForSingleZone(
        DesktopWorkspaceRelativeRect bounds,
        string categoryKey)
        => new(
            Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"),
            1,
            "Single zone",
            displayPolicies:
            [new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones:
            [new DesktopWorkspaceBlueprintZone("zone", "Zone", "primary", bounds)],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule(categoryKey, "zone")]);

    private static DesktopSemanticUnderstanding Understanding(params (string StableId, string CategoryKey)[] items)
    {
        var categoryKeys = items.Select(static item => item.CategoryKey).Distinct(StringComparer.Ordinal).ToArray();
        return new DesktopSemanticUnderstanding(
            "summary",
            categoryKeys.Select(key => new DesktopSemanticCategory(key, key, $"{key} items")),
            items.Select(item => new DesktopSemanticAssignment(item.StableId, item.CategoryKey, 1d, "test")));
    }

    private static DesktopWorkspaceCompilationItemFact Fact(string stableId, int x, int y)
        => new(stableId, "primary", new PixelPoint(x, y), isMovable: true);

    private static ExplorerWorkspaceGeometry Geometry(params ExplorerDisplayGeometry[] displays)
        => new(
            Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"),
            "geometry:test",
            isAutoArrangeEnabled: false,
            new PixelPoint(80, 80),
            displays);

    private static ExplorerDisplayGeometry Display(
        string displayRef,
        string deviceName,
        bool isPrimary,
        int columns,
        int rows,
        int originX = 30,
        int originY = 50,
        IEnumerable<ExplorerFixedObstacle>? fixedObstacles = null)
    {
        var slots = new List<ExplorerNativeSlot>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                slots.Add(new ExplorerNativeSlot(
                    $"{displayRef}:slot:{column:D4}:{row:D4}",
                    column,
                    row,
                    new PixelPoint(originX + column * 80, originY + row * 80)));
            }
        }

        var left = originX < 0 ? originX - 30 : 0;
        var top = 0;
        var right = originX + columns * 80;
        var bottom = originY + rows * 80;
        return new ExplorerDisplayGeometry(
            displayRef,
            deviceName,
            new PixelRect(left, top, right, bottom),
            new PixelRect(left, top, right, bottom),
            isPrimary,
            96,
            96,
            new PixelPoint(80, 80),
            new PixelPoint(30, 50),
            new PixelPoint(originX, originY),
            columns,
            rows,
            slots,
            fixedObstacles);
    }
}
