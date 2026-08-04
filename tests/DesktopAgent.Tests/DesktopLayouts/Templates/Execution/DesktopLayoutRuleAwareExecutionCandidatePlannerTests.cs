using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Execution;

public sealed class DesktopLayoutRuleAwareExecutionCandidatePlannerTests
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

    private DesktopLayoutRuleAwarePreviewPlan MakePreviewPlan(
        params DesktopIconSnapshotItem[] icons)
    {
        var template = new DesktopLayoutTemplate(_tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(_zid, "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var ruleSet = DesktopLayoutIconRuleSet.Empty(_tid);
        return DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, ruleSet, new[] { _dd }, null, _spacing, icons);
    }

    private DesktopIconReadResult MakeReadResult(params DesktopIconSnapshotItem[] items)
    {
        var descriptors = items.Select(it => new DesktopIconDescriptor(
            it.StableId, it.DisplayName, it.ParsingName, it.ItemKind,
            it.PixelPosition, it.MonitorDeviceName, it.IsSystemItem,
            it.IsFileSystemItem)).ToList();
        return new DesktopIconReadResult(descriptors, 0,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
    }

    // ═══ Stale input checks ═══

    [Fact] public void FailedItemCount_Rejected()
    {
        var plan = MakePreviewPlan(I(0, "a"));
        var result = new DesktopIconReadResult(
            new List<DesktopIconDescriptor>(), 3,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, result, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.False(r.IsSuccess);
        Assert.Contains(r.Issues, i => i.Code == DesktopLayoutRuleAwareExecutionCandidateIssueCode.CurrentReadIncomplete);
    }

    [Fact] public void AutoArrange_Rejected()
    {
        var icons = new[] { I(0, "a") };
        var plan = MakePreviewPlan(icons);
        var read = MakeReadResult(icons);
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(true, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.False(r.IsSuccess);
        Assert.Contains(r.Issues, i => i.Code == DesktopLayoutRuleAwareExecutionCandidateIssueCode.AutoArrangeEnabled);
    }

    [Fact] public void SpacingChanged_Rejected()
    {
        var icons = new[] { I(0, "a") };
        var plan = MakePreviewPlan(icons);
        var read = MakeReadResult(icons);
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, new PixelPoint(90, 90), ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.False(r.IsSuccess);
        Assert.Contains(r.Issues, i => i.Code == DesktopLayoutRuleAwareExecutionCandidateIssueCode.IconSpacingChanged);
    }

    [Fact] public void IconCountChanged_Rejected()
    {
        var icons = new[] { I(0, "a"), I(1, "b") };
        var plan = MakePreviewPlan(icons);
        var read = MakeReadResult(I(0, "a"));
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.False(r.IsSuccess);
    }

    [Fact] public void StableIdChanged_Rejected()
    {
        var icons = new[] { I(0, "a") };
        var plan = MakePreviewPlan(icons);
        var read = MakeReadResult(I(0, "b"));
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.False(r.IsSuccess);
        Assert.Contains(r.Issues, i => i.Code == DesktopLayoutRuleAwareExecutionCandidateIssueCode.CurrentIconIdentityChanged);
    }

    [Fact] public void PositionChanged_Rejected()
    {
        var icons = new[] { I(0, "a", pos: new PixelPoint(0, 0)) };
        var plan = MakePreviewPlan(icons);
        var read = MakeReadResult(I(0, "a", pos: new PixelPoint(80, 0)));
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.False(r.IsSuccess);
        Assert.Contains(r.Issues, i => i.Code == DesktopLayoutRuleAwareExecutionCandidateIssueCode.CurrentIconPositionChanged);
    }

    // ═══ Success path ═══

    [Fact] public void EmptyInput_NoOp_Success()
    {
        var plan = MakePreviewPlan();
        var read = MakeReadResult();
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.True(r.IsSuccess);
        Assert.NotNull(r.Candidate);
        Assert.True(r.Candidate!.IsNoOp);
        Assert.False(r.Candidate.RequiresUserConfirmation);
    }

    [Fact] public void SingleIcon_AlreadyAtTarget_Success()
    {
        var icons = new[] { I(0, "a", pos: new PixelPoint(0, 0)) };
        var plan = MakePreviewPlan(icons);
        var read = MakeReadResult(icons);
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.True(r.IsSuccess);
        Assert.NotNull(r.Candidate);
        Assert.True(r.Candidate!.IsNoOp);
    }

    // ═══ IconSpacing ═══

    [Fact] public void PreviewPlan_HasIconSpacing()
    {
        var plan = MakePreviewPlan(I(0, "a"));
        Assert.Equal(_spacing, plan.IconSpacing);
    }

    [Fact] public void PreviewPlan_NullSpacing_Rejected()
    {
        var template = new DesktopLayoutTemplate(_tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(_zid, "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopLayoutRuleAwarePreviewPlanner.Plan(
                template, DesktopLayoutIconRuleSet.Empty(_tid),
                new[] { _dd }, null, new PixelPoint(0, 0),
                new[] { I(0, "a") }));
    }

    // ═══ Candidate invariants ═══

    [Fact] public void Candidate_StatsCorrect()
    {
        var icons = new[] { I(0, "a", pos: new PixelPoint(0, 0)) };
        var plan = MakePreviewPlan(icons);
        var read = MakeReadResult(icons);
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.True(r.IsSuccess);
        var c = r.Candidate!;
        Assert.Equal(1, c.InputIconCount);
        Assert.Equal(plan.TargetDisplay.DeviceName, c.TargetDeviceName);
    }

    // ═══ Single-icon MoveRequired ═══
    [Fact] public void SingleIcon_MoveRequired_Executable()
    {
        var icons = new[] { I(0, "a", pos: new PixelPoint(80, 0)) };
        var plan = MakePreviewPlan(icons);
        var read = MakeReadResult(icons);
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.True(r.IsSuccess);
        Assert.NotNull(r.Candidate);
        Assert.True(r.Candidate!.IsExecutable);
        Assert.True(r.Candidate.RequiresUserConfirmation);
    }

    // ═══ Two-icon swap with staging ═══
    [Fact] public void TwoIconSwap_HasStaging_StillExecutable()
    {
        // Place two icons at each other's target positions → requires staging
        var pos0 = new PixelPoint(0, 0);
        var pos1 = new PixelPoint(80, 0);
        var icons = new[] { I(0, "a", pos: pos1), I(1, "b", pos: pos0) };
        var template = new DesktopLayoutTemplate(Guid.NewGuid(), "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) });
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template, DesktopLayoutIconRuleSet.Empty(template.TemplateId),
            new[] { _dd }, null, _spacing, icons);
        var read = MakeReadResult(icons);
        var r = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            plan, read, new[] { _dd },
            new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
            new DesktopLayoutSnapshotFactory());
        Assert.True(r.IsSuccess);
        Assert.NotNull(r.Candidate);
        // Even with staging, this is now considered executable
        Assert.True(r.Candidate!.IsExecutable);
    }

    // ═══ Safety ═══
    [Fact] public void NullInputs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
                null!, MakeReadResult(I(0,"a")), new[] { _dd },
                new DesktopIconPositioningCapabilities(false, _spacing, ConnectionMethod.FindWindowSW, null),
                new DesktopLayoutSnapshotFactory()));
    }
}
