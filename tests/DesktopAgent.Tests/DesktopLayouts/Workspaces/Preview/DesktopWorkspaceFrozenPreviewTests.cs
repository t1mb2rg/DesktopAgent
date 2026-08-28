using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Preview;

public sealed class DesktopWorkspaceFrozenPreviewTests
{
    [Fact]
    public void Create_CrossDisplayWorkspace_BindsBlueprintPlanCertificateAndPreservesZeroTargetSourceDisplay()
    {
        var fixture = CreateFixture();

        var first = CreatePreview(fixture, fixture.Facts);
        var second = CreatePreview(fixture, fixture.Facts);

        Assert.NotEqual(first.PreviewId, second.PreviewId);
        Assert.Equal(first.PreviewFingerprint, second.PreviewFingerprint);
        Assert.Equal(first.SourceStateFingerprint, second.SourceStateFingerprint);
        Assert.Equal(fixture.Geometry.ObservationId, first.SourceObservationId);
        Assert.Same(fixture.Blueprint, first.Blueprint);
        Assert.Same(fixture.Plan, first.ExactPlan);
        Assert.Equal(fixture.Certificate.CertificateFingerprint, first.RealizationCertificate.CertificateFingerprint);
        Assert.Equal(2, first.Displays.Count);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, first.CrossDisplayAssignmentCount);
        Assert.Equal(2, first.MoveCount);

        var primary = Assert.Single(first.Displays, static display => display.IsPrimary);
        Assert.Equal("primary", primary.DisplayRef);
        Assert.Equal(2, primary.SourceItemCount);
        Assert.Equal(0, primary.TargetPlacementCount);
        Assert.Equal(2, primary.OutboundCount);
        Assert.Equal(0, primary.InboundCount);
        Assert.Single(primary.ReservedRegions);
        Assert.Equal(0d, primary.TopologyBounds.Left, precision: 10);
        Assert.Equal(0.5d, primary.TopologyBounds.Width, precision: 10);

        var secondary = Assert.Single(first.Displays, static display => !display.IsPrimary);
        Assert.Equal("secondary_1", secondary.DisplayRef);
        Assert.Equal(0, secondary.SourceItemCount);
        Assert.Equal(2, secondary.TargetPlacementCount);
        Assert.Equal(2, secondary.InboundCount);
        Assert.Equal(0, secondary.OutboundCount);
        Assert.Single(secondary.Zones);
        Assert.Equal(0.5d, secondary.TopologyBounds.Left, precision: 10);
        Assert.Equal(0.5d, secondary.TopologyBounds.Width, precision: 10);
    }

    [Fact]
    public void Create_MovableSourcePositionChanges_ChangesSourceAndPreviewFingerprintEvenWhenPlanIsSame()
    {
        var fixture = CreateFixture();
        var first = CreatePreview(fixture, fixture.Facts);
        var movedFacts = fixture.Facts
            .Select(fact => fact.StableId == "a"
                ? new DesktopWorkspaceCompilationItemFact(
                    fact.StableId,
                    fact.CurrentDisplayRef,
                    new PixelPoint(fact.PixelPosition.X + 17, fact.PixelPosition.Y + 9),
                    fact.IsMovable)
                : fact)
            .ToArray();

        var second = CreatePreview(fixture, movedFacts);

        Assert.Equal(first.ExactPlan.PlanFingerprint, second.ExactPlan.PlanFingerprint);
        Assert.Equal(first.RealizationCertificate.CertificateFingerprint, second.RealizationCertificate.CertificateFingerprint);
        Assert.NotEqual(first.SourceStateFingerprint, second.SourceStateFingerprint);
        Assert.NotEqual(first.PreviewFingerprint, second.PreviewFingerprint);
        Assert.NotEqual(
            first.Items.Single(static item => item.StableId == "a").SourcePixelPosition,
            second.Items.Single(static item => item.StableId == "a").SourcePixelPosition);
    }

    [Fact]
    public void Create_SourceObservationMismatch_IsRejectedInsteadOfFreezingMixedReality()
    {
        var fixture = CreateFixture();
        var source = new DesktopWorkspacePreviewSourceSnapshot(
            Guid.NewGuid(),
            fixture.Facts,
            fixture.Presentations);

        var error = Assert.Throws<ArgumentException>(() =>
            DesktopWorkspaceFrozenPreviewFactory.Create(
                fixture.Blueprint,
                fixture.Geometry,
                fixture.Plan,
                fixture.Certificate,
                source));

        Assert.Contains("同一次 Observation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_TamperedCertificate_IsRejected()
    {
        var fixture = CreateFixture();
        var tampered = new WorkspaceRealizationCertificate(
            fixture.Certificate.BlueprintId,
            fixture.Certificate.BlueprintRevision,
            fixture.Certificate.GeometryFingerprint,
            "workspace-plan-v1:TAMPERED",
            fixture.Certificate.CertificateFingerprint,
            fixture.Certificate.ZoneProofs,
            fixture.Certificate.ReservedRegionProofs,
            fixture.Certificate.ItemProofs);
        var source = new DesktopWorkspacePreviewSourceSnapshot(
            fixture.Geometry.ObservationId,
            fixture.Facts,
            fixture.Presentations);

        var error = Assert.Throws<ArgumentException>(() =>
            DesktopWorkspaceFrozenPreviewFactory.Create(
                fixture.Blueprint,
                fixture.Geometry,
                fixture.Plan,
                tampered,
                source));

        Assert.Contains("Certificate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FrozenPreviewContract_HasNoConfirmationOrExecutionAuthority()
    {
        var propertyNames = typeof(DesktopWorkspaceFrozenPreview)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        foreach (var forbidden in new[] { "Confirmed", "Candidate", "Writer", "Execution", "Recovery", "Apply" })
        {
            Assert.DoesNotContain(
                propertyNames,
                property => property.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static DesktopWorkspaceFrozenPreview CreatePreview(
        Fixture fixture,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact> facts)
    {
        var source = new DesktopWorkspacePreviewSourceSnapshot(
            fixture.Geometry.ObservationId,
            facts,
            fixture.Presentations);
        return DesktopWorkspaceFrozenPreviewFactory.Create(
            fixture.Blueprint,
            fixture.Geometry,
            fixture.Plan,
            fixture.Certificate,
            source);
    }

    private static Fixture CreateFixture()
    {
        var observationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var geometry = CreateGeometry(observationId);
        var blueprint = new DesktopWorkspaceBlueprint(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            revision: 1,
            designSummary: "主屏中央留白，把开发项目整理到右侧副屏。",
            displayPolicies:
            [
                new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design),
                new DesktopWorkspaceBlueprintDisplayPolicy("secondary_1", DesktopWorkspaceDisplayPolicyKind.Design),
            ],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
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
                    "wallpaper-center",
                    "保持主屏中央壁纸可见",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0.2, 0.0, 0.6, 1.0)),
            ]);
        var semantics = new DesktopSemanticUnderstanding(
            "两个开发项目位于主屏。",
            [new DesktopSemanticCategory("dev", "开发工具", "IDE 与终端")],
            [
                new DesktopSemanticAssignment("a", "dev", 1, "dev"),
                new DesktopSemanticAssignment("b", "dev", 1, "dev"),
            ]);
        var facts = new[]
        {
            new DesktopWorkspaceCompilationItemFact("a", "primary", new PixelPoint(30, 50), isMovable: true),
            new DesktopWorkspaceCompilationItemFact("b", "primary", new PixelPoint(110, 50), isMovable: true),
        };
        var presentations = new[]
        {
            new DesktopWorkspacePreviewItemPresentation("a", "Visual Studio Code"),
            new DesktopWorkspacePreviewItemPresentation("b", "Windows Terminal"),
        };

        var compile = DesktopWorkspaceCompiler.Compile(
            blueprint,
            semantics,
            geometry,
            facts);
        Assert.True(compile.Succeeded);
        var plan = Assert.IsType<CompiledWorkspacePlan>(compile.Plan);
        var equivalence = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            blueprint,
            geometry,
            plan,
            facts);
        Assert.True(equivalence.Equivalent);
        var certificate = Assert.IsType<WorkspaceRealizationCertificate>(equivalence.Certificate);

        return new Fixture(blueprint, geometry, facts, presentations, plan, certificate);
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
            "geometry:frozen-preview-test",
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
        DesktopWorkspaceBlueprint Blueprint,
        ExplorerWorkspaceGeometry Geometry,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact> Facts,
        IReadOnlyList<DesktopWorkspacePreviewItemPresentation> Presentations,
        CompiledWorkspacePlan Plan,
        WorkspaceRealizationCertificate Certificate);
}
