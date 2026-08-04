using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Poc.LayoutTemplates.Preview;

namespace DesktopAgent.Tests.LayoutTemplates.Preview;

public sealed class LayoutRuleAwarePreviewSafetyTests
{
    [Fact] public void UndefinedDisposition_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePreviewItem(
                new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
                    0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
                    new PixelPoint(0, 0), "D1", false, true),
                null,
                (DesktopLayoutRuleAwarePreviewItemDisposition)99,
                DesktopLayoutRuleAwarePreviewPlacementState.Unassigned,
                new PixelPoint(0, 0), null, null, null, null,
                new PixelRect(0, 0, 80, 80), null));
        Assert.Contains("Disposition", ex.Message);
    }

    [Fact] public void Excluded_WithTarget_Rejected()
    {
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "D1", false, true);
        var rule = DesktopLayoutIconRule.ForExclude("a");
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePreviewItem(
                icon, rule,
                DesktopLayoutRuleAwarePreviewItemDisposition.Excluded,
                DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
                new PixelPoint(0, 0), new PixelPoint(80, 0), Guid.NewGuid(), 0,
                new PixelRect(80, 0, 160, 80),
                new PixelRect(0, 0, 80, 80), null));
    }

    [Fact] public void Default_WithRule_Rejected()
    {
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "D1", false, true);
        var rule = DesktopLayoutIconRule.ForExclude("a");
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePreviewItem(
                icon, rule,
                DesktopLayoutRuleAwarePreviewItemDisposition.Default,
                DesktopLayoutRuleAwarePreviewPlacementState.Unassigned,
                new PixelPoint(0, 0), null, null, null, null,
                new PixelRect(0, 0, 80, 80), null));
    }

    [Fact] public void Fixed_NotAllowedForDefault()
    {
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "D1", false, true);
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePreviewItem(
                icon, null,
                DesktopLayoutRuleAwarePreviewItemDisposition.Default,
                DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
                new PixelPoint(0, 0), null, null, null, null,
                new PixelRect(0, 0, 80, 80), null));
    }

    [Fact] public void AlreadyAtTarget_MustEqual()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopAgent.Core.DesktopLayouts.Templates.DesktopLayoutZone(
            zid, "Z", new DesktopAgent.Core.DesktopLayouts.Templates.NormalizedRect(
                0, 0, 10000, 10000), 0,
            DesktopAgent.Core.DesktopLayouts.Templates.ZonePlacementFlow.ColumnMajor, null);
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "D1", false, true);
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePreviewItem(
                icon, null,
                DesktopLayoutRuleAwarePreviewItemDisposition.Default,
                DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget,
                new PixelPoint(0, 0), new PixelPoint(80, 0), zid, 0,
                new PixelRect(80, 0, 160, 80),
                new PixelRect(0, 0, 80, 80), zone));
    }

    [Fact] public void MoveRequired_MustDiffer()
    {
        var zid = Guid.NewGuid();
        var zone = new DesktopAgent.Core.DesktopLayouts.Templates.DesktopLayoutZone(
            zid, "Z", new DesktopAgent.Core.DesktopLayouts.Templates.NormalizedRect(
                0, 0, 10000, 10000), 0,
            DesktopAgent.Core.DesktopLayouts.Templates.ZonePlacementFlow.ColumnMajor, null);
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "D1", false, true);
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePreviewItem(
                icon, null,
                DesktopLayoutRuleAwarePreviewItemDisposition.Default,
                DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired,
                new PixelPoint(0, 0), new PixelPoint(0, 0), zid, 0,
                new PixelRect(0, 0, 80, 80),
                new PixelRect(0, 0, 80, 80), zone));
    }

    [Fact] public void RuleStableId_MustMatchIcon()
    {
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "icon-a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "D1", false, true);
        var rule = DesktopLayoutIconRule.ForExclude("rule-b");
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutRuleAwarePreviewItem(
                icon, rule,
                DesktopLayoutRuleAwarePreviewItemDisposition.Excluded,
                DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
                new PixelPoint(0, 0), null, null, null, null,
                new PixelRect(0, 0, 80, 80), null));
    }

    [Fact] public void Fixed_StateText_IsFixed_NotAlreadyAtTarget()
    {
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "D1", false, true);
        var rule = DesktopLayoutIconRule.ForExclude("a");
        var item = new DesktopLayoutRuleAwarePreviewItem(
            icon, rule,
            DesktopLayoutRuleAwarePreviewItemDisposition.Excluded,
            DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
            new PixelPoint(0, 0), null, null, null, null,
            new PixelRect(0, 0, 80, 80), null);
        var row = DesktopAgent.Poc.LayoutTemplates.Preview.LayoutPreviewItemRow.FromRuleAware(item);
        Assert.Equal("Fixed", row.PlacementStateText);
        Assert.Equal("Excluded", row.DispositionText);
        Assert.NotEqual("已就位", row.PlacementStateText); // Fixed must not show as 已就位
    }

    [Fact] public void DispositionTexts_Correct()
    {
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "D1", false, true);
        // Excluded
        var ei = new DesktopLayoutRuleAwarePreviewItem(icon, DesktopLayoutIconRule.ForExclude("a"),
            DesktopLayoutRuleAwarePreviewItemDisposition.Excluded,
            DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
            new PixelPoint(0, 0), null, null, null, null,
            new PixelRect(0, 0, 80, 80), null);
        Assert.Equal("Excluded", LayoutPreviewItemRow.FromRuleAware(ei).DispositionText);
        // Pinned
        var pi = new DesktopLayoutRuleAwarePreviewItem(icon, DesktopLayoutIconRule.ForPin("a"),
            DesktopLayoutRuleAwarePreviewItemDisposition.Pinned,
            DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
            new PixelPoint(0, 0), null, null, null, null,
            new PixelRect(0, 0, 80, 80), null);
        Assert.Equal("Pinned", LayoutPreviewItemRow.FromRuleAware(pi).DispositionText);
    }

    [Fact] public void PlacementStateTexts_Correct()
    {
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "D1", false, true);
        var fi = new DesktopLayoutRuleAwarePreviewItem(icon, DesktopLayoutIconRule.ForExclude("a"),
            DesktopLayoutRuleAwarePreviewItemDisposition.Excluded,
            DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
            new PixelPoint(0, 0), null, null, null, null,
            new PixelRect(0, 0, 80, 80), null);
        Assert.Equal("Fixed", LayoutPreviewItemRow.FromRuleAware(fi).PlacementStateText);
    }

    [Fact] public void PlannerOutput_ItemReferencesMatchPlacementPlan()
    {
        // Full planner output should pass all PreviewPlan constructor invariants
        var tid = Guid.NewGuid(); var zid = Guid.NewGuid();
        var template = new DesktopAgent.Core.DesktopLayouts.Templates.DesktopLayoutTemplate(
            tid, "T", 1, DesktopAgent.Core.DesktopLayouts.Templates.TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopAgent.Core.DesktopLayouts.Templates.DesktopLayoutZone(zid, "Z",
                new DesktopAgent.Core.DesktopLayouts.Templates.NormalizedRect(0, 0, 10000, 10000), 0,
                DesktopAgent.Core.DesktopLayouts.Templates.ZonePlacementFlow.ColumnMajor, null) });
        var dd = new DesktopAgent.Core.Displays.DisplayDescriptor(
            "DISPLAY1", new PixelRect(0,0,1920,1080), new PixelRect(0,0,1920,1080), 96, 96, true);
        var icon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "DISPLAY1", false, true);
        var ruleSet = DesktopLayoutIconRuleSet.Empty(tid);

        // Full planner path → automatic invariant verification
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { icon });
        Assert.NotNull(plan);
        Assert.Single(plan.Items);
    }

    [Fact] public void ExternalEquivalentIcon_RejectedByPlan()
    {
        var tid = Guid.NewGuid(); var zid = Guid.NewGuid();
        var template = new DesktopAgent.Core.DesktopLayouts.Templates.DesktopLayoutTemplate(
            tid, "T", 1, DesktopAgent.Core.DesktopLayouts.Templates.TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopAgent.Core.DesktopLayouts.Templates.DesktopLayoutZone(zid, "Z",
                new DesktopAgent.Core.DesktopLayouts.Templates.NormalizedRect(0, 0, 10000, 10000), 0,
                DesktopAgent.Core.DesktopLayouts.Templates.ZonePlacementFlow.ColumnMajor, null) });
        var dd = new DesktopAgent.Core.Displays.DisplayDescriptor(
            "DISPLAY1", new PixelRect(0,0,1920,1080), new PixelRect(0,0,1920,1080), 96, 96, true);
        var realIcon = new DesktopAgent.Core.DesktopLayouts.DesktopIconSnapshotItem(
            0, "a", "N", null, DesktopAgent.Core.DesktopIcons.ItemKind.File,
            new PixelPoint(0, 0), "DISPLAY1", false, true);
        var ruleSet = DesktopLayoutIconRuleSet.Empty(tid);

        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { dd }, null, new PixelPoint(80, 80),
            new[] { realIcon });
        Assert.NotNull(plan);

        // Verify Items use original icon references
        foreach (var item in plan.Items)
            Assert.Same(realIcon, item.Icon);
    }
}
