using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Productization;

public sealed class DesktopUnderstandingMultiDisplayWorkspaceCompilerTests
{
    [Fact]
    public void Compile_PartitionsAssignmentsByObservedMonitor_AndKeepsSingleDisplayDrafts()
    {
        var understanding = CreateUnderstanding();
        var observation = CreateObservation();

        var result = DesktopUnderstandingMultiDisplayWorkspaceCompiler.Compile(
            understanding,
            observation);

        Assert.Equal(4, result.SourceAssignmentCount);
        Assert.Equal(4, result.ScopedAssignmentCount);
        Assert.Equal(0, result.UnscopedAssignmentCount);
        Assert.Equal(4, result.MappedAssignmentCount);
        Assert.Equal(0, result.UnmappedAssignmentCount);
        Assert.Equal(2, result.Displays.Count);

        var primary = Assert.Single(result.Displays, item => item.Display.DeviceName == @"\\.\DISPLAY1");
        var secondary = Assert.Single(result.Displays, item => item.Display.DeviceName == @"\\.\DISPLAY2");

        Assert.True(primary.Display.IsPrimary);
        Assert.Equal(2, primary.SourceAssignmentCount);
        Assert.Equal(2, primary.ObservedIconCount);
        Assert.Equal(TargetDisplayBindingKind.ExactDeviceName, primary.Draft.TargetDisplayBinding.Kind);
        Assert.Equal(@"\\.\DISPLAY1", primary.Draft.TargetDisplayBinding.DeviceName);
        Assert.All(primary.Draft.RuleSet.Rules, rule =>
            Assert.Contains(rule.StableId, new[] { "p-work", "p-game" }));

        Assert.False(secondary.Display.IsPrimary);
        Assert.Equal(2, secondary.SourceAssignmentCount);
        Assert.Equal(2, secondary.ObservedIconCount);
        Assert.Equal(TargetDisplayBindingKind.ExactDeviceName, secondary.Draft.TargetDisplayBinding.Kind);
        Assert.Equal(@"\\.\DISPLAY2", secondary.Draft.TargetDisplayBinding.DeviceName);
        Assert.All(secondary.Draft.RuleSet.Rules, rule =>
            Assert.Contains(rule.StableId, new[] { "s-work", "s-game" }));
    }

    [Fact]
    public void Compile_ZoneUsedOnBothDisplays_DoesNotShareExistingZoneIdentity()
    {
        var sharedExistingZoneId = Guid.NewGuid();
        var understanding = CreateUnderstanding(sharedExistingZoneId);
        var observation = CreateObservation();

        var result = DesktopUnderstandingMultiDisplayWorkspaceCompiler.Compile(
            understanding,
            observation);

        var workZones = result.Displays
            .SelectMany(display => display.Draft.Zones)
            .Where(zone => zone.Name == "工作")
            .ToArray();

        Assert.Equal(2, workZones.Length);
        Assert.All(workZones, zone => Assert.NotEqual(sharedExistingZoneId, zone.ZoneId));
        Assert.NotEqual(workZones[0].ZoneId, workZones[1].ZoneId);
    }

    [Fact]
    public void Compile_AssignmentWithoutObservedMonitor_IsReportedUnscopedInsteadOfGuessed()
    {
        var baseUnderstanding = CreateUnderstanding();
        var assignment = new DesktopUnderstandingAssignment(
            "missing", "work", "work-zone", 0.8d, "观察中不存在");
        var understanding = new DesktopUnderstandingDraft(
            baseUnderstanding.IntentSummary,
            baseUnderstanding.DesktopSummary,
            baseUnderstanding.Categories,
            baseUnderstanding.Zones,
            baseUnderstanding.Assignments.Concat(new[] { assignment }),
            baseUnderstanding.Questions);

        var result = DesktopUnderstandingMultiDisplayWorkspaceCompiler.Compile(
            understanding,
            CreateObservation());

        Assert.Equal(5, result.SourceAssignmentCount);
        Assert.Equal(4, result.ScopedAssignmentCount);
        Assert.Equal(1, result.UnscopedAssignmentCount);
        Assert.Equal(1, result.UnmappedAssignmentCount);
    }

    [Fact]
    public void Compile_PrimaryWithZeroTargets_KeepsReservedOutboundSourceParticipation()
    {
        var understanding = CreateUnderstanding();
        var observation = CreateObservation();
        var state = new DesktopLayoutConversationState(
            "Move most items to the secondary display and keep the primary center clear.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["work"] = "secondary_1",
                ["game"] = "secondary_1",
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, DesktopDisplayLoadPreference>(StringComparer.Ordinal)
            {
                ["primary"] = DesktopDisplayLoadPreference.Low,
                ["secondary_1"] = DesktopDisplayLoadPreference.High,
            },
            new[]
            {
                new DesktopReservedRegionConstraint(
                    "primary-wallpaper",
                    "primary",
                    DesktopZonePlacementHint.Center,
                    DesktopZoneSizeHint.Large,
                    "Show the wallpaper"),
            });

        var result = DesktopUnderstandingMultiDisplayWorkspaceCompiler.Compile(
            understanding,
            observation,
            state);

        Assert.Equal(2, result.Displays.Count);
        Assert.Equal(4, result.ScopedAssignmentCount);
        Assert.Equal(2, result.CrossDisplayAssignmentCount);

        var primary = Assert.Single(result.Displays, item => item.Display.DeviceName == @"\\.\DISPLAY1");
        Assert.Equal(0, primary.SourceAssignmentCount);
        Assert.Equal(0, primary.MappedAssignmentCount);
        Assert.Single(primary.ReservedRegions);
        Assert.NotEmpty(primary.Draft.Zones);
        Assert.All(primary.Draft.Zones, zone => Assert.True(
            zone.Bounds.Right <= 3_000
            || zone.Bounds.Left >= 7_000
            || zone.Bounds.Bottom <= 3_000
            || zone.Bounds.Top >= 7_000));
        Assert.Equal(
            new[] { "p-game", "p-work" },
            primary.Draft.RuleSet.Rules
                .Where(rule => rule.Kind == DesktopLayoutIconRuleKind.Exclude)
                .Select(static rule => rule.StableId)
                .ToArray());
        Assert.Empty(primary.Draft.ExplicitInboundStableIds);

        var secondary = Assert.Single(result.Displays, item => item.Display.DeviceName == @"\\.\DISPLAY2");
        Assert.Equal(4, secondary.SourceAssignmentCount);
        Assert.Equal(4, secondary.MappedAssignmentCount);
        Assert.Equal(new[] { "p-game", "p-work" }, secondary.Draft.ExplicitInboundStableIds);
    }

    private static DesktopUnderstandingDraft CreateUnderstanding(Guid? workExistingZoneId = null)
    {
        var categories = new[]
        {
            new DesktopUnderstandingCategoryDraft("work", "工作", "工作资料"),
            new DesktopUnderstandingCategoryDraft("game", "娱乐", "娱乐项目"),
        };
        var zones = new[]
        {
            new DesktopUnderstandingZoneDraft(
                "work-zone", "工作", workExistingZoneId,
                DesktopZonePlacementHint.Left,
                DesktopZoneSizeHint.Large,
                new[] { "work" }),
            new DesktopUnderstandingZoneDraft(
                "game-zone", "娱乐", null,
                DesktopZonePlacementHint.Right,
                DesktopZoneSizeHint.Medium,
                new[] { "game" }),
        };
        var assignments = new[]
        {
            new DesktopUnderstandingAssignment("p-work", "work", "work-zone", 0.95d, "主屏工作"),
            new DesktopUnderstandingAssignment("p-game", "game", "game-zone", 0.90d, "主屏娱乐"),
            new DesktopUnderstandingAssignment("s-work", "work", "work-zone", 0.94d, "副屏工作"),
            new DesktopUnderstandingAssignment("s-game", "game", "game-zone", 0.89d, "副屏娱乐"),
        };

        return new DesktopUnderstandingDraft(
            "按用途整理",
            "双屏桌面",
            categories,
            zones,
            assignments,
            Array.Empty<DesktopUnderstandingQuestion>());
    }

    private static DesktopObservation CreateObservation()
    {
        var display1 = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var display2 = new DisplayDescriptor(
            @"\\.\DISPLAY2",
            new PixelRect(-1080, 0, 0, 1920),
            new PixelRect(-1080, 0, 0, 1920),
            96,
            96,
            false);
        var icons = new[]
        {
            Icon("p-work", 30, 50, display1.DeviceName),
            Icon("p-game", 110, 130, display1.DeviceName),
            Icon("s-work", -1030, 50, display2.DeviceName),
            Icon("s-game", -950, 130, display2.DeviceName),
        };
        var capabilities = new DesktopIconPositioningCapabilities(
            false,
            new PixelPoint(80, 80),
            ConnectionMethod.FindWindowSW,
            null);

        return new DesktopObservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { display1, display2 },
            capabilities,
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(10));
    }

    private static DesktopIconDescriptor Icon(
        string stableId,
        int x,
        int y,
        string monitor)
        => new(
            stableId,
            stableId,
            null,
            ItemKind.File,
            new PixelPoint(x, y),
            monitor,
            false,
            true);
}
