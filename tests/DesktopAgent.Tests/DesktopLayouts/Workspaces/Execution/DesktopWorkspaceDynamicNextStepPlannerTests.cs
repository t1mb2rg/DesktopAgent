using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Execution;

public sealed class DesktopWorkspaceDynamicNextStepPlannerTests
{
    [Fact]
    public void Decide_MovingItemDriftedAfterEarlierWriter_UsesFreshFromAndKeepsFrozenTarget()
    {
        var geometry = Geometry(
            Display("primary", "DISPLAY1", true, 0, 0, columns: 4, rows: 2),
            Display("secondary_1", "DISPLAY2", false, 320, 0, columns: 4, rows: 2));
        var sourceFacts = new[]
        {
            Fact("a", "primary", 0, 0),
            Fact("b", "secondary_1", 320, 0),
        };
        var preview = Preview(
            geometry,
            sourceFacts,
            policies:
            [
                new("primary", DesktopWorkspaceDisplayPolicyKind.Design),
                new("secondary_1", DesktopWorkspaceDisplayPolicyKind.Design),
            ],
            zoneDisplayRef: "secondary_1");
        var a = preview.Items.Single(static item => item.StableId == "a");
        var b = preview.Items.Single(static item => item.StableId == "b");
        Assert.NotNull(a.TargetPixelPosition);
        Assert.NotNull(b.TargetPixelPosition);

        // 模拟真实机：前序 Writer 已把 b 放到自己的 Frozen target，
        // 但 Explorer 同时把尚未执行的 a 从原始 (0,0) 被动漂到 (0,80)。
        var freshFacts = new[]
        {
            Fact("a", "primary", 0, 80),
            Fact("b", b.TargetDisplayRef!, b.TargetPixelPosition!.Value.X, b.TargetPixelPosition.Value.Y),
        };

        var result = DesktopWorkspaceDynamicNextStepPlanner.Decide(
            preview,
            geometry,
            freshFacts,
            decisionIndex: 40);

        Assert.True(result.IsMoveReady);
        var move = Assert.IsType<DesktopWorkspaceDynamicMove>(result.Move);
        Assert.Equal("a", move.StableId);
        Assert.Equal(new PixelPoint(0, 80), move.FromPixelPosition);
        Assert.Equal("primary", move.FromDisplayRef);
        Assert.Equal(a.TargetDisplayRef, move.ToDisplayRef);
        Assert.Equal(a.TargetPixelPosition.Value, move.ToPixelPosition);
        Assert.Equal(a.TargetPixelPosition.Value, move.FrozenTargetPixelPosition);
        Assert.Equal(DesktopWorkspaceDynamicMoveKind.MoveToFrozenTarget, move.Kind);
    }

    [Fact]
    public void Decide_MovingItemAlreadyPassivelyReachedFrozenTarget_CompletesWithoutMove()
    {
        var geometry = Geometry(
            Display("primary", "DISPLAY1", true, 0, 0, columns: 3, rows: 1));
        var sourceFacts = new[]
        {
            Fact("a", "primary", 80, 0),
        };
        var preview = Preview(
            geometry,
            sourceFacts,
            policies: [new("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zoneDisplayRef: "primary");
        var a = Assert.Single(preview.Items);
        Assert.True(a.IsPositionChange);
        Assert.NotNull(a.TargetPixelPosition);

        var freshFacts = new[]
        {
            Fact("a", a.TargetDisplayRef!, a.TargetPixelPosition!.Value.X, a.TargetPixelPosition.Value.Y),
        };

        var result = DesktopWorkspaceDynamicNextStepPlanner.Decide(
            preview,
            geometry,
            freshFacts,
            decisionIndex: 1);

        Assert.True(result.IsComplete);
        Assert.Null(result.Move);
        Assert.Equal(1, result.SatisfiedMovingGoalCount);
        Assert.Equal(1, result.TotalMovingGoalCount);
    }

    [Fact]
    public void Decide_TwoItemSwap_UsesDesignNativeStagingInsteadOfChangingFrozenTargets()
    {
        var geometry = Geometry(
            Display("primary", "DISPLAY1", true, 0, 0, columns: 3, rows: 1));
        var sourceFacts = new[]
        {
            Fact("a", "primary", 80, 0),
            Fact("b", "primary", 0, 0),
        };
        var preview = Preview(
            geometry,
            sourceFacts,
            policies: [new("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zoneDisplayRef: "primary");

        var result = DesktopWorkspaceDynamicNextStepPlanner.Decide(
            preview,
            geometry,
            sourceFacts,
            decisionIndex: 0);

        Assert.True(result.IsMoveReady);
        var move = Assert.IsType<DesktopWorkspaceDynamicMove>(result.Move);
        Assert.Equal("a", move.StableId);
        Assert.Equal(DesktopWorkspaceDynamicMoveKind.MoveToTemporaryStaging, move.Kind);
        Assert.Equal(new PixelPoint(160, 0), move.ToPixelPosition);
        Assert.Equal("primary:slot:0002:0000", move.ToSlotId);
        Assert.Equal(preview.Items.Single(static item => item.StableId == "a").TargetPixelPosition, move.FrozenTargetPixelPosition);
        Assert.NotEqual(move.ToPixelPosition, move.FrozenTargetPixelPosition);
    }

    [Fact]
    public void Decide_ItemThatWasNotAuthorizedToMoveDrifts_Blocks()
    {
        var geometry = Geometry(
            Display("primary", "DISPLAY1", true, 0, 0, columns: 3, rows: 1));
        var sourceFacts = new[]
        {
            Fact("a", "primary", 0, 0),
        };
        var preview = Preview(
            geometry,
            sourceFacts,
            policies: [new("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zoneDisplayRef: "primary");
        var a = Assert.Single(preview.Items);
        Assert.False(a.IsPositionChange);

        var freshFacts = new[]
        {
            Fact("a", "primary", 80, 0),
        };

        var result = DesktopWorkspaceDynamicNextStepPlanner.Decide(
            preview,
            geometry,
            freshFacts,
            decisionIndex: 1);

        Assert.Equal(DesktopWorkspaceDynamicNextStepStatus.Blocked, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DesktopWorkspaceDynamicNextStepIssueCode.StationaryItemDrifted, issue.Code);
        Assert.Equal("a", issue.StableId);
    }

    private static DesktopWorkspaceFrozenPreview Preview(
        ExplorerWorkspaceGeometry geometry,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact> facts,
        IReadOnlyList<DesktopWorkspaceBlueprintDisplayPolicy> policies,
        string zoneDisplayRef)
    {
        var blueprint = new DesktopWorkspaceBlueprint(
            Guid.Parse("74444444-4444-4444-4444-444444444444"),
            revision: 1,
            designSummary: "Dynamic execution test workspace.",
            displayPolicies: policies,
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    zoneDisplayRef,
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1),
                    DesktopWorkspaceAnchorKind.TopLeft,
                    DesktopWorkspacePackingOrder.RowMajor),
            ],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule("dev", "tools")]);
        var semantics = new DesktopSemanticUnderstanding(
            "Dynamic execution test items.",
            [new DesktopSemanticCategory("dev", "开发", "开发工具")],
            facts.Select(fact => new DesktopSemanticAssignment(
                fact.StableId,
                "dev",
                1,
                "test")).ToArray());

        var compile = DesktopWorkspaceCompiler.Compile(
            blueprint,
            semantics,
            geometry,
            facts);
        Assert.True(compile.Succeeded, string.Join("；", compile.Issues.Select(static issue => issue.Message)));
        var plan = Assert.IsType<CompiledWorkspacePlan>(compile.Plan);
        var equivalence = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            blueprint,
            geometry,
            plan,
            facts);
        Assert.True(equivalence.Equivalent, string.Join("；", equivalence.Issues.Select(static issue => issue.Message)));
        var certificate = Assert.IsType<WorkspaceRealizationCertificate>(equivalence.Certificate);
        var presentations = facts.Select(fact =>
            new DesktopWorkspacePreviewItemPresentation(
                fact.StableId,
                fact.StableId.ToUpperInvariant())).ToArray();
        var snapshot = new DesktopWorkspacePreviewSourceSnapshot(
            geometry.ObservationId,
            facts,
            presentations);

        return DesktopWorkspaceFrozenPreviewFactory.Create(
            blueprint,
            geometry,
            plan,
            certificate,
            snapshot);
    }

    private static DesktopWorkspaceCompilationItemFact Fact(
        string stableId,
        string displayRef,
        int x,
        int y)
        => new(stableId, displayRef, new PixelPoint(x, y), isMovable: true);

    private static ExplorerWorkspaceGeometry Geometry(params ExplorerDisplayGeometry[] displays)
        => new(
            Guid.Parse("73333333-3333-3333-3333-333333333333"),
            GeometryFingerprint(displays),
            isAutoArrangeEnabled: false,
            new PixelPoint(80, 80),
            displays);

    private static string GeometryFingerprint(IEnumerable<ExplorerDisplayGeometry> displays)
        => "geometry:dynamic-test:" + string.Join(
            ";",
            displays.Select(display =>
                $"{display.DisplayRef}:{display.Bounds.Left},{display.Bounds.Top},{display.Columns}x{display.Rows}:{(display.IsPrimary ? 1 : 0)}"));

    private static ExplorerDisplayGeometry Display(
        string displayRef,
        string deviceName,
        bool isPrimary,
        int left,
        int top,
        int columns,
        int rows)
    {
        var spacing = new PixelPoint(80, 80);
        var bounds = new PixelRect(
            left,
            top,
            left + columns * spacing.X,
            top + rows * spacing.Y);
        var slots = new List<ExplorerNativeSlot>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                slots.Add(new ExplorerNativeSlot(
                    $"{displayRef}:slot:{column:D4}:{row:D4}",
                    column,
                    row,
                    new PixelPoint(
                        left + column * spacing.X,
                        top + row * spacing.Y)));
            }
        }

        return new ExplorerDisplayGeometry(
            displayRef,
            deviceName,
            bounds,
            bounds,
            isPrimary,
            dpiX: 96,
            dpiY: 96,
            spacing,
            phase: new PixelPoint(0, 0),
            gridOrigin: new PixelPoint(left, top),
            columns,
            rows,
            slots);
    }
}
