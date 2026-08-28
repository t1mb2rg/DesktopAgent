using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Execution;

public sealed class DesktopWorkspaceExecutionPlannerTests
{
    [Fact]
    public void Create_CrossDisplayDependency_MovesOccupantBeforeInboundItem()
    {
        var geometry = Geometry(
            Display("primary", "DISPLAY1", true, 0, 0, columns: 4, rows: 2),
            Display("secondary_1", "DISPLAY2", false, 320, 0, columns: 4, rows: 2));
        var facts = new[]
        {
            Fact("a", "primary", 0, 0),
            Fact("b", "secondary_1", 320, 0),
        };
        var preview = Preview(
            geometry,
            facts,
            policies:
            [
                new("primary", DesktopWorkspaceDisplayPolicyKind.Design),
                new("secondary_1", DesktopWorkspaceDisplayPolicyKind.Design),
            ],
            zoneDisplayRef: "secondary_1");
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);

        var result = DesktopWorkspaceExecutionPlanner.Create(confirmation, geometry, facts);

        Assert.True(result.Succeeded);
        var plan = Assert.IsType<DesktopWorkspaceExecutionPlan>(result.Plan);
        Assert.False(plan.UsesTemporaryStaging);
        Assert.Equal(2, plan.MoveItemCount);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("b", plan.Steps[0].StableId);
        Assert.Equal("secondary_1", plan.Steps[0].FromDisplayRef);
        Assert.Equal("secondary_1", plan.Steps[0].ToDisplayRef);
        Assert.Equal("a", plan.Steps[1].StableId);
        Assert.Equal("primary", plan.Steps[1].FromDisplayRef);
        Assert.Equal("secondary_1", plan.Steps[1].ToDisplayRef);
    }

    [Fact]
    public void Create_TwoItemSwap_UsesOneNativeStagingSlotAndConverges()
    {
        var geometry = Geometry(
            Display("primary", "DISPLAY1", true, 0, 0, columns: 3, rows: 1));
        var facts = new[]
        {
            Fact("a", "primary", 80, 0),
            Fact("b", "primary", 0, 0),
        };
        var preview = Preview(
            geometry,
            facts,
            policies: [new("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zoneDisplayRef: "primary");
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);

        var result = DesktopWorkspaceExecutionPlanner.Create(confirmation, geometry, facts);

        Assert.True(result.Succeeded);
        var plan = Assert.IsType<DesktopWorkspaceExecutionPlan>(result.Plan);
        Assert.True(plan.UsesTemporaryStaging);
        Assert.Equal(3, plan.Steps.Count);

        var staging = plan.Steps[0];
        Assert.Equal("a", staging.StableId);
        Assert.Equal(DesktopWorkspaceExecutionStepKind.MoveToTemporaryStaging, staging.Kind);
        Assert.Equal(new PixelPoint(160, 0), staging.ToPixelPosition);
        Assert.Equal("primary:slot:0002:0000", staging.ToSlotId);

        Assert.Equal("b", plan.Steps[1].StableId);
        Assert.Equal(DesktopWorkspaceExecutionStepKind.MoveToTarget, plan.Steps[1].Kind);
        Assert.Equal(new PixelPoint(80, 0), plan.Steps[1].ToPixelPosition);

        Assert.Equal("a", plan.Steps[2].StableId);
        Assert.Equal(DesktopWorkspaceExecutionStepKind.MoveFromTemporaryStagingToTarget, plan.Steps[2].Kind);
        Assert.Equal(new PixelPoint(0, 0), plan.Steps[2].ToPixelPosition);
    }

    [Fact]
    public void Create_CycleWithFreeSlotOnlyOnPreserveDisplay_FailsInsteadOfBorrowingPreserve()
    {
        var geometry = Geometry(
            Display("primary", "DISPLAY1", true, 0, 0, columns: 2, rows: 1),
            Display("secondary_1", "DISPLAY2", false, 160, 0, columns: 2, rows: 1));
        var facts = new[]
        {
            Fact("a", "primary", 80, 0),
            Fact("b", "primary", 0, 0),
        };
        var preview = Preview(
            geometry,
            facts,
            policies:
            [
                new("primary", DesktopWorkspaceDisplayPolicyKind.Design),
                new("secondary_1", DesktopWorkspaceDisplayPolicyKind.Preserve),
            ],
            zoneDisplayRef: "primary");
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);

        var result = DesktopWorkspaceExecutionPlanner.Create(confirmation, geometry, facts);

        Assert.False(result.Succeeded);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DesktopWorkspaceExecutionPlanIssueCode.NoSafeStagingSlot, issue.Code);
    }

    [Fact]
    public void Create_SourceItemMovedAfterConfirmation_FailsFreshCheckWithZeroPlan()
    {
        var geometry = Geometry(
            Display("primary", "DISPLAY1", true, 0, 0, columns: 3, rows: 1));
        var facts = new[]
        {
            Fact("a", "primary", 80, 0),
            Fact("b", "primary", 0, 0),
        };
        var preview = Preview(
            geometry,
            facts,
            policies: [new("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zoneDisplayRef: "primary");
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var changed = facts
            .Select(fact => fact.StableId == "a"
                ? new DesktopWorkspaceCompilationItemFact(
                    fact.StableId,
                    fact.CurrentDisplayRef,
                    new PixelPoint(fact.PixelPosition.X + 1, fact.PixelPosition.Y),
                    fact.IsMovable)
                : fact)
            .ToArray();

        var result = DesktopWorkspaceExecutionPlanner.Create(confirmation, geometry, changed);

        Assert.False(result.Succeeded);
        Assert.Null(result.Plan);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DesktopWorkspaceExecutionPlanIssueCode.FreshCheckRejected, issue.Code);
    }

    private static DesktopWorkspaceFrozenPreview Preview(
        ExplorerWorkspaceGeometry geometry,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact> facts,
        IReadOnlyList<DesktopWorkspaceBlueprintDisplayPolicy> policies,
        string zoneDisplayRef)
    {
        var blueprint = new DesktopWorkspaceBlueprint(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            revision: 1,
            designSummary: "把两个开发项目整理到目标区域。",
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
            "两个开发项目。",
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
                fact.StableId == "a" ? "A" : "B")).ToArray();
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
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            GeometryFingerprint(displays),
            isAutoArrangeEnabled: false,
            new PixelPoint(80, 80),
            displays);

    private static string GeometryFingerprint(IEnumerable<ExplorerDisplayGeometry> displays)
        => "geometry:test:" + string.Join(
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
