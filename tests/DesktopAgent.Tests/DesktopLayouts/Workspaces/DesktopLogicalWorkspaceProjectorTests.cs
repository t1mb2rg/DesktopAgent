using System.Text.Json;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopLogicalWorkspaceProjectorTests
{
    private static readonly Guid ObservationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly PixelPoint DefaultSpacing = new(100, 100);

    [Fact]
    public void Project_TwoDisplays_ProducesProviderSafeGridAndLocalAuthority()
    {
        var result = DesktopLogicalWorkspaceProjector.Project(CreateObservation());

        Assert.True(result.Succeeded);
        var workspace = Assert.IsType<DesktopObservedLogicalWorkspace>(result.Workspace);
        Assert.Empty(result.Issues);
        Assert.Equal(ObservationId, workspace.ObservationId);
        Assert.StartsWith("topology-v1:", workspace.TopologyFingerprint, StringComparison.Ordinal);
        Assert.StartsWith("grid-v1:", workspace.GridFingerprint, StringComparison.Ordinal);

        var primary = Assert.Single(workspace.Displays, static display => display.IsPrimary);
        Assert.Equal("primary", primary.DisplayRef);
        Assert.Equal(new DesktopLogicalGridSize(9, 5), primary.GridSize);
        Assert.Equal(new DesktopLogicalTopologyRect(0, 0, 5_555, 10_000), primary.TopologyBounds);

        var secondary = Assert.Single(workspace.Displays, static display => !display.IsPrimary);
        Assert.Equal("secondary_1", secondary.DisplayRef);
        Assert.Equal(new DesktopLogicalGridSize(7, 5), secondary.GridSize);
        Assert.Equal(new DesktopLogicalTopologyRect(5_555, 0, 10_000, 10_000), secondary.TopologyBounds);

        Assert.Collection(
            workspace.Items,
            item =>
            {
                Assert.Equal("item-0001", item.ItemRef);
                Assert.Equal("Secondary.txt", item.DisplayName);
                Assert.Equal("secondary_1", item.CurrentDisplayRef);
                Assert.Equal(new DesktopLogicalGridCell(0, 0), item.CurrentCell);
                Assert.True(item.IsMovable);
                Assert.True(item.IsGridAligned);
                Assert.Equal(new[] { new DesktopLogicalGridCell(0, 0) }, item.OccupiedCells);
            },
            item =>
            {
                Assert.Equal("item-0002", item.ItemRef);
                Assert.Equal("Primary.txt", item.DisplayName);
                Assert.Equal("primary", item.CurrentDisplayRef);
                Assert.Equal(new DesktopLogicalGridCell(0, 0), item.CurrentCell);
                Assert.True(item.IsMovable);
                Assert.True(item.IsGridAligned);
                Assert.Equal(new[] { new DesktopLogicalGridCell(0, 0) }, item.OccupiedCells);
            },
            item =>
            {
                Assert.Equal("item-0003", item.ItemRef);
                Assert.Equal("回收站", item.DisplayName);
                Assert.Equal("primary", item.CurrentDisplayRef);
                Assert.Equal(new DesktopLogicalGridCell(1, 1), item.CurrentCell);
                Assert.False(item.IsMovable);
                Assert.True(item.IsGridAligned);
                Assert.Equal(new[] { new DesktopLogicalGridCell(1, 1) }, item.OccupiedCells);
            });

        var authority = Assert.IsType<DesktopLogicalWorkspaceProjectionAuthority>(result.Authority);
        Assert.Equal("secret-stable-a", authority.ResolveItem("item-0001").StableId);
        Assert.Equal("secret-stable-b", authority.ResolveItem("item-0002").StableId);
        var secondaryCalibration = authority.ResolveDisplay("secondary_1");
        Assert.Equal(@"\\.\DISPLAY2", secondaryCalibration.DeviceName);
        Assert.Equal(new PixelPoint(10, 20), secondaryCalibration.GridOrigin);
        Assert.Equal(DefaultSpacing, secondaryCalibration.Spacing);
    }

    [Fact]
    public void Project_IsDeterministicAcrossObservationCollectionOrder()
    {
        var firstObservation = CreateObservation();
        var secondObservation = CreateObservation(
            observationId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            displays: DefaultDisplays().Reverse().ToArray(),
            icons: DefaultIcons().Reverse().ToArray());

        var first = DesktopLogicalWorkspaceProjector.Project(firstObservation).Workspace!;
        var second = DesktopLogicalWorkspaceProjector.Project(secondObservation).Workspace!;

        Assert.Equal(first.TopologyFingerprint, second.TopologyFingerprint);
        Assert.Equal(first.GridFingerprint, second.GridFingerprint);
        Assert.Equal(
            first.Displays.Select(static display =>
                (display.DisplayRef, display.IsPrimary, display.TopologyBounds, display.GridSize)),
            second.Displays.Select(static display =>
                (display.DisplayRef, display.IsPrimary, display.TopologyBounds, display.GridSize)));
        Assert.Equal(
            first.Items.Select(static item =>
                (item.ItemRef, item.DisplayName, item.CurrentDisplayRef, item.CurrentCell, item.IsMovable)),
            second.Items.Select(static item =>
                (item.ItemRef, item.DisplayName, item.CurrentDisplayRef, item.CurrentCell, item.IsMovable)));
    }

    [Fact]
    public void Fingerprints_ChangeWhenTopologyOrGridCalibrationChanges()
    {
        var baseline = DesktopLogicalWorkspaceProjector.Project(CreateObservation()).Workspace!;
        var movedSecondary = new DisplayDescriptor(
            @"\\.\DISPLAY2",
            new PixelRect(0, 600, 800, 1_200),
            new PixelRect(0, 600, 800, 1_200),
            96,
            96,
            false);
        var topologyChanged = DesktopLogicalWorkspaceProjector.Project(CreateObservation(
            displays: [DefaultDisplays()[0], movedSecondary],
            icons:
            [
                DefaultIcons()[0] with { PixelPosition = new PixelPoint(10, 620) },
                DefaultIcons()[1],
                DefaultIcons()[2],
            ])).Workspace!;

        var spacingChanged = DesktopLogicalWorkspaceProjector.Project(CreateObservation(
            spacing: new PixelPoint(80, 100),
            icons:
            [
                DefaultIcons()[0],
                DefaultIcons()[1] with { PixelPosition = new PixelPoint(-950, 20) },
                DefaultIcons()[2] with { PixelPosition = new PixelPoint(-870, 120) },
            ])).Workspace!;

        Assert.NotEqual(baseline.TopologyFingerprint, topologyChanged.TopologyFingerprint);
        Assert.NotEqual(baseline.GridFingerprint, spacingChanged.GridFingerprint);
    }

    [Fact]
    public void Project_OffGridItem_PreservesAlignmentAndOccupiedCellsWithoutLeakingPixels()
    {
        var icons = DefaultIcons();
        icons[2] = icons[2] with { PixelPosition = new PixelPoint(-889, 120) };

        var result = DesktopLogicalWorkspaceProjector.Project(CreateObservation(icons: icons));

        Assert.True(result.Succeeded);
        var systemItem = Assert.Single(result.Workspace!.Items, static item => item.DisplayName == "回收站");
        Assert.False(systemItem.IsGridAligned);
        Assert.Equal(new DesktopLogicalGridCell(1, 1), systemItem.CurrentCell);
        Assert.Equal(
            new[] { new DesktopLogicalGridCell(1, 1), new DesktopLogicalGridCell(2, 1) },
            systemItem.OccupiedCells);
    }

    [Fact]
    public void Project_UnknownDisplay_FailsBeforeCreatingAuthority()
    {
        var icons = DefaultIcons();
        icons[0] = icons[0] with { MonitorDeviceName = @"\\.\MISSING" };

        var result = DesktopLogicalWorkspaceProjector.Project(CreateObservation(icons: icons));

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Null(result.Authority);
        Assert.Contains(result.Issues, static issue =>
            issue.Code == DesktopLogicalWorkspaceProjectionIssueCode.UnknownIconDisplay);
    }

    [Fact]
    public void Project_InvalidDisplayOrItemFacts_ReturnsStructuredIssues()
    {
        var displays = DefaultDisplays();
        displays[0] = displays[0] with { DpiX = 0 };
        var icons = DefaultIcons();
        icons[0] = icons[0] with { ItemKind = (ItemKind)999 };

        var result = DesktopLogicalWorkspaceProjector.Project(CreateObservation(
            displays: displays,
            icons: icons));

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Null(result.Authority);
        Assert.Contains(result.Issues, static issue =>
            issue.Code == DesktopLogicalWorkspaceProjectionIssueCode.InvalidDisplayGeometry);
        Assert.Contains(result.Issues, static issue =>
            issue.Code == DesktopLogicalWorkspaceProjectionIssueCode.InvalidIconIdentity);
    }

    [Fact]
    public void Project_ExistingOverlappingItems_AreRepresentedWithoutInventingARepair()
    {
        var icons = DefaultIcons();
        icons[2] = icons[2] with { PixelPosition = icons[1].PixelPosition };

        var result = DesktopLogicalWorkspaceProjector.Project(CreateObservation(icons: icons));

        Assert.True(result.Succeeded);
        var primaryItems = result.Workspace!.Items
            .Where(static item => item.CurrentDisplayRef == "primary")
            .ToArray();
        Assert.Equal(2, primaryItems.Length);
        Assert.All(primaryItems, static item =>
            Assert.Equal(new DesktopLogicalGridCell(0, 0), item.CurrentCell));
    }

    [Fact]
    public void Project_InsufficientGridConsensus_RefusesToGuess()
    {
        var icon = DefaultIcons()[0];
        var result = DesktopLogicalWorkspaceProjector.Project(CreateObservation(icons: [icon]));

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Contains(result.Issues, static issue =>
            issue.Code == DesktopLogicalWorkspaceProjectionIssueCode.GridPhaseUnavailable);
    }

    [Fact]
    public void PublicProjectionSerialization_DoesNotContainLocalAuthorityFacts()
    {
        var result = DesktopLogicalWorkspaceProjector.Project(CreateObservation());

        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("secret-stable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPLAY1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPLAY2", json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PixelPosition", json, StringComparison.Ordinal);
        Assert.DoesNotContain("GridOrigin", json, StringComparison.Ordinal);
        Assert.Contains("item-0001", json, StringComparison.Ordinal);
        Assert.Contains("secondary_1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_DefaultOrInvalidTopologyBounds_CannotEnterProviderWorkspace()
    {
        Assert.Throws<ArgumentException>(() => new DesktopObservedLogicalDisplay(
            "primary",
            true,
            default,
            new DesktopLogicalGridSize(2, 2)));
    }

    private static DesktopObservation CreateObservation(
        Guid? observationId = null,
        IReadOnlyList<DisplayDescriptor>? displays = null,
        IReadOnlyList<DesktopIconDescriptor>? icons = null,
        PixelPoint? spacing = null)
        => new(
            observationId ?? ObservationId,
            DateTimeOffset.Parse("2026-08-19T12:00:00Z"),
            displays ?? DefaultDisplays(),
            new DesktopIconPositioningCapabilities(
                false,
                spacing ?? DefaultSpacing,
                ConnectionMethod.FindWindowSW,
                null),
            icons ?? DefaultIcons(),
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(10));

    private static DisplayDescriptor[] DefaultDisplays() =>
    [
        new(
            @"\\.\DISPLAY1",
            new PixelRect(-1_000, 0, 0, 600),
            new PixelRect(-1_000, 0, 0, 600),
            96,
            96,
            true),
        new(
            @"\\.\DISPLAY2",
            new PixelRect(0, 0, 800, 600),
            new PixelRect(0, 0, 800, 600),
            144,
            144,
            false),
    ];

    private static DesktopIconDescriptor[] DefaultIcons() =>
    [
        new(
            "secret-stable-a",
            "Secondary.txt",
            @"C:\Secret\Secondary.txt",
            ItemKind.File,
            new PixelPoint(10, 20),
            @"\\.\DISPLAY2",
            false,
            true),
        new(
            "secret-stable-b",
            "Primary.txt",
            @"C:\Secret\Primary.txt",
            ItemKind.File,
            new PixelPoint(-990, 20),
            @"\\.\DISPLAY1",
            false,
            true),
        new(
            "secret-stable-system",
            "回收站",
            "::{645FF040-5081-101B-9F08-00AA002F954E}",
            ItemKind.SystemItem,
            new PixelPoint(-890, 120),
            @"\\.\DISPLAY1",
            true,
            false),
    ];
}
