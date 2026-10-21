using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopLogicalWorkspaceProjectorMultiDisplayGridEvidenceTests
{
    private static readonly PixelPoint Spacing = new(100, 100);

    [Fact]
    public void Project_ThreeDisplays_OneFreePlacementDisplayDoesNotInvalidateReliableSharedPhase()
    {
        var displays = new[]
        {
            Display(@"\\.\DISPLAY1", new PixelRect(0, 0, 1_000, 600), isPrimary: true),
            Display(@"\\.\DISPLAY2", new PixelRect(1_000, 0, 1_800, 600)),
            Display(@"\\.\DISPLAY3", new PixelRect(-800, 0, 0, 600)),
        };
        var icons = new[]
        {
            Icon("p-1", new PixelPoint(10, 20), @"\\.\DISPLAY1"),
            Icon("p-2", new PixelPoint(110, 120), @"\\.\DISPLAY1"),
            Icon("p-3", new PixelPoint(210, 220), @"\\.\DISPLAY1"),
            Icon("r-1", new PixelPoint(1_010, 20), @"\\.\DISPLAY2"),
            Icon("r-2", new PixelPoint(1_110, 120), @"\\.\DISPLAY2"),
            Icon("r-3", new PixelPoint(1_210, 220), @"\\.\DISPLAY2"),
            Icon("l-1", new PixelPoint(-790, 20), @"\\.\DISPLAY3"),
            Icon("l-2", new PixelPoint(-680, 130), @"\\.\DISPLAY3"),
            Icon("l-3", new PixelPoint(-570, 240), @"\\.\DISPLAY3"),
            Icon("l-4", new PixelPoint(-460, 350), @"\\.\DISPLAY3"),
        };

        var result = DesktopLogicalWorkspaceProjector.Project(Observation(displays, icons));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Issues);
        Assert.Equal(3, result.Workspace!.Displays.Count);
        Assert.Equal(icons.Length, result.Workspace.Items.Count);

        var authority = Assert.IsType<DesktopLogicalWorkspaceProjectionAuthority>(result.Authority);
        Assert.Equal(
            new PixelPoint(10, 20),
            Assert.Single(authority.DisplayCalibrations,
                static calibration => calibration.DeviceName == @"\\.\DISPLAY1").GridOrigin);
        Assert.Equal(
            new PixelPoint(1_010, 20),
            Assert.Single(authority.DisplayCalibrations,
                static calibration => calibration.DeviceName == @"\\.\DISPLAY2").GridOrigin);
        Assert.Equal(
            new PixelPoint(-790, 20),
            Assert.Single(authority.DisplayCalibrations,
                static calibration => calibration.DeviceName == @"\\.\DISPLAY3").GridOrigin);
    }

    [Fact]
    public void Project_ReliableDisplaysDisagree_FailsClosedWithConflictIssue()
    {
        var displays = new[]
        {
            Display(@"\\.\DISPLAY1", new PixelRect(0, 0, 1_000, 600), isPrimary: true),
            Display(@"\\.\DISPLAY2", new PixelRect(1_000, 0, 1_800, 600)),
        };
        var icons = new[]
        {
            Icon("a-1", new PixelPoint(10, 20), @"\\.\DISPLAY1"),
            Icon("a-2", new PixelPoint(110, 120), @"\\.\DISPLAY1"),
            Icon("a-3", new PixelPoint(210, 220), @"\\.\DISPLAY1"),
            Icon("b-1", new PixelPoint(1_030, 40), @"\\.\DISPLAY2"),
            Icon("b-2", new PixelPoint(1_130, 140), @"\\.\DISPLAY2"),
            Icon("b-3", new PixelPoint(1_230, 240), @"\\.\DISPLAY2"),
        };

        var result = DesktopLogicalWorkspaceProjector.Project(Observation(displays, icons));

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Null(result.Authority);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DesktopLogicalWorkspaceProjectionIssueCode.GridPhaseConflict, issue.Code);
        Assert.Contains("不一致", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_NoDisplayHasReliablePhaseAndGlobalFallbackHasNoMajority_FailsClosed()
    {
        var displays = new[]
        {
            Display(@"\\.\DISPLAY1", new PixelRect(0, 0, 1_000, 600), isPrimary: true),
            Display(@"\\.\DISPLAY2", new PixelRect(1_000, 0, 1_800, 600)),
        };
        var icons = new[]
        {
            Icon("p-1", new PixelPoint(10, 20), @"\\.\DISPLAY1"),
            Icon("p-2", new PixelPoint(130, 40), @"\\.\DISPLAY1"),
            Icon("s-1", new PixelPoint(1_050, 60), @"\\.\DISPLAY2"),
            Icon("s-2", new PixelPoint(1_170, 80), @"\\.\DISPLAY2"),
        };

        var result = DesktopLogicalWorkspaceProjector.Project(Observation(displays, icons));

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Null(result.Authority);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DesktopLogicalWorkspaceProjectionIssueCode.GridPhaseUnavailable, issue.Code);
        Assert.Contains("全局回退", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_SingleDisplayStrongPhase_PreservesExistingBehavior()
    {
        var displays = new[]
        {
            Display(@"\\.\DISPLAY1", new PixelRect(-1_000, 0, 0, 600), isPrimary: true),
        };
        var icons = new[]
        {
            Icon("n-1", new PixelPoint(-990, 20), @"\\.\DISPLAY1"),
            Icon("n-2", new PixelPoint(-890, 120), @"\\.\DISPLAY1"),
            Icon("n-3", new PixelPoint(-790, 220), @"\\.\DISPLAY1"),
        };

        var result = DesktopLogicalWorkspaceProjector.Project(Observation(displays, icons));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Issues);
        var authority = Assert.IsType<DesktopLogicalWorkspaceProjectionAuthority>(result.Authority);
        Assert.Equal(new PixelPoint(-990, 20), authority.ResolveDisplay("primary").GridOrigin);
    }

    private static DesktopObservation Observation(
        IReadOnlyList<DisplayDescriptor> displays,
        IReadOnlyList<DesktopIconDescriptor> icons)
        => new(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            DateTimeOffset.Parse("2026-08-26T11:00:00Z"),
            displays,
            new DesktopIconPositioningCapabilities(
                false,
                Spacing,
                ConnectionMethod.FindWindowSW,
                null),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(10));

    private static DisplayDescriptor Display(
        string deviceName,
        PixelRect bounds,
        bool isPrimary = false)
        => new(
            deviceName,
            bounds,
            bounds,
            96,
            96,
            isPrimary);

    private static DesktopIconDescriptor Icon(
        string stableId,
        PixelPoint position,
        string monitorDeviceName)
        => new(
            stableId,
            stableId,
            null,
            ItemKind.File,
            position,
            monitorDeviceName,
            false,
            true);
}
