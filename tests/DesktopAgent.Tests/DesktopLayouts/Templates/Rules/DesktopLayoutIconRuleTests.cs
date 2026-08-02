using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Rules;

public sealed class DesktopLayoutIconRuleTests
{
    // ═══ 合法构造 ═══

    [Fact] public void Exclude_Valid()
    {
        var r = new DesktopLayoutIconRule("guid-123", DesktopLayoutIconRuleKind.Exclude, null);
        Assert.Equal("guid-123", r.StableId);
        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, r.Kind);
        Assert.Null(r.TargetZoneId);
    }

    [Fact] public void Pin_Valid()
    {
        var r = new DesktopLayoutIconRule("guid-456", DesktopLayoutIconRuleKind.Pin, null);
        Assert.Equal("guid-456", r.StableId);
        Assert.Equal(DesktopLayoutIconRuleKind.Pin, r.Kind);
        Assert.Null(r.TargetZoneId);
    }

    [Fact] public void ZoneOverride_Valid()
    {
        var zid = Guid.NewGuid();
        var r = new DesktopLayoutIconRule("guid-789", DesktopLayoutIconRuleKind.ZoneOverride, zid);
        Assert.Equal("guid-789", r.StableId);
        Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, r.Kind);
        Assert.Equal(zid, r.TargetZoneId);
    }

    // ═══ 非法构造 ═══

    [Fact] public void StableId_Null_Fails()
        => Assert.Throws<ArgumentNullException>(() =>
            new DesktopLayoutIconRule(null!, DesktopLayoutIconRuleKind.Exclude, null));

    [Fact] public void StableId_Empty_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRule("", DesktopLayoutIconRuleKind.Exclude, null));

    [Fact] public void StableId_NotTrimmed()
    {
        // 前导/尾随空格不被 Trim，应保留原字符串
        var r = new DesktopLayoutIconRule("  guid  ", DesktopLayoutIconRuleKind.Exclude, null);
        Assert.Equal("  guid  ", r.StableId);
    }

    [Fact] public void Kind_Undefined_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRule("id", (DesktopLayoutIconRuleKind)99, null));

    [Fact] public void Exclude_WithTargetZoneId_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRule("id", DesktopLayoutIconRuleKind.Exclude, Guid.NewGuid()));

    [Fact] public void Pin_WithTargetZoneId_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRule("id", DesktopLayoutIconRuleKind.Pin, Guid.NewGuid()));

    [Fact] public void ZoneOverride_NullTargetZoneId_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRule("id", DesktopLayoutIconRuleKind.ZoneOverride, null));

    [Fact] public void ZoneOverride_GuidEmpty_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRule("id", DesktopLayoutIconRuleKind.ZoneOverride, Guid.Empty));

    // ═══ 静态工厂 ═══

    [Fact] public void ForExclude_Semantics()
    {
        var r = DesktopLayoutIconRule.ForExclude("abc");
        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, r.Kind);
        Assert.Null(r.TargetZoneId);
    }

    [Fact] public void ForPin_Semantics()
    {
        var r = DesktopLayoutIconRule.ForPin("abc");
        Assert.Equal(DesktopLayoutIconRuleKind.Pin, r.Kind);
        Assert.Null(r.TargetZoneId);
    }

    [Fact] public void ForZoneOverride_Semantics()
    {
        var zid = Guid.NewGuid();
        var r = DesktopLayoutIconRule.ForZoneOverride("abc", zid);
        Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, r.Kind);
        Assert.Equal(zid, r.TargetZoneId);
    }

    // ═══ 不可变性 / get-only ═══

    [Fact] public void Properties_GetOnly()
    {
        var r = DesktopLayoutIconRule.ForExclude("id");
        // 类型系统强制 get-only（sealed class 无 set/init）
        Assert.Equal("id", r.StableId);
        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, r.Kind);
        Assert.Null(r.TargetZoneId);
    }

    [Fact] public void Equality_ByReference()
    {
        var r1 = new DesktopLayoutIconRule("a", DesktopLayoutIconRuleKind.Exclude, null);
        var r2 = new DesktopLayoutIconRule("a", DesktopLayoutIconRuleKind.Exclude, null);
        // sealed class 无 value equality — 引用相等
        Assert.NotSame(r1, r2);
        Assert.False(ReferenceEquals(r1, r2));
    }
}
