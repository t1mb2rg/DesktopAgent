using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Execution;

public sealed class DesktopWorkspaceApplyFreshCheckTests
{
    [Fact]
    public void Evaluate_UnchangedFreshReality_IsReady()
    {
        var fixture = CreateFixture();
        var result = DesktopWorkspaceApplyFreshCheck.Evaluate(
            fixture.Confirmation,
            fixture.Geometry,
            fixture.Facts);

        Assert.True(result.IsReady);
        Assert.Equal(DesktopWorkspaceApplyFreshCheckStatus.Ready, result.Status);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Evaluate_ItemMovedAfterPreview_IsRejectedWithoutApproximateTolerance()
    {
        var fixture = CreateFixture();
        var moved = fixture.Facts.Select(fact => fact.StableId == "a"
            ? new DesktopWorkspaceCompilationItemFact(
                fact.StableId,
                fact.CurrentDisplayRef,
                new PixelPoint(fact.PixelPosition.X + 1, fact.PixelPosition.Y),
                fact.IsMovable)
            : fact).ToArray();

        var result = DesktopWorkspaceApplyFreshCheck.Evaluate(
            fixture.Confirmation,
            fixture.Geometry,
            moved);

        Assert.False(result.IsReady);
        Assert.Equal(DesktopWorkspaceApplyFreshCheckStatus.ItemStateChanged, result.Status);
        Assert.Contains(result.Issues, issue => issue.Contains("Visual Studio Code", StringComparison.Ordinal));
        var issue = Assert.Single(result.Issues);
        Assert.Contains("1 个项目", issue);
        Assert.Contains("位置：预期 (30, 50)，实际 (31, 50)", issue);
        Assert.DoesNotContain("所属显示器：", issue);
        Assert.DoesNotContain("可移动状态：", issue);
        Assert.DoesNotContain("以上", issue);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Evaluate_DisplayOrMovabilityChanged_ReportsOnlyChangedFields(
        bool changeDisplay,
        bool changeMovability)
    {
        var fixture = CreateFixture();
        var changed = fixture.Facts.Select(fact => fact.StableId == "a"
            ? new DesktopWorkspaceCompilationItemFact(
                fact.StableId,
                changeDisplay ? "secondary_1" : fact.CurrentDisplayRef,
                fact.PixelPosition,
                changeMovability ? !fact.IsMovable : fact.IsMovable)
            : fact).ToArray();

        var result = DesktopWorkspaceApplyFreshCheck.Evaluate(
            fixture.Confirmation, fixture.Geometry, changed);

        Assert.Equal(DesktopWorkspaceApplyFreshCheckStatus.ItemStateChanged, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(changeDisplay, issue.Contains("所属显示器：预期 primary，实际 secondary_1", StringComparison.Ordinal));
        Assert.Equal(changeMovability, issue.Contains("可移动状态：预期 可移动，实际 不可移动", StringComparison.Ordinal));
        Assert.DoesNotContain("位置：", issue);
    }

    [Fact]
    public void Evaluate_MoreThanFiveChanges_ReportsExactCountAndLimitsDetailInStableIdOrder()
    {
        var fixture = CreateFixture(itemCount: 6);
        var changed = fixture.Facts.Reverse().Select(fact => new DesktopWorkspaceCompilationItemFact(
            fact.StableId,
            fact.CurrentDisplayRef,
            new PixelPoint(fact.PixelPosition.X + 1, fact.PixelPosition.Y),
            fact.IsMovable)).ToArray();

        var result = DesktopWorkspaceApplyFreshCheck.Evaluate(
            fixture.Confirmation, fixture.Geometry, changed);

        Assert.Equal(DesktopWorkspaceApplyFreshCheckStatus.ItemStateChanged, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Contains("6 个项目", issue);
        Assert.Contains("仅列出前 5 项", issue);
        Assert.Equal(5, issue.Split("位置：预期", StringSplitOptions.None).Length - 1);
        Assert.Contains("Item e", issue);
        Assert.DoesNotContain("Item f", issue);
    }

    [Fact]
    public void Evaluate_ItemSetChanged_IsRejected()
    {
        var fixture = CreateFixture();
        var missing = fixture.Facts.Where(static fact => fact.StableId != "b").ToArray();

        var result = DesktopWorkspaceApplyFreshCheck.Evaluate(
            fixture.Confirmation,
            fixture.Geometry,
            missing);

        Assert.False(result.IsReady);
        Assert.Equal(DesktopWorkspaceApplyFreshCheckStatus.ItemSetChanged, result.Status);
    }

    [Fact]
    public void Evaluate_SameTopologyButDifferentGeometryFingerprint_IsRejected()
    {
        var fixture = CreateFixture();
        var freshGeometry = new ExplorerWorkspaceGeometry(
            Guid.NewGuid(),
            "geometry:changed-phase-or-slots",
            fixture.Geometry.IsAutoArrangeEnabled,
            fixture.Geometry.Spacing,
            fixture.Geometry.Displays);

        var result = DesktopWorkspaceApplyFreshCheck.Evaluate(
            fixture.Confirmation,
            freshGeometry,
            fixture.Facts);

        Assert.False(result.IsReady);
        Assert.Equal(DesktopWorkspaceApplyFreshCheckStatus.GeometryChanged, result.Status);
    }

    [Fact]
    public void Evaluate_DisplayTopologyChanged_IsRejectedBeforeGeometryComparison()
    {
        var fixture = CreateFixture();
        var primary = fixture.Geometry.Displays.Single(static display => display.IsPrimary);
        var freshGeometry = new ExplorerWorkspaceGeometry(
            Guid.NewGuid(),
            "geometry:single-display",
            isAutoArrangeEnabled: false,
            fixture.Geometry.Spacing,
            [primary]);

        var result = DesktopWorkspaceApplyFreshCheck.Evaluate(
            fixture.Confirmation,
            freshGeometry,
            fixture.Facts);

        Assert.False(result.IsReady);
        Assert.Equal(DesktopWorkspaceApplyFreshCheckStatus.TopologyChanged, result.Status);
    }

    [Fact]
    public void Confirmation_IsSessionBoundPreviewEvidence_NotExecutionAuthority()
    {
        var fixture = CreateFixture();
        var properties = typeof(DesktopWorkspaceApplyConfirmation)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        Assert.Same(fixture.Preview, fixture.Confirmation.FrozenPreview);
        Assert.DoesNotContain(properties, name => name.Contains("Writer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Recovery", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Candidate", StringComparison.OrdinalIgnoreCase));
    }

    private static Fixture CreateFixture(int itemCount = 2)
    {
        var observationId = Guid.Parse("73333333-3333-3333-3333-333333333333");
        var geometry = CreateGeometry(observationId);
        var blueprint = new DesktopWorkspaceBlueprint(
            Guid.Parse("74444444-4444-4444-4444-444444444444"),
            revision: 1,
            designSummary: "主屏留白，项目移动到副屏。",
            displayPolicies:
            [
                new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design),
                new DesktopWorkspaceBlueprintDisplayPolicy("secondary_1", DesktopWorkspaceDisplayPolicyKind.Design),
            ],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "工具",
                    "secondary_1",
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1),
                    DesktopWorkspaceAnchorKind.TopLeft,
                    DesktopWorkspacePackingOrder.RowMajor),
            ],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule("dev", "tools")],
            reservedRegions:
            [
                new DesktopWorkspaceBlueprintReservedRegion(
                    "wallpaper",
                    "保留壁纸",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0.2, 0.0, 0.6, 1.0)),
            ]);
        var semantics = new DesktopSemanticUnderstanding(
            "两个开发项目位于主屏。",
            [new DesktopSemanticCategory("dev", "开发工具", "IDE 与终端")],
            Enumerable.Range(0, itemCount).Select(index => new DesktopSemanticAssignment(
                ((char)('a' + index)).ToString(), "dev", 1, "dev")));
        var facts = Enumerable.Range(0, itemCount).Select(index => new DesktopWorkspaceCompilationItemFact(
            ((char)('a' + index)).ToString(),
            "primary",
            new PixelPoint(30 + index % 5 * 80, 50 + index / 5 * 80),
            isMovable: true)).ToArray();
        var presentations = facts.Select(fact => new DesktopWorkspacePreviewItemPresentation(
            fact.StableId,
            fact.StableId switch
            {
                "a" => "Visual Studio Code",
                "b" => "Windows Terminal",
                _ => $"Item {fact.StableId}",
            })).ToArray();

        var compile = DesktopWorkspaceCompiler.Compile(blueprint, semantics, geometry, facts);
        Assert.True(compile.Succeeded);
        var plan = Assert.IsType<CompiledWorkspacePlan>(compile.Plan);
        var equivalence = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            blueprint,
            geometry,
            plan,
            facts);
        Assert.True(equivalence.Equivalent);
        var certificate = Assert.IsType<WorkspaceRealizationCertificate>(equivalence.Certificate);
        var source = new DesktopWorkspacePreviewSourceSnapshot(observationId, facts, presentations);
        var preview = DesktopWorkspaceFrozenPreviewFactory.Create(
            blueprint,
            geometry,
            plan,
            certificate,
            source);
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);

        return new Fixture(geometry, facts, preview, confirmation);
    }

    private static ExplorerWorkspaceGeometry CreateGeometry(Guid observationId)
    {
        var spacing = new PixelPoint(80, 80);
        var primary = Display(
            "primary",
            "DISPLAY1",
            isPrimary: true,
            new PixelRect(0, 0, 400, 240),
            spacing);
        var secondary = Display(
            "secondary_1",
            "DISPLAY2",
            isPrimary: false,
            new PixelRect(400, 0, 800, 240),
            spacing);
        return new ExplorerWorkspaceGeometry(
            observationId,
            "geometry:workspace-apply-fresh-check",
            isAutoArrangeEnabled: false,
            spacing,
            [primary, secondary]);
    }

    private static ExplorerDisplayGeometry Display(
        string displayRef,
        string deviceName,
        bool isPrimary,
        PixelRect bounds,
        PixelPoint spacing)
    {
        const int columns = 5;
        const int rows = 3;
        var slots = new List<ExplorerNativeSlot>();
        var gridOrigin = new PixelPoint(bounds.Left + 30, bounds.Top + 50);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                slots.Add(new ExplorerNativeSlot(
                    $"{displayRef}:slot:{column:D4}:{row:D4}",
                    column,
                    row,
                    new PixelPoint(
                        gridOrigin.X + column * spacing.X,
                        gridOrigin.Y + row * spacing.Y)));
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
            new PixelPoint(30, 50),
            gridOrigin,
            columns,
            rows,
            slots);
    }

    private sealed record Fixture(
        ExplorerWorkspaceGeometry Geometry,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact> Facts,
        DesktopWorkspaceFrozenPreview Preview,
        DesktopWorkspaceApplyConfirmation Confirmation);
}
