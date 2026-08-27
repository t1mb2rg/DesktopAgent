using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class DesktopExplorerWorkspaceGeometryAdapterTests
{
    [Fact]
    public void Build_StrongPhaseWithNegativeDisplay_ProducesNativeSlotsWithoutCurrentCellProjection()
    {
        var displays = new[]
        {
            Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 760, isPrimary: true),
            Display("DISPLAY2", -800, 0, 0, 800, -800, 0, 0, 800, isPrimary: false),
        };
        var observation = Observation(
            displays,
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                // 合法 Windows 事实，但位于 native slot surface 尾部，不要求先投影成 CurrentCell。
                Icon("tail", "Tail", 799, 759, "DISPLAY1"),
                Icon("l1", "L1", -770, 50, "DISPLAY2"),
                Icon("l2", "L2", -690, 130, "DISPLAY2"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.True(result.Succeeded);
        var geometry = Assert.IsType<ExplorerWorkspaceGeometry>(result.Geometry);
        Assert.Equal(2, geometry.Displays.Count);
        Assert.Equal("primary", geometry.Displays.Single(display => display.IsPrimary).DisplayRef);

        var left = geometry.Displays.Single(display => display.DeviceName == "DISPLAY2");
        Assert.Equal("secondary_1", left.DisplayRef);
        Assert.True(left.GridOrigin.X < 0);
        Assert.Equal(30, PositiveModulo(left.GridOrigin.X, geometry.Spacing.X));
        Assert.Equal(50, PositiveModulo(left.GridOrigin.Y, geometry.Spacing.Y));

        Assert.All(geometry.Displays.SelectMany(static display => display.Slots), slot =>
        {
            Assert.Equal(30, PositiveModulo(slot.PixelPosition.X, geometry.Spacing.X));
            Assert.Equal(50, PositiveModulo(slot.PixelPosition.Y, geometry.Spacing.Y));
        });

        Assert.DoesNotContain(
            typeof(ExplorerWorkspaceGeometry).GetProperties(),
            property => property.Name.Contains("CurrentCell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(ExplorerFixedObstacle).GetProperties(),
            property => property.Name.Contains("CurrentCell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_SystemItemKind_IsFixedObstacleEvenWhenLegacyFlagIsFalse()
    {
        var observation = Observation(
            [Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true)],
            [
                Icon(
                    "recycle",
                    "Recycle Bin",
                    30,
                    50,
                    "DISPLAY1",
                    itemKind: ItemKind.SystemItem,
                    isSystemItem: false),
                Icon("a", "A", 110, 130, "DISPLAY1"),
                Icon("b", "B", 190, 210, "DISPLAY1"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.True(result.Succeeded);
        var display = Assert.Single(result.Geometry!.Displays);
        var obstacle = Assert.Single(display.FixedObstacles);
        Assert.Equal("recycle", obstacle.StableId);
        Assert.Contains("primary:slot:0000:0000", obstacle.BlockedSlotIds);
        Assert.DoesNotContain(display.FixedObstacles, item => item.StableId == "a" || item.StableId == "b");
    }

    [Fact]
    public void Build_ScatteredFreePlacement_FailsGeometryWithoutInventingPhase()
    {
        var observation = Observation(
            [Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true)],
            [
                Icon("a", "A", 3, 5, "DISPLAY1"),
                Icon("b", "B", 22, 17, "DISPLAY1"),
                Icon("c", "C", 41, 39, "DISPLAY1"),
                Icon("d", "D", 63, 71, "DISPLAY1"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.False(result.Succeeded);
        Assert.Null(result.Geometry);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseUnavailable);
    }

    [Fact]
    public void Build_ReliableButConflictingDisplayPhases_FailsClosed()
    {
        var observation = Observation(
            [
                Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true),
                Display("DISPLAY2", -800, 0, 0, 800, -800, 0, 0, 800, isPrimary: false),
            ],
            [
                Icon("p1", "P1", 30, 50, "DISPLAY1"),
                Icon("p2", "P2", 110, 130, "DISPLAY1"),
                Icon("l1", "L1", -790, 20, "DISPLAY2"),
                Icon("l2", "L2", -710, 100, "DISPLAY2"),
            ]);

        var result = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseConflict);
    }

    [Fact]
    public void GeometryFingerprint_IgnoresMovableSourcePositionButTracksFixedObstacleSurface()
    {
        var display = Display("DISPLAY1", 0, 0, 800, 800, 0, 0, 800, 800, isPrimary: true);
        var first = Observation(
            [display],
            [
                Icon("system", "System", 30, 50, "DISPLAY1", ItemKind.SystemItem, isSystemItem: false),
                Icon("a", "A", 110, 130, "DISPLAY1"),
                Icon("b", "B", 190, 210, "DISPLAY1"),
            ]);
        var second = Observation(
            [display],
            [
                Icon("system", "System", 30, 50, "DISPLAY1", ItemKind.SystemItem, isSystemItem: false),
                Icon("a", "A", 270, 290, "DISPLAY1"),
                Icon("b", "B", 350, 370, "DISPLAY1"),
            ]);
        var fixedMoved = Observation(
            [display],
            [
                Icon("system", "System", 110, 130, "DISPLAY1", ItemKind.SystemItem, isSystemItem: false),
                Icon("a", "A", 270, 290, "DISPLAY1"),
                Icon("b", "B", 350, 370, "DISPLAY1"),
            ]);

        var firstGeometry = DesktopExplorerWorkspaceGeometryAdapter.Build(first).Geometry!;
        var secondGeometry = DesktopExplorerWorkspaceGeometryAdapter.Build(second).Geometry!;
        var fixedMovedGeometry = DesktopExplorerWorkspaceGeometryAdapter.Build(fixedMoved).Geometry!;

        Assert.Equal(firstGeometry.GeometryFingerprint, secondGeometry.GeometryFingerprint);
        Assert.NotEqual(firstGeometry.GeometryFingerprint, fixedMovedGeometry.GeometryFingerprint);
    }

    private static DesktopObservation Observation(
        IReadOnlyList<DisplayDescriptor> displays,
        IReadOnlyList<DesktopIconDescriptor> icons)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            displays,
            new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled: false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                primaryPathException: null),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);

    private static DisplayDescriptor Display(
        string deviceName,
        int left,
        int top,
        int right,
        int bottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        bool isPrimary)
        => new(
            deviceName,
            new PixelRect(left, top, right, bottom),
            new PixelRect(workLeft, workTop, workRight, workBottom),
            96,
            96,
            isPrimary);

    private static DesktopIconDescriptor Icon(
        string stableId,
        string displayName,
        int x,
        int y,
        string monitorDeviceName,
        ItemKind itemKind = ItemKind.File,
        bool isSystemItem = false)
        => new(
            stableId,
            displayName,
            ParsingName: null,
            itemKind,
            new PixelPoint(x, y),
            monitorDeviceName,
            isSystemItem,
            IsFileSystemItem: itemKind != ItemKind.SystemItem);

    private static int PositiveModulo(int value, int modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }
}
