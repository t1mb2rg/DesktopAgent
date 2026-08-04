using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Poc.LayoutTemplates.Editor;

namespace DesktopAgent.Tests.LayoutTemplates.Editor;

public sealed class LayoutIconRuleEditorDraftTests
{
    [Fact] public void CreateNew_IsExclude_EmptyStableId()
    {
        var d = LayoutIconRuleEditorDraft.CreateNew();
        Assert.Equal("", d.StableId);
        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, d.Kind);
        Assert.Null(d.TargetZoneId);
        Assert.False(d.IsZoneOverride);
    }

    [Fact] public void FromRule_Exclude_RoundTrip()
    {
        var rule = DesktopLayoutIconRule.ForExclude("myapp.exe");
        var d = LayoutIconRuleEditorDraft.FromRule(rule);
        Assert.Equal("myapp.exe", d.StableId);
        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, d.Kind);
        Assert.Null(d.TargetZoneId);
        var built = d.TryBuild(out var err);
        Assert.NotNull(built);
        Assert.Null(err);
        Assert.Equal("myapp.exe", built!.StableId);
    }

    [Fact] public void FromRule_Pin_RoundTrip()
    {
        var rule = DesktopLayoutIconRule.ForPin("app");
        var d = LayoutIconRuleEditorDraft.FromRule(rule);
        Assert.Equal(DesktopLayoutIconRuleKind.Pin, d.Kind);
        Assert.NotNull(d.TryBuild(out _));
    }

    [Fact] public void FromRule_ZoneOverride_RoundTrip()
    {
        var zid = Guid.NewGuid();
        var rule = DesktopLayoutIconRule.ForZoneOverride("app", zid);
        var d = LayoutIconRuleEditorDraft.FromRule(rule);
        Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, d.Kind);
        Assert.Equal(zid, d.TargetZoneId);
        Assert.True(d.IsZoneOverride);
        var built = d.TryBuild(out var err);
        Assert.NotNull(built);
        Assert.Null(err);
        Assert.Equal(zid, built!.TargetZoneId);
    }

    [Fact] public void StableId_NotTrimmed()
    {
        var rule = DesktopLayoutIconRule.ForExclude("  app  ");
        var d = LayoutIconRuleEditorDraft.FromRule(rule);
        Assert.Equal("  app  ", d.StableId);
    }

    [Fact] public void StableId_PureWhitespace_Preserved()
    {
        var rule = DesktopLayoutIconRule.ForExclude("   ");
        var d = LayoutIconRuleEditorDraft.FromRule(rule);
        Assert.Equal("   ", d.StableId);
        Assert.NotNull(d.TryBuild(out _));
    }

    [Fact] public void StableId_Empty_Rejected()
    {
        var d = LayoutIconRuleEditorDraft.CreateNew();
        d.StableId = "";
        Assert.Null(d.TryBuild(out var err));
        Assert.Contains("StableId", err);
    }

    [Fact] public void KindSwitch_ExcludeToPin_ClearsTargetZone()
    {
        var zid = Guid.NewGuid();
        var d = LayoutIconRuleEditorDraft.FromRule(
            DesktopLayoutIconRule.ForZoneOverride("app", zid));
        Assert.Equal(zid, d.TargetZoneId);
        d.Kind = DesktopLayoutIconRuleKind.Pin;
        Assert.Null(d.TargetZoneId);
    }

    [Fact] public void ZoneOverride_NoZone_Rejected()
    {
        var d = LayoutIconRuleEditorDraft.CreateNew();
        d.StableId = "app";
        d.Kind = DesktopLayoutIconRuleKind.ZoneOverride;
        d.TargetZoneId = null;
        Assert.Null(d.TryBuild(out var err));
        Assert.Contains("TargetZoneId", err);
    }

    [Fact] public void ZoneOverride_EmptyGuid_Rejected()
    {
        var d = LayoutIconRuleEditorDraft.CreateNew();
        d.StableId = "app";
        d.Kind = DesktopLayoutIconRuleKind.ZoneOverride;
        d.TargetZoneId = Guid.Empty;
        Assert.Null(d.TryBuild(out var err));
    }

    [Fact] public void UndefinedKind_Rejected()
    {
        var d = LayoutIconRuleEditorDraft.CreateNew();
        d.StableId = "app";
        d.Kind = (DesktopLayoutIconRuleKind)99;
        Assert.Null(d.TryBuild(out var err));
    }

    [Fact] public void ExcludeToOverride_IsZoneOverrideUpdates()
    {
        var zid = Guid.NewGuid();
        var d = LayoutIconRuleEditorDraft.FromRule(DesktopLayoutIconRule.ForExclude("app"));
        Assert.False(d.IsZoneOverride);
        d.Kind = DesktopLayoutIconRuleKind.ZoneOverride;
        d.TargetZoneId = zid;
        Assert.True(d.IsZoneOverride);
    }
}
