using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Preview;

public sealed class DesktopLayoutRuleAwarePreviewPlannerTests
{
    private static DesktopIconSnapshotItem I(int ord, string sid, PixelPoint? pos = null,
        string? mon = "DISPLAY1")
        => new(ord, sid, $"N_{sid}", null, ItemKind.File,
            pos ?? new PixelPoint(ord * 80, 0), mon, false, true);

    private static readonly DisplayDescriptor _dd = new("DISPLAY1",
        new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 96, 96, true);

    private static readonly Guid _tid = Guid.NewGuid();
    private static readonly Guid _zid0 = Guid.NewGuid();
    private static readonly PixelPoint _spacing = new(80, 80);

    private static DesktopLayoutTemplate MakeTemplate()
        => new(_tid, "T", 1, TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(_zid0, "Z0",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });

    [Fact] public void EmptyRuleSet_CompatibleWithOldPlanner()
    {
        var template = MakeTemplate();
        var ruleSet = DesktopLayoutIconRuleSet.Empty(_tid);
        var icons = new[] { I(0, "a"), I(1, "b") };

        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);

        Assert.Equal(2, plan.InputIconCount);
        Assert.Equal(2, plan.TargetDisplayIconCount);
        Assert.Equal(0, plan.OutOfScopeIconCount);
        Assert.Equal(0, plan.ExcludedCount);
        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(0, plan.OverrideAssignedCount);
        Assert.Equal(2, plan.DefaultAssignedCount);
        Assert.Equal(0, plan.DefaultUnassignedCount);

        // Compare with old planner — per-item
        var factory = new DesktopLayoutSnapshotFactory();
        var descriptors = icons.Select(icon => new DesktopIconDescriptor(
            icon.StableId, icon.DisplayName, null, ItemKind.File,
            icon.PixelPosition, icon.MonitorDeviceName, false, true)).ToList();
        var snapshot = factory.Create(descriptors, "DISPLAY1",
            ConnectionMethod.FindWindowSW);
        var oldPlan = DesktopLayoutPreviewPlanner.Plan(
            template, new[] { _dd }, null, _spacing, snapshot.Icons);
        Assert.Equal(oldPlan.AssignedCount, plan.DefaultAssignedCount);
        Assert.Equal(oldPlan.UnassignedCount, plan.DefaultUnassignedCount);

        // Per-item comparison: StableId, SlotIndex, TargetPosition, TargetCellBounds
        for (int i = 0; i < plan.Items.Count; i++)
        {
            var newItem = plan.Items[i];
            var oldItem = oldPlan.Items[i];
            Assert.Equal(oldItem.Icon.StableId, newItem.Icon.StableId);
            Assert.Equal(oldItem.Icon.Ordinal, newItem.Icon.Ordinal);
            if (oldItem.IsAssigned && newItem.IsAssigned)
            {
                Assert.Equal(oldItem.Placement!.SlotIndex, newItem.SlotIndex!.Value);
                Assert.Equal(oldItem.TargetPosition!.Value, newItem.TargetPosition!.Value);
                Assert.Equal(oldItem.Placement.TargetCellBounds, newItem.TargetCellBounds!);
                Assert.Equal(oldItem.Placement.Zone.Zone.ZoneId, newItem.ZoneId!.Value);
            }
        }
    }

    [Fact] public void Exclude_GeneratesFixedItem()
    {
        var template = MakeTemplate();
        var ruleSet = new DesktopLayoutIconRuleSet(_tid,
            new[] { DesktopLayoutIconRule.ForExclude("a") });
        var icons = new[] { I(0, "a"), I(1, "b") };
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);

        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(0, plan.PinnedCount);
        var excluded = plan.Items.First(i => i.Disposition == DesktopLayoutRuleAwarePreviewItemDisposition.Excluded);
        Assert.Equal(DesktopLayoutRuleAwarePreviewPlacementState.Fixed, excluded.PlacementState);
        Assert.Null(excluded.TargetPosition);
    }

    [Fact] public void Pin_GeneratesFixedItem()
    {
        var template = MakeTemplate();
        var ruleSet = new DesktopLayoutIconRuleSet(_tid,
            new[] { DesktopLayoutIconRule.ForPin("a") });
        var icons = new[] { I(0, "a"), I(1, "b") };
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);

        Assert.Equal(1, plan.PinnedCount);
        var pinned = plan.Items.First(i => i.Disposition == DesktopLayoutRuleAwarePreviewItemDisposition.Pinned);
        Assert.Equal(DesktopLayoutRuleAwarePreviewPlacementState.Fixed, pinned.PlacementState);
    }

    [Fact] public void Override_Assigned()
    {
        var template = MakeTemplate();
        var ruleSet = new DesktopLayoutIconRuleSet(_tid,
            new[] { DesktopLayoutIconRule.ForZoneOverride("a", _zid0) });
        var icons = new[] { I(0, "a"), I(1, "b") };
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);

        Assert.Equal(1, plan.OverrideAssignedCount);
        var ovr = plan.Items.First(i => i.Disposition == DesktopLayoutRuleAwarePreviewItemDisposition.ZoneOverride);
        Assert.Equal(_zid0, ovr.ZoneId);
    }

    [Fact] public void ExcludeAndPin_DoNotGetTargetPositions()
    {
        var template = MakeTemplate();
        var ruleSet = new DesktopLayoutIconRuleSet(_tid,
            new[] { DesktopLayoutIconRule.ForExclude("a"), DesktopLayoutIconRule.ForPin("b") });
        var icons = new[] { I(0, "a"), I(1, "b"), I(2, "c") };
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);

        foreach (var item in plan.Items)
        {
            if (item.Disposition == DesktopLayoutRuleAwarePreviewItemDisposition.Excluded
                || item.Disposition == DesktopLayoutRuleAwarePreviewItemDisposition.Pinned)
            {
                Assert.Equal(DesktopLayoutRuleAwarePreviewPlacementState.Fixed, item.PlacementState);
                Assert.Null(item.TargetPosition);
                Assert.Null(item.ZoneId);
            }
        }
    }

    [Fact] public void Default_Unassigned_NoTarget()
    {
        var zid = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(zid, "Z",
                new NormalizedRect(0, 0, 5000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, 1) });
        var ruleSet = DesktopLayoutIconRuleSet.Empty(template.TemplateId);
        var icons = new[] { I(0, "a"), I(1, "b"), I(2, "c") };
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);

        var unassigned = plan.Items.Where(
            i => i.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.Unassigned);
        foreach (var u in unassigned)
        {
            Assert.Null(u.TargetPosition);
            Assert.Null(u.ZoneId);
        }
    }
}
