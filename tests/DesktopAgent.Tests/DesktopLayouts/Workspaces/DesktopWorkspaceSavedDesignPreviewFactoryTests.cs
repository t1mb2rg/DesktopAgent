using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopWorkspaceSavedDesignPreviewFactoryTests
{
    [Fact]
    public void Reuse_CompilesSameDesignAgainstFreshPositions_AndStillRejectsLaterDrift()
    {
        var fixture = CreateFixture();
        var fresh = Observation(fixture.Source.Icons[0] with { PixelPosition = new PixelPoint(110, 50) });
        var geometry = Geometry(fresh.ObservationId);
        var preview = DesktopWorkspaceSavedDesignPreviewFactory.Create(
            fixture.Blueprint, fixture.Understanding, fresh, geometry);

        Assert.Same(fixture.Blueprint, preview.Blueprint);
        Assert.Equal(fresh.ObservationId, preview.SourceObservationId);
        Assert.Equal(new PixelPoint(110, 50), Assert.Single(preview.Items).SourcePixelPosition);
        var facts = DesktopWorkspaceLocalItemFactProjector.Project(fresh, geometry);
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        Assert.True(DesktopWorkspaceApplyFreshCheck.Evaluate(confirmation, geometry, facts).IsReady);
        var drift = new DesktopWorkspaceCompilationItemFact("a", "primary", new PixelPoint(111, 50), true);
        Assert.False(DesktopWorkspaceApplyFreshCheck.Evaluate(confirmation, geometry, [drift]).IsReady);
    }

    [Fact]
    public void Reuse_ChangedSemanticFactsCannotUseOldClassification()
    {
        var fixture = CreateFixture();
        var changed = Observation(fixture.Source.Icons[0] with { DisplayName = "Different file" });
        var error = Assert.Throws<InvalidDataException>(() => DesktopWorkspaceSavedDesignPreviewFactory.Create(
            fixture.Blueprint, fixture.Understanding, changed, Geometry(changed.ObservationId)));
        Assert.Contains("重新理解桌面", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeDesktopDesign_EmptyPrimaryEvacuatesToTwoSecondaryScreens(bool systemIcon)
    {
        var firstId = systemIcon ? "::{645FF040-5081-101B-9F08-00AA002F954E}" : "a";
        var displays = new[]
        {
            new DisplayDescriptor("DISPLAY1", new(0, 0, 400, 240), new(0, 0, 400, 240), 96, 96, true),
            new DisplayDescriptor("DISPLAY2", new(-400, 0, 0, 240), new(-400, 0, 0, 240), 96, 96, false),
            new DisplayDescriptor("DISPLAY3", new(0, 240, 400, 480), new(0, 240, 400, 480), 96, 96, false),
        };
        var spacing = new PixelPoint(80, 80);
        var source = new DesktopObservation(Guid.NewGuid(), DateTimeOffset.UtcNow, displays,
            new(false, spacing, ConnectionMethod.FindWindowSW, null),
            [new(firstId, systemIcon ? "回收站" : "工作文件", systemIcon ? firstId : null,
                systemIcon ? ItemKind.SystemItem : ItemKind.File, new(30, 50), "DISPLAY1", systemIcon, !systemIcon),
             new("b", "待确认文件", null, ItemKind.File, new(110, 50), "DISPLAY1", false, true)],
            ConnectionMethod.FindWindowSW, TimeSpan.Zero);
        var refs = new[] { "primary", "secondary_1", "secondary_2" };
        var geometry = new ExplorerWorkspaceGeometry(source.ObservationId, "geometry:evacuate", false, spacing,
            displays.Select((display, index) => new ExplorerDisplayGeometry(refs[index], display.DeviceName, display.Bounds,
                display.WorkArea, display.IsPrimary, 96, 96, spacing,
                new(30, 50), new(display.Bounds.Left + 30, display.Bounds.Top + 50), 5, 3,
                Enumerable.Range(0, 15).Select(slot => new ExplorerNativeSlot($"{refs[index]}:{slot}", slot % 5, slot / 5,
                    new(display.Bounds.Left + 30 + slot % 5 * 80, display.Bounds.Top + 50 + slot / 5 * 80))))));
        var semantic = new DesktopSemanticUnderstanding("工作与待确认", [new("dev", "工作", "工作"), new("unknown", "待确认", "待确认")],
            [new(firstId, "dev", 1, "工作"), new("b", "unknown", 1, "待确认")]);
        var understanding = new DesktopSemanticUnderstandingSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, source.ObservationId,
            DesktopSemanticUnderstandingSourceFingerprint.Create(source), 2, semantic);
        var draft = new DesktopAgent.Core.DesktopLayouts.Workspaces.Editing.DesktopWorkspaceDesignDraft(Guid.NewGuid(), "主屏清空",
            zones: [new("work", "工作", "secondary_1", new(0, 0, 1, 1)), new("inbox", "待确认", "secondary_2", new(0, 0, 1, 1))],
            categoryRules: [new("dev", "work"), new("unknown", "inbox")]);
        var blueprint = DesktopWorkspaceWholeDesktopPolicy.CompleteIfAbsent(draft, refs).ToBlueprint();
        var preview = DesktopWorkspaceSavedDesignPreviewFactory.Create(blueprint, understanding, source, geometry);
        Assert.Equal(2, preview.MoveCount);
        Assert.Equal("secondary_1", preview.Items.Single(item => item.StableId == firstId).TargetDisplayRef);
        Assert.Equal("secondary_2", preview.Items.Single(item => item.StableId == "b").TargetDisplayRef);
        Assert.DoesNotContain(preview.Items, item => item.TargetDisplayRef == "primary");
    }

    [Fact]
    public void Reuse_ItemOverrideRequiresExplicitBinding_AndPreservesConfirmedKeep()
    {
        var fixture = CreateFixture(withOverride: true);
        var geometry = Geometry(fixture.Source.ObservationId);
        Assert.Throws<InvalidDataException>(() => DesktopWorkspaceSavedDesignPreviewFactory.Create(
            fixture.Blueprint, fixture.Understanding, fixture.Source, geometry));

        var preview = DesktopWorkspaceSavedDesignPreviewFactory.Create(
            fixture.Blueprint, fixture.Understanding, fixture.Source, geometry,
            [new DesktopWorkspaceCompilationItemRefBinding("old-request-ref", "a")]);
        Assert.Equal(DesktopWorkspaceCompiledItemDisposition.Keep, Assert.Single(preview.Items).Disposition);
        Assert.Equal(0, preview.MoveCount);
    }

    [Fact]
    public void Reuse_UnknownBindingFailsInsteadOfGuessingItemIdentity()
    {
        var fixture = CreateFixture(withOverride: true);
        Assert.Throws<InvalidDataException>(() => DesktopWorkspaceSavedDesignPreviewFactory.Create(
            fixture.Blueprint, fixture.Understanding, fixture.Source, Geometry(fixture.Source.ObservationId),
            [new DesktopWorkspaceCompilationItemRefBinding("old-request-ref", "not-a-current-item")]));
    }

    [Fact]
    public void Overlay_UsesRealizedBoundsAndStableZoneIdentity_WithoutChangingCompiledZone()
    {
        var requested = new DesktopWorkspaceRelativeRect(0.1, 0.1, 0.6, 0.6);
        var realized = new DesktopWorkspaceRelativeRect(0.25, 0, 0.5, 1);
        var zone = new CompiledWorkspaceZone("tools", "工具", "primary", requested, realized,
            new DesktopWorkspaceNativeRect(1, 0, 2, 3), DesktopWorkspaceAnchorKind.TopLeft,
            DesktopWorkspacePackingOrder.RowMajor, 6);
        var overlay = Assert.Single(DesktopWorkspaceOverlayProjection.Project([zone]));
        Assert.Equal(new NormalizedRect(2500, 0, 7500, 10000), overlay.Bounds);
        Assert.Equal(ZonePlacementFlow.RowMajor, overlay.PlacementFlow);
        Assert.Equal(overlay.ZoneId, Assert.Single(DesktopWorkspaceOverlayProjection.Project([zone])).ZoneId);
        Assert.Equal(requested, zone.RequestedBounds);
        Assert.Equal(realized, zone.RealizedBounds);
    }

    private static (DesktopWorkspaceBlueprint Blueprint, DesktopSemanticUnderstandingSnapshot Understanding,
        DesktopObservation Source) CreateFixture(bool withOverride = false)
    {
        var source = Observation(new DesktopIconDescriptor("a", "Tool", null, ItemKind.File,
            new PixelPoint(30, 50), "DISPLAY1", false, true));
        var semantic = new DesktopSemanticUnderstanding("工具", [new DesktopSemanticCategory("dev", "工具", "工具")],
            [new DesktopSemanticAssignment("a", "dev", 1, "工具")]);
        var snapshot = new DesktopSemanticUnderstandingSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow,
            source.ObservationId, DesktopSemanticUnderstandingSourceFingerprint.Create(source), 1, semantic);
        var blueprint = new DesktopWorkspaceBlueprint(Guid.NewGuid(), 1, "工具放到主屏",
            displayPolicies: [new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones: [new DesktopWorkspaceBlueprintZone("tools", "工具", "primary", new DesktopWorkspaceRelativeRect(0, 0, 1, 1),
                DesktopWorkspaceAnchorKind.TopLeft, DesktopWorkspacePackingOrder.RowMajor)],
            categoryRules: [new DesktopWorkspaceBlueprintCategoryRule("dev", "tools")],
            itemOverrides: withOverride ? [new DesktopWorkspaceBlueprintItemOverride("old-request-ref", DesktopWorkspaceItemOverrideKind.Keep)] : []);
        return (blueprint, snapshot, source);
    }

    private static DesktopObservation Observation(DesktopIconDescriptor icon)
        => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new DisplayDescriptor("DISPLAY1", new PixelRect(0, 0, 400, 240), new PixelRect(0, 0, 400, 240), 96, 96, true)],
            new DesktopIconPositioningCapabilities(false, new PixelPoint(80, 80), ConnectionMethod.FindWindowSW, null),
            [icon], ConnectionMethod.FindWindowSW, TimeSpan.Zero);

    private static ExplorerWorkspaceGeometry Geometry(Guid observationId)
    {
        var spacing = new PixelPoint(80, 80);
        var slots = Enumerable.Range(0, 15).Select(index => new ExplorerNativeSlot($"slot:{index}", index % 5,
            index / 5, new PixelPoint(30 + index % 5 * 80, 50 + index / 5 * 80)));
        var display = new ExplorerDisplayGeometry("primary", "DISPLAY1", new PixelRect(0, 0, 400, 240),
            new PixelRect(0, 0, 400, 240), true, 96, 96, spacing, new PixelPoint(30, 50), new PixelPoint(30, 50), 5, 3, slots);
        return new ExplorerWorkspaceGeometry(observationId, "geometry:saved-design", false, spacing, [display]);
    }
}
