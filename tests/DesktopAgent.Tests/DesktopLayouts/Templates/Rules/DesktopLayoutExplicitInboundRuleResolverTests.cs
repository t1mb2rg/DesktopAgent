using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Rules;

public sealed class DesktopLayoutExplicitInboundRuleResolverTests
{
    [Fact]
    public void Resolve_OutOfScopeZoneOverride_RemainsInertWithoutExplicitInbound()
    {
        var (template, zone, display) = CreateTemplate();
        var icon = Icon("cross", @"\\.\DISPLAY2", -900, 100);
        var rules = new DesktopLayoutIconRuleSet(
            template.TemplateId,
            new[] { DesktopLayoutIconRule.ForZoneOverride(icon.StableId, zone.ZoneId) });

        var plan = DesktopLayoutIconRuleResolver.Resolve(
            template,
            display,
            new[] { icon },
            rules);

        Assert.Equal(0, plan.InboundMigrationIconCount);
        Assert.Equal(0, plan.ZoneOverrideCount);
        Assert.Equal(1, plan.OutOfScopeIconCount);
        Assert.Equal(1, plan.OutOfScopeRuleMatchCount);
    }

    [Fact]
    public void Resolve_ExplicitInboundZoneOverride_BecomesManagedWithoutPretendingPhysicalMembership()
    {
        var (template, zone, display) = CreateTemplate();
        var icon = Icon("cross", @"\\.\DISPLAY2", -900, 100);
        var rules = new DesktopLayoutIconRuleSet(
            template.TemplateId,
            new[] { DesktopLayoutIconRule.ForZoneOverride(icon.StableId, zone.ZoneId) });

        var plan = DesktopLayoutIconRuleResolver.Resolve(
            template,
            display,
            new[] { icon },
            rules,
            new HashSet<string>(StringComparer.Ordinal) { icon.StableId });

        Assert.Equal(0, plan.TargetDisplayIconCount);
        Assert.Equal(1, plan.InboundMigrationIconCount);
        Assert.Equal(1, plan.ManagedTargetIconCount);
        Assert.Equal(0, plan.OutOfScopeIconCount);
        Assert.Equal(1, plan.ZoneOverrideCount);
        Assert.Equal(1, plan.InboundZoneOverrideCount);
        Assert.True(plan.HasInboundMigrations);

        var match = Assert.Single(plan.ZoneOverrideMatches);
        Assert.False(match.IsOnTargetDisplay);
        Assert.Same(icon, match.Icon);
        Assert.Same(zone, match.TargetZone);
    }

    [Fact]
    public void Resolve_ExplicitInboundRequiresZoneOverride()
    {
        var (template, _, display) = CreateTemplate();
        var icon = Icon("cross", @"\\.\DISPLAY2", -900, 100);
        var rules = new DesktopLayoutIconRuleSet(
            template.TemplateId,
            new[] { DesktopLayoutIconRule.ForPin(icon.StableId) });

        var ex = Assert.Throws<ArgumentException>(() =>
            DesktopLayoutIconRuleResolver.Resolve(
                template,
                display,
                new[] { icon },
                rules,
                new HashSet<string>(StringComparer.Ordinal) { icon.StableId }));

        Assert.Contains("ZoneOverride", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ExplicitInboundRequiresKnownSourceMonitor()
    {
        var (template, zone, display) = CreateTemplate();
        var icon = Icon("cross", null, -900, 100);
        var rules = new DesktopLayoutIconRuleSet(
            template.TemplateId,
            new[] { DesktopLayoutIconRule.ForZoneOverride(icon.StableId, zone.ZoneId) });

        var ex = Assert.Throws<ArgumentException>(() =>
            DesktopLayoutIconRuleResolver.Resolve(
                template,
                display,
                new[] { icon },
                rules,
                new HashSet<string>(StringComparer.Ordinal) { icon.StableId }));

        Assert.Contains("MonitorDeviceName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ExplicitInboundUnknownStableId_FailsClosed()
    {
        var (template, zone, display) = CreateTemplate();
        var icon = Icon("present", @"\\.\DISPLAY2", -900, 100);
        var rules = new DesktopLayoutIconRuleSet(
            template.TemplateId,
            new[] { DesktopLayoutIconRule.ForZoneOverride("missing", zone.ZoneId) });

        Assert.Throws<ArgumentException>(() =>
            DesktopLayoutIconRuleResolver.Resolve(
                template,
                display,
                new[] { icon },
                rules,
                new HashSet<string>(StringComparer.Ordinal) { "missing" }));
    }

    private static (DesktopLayoutTemplate Template, DesktopLayoutZone Zone, DisplayDescriptor Display)
        CreateTemplate()
    {
        var templateId = Guid.NewGuid();
        var zone = new DesktopLayoutZone(
            Guid.NewGuid(),
            "目标区",
            new NormalizedRect(0, 0, 10000, 10000),
            0,
            ZonePlacementFlow.ColumnMajor,
            null);
        var template = new DesktopLayoutTemplate(
            templateId,
            "跨屏测试",
            1,
            TargetDisplayBinding.ForExactDeviceName(@"\\.\DISPLAY1"),
            new[] { zone });
        var display = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        return (template, zone, display);
    }

    private static DesktopIconSnapshotItem Icon(
        string stableId,
        string? monitor,
        int x,
        int y)
        => new(
            0,
            stableId,
            stableId,
            null,
            ItemKind.File,
            new PixelPoint(x, y),
            monitor,
            false,
            true);
}
