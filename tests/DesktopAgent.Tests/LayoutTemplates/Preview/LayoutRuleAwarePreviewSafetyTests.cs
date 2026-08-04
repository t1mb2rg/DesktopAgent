using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Geometry;

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
}
