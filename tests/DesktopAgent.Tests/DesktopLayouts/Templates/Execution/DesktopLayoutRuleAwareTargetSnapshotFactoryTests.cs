using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Execution;

public sealed class DesktopLayoutRuleAwareTargetSnapshotFactoryTests
{
    private static DesktopIconSnapshotItem I(int ord, string sid, PixelPoint? pos = null,
        string? mon = "DISPLAY1")
        => new(ord, sid, $"N_{sid}", null, ItemKind.File,
            pos ?? new PixelPoint(ord * 80, 0), mon, false, true);

    private static readonly DisplayDescriptor _dd = new("DISPLAY1",
        new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 96, 96, true);
    private static readonly Guid _tid = Guid.NewGuid();
    private static readonly Guid _zid = Guid.NewGuid();
    private static readonly PixelPoint _spacing = new(80, 80);

    private DesktopLayoutRuleAwarePreviewPlan MakePlan(params DesktopIconSnapshotItem[] icons)
    {
        var template = new DesktopLayoutTemplate(_tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(_zid, "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        return DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, DesktopLayoutIconRuleSet.Empty(_tid),
            new[] { _dd }, null, _spacing, icons);
    }

    private DesktopIconReadResult Read(params DesktopIconSnapshotItem[] items)
    {
        var descs = items.Select(it => new DesktopIconDescriptor(
            it.StableId, it.DisplayName, it.ParsingName, it.ItemKind,
            it.PixelPosition, it.MonitorDeviceName, it.IsSystemItem,
            it.IsFileSystemItem)).ToList();
        return new DesktopIconReadResult(descs, 0,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
    }

    [Fact] public void AllAlreadyAtTarget_CreatesValidSnapshot()
    {
        var icons = new[] { I(0, "a", pos: new PixelPoint(0, 0)) };
        var plan = MakePlan(icons);
        var snap = DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
            plan, Read(icons), new DesktopLayoutSnapshotFactory());
        Assert.Equal(1, snap.Icons.Count);
        Assert.Equal("DISPLAY1", snap.TargetMonitorDeviceName);
    }

    [Fact] public void MoveRequired_UsesTargetPosition()
    {
        var icons = new[] { I(0, "a", pos: new PixelPoint(80, 0)) };
        var plan = MakePlan(icons);
        var snap = DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
            plan, Read(icons), new DesktopLayoutSnapshotFactory());
        Assert.Equal(new PixelPoint(0, 0), snap.Icons[0].PixelPosition);
    }

    [Fact] public void Exclude_KeepsCurrentPosition()
    {
        var zid2 = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(zid2, "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var ruleSet = new DesktopLayoutIconRuleSet(template.TemplateId,
            new[] { DesktopLayoutIconRule.ForExclude("a") });
        var icons = new[] { I(0, "a", pos: new PixelPoint(42, 42)) };
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);
        Assert.True(plan.IsComplete);
        var snap = DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
            plan, Read(icons), new DesktopLayoutSnapshotFactory());
        var excludedIcon = snap.Icons.First(i => i.StableId == "a");
        Assert.Equal(new PixelPoint(42, 42), excludedIcon.PixelPosition);
    }

    [Fact] public void Pin_KeepsCurrentPosition()
    {
        var zid2 = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(zid2, "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var ruleSet = new DesktopLayoutIconRuleSet(template.TemplateId,
            new[] { DesktopLayoutIconRule.ForPin("a") });
        var icons = new[] { I(0, "a", pos: new PixelPoint(100, 100)) };
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);
        var snap = DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
            plan, Read(icons), new DesktopLayoutSnapshotFactory());
        Assert.Equal(new PixelPoint(100, 100), snap.Icons[0].PixelPosition);
    }

    [Fact] public void Unassigned_Throws()
    {
        var zid2 = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(zid2, "Z",
                new NormalizedRect(0, 0, 5000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, 1) });
        var icons = new[] { I(0, "a"), I(1, "b") };
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, DesktopLayoutIconRuleSet.Empty(template.TemplateId),
            new[] { _dd }, null, _spacing, icons);
        Assert.False(plan.IsComplete);
        Assert.Throws<ArgumentException>(() =>
            DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
                plan, Read(icons), new DesktopLayoutSnapshotFactory()));
    }
}
