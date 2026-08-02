using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Rules;

public sealed class DesktopLayoutResolvedIconRuleTests
{
    private static DesktopIconSnapshotItem Icon(string sid, int ord = 0)
        => new(ord, sid, $"N_{sid}", null, ItemKind.File, new PixelPoint(0, 0), "DISPLAY1", false, true);

    private static DesktopLayoutZone Zone(Guid zid) =>
        new(zid, "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);

    // ═══ 合法匹配 ═══

    [Fact] public void Exclude_Valid()
    {
        var icon = Icon("sid-1");
        var rule = DesktopLayoutIconRule.ForExclude("sid-1");
        var resolved = new DesktopLayoutResolvedIconRule(rule, icon, null, isOnTargetDisplay: true);
        Assert.Same(rule, resolved.Rule);
        Assert.Same(icon, resolved.Icon);
        Assert.Null(resolved.TargetZone);
        Assert.True(resolved.IsOnTargetDisplay);
        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, resolved.Kind);
    }

    [Fact] public void Pin_Valid()
    {
        var icon = Icon("sid-2");
        var rule = DesktopLayoutIconRule.ForPin("sid-2");
        var resolved = new DesktopLayoutResolvedIconRule(rule, icon, null, isOnTargetDisplay: true);
        Assert.Same(rule, resolved.Rule);
        Assert.Same(icon, resolved.Icon);
        Assert.Null(resolved.TargetZone);
        Assert.True(resolved.IsOnTargetDisplay);
    }

    [Fact] public void ZoneOverride_Valid()
    {
        var zid = Guid.NewGuid();
        var zone = Zone(zid);
        var icon = Icon("sid-3");
        var rule = DesktopLayoutIconRule.ForZoneOverride("sid-3", zid);
        var resolved = new DesktopLayoutResolvedIconRule(rule, icon, zone, isOnTargetDisplay: true);
        Assert.Same(rule, resolved.Rule);
        Assert.Same(icon, resolved.Icon);
        Assert.Same(zone, resolved.TargetZone);
        Assert.True(resolved.IsOnTargetDisplay);
    }

    [Fact] public void OutOfScope_Valid()
    {
        var icon = Icon("sid-oos");
        var rule = DesktopLayoutIconRule.ForExclude("sid-oos");
        var resolved = new DesktopLayoutResolvedIconRule(rule, icon, null, isOnTargetDisplay: false);
        Assert.False(resolved.IsOnTargetDisplay);
    }

    // ═══ 非法构造 ═══

    [Fact] public void Rule_Null_Fails()
        => Assert.Throws<ArgumentNullException>(() =>
            new DesktopLayoutResolvedIconRule(null!, Icon("x"), null, true));

    [Fact] public void Icon_Null_Fails()
        => Assert.Throws<ArgumentNullException>(() =>
            new DesktopLayoutResolvedIconRule(DesktopLayoutIconRule.ForExclude("x"), null!, null, true));

    [Fact] public void StableId_Mismatch_Fails()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutResolvedIconRule(
                DesktopLayoutIconRule.ForExclude("rule-id"), Icon("icon-id"), null, true));
        Assert.Contains("rule-id", ex.Message);
        Assert.Contains("icon-id", ex.Message);
    }

    [Fact] public void StableId_CaseSensitiveMismatch_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutResolvedIconRule(
                DesktopLayoutIconRule.ForExclude("Abc"), Icon("abc"), null, true));

    [Fact] public void Exclude_WithZone_Fails()
    {
        var zone = Zone(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutResolvedIconRule(
                DesktopLayoutIconRule.ForExclude("x"), Icon("x"), zone, true));
    }

    [Fact] public void Pin_WithZone_Fails()
    {
        var zone = Zone(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutResolvedIconRule(
                DesktopLayoutIconRule.ForPin("x"), Icon("x"), zone, true));
    }

    [Fact] public void ZoneOverride_NullZone_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutResolvedIconRule(
                DesktopLayoutIconRule.ForZoneOverride("x", Guid.NewGuid()), Icon("x"), null, true));

    [Fact] public void ZoneOverride_ZoneIdMismatch_Fails()
    {
        var ruleZoneId = Guid.NewGuid();
        var otherZoneId = Guid.NewGuid();
        var zone = Zone(otherZoneId);
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutResolvedIconRule(
                DesktopLayoutIconRule.ForZoneOverride("x", ruleZoneId), Icon("x"), zone, true));
    }

    // ═══ 引用保留 ═══

    [Fact] public void PreservesRuleReference()
    {
        var rule = DesktopLayoutIconRule.ForPin("x");
        var resolved = new DesktopLayoutResolvedIconRule(rule, Icon("x"), null, true);
        Assert.Same(rule, resolved.Rule);
    }

    [Fact] public void PreservesIconReference()
    {
        var icon = Icon("x");
        var resolved = new DesktopLayoutResolvedIconRule(
            DesktopLayoutIconRule.ForPin("x"), icon, null, true);
        Assert.Same(icon, resolved.Icon);
    }

    [Fact] public void PreservesZoneReference()
    {
        var zone = Zone(Guid.NewGuid());
        var rule = DesktopLayoutIconRule.ForZoneOverride("x", zone.ZoneId);
        var resolved = new DesktopLayoutResolvedIconRule(rule, Icon("x"), zone, true);
        Assert.Same(zone, resolved.TargetZone);
    }
}
