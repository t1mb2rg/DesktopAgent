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

    [Fact] public void Exclude_IsNotExecutionTarget()
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

        Assert.Empty(snap.Icons);
    }

    [Fact] public void Pin_IsNotExecutionTarget()
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

        Assert.Empty(snap.Icons);
    }

    [Fact] public void FixedRemoved_PreservesRemainingInputOrder()
    {
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var icons = new[]
        {
            I(0, "fixed", pos: new PixelPoint(240, 0)),
            I(1, "a", pos: new PixelPoint(80, 0)),
            I(2, "b", pos: new PixelPoint(160, 0)),
        };
        var ruleSet = new DesktopLayoutIconRuleSet(template.TemplateId,
            new[] { DesktopLayoutIconRule.ForExclude("fixed") });
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);

        var snap = DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
            plan, Read(icons), new DesktopLayoutSnapshotFactory());

        Assert.Equal(new[] { "a", "b" }, snap.Icons.Select(i => i.StableId));
        Assert.Equal(new[] { 0, 1 }, snap.Icons.Select(i => i.Ordinal));
    }

    [Fact]
    public void StableCandidate_HighSlotSnapshotRetainsPlacementAndDoesNotExecutePinOrUnrelatedScreen()
    {
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "Stable", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            [new DesktopLayoutZone(Guid.NewGuid(), "Zone",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null)]);
        var rules = new DesktopLayoutIconRuleSet(template.TemplateId,
            [DesktopLayoutIconRule.ForPin("pin")]);
        var icons = new[]
        {
            I(0, "new", new PixelPoint(17, 480)),
            I(1, "pin", new PixelPoint(0, 0)),
            I(2, "a", new PixelPoint(0, 80)),
            I(3, "b", new PixelPoint(0, 400)),
            I(4, "remote", new PixelPoint(2000, 0), "DISPLAY2"),
        };

        var preview = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, rules, [_dd], null, _spacing, icons,
            preserveExistingGridSlots: true);

        Assert.True(preview.IsComplete);
        Assert.Equal(1, preview.MoveRequiredCount);
        Assert.Equal(2, preview.AlreadyAtTargetCount);
        Assert.Equal(1, preview.PinnedCount);
        Assert.Single(preview.OutOfScopeIcons);

        var snapshot = DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
            preview, Read(icons), new DesktopLayoutSnapshotFactory());

        Assert.Equal(new[] { "new", "a", "b", "remote" },
            snapshot.Icons.Select(icon => icon.StableId));
        Assert.Equal(new PixelPoint(0, 80),
            snapshot.Icons.Single(icon => icon.StableId == "a").PixelPosition);
        Assert.Equal(new PixelPoint(0, 400),
            snapshot.Icons.Single(icon => icon.StableId == "b").PixelPosition);
        Assert.Equal(new PixelPoint(0, 160),
            snapshot.Icons.Single(icon => icon.StableId == "new").PixelPosition);
        Assert.Equal(new PixelPoint(2000, 0),
            snapshot.Icons.Single(icon => icon.StableId == "remote").PixelPosition);
        Assert.Equal("DISPLAY2",
            snapshot.Icons.Single(icon => icon.StableId == "remote").MonitorDeviceName);
        Assert.DoesNotContain(snapshot.Icons, icon => icon.StableId == "pin");
    }

    [Fact]
    public void StableCandidate_ExcludedObstacleIsNeverMovedOrReplaced()
    {
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "Exclude", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            [new DesktopLayoutZone(Guid.NewGuid(), "Zone",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null)]);
        var rules = new DesktopLayoutIconRuleSet(template.TemplateId,
            [DesktopLayoutIconRule.ForExclude("excluded")]);
        var icons = new[]
        {
            I(0, "new", new PixelPoint(17, 480)),
            I(1, "excluded", new PixelPoint(0, 0)),
            I(2, "kept", new PixelPoint(0, 80)),
        };

        var preview = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, rules, [_dd], null, _spacing, icons,
            preserveExistingGridSlots: true);
        Assert.True(preview.IsComplete);
        Assert.Equal(1, preview.ExcludedCount);
        Assert.Equal(1, preview.AlreadyAtTargetCount);
        Assert.Equal(1, preview.MoveRequiredCount);

        var snapshot = DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
            preview, Read(icons), new DesktopLayoutSnapshotFactory());
        Assert.DoesNotContain(snapshot.Icons, icon => icon.StableId == "excluded");
        Assert.Equal(new PixelPoint(0, 80),
            snapshot.Icons.Single(icon => icon.StableId == "kept").PixelPosition);
        Assert.Equal(new PixelPoint(0, 160),
            snapshot.Icons.Single(icon => icon.StableId == "new").PixelPosition);
    }

    [Fact]
    public void StableCandidate_ZeroSlotZoneCannotCreateExecutableSnapshot()
    {
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "No slots", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            [new DesktopLayoutZone(Guid.NewGuid(), "Tiny",
                new NormalizedRect(0, 0, 250, 250), 0,
                ZonePlacementFlow.ColumnMajor, null)]);
        var icons = new[] { I(0, "a", new PixelPoint(0, 0)) };

        var preview = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, DesktopLayoutIconRuleSet.Empty(template.TemplateId),
            [_dd], null, _spacing, icons, preserveExistingGridSlots: true);

        Assert.False(preview.IsComplete);
        Assert.Equal(1, preview.DefaultUnassignedCount);
        Assert.Throws<ArgumentException>(() =>
            DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
                preview, Read(icons), new DesktopLayoutSnapshotFactory()));
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

    [Fact] public void OverrideUnassigned_Throws()
    {
        var zid2 = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(zid2, "Z",
                new NormalizedRect(0, 0, 5000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, 1) });
        var ruleSet = new DesktopLayoutIconRuleSet(template.TemplateId,
            new[] { DesktopLayoutIconRule.ForZoneOverride("a", zid2),
                    DesktopLayoutIconRule.ForZoneOverride("b", zid2) });
        var icons = new[] { I(0, "a"), I(1, "b") };
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);
        Assert.False(plan.IsComplete);
        Assert.Throws<ArgumentException>(() =>
            DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
                plan, Read(icons), new DesktopLayoutSnapshotFactory()));
    }
}
