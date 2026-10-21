using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Equivalence;

public sealed class DesktopWorkspaceSemanticEquivalenceCheckerTests
{
    [Fact]
    public void Check_CompilerPlan_ProducesCertificateAndProvesNearestNativeQuantization()
    {
        var fixture = CreateFixture();
        var compile = Compile(fixture);
        Assert.True(compile.Succeeded);

        var result = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            fixture.Blueprint,
            fixture.Geometry,
            compile.Plan!,
            fixture.ItemFacts,
            fixture.Bindings);

        Assert.True(result.Equivalent);
        var certificate = Assert.IsType<WorkspaceRealizationCertificate>(result.Certificate);
        Assert.Equal(fixture.Blueprint.BlueprintId, certificate.BlueprintId);
        Assert.Equal(fixture.Blueprint.Revision, certificate.BlueprintRevision);
        Assert.Equal(compile.Plan!.PlanFingerprint, certificate.PlanFingerprint);
        Assert.StartsWith("workspace-certificate-v1:", certificate.CertificateFingerprint, StringComparison.Ordinal);

        var zoneProof = Assert.Single(certificate.ZoneProofs);
        Assert.Equal("tools", zoneProof.Key);
        Assert.Equal(4, zoneProof.NativeRect.ColumnCount);
        Assert.Equal(0.4d, zoneProof.RealizedBounds.Width, precision: 10);
        Assert.Equal(2, certificate.ItemProofs.Count);
    }

    [Fact]
    public void Check_SameInputs_ProducesSameCertificateFingerprint()
    {
        var fixture = CreateFixture();
        var plan = Compile(fixture).Plan!;

        var first = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            fixture.Blueprint,
            fixture.Geometry,
            plan,
            fixture.ItemFacts,
            fixture.Bindings);
        var second = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            fixture.Blueprint,
            fixture.Geometry,
            plan,
            fixture.ItemFacts,
            fixture.Bindings);

        Assert.True(first.Equivalent);
        Assert.True(second.Equivalent);
        Assert.Equal(
            first.Certificate!.CertificateFingerprint,
            second.Certificate!.CertificateFingerprint);
    }

    [Fact]
    public void Check_TamperedSemanticResize_FailsInsteadOfAcceptingCloseEnoughGeometry()
    {
        var fixture = CreateFixture();
        var original = Compile(fixture).Plan!;
        var zone = Assert.Single(original.Zones);
        var tamperedZone = new CompiledWorkspaceZone(
            zone.ZoneKey,
            zone.Name,
            zone.DisplayRef,
            zone.RequestedBounds,
            new DesktopWorkspaceRelativeRect(0, 0, 0.5, 1),
            new DesktopWorkspaceNativeRect(0, 0, 5, 4),
            zone.Anchor,
            zone.PackingOrder,
            availableSlotCount: 20);
        var tampered = CopyPlan(original, zones: [tamperedZone]);

        var result = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            fixture.Blueprint,
            fixture.Geometry,
            tampered,
            fixture.ItemFacts,
            fixture.Bindings);

        Assert.False(result.Equivalent);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == DesktopWorkspaceEquivalenceIssueCode.ZoneQuantizationMismatch);
    }

    [Fact]
    public void Check_SwappedTargetSlots_FailsIndependentPackingProof()
    {
        var fixture = CreateFixture();
        var original = Compile(fixture).Plan!;
        var placed = original.Items
            .Where(static item => item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place)
            .OrderBy(static item => item.StableId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, placed.Length);

        var tamperedItems = original.Items
            .Select(item =>
            {
                if (item.StableId == placed[0].StableId)
                    return CopyPlacedItem(item, placed[1]);
                if (item.StableId == placed[1].StableId)
                    return CopyPlacedItem(item, placed[0]);
                return item;
            })
            .ToArray();
        var tampered = CopyPlan(original, items: tamperedItems);

        var result = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            fixture.Blueprint,
            fixture.Geometry,
            tampered,
            fixture.ItemFacts,
            fixture.Bindings);

        Assert.False(result.Equivalent);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == DesktopWorkspaceEquivalenceIssueCode.ItemPackingMismatch);
    }

    [Fact]
    public void Check_ExplicitItemAnchor_IsHardSemantic()
    {
        var fixture = CreateFixture(withTopRightOverride: true);
        var original = Compile(fixture).Plan!;
        var anchored = original.Items.Single(static item => item.StableId == "a");
        Assert.Equal("primary:slot:0003:0000", anchored.TargetSlotId);

        var wrongSlot = fixture.Geometry.ResolveDisplay("primary").Slots
            .Single(static slot => slot.SlotId == "primary:slot:0000:0000");
        var tamperedItems = original.Items
            .Select(item => item.StableId == "a"
                ? new CompiledWorkspaceItem(
                    item.StableId,
                    item.CategoryKey,
                    item.SourceDisplayRef,
                    DesktopWorkspaceCompiledItemDisposition.Place,
                    item.ZoneKey,
                    item.TargetDisplayRef,
                    wrongSlot.SlotId,
                    wrongSlot.PixelPosition)
                : item)
            .ToArray();
        var tampered = CopyPlan(original, items: tamperedItems);

        var result = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            fixture.Blueprint,
            fixture.Geometry,
            tampered,
            fixture.ItemFacts,
            fixture.Bindings);

        Assert.False(result.Equivalent);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == DesktopWorkspaceEquivalenceIssueCode.ItemPackingMismatch &&
                     issue.StableId == "a");
    }

    [Fact]
    public void CertificateContract_DoesNotGrantExecutionAuthority()
    {
        var propertyNames = typeof(WorkspaceRealizationCertificate)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();
        foreach (var forbidden in new[] { "Writer", "Candidate", "Execution", "Recovery", "Confirmed" })
        {
            Assert.DoesNotContain(
                propertyNames,
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static DesktopWorkspaceCompileResult Compile(Fixture fixture)
        => DesktopWorkspaceCompiler.Compile(
            fixture.Blueprint,
            fixture.Semantics,
            fixture.Geometry,
            fixture.ItemFacts,
            fixture.Bindings);

    private static Fixture CreateFixture(bool withTopRightOverride = false)
    {
        var blueprintId = Guid.NewGuid();
        var blueprint = new DesktopWorkspaceBlueprint(
            blueprintId,
            revision: 1,
            designSummary: "开发工具位于主屏左侧 35%。",
            displayPolicies:
            [new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0, 0, 0.35, 1),
                    DesktopWorkspaceAnchorKind.TopLeft,
                    DesktopWorkspacePackingOrder.RowMajor),
            ],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule("dev", "tools")],
            itemOverrides: withTopRightOverride
                ?
                [
                    new DesktopWorkspaceBlueprintItemOverride(
                        "item-a",
                        DesktopWorkspaceItemOverrideKind.PlaceInZone,
                        "tools",
                        DesktopWorkspaceAnchorKind.TopRight),
                ]
                : null);

        var semantics = new DesktopSemanticUnderstanding(
            "两个开发工具。",
            [new DesktopSemanticCategory("dev", "开发工具", "IDE 与终端")],
            [
                new DesktopSemanticAssignment("a", "dev", 1, "dev"),
                new DesktopSemanticAssignment("b", "dev", 1, "dev"),
            ]);
        var geometry = Geometry();
        var facts = new[]
        {
            new DesktopWorkspaceCompilationItemFact("a", "primary", new PixelPoint(30, 50), isMovable: true),
            new DesktopWorkspaceCompilationItemFact("b", "primary", new PixelPoint(110, 50), isMovable: true),
        };
        var bindings = withTopRightOverride
            ? new[] { new DesktopWorkspaceCompilationItemRefBinding("item-a", "a") }
            : [];
        return new Fixture(blueprint, semantics, geometry, facts, bindings);
    }

    private static ExplorerWorkspaceGeometry Geometry()
    {
        const int columns = 10;
        const int rows = 4;
        var spacing = new PixelPoint(80, 80);
        var slots = new List<ExplorerNativeSlot>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                slots.Add(new ExplorerNativeSlot(
                    $"primary:slot:{column:D4}:{row:D4}",
                    column,
                    row,
                    new PixelPoint(30 + column * spacing.X, 50 + row * spacing.Y)));
            }
        }

        var observationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var display = new ExplorerDisplayGeometry(
            "primary",
            "DISPLAY1",
            new PixelRect(0, 0, 800, 400),
            new PixelRect(0, 0, 800, 400),
            isPrimary: true,
            dpiX: 96,
            dpiY: 96,
            spacing,
            new PixelPoint(30, 50),
            new PixelPoint(30, 50),
            columns,
            rows,
            slots);
        return new ExplorerWorkspaceGeometry(
            observationId,
            "geometry:test",
            isAutoArrangeEnabled: false,
            spacing,
            [display]);
    }

    private static CompiledWorkspacePlan CopyPlan(
        CompiledWorkspacePlan source,
        IEnumerable<CompiledWorkspaceZone>? zones = null,
        IEnumerable<CompiledWorkspaceItem>? items = null)
        => new(
            source.BlueprintId,
            source.BlueprintRevision,
            source.ObservationId,
            source.GeometryFingerprint,
            source.PlanFingerprint,
            zones ?? source.Zones,
            source.ReservedRegions,
            items ?? source.Items);

    private static CompiledWorkspaceItem CopyPlacedItem(
        CompiledWorkspaceItem identity,
        CompiledWorkspaceItem target)
        => new(
            identity.StableId,
            identity.CategoryKey,
            identity.SourceDisplayRef,
            DesktopWorkspaceCompiledItemDisposition.Place,
            identity.ZoneKey,
            identity.TargetDisplayRef,
            target.TargetSlotId,
            target.TargetPixelPosition);

    private sealed record Fixture(
        DesktopWorkspaceBlueprint Blueprint,
        DesktopSemanticUnderstanding Semantics,
        ExplorerWorkspaceGeometry Geometry,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact> ItemFacts,
        IReadOnlyList<DesktopWorkspaceCompilationItemRefBinding> Bindings);
}
