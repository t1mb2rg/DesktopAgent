using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopClassificationRuleDraftTests
{
    [Fact]
    public void Confirm_AlwaysCreatesExplicitUserAiConfirmedRule()
    {
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var draft = new DesktopClassificationRuleDraft(
            Guid.NewGuid(),
            "课程 PDF",
            new DesktopClassificationRuleCondition(fileExtension: ".PDF"),
            "Course",
            templateId,
            zoneId,
            "用户要求把 PDF 归入课程区域。");

        var rule = draft.Confirm(Guid.NewGuid(), priority: 42);

        Assert.Equal(".pdf", rule.Condition.FileExtension);
        Assert.Equal("Course", rule.Category);
        Assert.Equal(templateId, rule.TargetTemplateId);
        Assert.Equal(zoneId, rule.TargetZoneId);
        Assert.Equal(42, rule.Priority);
        Assert.True(rule.IsEnabled);
        Assert.Equal(DesktopClassificationRuleAuthority.ExplicitUser, rule.Authority);
        Assert.Equal(DesktopClassificationRuleProvenance.AiProposedAndConfirmed, rule.Provenance);
    }

    [Fact]
    public void WithEdits_PreservesDraftIdentityAndExplanationButRevalidatesRuleFields()
    {
        var draftId = Guid.NewGuid();
        var draft = new DesktopClassificationRuleDraft(
            draftId,
            "初稿",
            new DesktopClassificationRuleCondition(itemKind: ItemKind.File),
            "Document",
            null,
            null,
            "AI explanation");
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();

        var edited = draft.WithEdits(
            "用户修改后的规则",
            new DesktopClassificationRuleCondition(fileNamePattern: "*实验报告*.pdf"),
            "Course",
            templateId,
            zoneId);

        Assert.Equal(draftId, edited.DraftId);
        Assert.Equal("AI explanation", edited.Reason);
        Assert.Equal("用户修改后的规则", edited.Name);
        Assert.Equal("Course", edited.Category);
        Assert.Equal(zoneId, edited.TargetZoneId);
    }

    [Fact]
    public void Draft_RejectsUnpairedTemplateZoneAndOversizedText()
    {
        var condition = new DesktopClassificationRuleCondition(itemKind: ItemKind.File);

        Assert.Throws<ArgumentException>(() => new DesktopClassificationRuleDraft(
            Guid.NewGuid(),
            "bad",
            condition,
            "Document",
            Guid.NewGuid(),
            null,
            "reason"));
        Assert.Throws<ArgumentException>(() => new DesktopClassificationRuleDraft(
            Guid.NewGuid(),
            new string('n', DesktopClassificationRuleDraft.MaxNameLength + 1),
            condition,
            "Document",
            null,
            null,
            "reason"));
    }
}
