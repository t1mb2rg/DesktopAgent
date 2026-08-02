using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Rules;

public sealed class DesktopLayoutIconRuleSetTests
{
    private static DesktopLayoutIconRule R(string id)
        => DesktopLayoutIconRule.ForExclude(id);

    // ═══ 合法构造 ═══

    [Fact] public void Empty_Valid()
    {
        var tid = Guid.NewGuid();
        var rs = DesktopLayoutIconRuleSet.Empty(tid);
        Assert.Equal(tid, rs.TemplateId);
        Assert.Empty(rs.Rules);
        Assert.False(rs.HasRules);
        Assert.Equal(0, rs.RuleCount);
    }

    [Fact] public void SingleRule_Valid()
    {
        var tid = Guid.NewGuid();
        var rs = new DesktopLayoutIconRuleSet(tid, new[] { R("a") });
        Assert.Single(rs.Rules);
        Assert.True(rs.HasRules);
        Assert.Equal(1, rs.RuleCount);
    }

    [Fact] public void MultipleRules_StableIdSorting()
    {
        var tid = Guid.NewGuid();
        var rs = new DesktopLayoutIconRuleSet(tid, new[] { R("c"), R("a"), R("b") });
        Assert.Equal("a", rs.Rules[0].StableId);
        Assert.Equal("b", rs.Rules[1].StableId);
        Assert.Equal("c", rs.Rules[2].StableId);
    }

    [Fact] public void InputOrder_DoesNotAffectOutput()
    {
        var tid = Guid.NewGuid();
        var rules = new[] { R("z"), R("a"), R("m") };
        var rs1 = new DesktopLayoutIconRuleSet(tid, rules);
        var rs2 = new DesktopLayoutIconRuleSet(tid, rules.Reverse().ToList());
        for (int i = 0; i < rs1.Rules.Count; i++)
            Assert.Same(rs1.Rules[i], rs2.Rules[i]);
    }

    // ═══ 非法构造 ═══

    [Fact] public void TemplateId_GuidEmpty_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRuleSet(Guid.Empty, Array.Empty<DesktopLayoutIconRule>()));

    [Fact] public void Rules_Null_Fails()
        => Assert.Throws<ArgumentNullException>(() =>
            new DesktopLayoutIconRuleSet(Guid.NewGuid(), null!));

    [Fact] public void NullElement_Fails()
        => Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRuleSet(Guid.NewGuid(), new DesktopLayoutIconRule[] { null! }));

    [Fact] public void DuplicateStableId_Fails()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRuleSet(Guid.NewGuid(), new[] { R("dup"), R("dup") }));
        Assert.Contains("dup", ex.Message);
    }

    [Fact] public void CaseSensitive_DifferentAllowed()
    {
        var tid = Guid.NewGuid();
        var rs = new DesktopLayoutIconRuleSet(tid, new[] { R("Abc"), R("abc") });
        Assert.Equal(2, rs.RuleCount);
        Assert.Equal("Abc", rs.Rules[0].StableId);
        Assert.Equal("abc", rs.Rules[1].StableId);
    }

    [Fact] public void Whitespace_Significant()
    {
        var tid = Guid.NewGuid();
        var rs = new DesktopLayoutIconRuleSet(tid, new[] { R(" a"), R("a") });
        Assert.Equal(2, rs.RuleCount);
        Assert.Equal(" a", rs.Rules[0].StableId);
        Assert.Equal("a", rs.Rules[1].StableId);
    }

    [Fact] public void Duplicate_EquivalentNewInstances_Rejected()
    {
        // 等值新实例（相同 StableId）仍被拒绝
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutIconRuleSet(Guid.NewGuid(),
                new[] { R("x"), DesktopLayoutIconRule.ForPin("x") }));
    }

    // ═══ 防御性 ═══

    [Fact] public void DefensiveCopy_InputListUnchanged()
    {
        var tid = Guid.NewGuid();
        var input = new List<DesktopLayoutIconRule> { R("b"), R("a") };
        var rs = new DesktopLayoutIconRuleSet(tid, input);
        input.Clear();
        Assert.Equal(2, rs.RuleCount);
        Assert.False(rs.Rules.SequenceEqual(input));
    }

    [Fact] public void ReadOnly_ThrowsOnCast()
    {
        var rs = new DesktopLayoutIconRuleSet(Guid.NewGuid(), new[] { R("a") });
        Assert.Throws<NotSupportedException>(() =>
            ((IList<DesktopLayoutIconRule>)rs.Rules).Clear());
    }

    [Fact] public void PreservesRuleReferences()
    {
        var rule = R("ref");
        var rs = new DesktopLayoutIconRuleSet(Guid.NewGuid(), new[] { rule });
        Assert.Same(rule, rs.Rules[0]);
    }

    [Fact] public void Empty_Factory_EqualsConstructor()
    {
        var tid = Guid.NewGuid();
        var e = DesktopLayoutIconRuleSet.Empty(tid);
        var c = new DesktopLayoutIconRuleSet(tid, Array.Empty<DesktopLayoutIconRule>());
        Assert.Equal(e.TemplateId, c.TemplateId);
        Assert.Equal(e.RuleCount, c.RuleCount);
    }
}
