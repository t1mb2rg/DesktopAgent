using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopClassificationPolicyResolverTests
{
    private static readonly Guid ActiveTemplateId =
        Guid.Parse("00000000-0000-0000-0000-00000000a401");

    [Fact]
    public void Condition_CombinesBoundedFactsAndSafeGlob()
    {
        var condition = new DesktopClassificationRuleCondition(
            fileExtension: ".PDF",
            fileNamePattern: "*实验?告.pdf",
            itemKind: ItemKind.File);
        var matching = CreateFacts("report", "通信原理实验报告.pdf", ItemKind.File, ".pdf");
        var wrongKind = CreateFacts("shortcut", "通信原理实验报告.pdf", ItemKind.Shortcut, ".pdf");

        Assert.True(condition.Matches(matching));
        Assert.False(condition.Matches(wrongKind));
        Assert.Equal(111, condition.SpecificityScore);

        Assert.Throws<ArgumentException>(() => new DesktopClassificationRuleCondition());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopClassificationRuleCondition(fileNamePattern: new string('x', 257)));
    }

    [Fact]
    public void Resolve_UsesAuthorityBeforePriorityAndTreatsConfirmedAiRuleAsUserAuthority()
    {
        var item = CreateFacts("report", "通信原理实验报告.pdf", ItemKind.File, ".pdf");
        var localZone = Guid.NewGuid();
        var userZone = Guid.NewGuid();
        var aiZone = Guid.NewGuid();

        var localRule = CreateRule(
            Guid.Parse("00000000-0000-0000-0000-000000000010"),
            "内置 PDF",
            new DesktopClassificationRuleCondition(fileExtension: ".pdf"),
            "Document",
            localZone,
            DesktopClassificationRuleAuthority.DeterministicLocal,
            DesktopClassificationRuleProvenance.BuiltIn,
            priority: 10000);
        var confirmedAiRule = CreateRule(
            Guid.Parse("00000000-0000-0000-0000-000000000020"),
            "已确认课程报告",
            new DesktopClassificationRuleCondition(fileNamePattern: "*实验报告*"),
            "Course",
            userZone,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.AiProposedAndConfirmed,
            priority: -10000);
        var aiSuggestion = new DesktopItemClassificationSuggestion(
            "Temporary",
            aiZone,
            0.99,
            "模型临时建议",
            DesktopItemClassificationSource.AiProvider);

        var result = DesktopClassificationPolicyResolver.Resolve(
            item,
            ActiveTemplateId,
            new[] { localRule, confirmedAiRule },
            new[] { localZone, userZone, aiZone },
            aiSuggestion: aiSuggestion);

        var decision = Assert.IsType<DesktopItemClassificationDecision>(result.Decision);
        Assert.Equal("Course", decision.Category);
        Assert.Equal(userZone, decision.TargetZoneId);
        Assert.Equal(DesktopItemClassificationSource.UserRule, decision.Source);
        Assert.False(result.RequiresUserConfirmation);
        Assert.Equal(confirmedAiRule.RuleId, result.CategoryRuleId);
        Assert.Equal(confirmedAiRule.RuleId, result.TargetZoneRuleId);
        Assert.False(result.HasConflict);
    }

    [Fact]
    public void Resolve_CategoryAndZoneAreIndependentAndM2OverrideOnlyWinsZone()
    {
        var item = CreateFacts("tool", "Visual Studio Code.lnk", ItemKind.Shortcut, ".lnk");
        var defaultToolZone = Guid.NewGuid();
        var explicitLayoutZone = Guid.NewGuid();

        var explicitCategoryOnly = CreateRule(
            Guid.NewGuid(),
            "这是开发工具",
            new DesktopClassificationRuleCondition(stableId: item.StableId),
            "Development",
            null,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.UserAuthored);
        var localZoneRule = CreateRule(
            Guid.NewGuid(),
            "快捷方式默认区",
            new DesktopClassificationRuleCondition(itemKind: ItemKind.Shortcut),
            "Shortcut",
            defaultToolZone,
            DesktopClassificationRuleAuthority.DeterministicLocal,
            DesktopClassificationRuleProvenance.BuiltIn);

        var withoutLayoutOverride = DesktopClassificationPolicyResolver.Resolve(
            item,
            ActiveTemplateId,
            new[] { localZoneRule, explicitCategoryOnly },
            new[] { defaultToolZone, explicitLayoutZone });
        var withoutOverrideDecision = Assert.IsType<DesktopItemClassificationDecision>(withoutLayoutOverride.Decision);
        Assert.Equal("Development", withoutOverrideDecision.Category);
        Assert.Equal(defaultToolZone, withoutOverrideDecision.TargetZoneId);
        Assert.Equal(localZoneRule.RuleId, withoutLayoutOverride.TargetZoneRuleId);

        var layoutOverride = DesktopLayoutIconRule.ForZoneOverride(item.StableId, explicitLayoutZone);
        var withLayoutOverride = DesktopClassificationPolicyResolver.Resolve(
            item,
            ActiveTemplateId,
            new[] { localZoneRule, explicitCategoryOnly },
            new[] { defaultToolZone, explicitLayoutZone },
            matchingLayoutRule: layoutOverride);

        var decision = Assert.IsType<DesktopItemClassificationDecision>(withLayoutOverride.Decision);
        Assert.Equal("Development", decision.Category);
        Assert.Equal(explicitLayoutZone, decision.TargetZoneId);
        Assert.Equal(DesktopItemClassificationSource.UserRule, decision.Source);
        Assert.True(withLayoutOverride.TargetZoneOverriddenByLayoutRule);
        Assert.Null(withLayoutOverride.TargetZoneRuleId);
        Assert.False(withLayoutOverride.HasConflict);
        Assert.False(withLayoutOverride.RequiresUserConfirmation);
    }

    [Fact]
    public void Resolve_ConfirmedProfileSitsBetweenExplicitAndLocalZoneAuthority()
    {
        var item = CreateFacts("report", "course.pdf", ItemKind.File, ".pdf");
        var localZone = Guid.NewGuid();
        var profileZone = Guid.NewGuid();
        var explicitZone = Guid.NewGuid();

        var explicitCategoryOnly = CreateRule(
            Guid.NewGuid(),
            "用户确认这是课程资料",
            new DesktopClassificationRuleCondition(stableId: item.StableId),
            "Course",
            null,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.UserAuthored);
        var localZoneRule = CreateRule(
            Guid.NewGuid(),
            "PDF 默认区",
            new DesktopClassificationRuleCondition(fileExtension: ".pdf"),
            "Document",
            localZone,
            DesktopClassificationRuleAuthority.DeterministicLocal,
            DesktopClassificationRuleProvenance.BuiltIn);
        var preference = new DesktopCategoryZonePreference(
            Guid.NewGuid(),
            ActiveTemplateId,
            "course",
            profileZone,
            confidence: 0.9,
            evidenceCount: 4,
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));

        var profileWins = DesktopClassificationPolicyResolver.Resolve(
            item,
            ActiveTemplateId,
            new[] { localZoneRule, explicitCategoryOnly },
            new[] { localZone, profileZone, explicitZone },
            userProfilePreference: preference);

        var profileDecision = Assert.IsType<DesktopItemClassificationDecision>(profileWins.Decision);
        Assert.Equal("Course", profileDecision.Category);
        Assert.Equal(profileZone, profileDecision.TargetZoneId);
        Assert.Equal(preference.PreferenceId, profileWins.TargetZonePreferenceId);
        Assert.Null(profileWins.TargetZoneRuleId);
        Assert.False(profileWins.RequiresUserConfirmation);

        var explicitZoneRule = CreateRule(
            Guid.NewGuid(),
            "用户明确指定课程区",
            new DesktopClassificationRuleCondition(stableId: item.StableId),
            "Course",
            explicitZone,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.UserAuthored);

        var explicitWins = DesktopClassificationPolicyResolver.Resolve(
            item,
            ActiveTemplateId,
            new[] { localZoneRule, explicitCategoryOnly, explicitZoneRule },
            new[] { localZone, profileZone, explicitZone },
            userProfilePreference: preference);

        var explicitDecision = Assert.IsType<DesktopItemClassificationDecision>(explicitWins.Decision);
        Assert.Equal(explicitZone, explicitDecision.TargetZoneId);
        Assert.Equal(explicitZoneRule.RuleId, explicitWins.TargetZoneRuleId);
        Assert.Null(explicitWins.TargetZonePreferenceId);
    }

    [Fact]
    public void Resolve_SameRankContradictionsAreAuditedAndDeterministicallyTied()
    {
        var item = CreateFacts("report", "report.pdf", ItemKind.File, ".pdf");
        var zoneA = Guid.NewGuid();
        var zoneB = Guid.NewGuid();
        var ruleA = CreateRule(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "A",
            new DesktopClassificationRuleCondition(fileExtension: ".pdf"),
            "Course",
            zoneA,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.UserAuthored);
        var ruleB = CreateRule(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "B",
            new DesktopClassificationRuleCondition(fileExtension: ".pdf"),
            "Document",
            zoneB,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.UserAuthored);

        var result = DesktopClassificationPolicyResolver.Resolve(
            item,
            ActiveTemplateId,
            new[] { ruleB, ruleA },
            new[] { zoneA, zoneB });

        var decision = Assert.IsType<DesktopItemClassificationDecision>(result.Decision);
        Assert.Equal("Course", decision.Category);
        Assert.Equal(zoneA, decision.TargetZoneId);
        Assert.Equal(ruleA.RuleId, result.CategoryRuleId);
        Assert.Equal(ruleA.RuleId, result.TargetZoneRuleId);
        Assert.True(result.RequiresUserConfirmation);
        Assert.True(result.HasConflict);
        Assert.Equal(2, result.Conflicts.Count);
        Assert.Contains(result.Conflicts, conflict => conflict.Field == DesktopClassificationPolicyField.Category);
        Assert.Contains(result.Conflicts, conflict => conflict.Field == DesktopClassificationPolicyField.TargetZone);
    }

    [Fact]
    public void Resolve_AiOnlyRemainsConfirmableAndOnlyMatchingDanglingZoneFailsClosed()
    {
        var item = CreateFacts("unknown", "notes.xyz", ItemKind.File, ".xyz");
        var validZone = Guid.NewGuid();
        var invalidZone = Guid.NewGuid();
        var aiSuggestion = new DesktopItemClassificationSuggestion(
            "Notes",
            validZone,
            0.72,
            "看起来像笔记",
            DesktopItemClassificationSource.AiProvider);

        var unrelatedDanglingRule = CreateRule(
            Guid.NewGuid(),
            "另一类文件的旧 Zone",
            new DesktopClassificationRuleCondition(fileExtension: ".pdf"),
            "Document",
            invalidZone,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.UserAuthored);
        var aiOnly = DesktopClassificationPolicyResolver.Resolve(
            item,
            ActiveTemplateId,
            new[] { unrelatedDanglingRule },
            new[] { validZone },
            aiSuggestion: aiSuggestion);

        var decision = Assert.IsType<DesktopItemClassificationDecision>(aiOnly.Decision);
        Assert.Equal("Notes", decision.Category);
        Assert.Equal(validZone, decision.TargetZoneId);
        Assert.True(aiOnly.RequiresUserConfirmation);
        Assert.Equal(DesktopItemClassificationSource.AiProvider, decision.Source);
        Assert.Empty(aiOnly.MatchedRuleIds);

        var matchingDanglingRule = CreateRule(
            Guid.NewGuid(),
            "当前项目悬空 Zone",
            new DesktopClassificationRuleCondition(fileExtension: ".xyz"),
            "Notes",
            invalidZone,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.UserAuthored);
        Assert.Throws<ArgumentException>(() => DesktopClassificationPolicyResolver.Resolve(
            item,
            ActiveTemplateId,
            new[] { matchingDanglingRule },
            new[] { validZone }));
    }

    private static DesktopClassificationRule CreateRule(
        Guid ruleId,
        string name,
        DesktopClassificationRuleCondition condition,
        string category,
        Guid? targetZoneId,
        DesktopClassificationRuleAuthority authority,
        DesktopClassificationRuleProvenance provenance,
        int priority = 0)
        => new(
            ruleId,
            name,
            condition,
            category,
            targetZoneId.HasValue ? ActiveTemplateId : null,
            targetZoneId,
            authority,
            provenance,
            priority);

    private static DesktopItemFacts CreateFacts(
        string stableId,
        string displayName,
        ItemKind itemKind,
        string? extension)
        => new(
            stableId,
            displayName,
            itemKind,
            extension,
            isSystemItem: false,
            isFileSystemItem: true);
}
