using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Poc.LayoutTemplates.Editor;

namespace DesktopAgent.Tests.LayoutTemplates.Editor;

public sealed class LayoutTemplateEditorRuleDraftTests
{
    private static DesktopLayoutTemplate MakeTemplate(Guid? tid = null, Guid? zid = null)
    {
        zid ??= Guid.NewGuid();
        return new DesktopLayoutTemplate(tid ?? Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(zid.Value, "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
    }

    [Fact] public void FromTemplateAndRuleSet_LoadsBoth()
    {
        var tid = Guid.NewGuid(); var zid = Guid.NewGuid();
        var template = MakeTemplate(tid, zid);
        var ruleSet = new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("a") });
        var draft = LayoutTemplateEditorDraft.FromTemplateAndRuleSet(template, ruleSet);
        Assert.False(draft.IsDirty);
        Assert.True(draft.IsPersisted);
        Assert.Single(draft.Rules);
        Assert.Equal("a", draft.Rules[0].StableId);
    }

    [Fact] public void FromTemplateAndRuleSet_TemplateIdMismatch_Throws()
    {
        var t = MakeTemplate(Guid.NewGuid(), Guid.NewGuid());
        var rs = new DesktopLayoutIconRuleSet(Guid.NewGuid(),
            new[] { DesktopLayoutIconRule.ForExclude("a") });
        Assert.Throws<ArgumentException>(() =>
            LayoutTemplateEditorDraft.FromTemplateAndRuleSet(t, rs));
    }

    [Fact] public void AddRule_SetsDirty()
    {
        var tid = Guid.NewGuid(); var zid = Guid.NewGuid();
        var draft = LayoutTemplateEditorDraft.FromTemplateAndRuleSet(
            MakeTemplate(tid, zid),
            DesktopLayoutIconRuleSet.Empty(tid));
        Assert.False(draft.IsDirty);
        draft.Rules.Add(LayoutIconRuleEditorDraft.CreateNew());
        Assert.True(draft.IsDirty);
    }

    [Fact] public void RemoveRule_SetsDirty()
    {
        var tid = Guid.NewGuid();
        var draft = LayoutTemplateEditorDraft.FromTemplateAndRuleSet(
            MakeTemplate(tid, Guid.NewGuid()),
            new DesktopLayoutIconRuleSet(tid,
                new[] { DesktopLayoutIconRule.ForExclude("a") }));
        draft.MarkSaved();
        Assert.False(draft.IsDirty);
        draft.Rules.RemoveAt(0);
        Assert.True(draft.IsDirty);
    }

    [Fact] public void ModifyRule_SetsDirty()
    {
        var tid = Guid.NewGuid();
        var draft = LayoutTemplateEditorDraft.FromTemplateAndRuleSet(
            MakeTemplate(tid, Guid.NewGuid()),
            new DesktopLayoutIconRuleSet(tid,
                new[] { DesktopLayoutIconRule.ForExclude("a") }));
        draft.MarkSaved();
        Assert.False(draft.IsDirty);
        draft.Rules[0].StableId = "changed";
        Assert.True(draft.IsDirty);
    }

    [Fact] public void MarkSaved_ThenModify_Redirty()
    {
        var tid = Guid.NewGuid();
        var draft = LayoutTemplateEditorDraft.FromTemplateAndRuleSet(
            MakeTemplate(tid, Guid.NewGuid()),
            DesktopLayoutIconRuleSet.Empty(tid));
        Assert.False(draft.IsDirty);
        draft.Rules.Add(LayoutIconRuleEditorDraft.CreateNew());
        Assert.True(draft.IsDirty);
        draft.MarkSaved();
        Assert.False(draft.IsDirty);
        draft.Rules[0].Kind = DesktopLayoutIconRuleKind.Pin;
        Assert.True(draft.IsDirty);
    }

    [Fact] public void TryBuildRuleSet_DuplicateStableId_Fails()
    {
        var tid = Guid.NewGuid();
        var draft = LayoutTemplateEditorDraft.FromTemplateAndRuleSet(
            MakeTemplate(tid, Guid.NewGuid()),
            DesktopLayoutIconRuleSet.Empty(tid));
        draft.Rules.Add(new LayoutIconRuleEditorDraft("a",
            DesktopLayoutIconRuleKind.Exclude, null));
        draft.Rules.Add(new LayoutIconRuleEditorDraft("a",
            DesktopLayoutIconRuleKind.Pin, null));
        var result = draft.TryBuildRuleSet();
        Assert.False(result.IsValid);
        Assert.Null(result.RuleSet);
    }

    [Fact] public void TryBuildRuleSet_ZoneOverride_MissingZone_Fails()
    {
        var tid = Guid.NewGuid(); var zid = Guid.NewGuid();
        var draft = LayoutTemplateEditorDraft.FromTemplateAndRuleSet(
            MakeTemplate(tid, zid),
            DesktopLayoutIconRuleSet.Empty(tid));
        draft.Rules.Add(new LayoutIconRuleEditorDraft("a",
            DesktopLayoutIconRuleKind.ZoneOverride, Guid.NewGuid()));
        var result = draft.TryBuildRuleSet();
        Assert.False(result.IsValid);
    }

    [Fact] public void UnsubscribedRule_DoesNotAffectDraft()
    {
        var tid = Guid.NewGuid();
        var draft = LayoutTemplateEditorDraft.FromTemplateAndRuleSet(
            MakeTemplate(tid, Guid.NewGuid()),
            new DesktopLayoutIconRuleSet(tid,
                new[] { DesktopLayoutIconRule.ForExclude("a") }));
        var removed = draft.Rules[0];
        draft.Rules.RemoveAt(0);
        draft.MarkSaved();
        Assert.False(draft.IsDirty);
        removed.StableId = "polluted";
        Assert.False(draft.IsDirty); // 解除订阅后不污染
    }
}
