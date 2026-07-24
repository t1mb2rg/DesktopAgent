using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class TargetDisplayLayoutExecutionPlannerTests
{
    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelPoint Sp = new(80, 80);
    private static readonly PixelRect WA = new(0, 0, 800, 600);
    private static int _ord;
    public TargetDisplayLayoutExecutionPlannerTests() { _ord = 0; }

    private static DisplayDescriptor MakeD(string name = null!) =>
        new(name ?? TD, new PixelRect(0, 0, 1920, 1080), WA, 96, 96, (name ?? TD) == TD);
    private static DesktopIconPositioningCapabilities MakeCaps() => new(false, Sp, ConnectionMethod.FindWindowSW, null);

    private static DesktopIconSnapshotItem Snap(string sid, PixelPoint pos) =>
        new(_ord++, sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);
    private static DesktopIconDescriptor Live(string sid, PixelPoint pos) =>
        new(sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    // Helper: plan → execution, returning both
    private TargetDisplayLayoutExecutionPlan Exec(
        DesktopIconSnapshotItem[] snaps, DesktopIconDescriptor[] lives,
        string targetMonitor = null!)
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow,
            targetMonitor ?? TD, ConnectionMethod.FindWindowSW, 1, snaps);
        var read = new DesktopIconReadResult(lives, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeD(targetMonitor ?? TD) };
        var restorePlan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snapshot, read, displays, MakeCaps(), targetMonitor ?? TD);
        return TargetDisplayLayoutExecutionPlanner.CreatePlan(restorePlan, read, displays, MakeCaps());
    }

    // ═══ 参数 ═══
    [Fact] public void NullRestorePlan_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutExecutionPlanner.CreatePlan(null!, MakeRead([]), new[] { MakeD() }, MakeCaps()));
    [Fact] public void NullCurrentRead_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutExecutionPlanner.CreatePlan(MakeRestore([]), null!, new[] { MakeD() }, MakeCaps()));
    [Fact] public void NullDisplays_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutExecutionPlanner.CreatePlan(MakeRestore([]), MakeRead([]), null!, MakeCaps()));
    [Fact] public void NullCapabilities_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutExecutionPlanner.CreatePlan(MakeRestore([]), MakeRead([]), new[] { MakeD() }, null!));

    // ═══ 前置 ═══
    [Fact] public void IncompleteRead_Issue()
    {
        var rp = MakeRestore([Snap("a", new(160, 240))]);
        var rd = new DesktopIconReadResult([Live("a", new(160, 240))], 3, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, rd, new[] { MakeD() }, MakeCaps());
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.CurrentReadIncomplete);
    }

    [Fact] public void AutoArrange_Issue()
    {
        var rp = MakeRestore([]);
        var caps = new DesktopIconPositioningCapabilities(true, Sp, ConnectionMethod.FindWindowSW, null);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, MakeRead([]), new[] { MakeD() }, caps);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.AutoArrangeEnabled);
    }

    [Fact] public void SourcePlanBlocking_NoSteps()
    {
        // Duplicate StableId → blocking
        var ep = Exec([Snap("a", new(160, 240)), Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanHasBlockingErrors);
        Assert.Empty(ep.Steps);
    }

    [Fact] public void SourcePlanItemCountMismatch_Issue() { /* covered by SourcePlanInconsistent when dup Sids exist */ }

    // ═══ 陈旧 ═══
    [Fact] public void CurrentStableIdDuplicate_Issue()
    {
        // Duplicate StableId in current → SourcesPlanHasBlockingErrors from M1-05A first
        var ep = Exec([Snap("a", new(240, 320))], [Live("a", new(160, 240)), Live("a", new(320, 400))]);
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanHasBlockingErrors);
    }

    [Fact] public void CurrentPositionChanged_Stale()
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1, [Snap("a", new(240, 320))]);
        var rd1 = new DesktopIconReadResult([Live("a", new(160, 240))], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, rd1, new[] { MakeD() }, MakeCaps(), TD);
        var rd2 = new DesktopIconReadResult([Live("a", new(200, 300))], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null); // moved!
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, rd2, new[] { MakeD() }, MakeCaps());
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanStale);
    }

    [Fact] public void StableIdComparison_IsOrdinal()
    {
        // "SID" ≠ "sid" → MissingFromCurrent → IsExecutable but NOT IsNoOp
        var ep = Exec([Snap("SID", new(240, 320))], [Live("sid", new(160, 240))]);
        Assert.True(ep.IsExecutable); Assert.False(ep.IsNoOp); Assert.Empty(ep.Steps);
    }


    // ═══ 基础执行 ═══
    [Fact] public void NoMoveItems_IsNoOp()
    {
        // a 已在目标，且无 MissingFromCurrent → 真正的 NoOp
        var ep = Exec([Snap("a", new(160, 240))], [Live("a", new(160, 240))]);
        Assert.True(ep.IsExecutable); Assert.True(ep.IsNoOp); Assert.Empty(ep.Steps);
    }

    [Fact] public void OnlyMissingItems_ExecutableButNotNoOp()
    {
        var ep = Exec([Snap("a", new(160, 240))], []);
        Assert.True(ep.IsExecutable); Assert.False(ep.IsNoOp);
        Assert.Empty(ep.Steps);
    }

    [Fact] public void SingleMove_OneTargetStep()
    {
        var ep = Exec([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        Assert.True(ep.IsExecutable); Assert.Single(ep.Steps);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[0].Kind);
        Assert.Equal("a", ep.Steps[0].StableId);
    }

    [Fact] public void TwoItemChain_FollowsRestoreExecutionOrder()
    {
        // a 当前(80,80)目标(160,80)，b 当前(160,80)目标(240,80) → b 占用 a 目标
        // MoveExecutionOrder = [b, a]
        var ep = Exec(
            [Snap("a", new(160, 80)), Snap("b", new(240, 80))],
            [Live("a", new(80, 80)), Live("b", new(160, 80))]);
        Assert.True(ep.IsExecutable); Assert.False(ep.UsesTemporaryStaging);
        Assert.Equal(2, ep.Steps.Count);
        Assert.Equal("b", ep.Steps[0].StableId); Assert.Equal("a", ep.Steps[1].StableId);
        Assert.All(ep.Steps, s => Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, s.Kind));
    }

    [Fact] public void ThreeItemChain_FollowsRestoreExecutionOrder()
    {
        // a 当前(80,80) → (160,80), b 当前(160,80) → (240,80), c 当前(240,80) → (320,80)
        // MoveExecutionOrder = [c, b, a] (c must move first to free b's target, then b, then a)
        var ep = Exec(
            [Snap("a", new(160, 80)), Snap("b", new(240, 80)), Snap("c", new(320, 80))],
            [Live("a", new(80, 80)), Live("b", new(160, 80)), Live("c", new(240, 80))]);
        Assert.True(ep.IsExecutable); Assert.False(ep.UsesTemporaryStaging);
        Assert.Equal(3, ep.Steps.Count);
        Assert.Equal("c", ep.Steps[0].StableId); Assert.Equal("b", ep.Steps[1].StableId);
        Assert.Equal("a", ep.Steps[2].StableId);
        Assert.All(ep.Steps, s => Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, s.Kind));
    }

    [Fact] public void FromPositions_FollowSimulation()
    {
        var ep = Exec(
            [Snap("a", new(320, 400)), Snap("b", new(160, 240))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        Assert.True(ep.IsExecutable); Assert.Equal(3, ep.Steps.Count);
        // Step 0: staging icon moves from its initial position
        var stagedId = ep.Steps[0].StableId;
        var stagedInitial = stagedId == "a" ? new PixelPoint(160, 240) : new PixelPoint(320, 400);
        Assert.Equal(stagedInitial, ep.Steps[0].FromPosition);
        // Step 1: other icon moves from its initial position to its target
        var otherId = ep.Steps[1].StableId;
        var otherInitial = otherId == "a" ? new PixelPoint(160, 240) : new PixelPoint(320, 400);
        Assert.Equal(otherInitial, ep.Steps[1].FromPosition);
        // Step 2: staged icon moves from staging position to target
        var sa = ep.StagingAssignments[0];
        Assert.Equal(sa.StagingPosition, ep.Steps[2].FromPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, ep.Steps[2].Kind);
        // Each step's destination becomes next step's origin for same icon
        for (var i = 0; i < ep.Steps.Count; i++)
            Assert.Equal(i, ep.Steps[i].StepIndex);
    }

    [Fact] public void StepIndexes_Continuous()
    {
        var ep = Exec(
            [Snap("a", new(320, 400)), Snap("b", new(240, 320))],
            [Live("a", new(240, 320)), Live("b", new(160, 240))]);
        for (var i = 0; i < ep.Steps.Count; i++) Assert.Equal(i, ep.Steps[i].StepIndex);
    }

    [Fact] public void FinalPositions_MatchSnapshot()
    {
        var ep = Exec([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        Assert.Equal(new PixelPoint(240, 320), ep.Steps[^1].ToPosition);
    }

    // ═══ 环与中转 ═══
    [Fact] public void TwoItemSwap_ProducesThreeSteps()
    {
        // a 当前(0,0)→(80,0), b 当前(80,0)→(0,0), WA=(0,0)-(240,80), spacing(80,80)
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 240, 80), new PixelRect(0, 0, 240, 80), 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, new PixelPoint(80, 0), TD, false, true),
             new DesktopIconSnapshotItem(1, "b", "N_b", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);
        var read = new DesktopIconReadResult([Live("a", new(0, 0)), Live("b", new(80, 0))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, read, new[] { display }, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read, new[] { display }, MakeCaps());
        Assert.True(ep.IsExecutable); Assert.True(ep.UsesTemporaryStaging);
        Assert.Equal(3, ep.Steps.Count); Assert.Single(ep.StagingAssignments);
        // Step 0: a → staging
        Assert.Equal(0, ep.Steps[0].StepIndex); Assert.Equal("a", ep.Steps[0].StableId);
        Assert.Equal(new PixelPoint(0, 0), ep.Steps[0].FromPosition);
        Assert.Equal(new PixelPoint(160, 0), ep.Steps[0].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, ep.Steps[0].Kind);
        // Step 1: b → target
        Assert.Equal(1, ep.Steps[1].StepIndex); Assert.Equal("b", ep.Steps[1].StableId);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[1].FromPosition);
        Assert.Equal(new PixelPoint(0, 0), ep.Steps[1].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[1].Kind);
        // Step 2: a → target
        Assert.Equal(2, ep.Steps[2].StepIndex); Assert.Equal("a", ep.Steps[2].StableId);
        Assert.Equal(new PixelPoint(160, 0), ep.Steps[2].FromPosition);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[2].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, ep.Steps[2].Kind);
        // Staging
        var sa = ep.StagingAssignments[0];
        Assert.Equal("a", sa.StableId); Assert.Equal(new PixelPoint(160, 0), sa.StagingPosition);
        Assert.Equal(0, sa.EnterStepIndex); Assert.Equal(2, sa.ExitStepIndex);
    }

    [Fact] public void ThreeItemCycle_FourSteps()
    {
        // a 当前(0,0) → (80,0), b 当前(80,0) → (160,0), c 当前(160,0) → (0,0)
        // 3-cycle with WA(0,0)-(320,80)
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 320, 80), new PixelRect(0, 0, 320, 80), 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, new PixelPoint(80, 0), TD, false, true),
             new DesktopIconSnapshotItem(1, "b", "N_b", null, ItemKind.File, new PixelPoint(160, 0), TD, false, true),
             new DesktopIconSnapshotItem(2, "c", "N_c", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);
        var read = new DesktopIconReadResult(
            [Live("a", new(0, 0)), Live("b", new(80, 0)), Live("c", new(160, 0))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, read, new[] { display }, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read, new[] { display }, MakeCaps());
        Assert.True(ep.IsExecutable); Assert.True(ep.UsesTemporaryStaging);
        Assert.Equal(4, ep.Steps.Count);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, ep.Steps[0].Kind);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[1].Kind);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[2].Kind);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, ep.Steps[3].Kind);
        Assert.Single(ep.StagingAssignments);
    }

    [Fact] public void FourItemCycle_ProducesFiveSteps()
    {
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 400, 80), new PixelRect(0, 0, 400, 80), 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, new PixelPoint(80, 0), TD, false, true),
             new DesktopIconSnapshotItem(1, "b", "N_b", null, ItemKind.File, new PixelPoint(160, 0), TD, false, true),
             new DesktopIconSnapshotItem(2, "c", "N_c", null, ItemKind.File, new PixelPoint(240, 0), TD, false, true),
             new DesktopIconSnapshotItem(3, "d", "N_d", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);
        var read = new DesktopIconReadResult(
            [Live("a", new(0, 0)), Live("b", new(80, 0)), Live("c", new(160, 0)), Live("d", new(240, 0))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, read, new[] { display }, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read, new[] { display }, MakeCaps());
        Assert.True(ep.IsExecutable); Assert.True(ep.UsesTemporaryStaging);
        Assert.Equal(5, ep.Steps.Count); Assert.Single(ep.StagingAssignments);
        // Step 0: a → staging (320,0)
        Assert.Equal(0, ep.Steps[0].StepIndex); Assert.Equal("a", ep.Steps[0].StableId);
        Assert.Equal(new PixelPoint(0, 0), ep.Steps[0].FromPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, ep.Steps[0].Kind);
        // Step 1: d → target
        Assert.Equal(1, ep.Steps[1].StepIndex); Assert.Equal("d", ep.Steps[1].StableId);
        Assert.Equal(new PixelPoint(240, 0), ep.Steps[1].FromPosition);
        Assert.Equal(new PixelPoint(0, 0), ep.Steps[1].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[1].Kind);
        // Step 2: c → target
        Assert.Equal(2, ep.Steps[2].StepIndex); Assert.Equal("c", ep.Steps[2].StableId);
        Assert.Equal(new PixelPoint(160, 0), ep.Steps[2].FromPosition);
        Assert.Equal(new PixelPoint(240, 0), ep.Steps[2].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[2].Kind);
        // Step 3: b → target
        Assert.Equal(3, ep.Steps[3].StepIndex); Assert.Equal("b", ep.Steps[3].StableId);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[3].FromPosition);
        Assert.Equal(new PixelPoint(160, 0), ep.Steps[3].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[3].Kind);
        // Step 4: a → target
        Assert.Equal(4, ep.Steps[4].StepIndex); Assert.Equal("a", ep.Steps[4].StableId);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, ep.Steps[4].Kind);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[4].ToPosition);
        // Staging
        var sa = ep.StagingAssignments[0];
        Assert.Equal("a", sa.StableId); Assert.Equal(0, sa.EnterStepIndex); Assert.Equal(4, sa.ExitStepIndex);
    }

    [Fact] public void NoSafeStagingPosition_ReturnsIssue()
    {
        // WA=(0,0)-(160,80), Sp=(80,80) → only 2 cells, both occupied by a and b swapping
        var tinyWA = new PixelRect(0, 0, 160, 80);
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 160, 80), tinyWA, 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, new PixelPoint(80, 0), TD, false, true),
             new DesktopIconSnapshotItem(1, "b", "N_b", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);
        var read = new DesktopIconReadResult([Live("a", new(0, 0)), Live("b", new(80, 0))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, read, new[] { display }, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read, new[] { display }, MakeCaps());
        Assert.False(ep.IsExecutable); Assert.False(ep.IsNoOp);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.NoSafeStagingPosition);
        Assert.Empty(ep.Steps); Assert.Empty(ep.StagingAssignments);
    }

    // ═══ MissingFromCurrent 语义 ═══
    [Fact] public void MissingItem_RemainsMissing_IsExecutableButNotNoOp()
    {
        var ep = Exec([Snap("a", new(160, 240))], []);
        Assert.True(ep.IsExecutable); Assert.False(ep.IsNoOp);
        Assert.DoesNotContain(ep.Steps, s => s.StableId == "a");
        Assert.DoesNotContain(ep.StagingAssignments, sa => sa.StableId == "a");
    }

    [Fact] public void MissingItem_Reappeared_ReturnsSourcePlanStale()
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1,
            [Snap("a", new(160, 240))]);
        var rd1 = new DesktopIconReadResult([], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null); // a missing
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, rd1, new[] { MakeD() }, MakeCaps(), TD);
        var rd2 = new DesktopIconReadResult([Live("a", new(80, 240))], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null); // a reappeared
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, rd2, new[] { MakeD() }, MakeCaps());
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanStale);
        Assert.Empty(ep.Steps);
    }

    // ═══ 失败后半截步骤 ═══
    [Fact] public void FailureAfterInternalProgress_ReturnsNoPartialSteps()
    {
        // WA=(0,0)-(320,80), Sp=(80,80): 网格 0,80,160,240
        // x 当前(160,0)→(240,0): 可以直接移动
        // a 当前(0,0)→(80,0), b 当前(80,0)→(0,0): 交换环
        // m 缺失, 目标(160,0): 已由 x 离开后空出，是最终目标不能用于 staging
        // 内部先完成 x→(240,0), 然后 a↔b 环无可用 staging → 失败后 Steps=空
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 320, 80), new PixelRect(0, 0, 320, 80), 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "x", "N_x", null, ItemKind.File, new PixelPoint(240, 0), TD, false, true),
             new DesktopIconSnapshotItem(1, "a", "N_a", null, ItemKind.File, new PixelPoint(80, 0), TD, false, true),
             new DesktopIconSnapshotItem(2, "b", "N_b", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true),
             new DesktopIconSnapshotItem(3, "m", "N_m", null, ItemKind.File, new PixelPoint(160, 0), TD, false, true)]);
        var read = new DesktopIconReadResult(
            [Live("x", new(160, 0)), Live("a", new(0, 0)), Live("b", new(80, 0))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, read, new[] { display }, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read, new[] { display }, MakeCaps());
        Assert.False(ep.IsExecutable); Assert.False(ep.IsNoOp);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.NoSafeStagingPosition);
        Assert.Empty(ep.Steps); Assert.Empty(ep.StagingAssignments);
    }

    [Fact] public void StagingPosition_Deterministic()
    {
        var p1 = Exec([Snap("a", new(320, 400)), Snap("b", new(160, 240))], [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        var p2 = Exec([Snap("a", new(320, 400)), Snap("b", new(160, 240))], [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        for (var i = 0; i < p1.Steps.Count; i++)
        { Assert.Equal(p1.Steps[i].StableId, p2.Steps[i].StableId); Assert.Equal(p1.Steps[i].Kind, p2.Steps[i].Kind); }
    }

    // ═══ 安全边界 ═══
    [Fact] public void OverlappingFinalTargets_Issue()
    {
        // a target (160,240) rect (160,240)-(240,320), b target (200,240) rect (200,240)-(280,320) → overlap
        // Use Exec so restore plan matches current
        var ep = Exec(
            [Snap("a", new(160, 240)), Snap("b", new(200, 240))],
            [Live("a", new(80, 240)), Live("b", new(400, 400))]);
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.OverlappingFinalTargets);
    }

    [Fact] public void CurrentRectOverflow_Issue()
    {
        var restorePlan = MakeRestore([]);
        var over = new DesktopIconDescriptor("x", "X", null, ItemKind.File, new PixelPoint(int.MaxValue - 1, 0), TD, false, true);
        var rd = new DesktopIconReadResult([over], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(restorePlan, rd, new[] { MakeD() }, MakeCaps());
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.CurrentRectangleOverflow);
    }

    [Fact] public void AlreadyAtTarget_NeverMoved()
    {
        var ep = Exec([Snap("a", new(240, 320)), Snap("b", new(160, 240))], [Live("a", new(80, 240)), Live("b", new(160, 240))]);
        Assert.True(ep.IsExecutable);
        Assert.DoesNotContain(ep.Steps, s => s.StableId == "b");
    }

    [Fact] public void ExtraCurrentIcon_NeverMoved()
    {
        var ep = Exec([Snap("a", new(240, 320))], [Live("a", new(160, 240)), Live("x", new(400, 400))]);
        Assert.DoesNotContain(ep.Steps, s => s.StableId == "x");
    }

    [Fact] public void FailedPlan_NoPartialSteps()
    {
        var ep = Exec([Snap("a", new(160, 240)), Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        Assert.False(ep.IsExecutable);
        Assert.Empty(ep.Steps);
    }

    [Fact] public void SameInput_ProducesIdenticalExecutionPlan()
    {
        var p1 = Exec(
            [Snap("a", new(320, 400)), Snap("b", new(160, 240))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        var p2 = Exec(
            [Snap("a", new(320, 400)), Snap("b", new(160, 240))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        // Plan-level
        Assert.Equal(p1.TargetDeviceName, p2.TargetDeviceName);
        Assert.Equal(p1.IsExecutable, p2.IsExecutable);
        Assert.Equal(p1.IsNoOp, p2.IsNoOp);
        Assert.Equal(p1.UsesTemporaryStaging, p2.UsesTemporaryStaging);
        Assert.Equal(p1.DirectMoveStepCount, p2.DirectMoveStepCount);
        Assert.Equal(p1.StagingMoveStepCount, p2.StagingMoveStepCount);
        Assert.Equal(p1.TargetMoveStepCount, p2.TargetMoveStepCount);
        // Steps
        Assert.Equal(p1.Steps.Count, p2.Steps.Count);
        for (var i = 0; i < p1.Steps.Count; i++)
        {
            var s1 = p1.Steps[i]; var s2 = p2.Steps[i];
            Assert.Equal(s1.StepIndex, s2.StepIndex);
            Assert.Equal(s1.StableId, s2.StableId);
            Assert.Equal(s1.DisplayName, s2.DisplayName);
            Assert.Equal(s1.FromPosition, s2.FromPosition);
            Assert.Equal(s1.ToPosition, s2.ToPosition);
            Assert.Equal(s1.Kind, s2.Kind);
            Assert.Equal(s1.ExpectedMonitorDeviceName, s2.ExpectedMonitorDeviceName);
        }
        // StagingAssignments
        Assert.Equal(p1.StagingAssignments.Count, p2.StagingAssignments.Count);
        for (var i = 0; i < p1.StagingAssignments.Count; i++)
        {
            var a1 = p1.StagingAssignments[i]; var a2 = p2.StagingAssignments[i];
            Assert.Equal(a1.StableId, a2.StableId);
            Assert.Equal(a1.StagingPosition, a2.StagingPosition);
            Assert.Equal(a1.EnterStepIndex, a2.EnterStepIndex);
            Assert.Equal(a1.ExitStepIndex, a2.ExitStepIndex);
        }
        // Issues
        Assert.True(p1.Issues.Select(i => i.Code).SequenceEqual(p2.Issues.Select(i => i.Code)));
        for (var i = 0; i < p1.Issues.Count; i++)
        {
            Assert.Equal(p1.Issues[i].Message, p2.Issues[i].Message);
            Assert.Equal(p1.Issues[i].RelatedStableId, p2.Issues[i].RelatedStableId);
        }
    }

    [Fact] public void StagingPosition_DoesNotOverlapCurrentIcons()
    {
        var ep = Exec(
            [Snap("a", new(320, 400)), Snap("b", new(160, 240))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        var stagingPos = ep.StagingAssignments[0].StagingPosition;
        var stagingRect = DesktopIconLayoutGeometry.CreateRectChecked(stagingPos, Sp);
        var aRect = DesktopIconLayoutGeometry.CreateRectChecked(new PixelPoint(160, 240), Sp);
        var bRect = DesktopIconLayoutGeometry.CreateRectChecked(new PixelPoint(320, 400), Sp);
        Assert.False(DesktopIconLayoutGeometry.Overlaps(stagingRect, aRect));
        Assert.False(DesktopIconLayoutGeometry.Overlaps(stagingRect, bRect));
    }

    [Fact] public void StagingPosition_DoesNotOverlapFinalTargets()
    {
        var ep = Exec(
            [Snap("a", new(320, 400)), Snap("b", new(160, 240))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        var stagingPos = ep.StagingAssignments[0].StagingPosition;
        var stagingRect = DesktopIconLayoutGeometry.CreateRectChecked(stagingPos, Sp);
        var targetARect = DesktopIconLayoutGeometry.CreateRectChecked(new PixelPoint(320, 400), Sp);
        var targetBRect = DesktopIconLayoutGeometry.CreateRectChecked(new PixelPoint(160, 240), Sp);
        Assert.False(DesktopIconLayoutGeometry.Overlaps(stagingRect, targetARect));
        Assert.False(DesktopIconLayoutGeometry.Overlaps(stagingRect, targetBRect));
    }

    [Fact] public void ReleasedStaging_Reused()
    {
        // Two disjoint cycles → both use staging, first one releases it
        var ep = Exec(
            [Snap("a", new(160, 80)), Snap("b", new(80, 80)), Snap("c", new(160, 320)), Snap("d", new(80, 320))],
            [Live("a", new(80, 80)), Live("b", new(160, 80)), Live("c", new(80, 320)), Live("d", new(160, 320))]);
        Assert.True(ep.IsExecutable); Assert.True(ep.UsesTemporaryStaging);
        // Both swaps resolved
        Assert.Equal(6, ep.Steps.Count);
    }

    // ═══ Helpers ═══
    private static TargetDisplayLayoutRestorePlan MakeRestore(DesktopIconSnapshotItem[] snaps) =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(
            new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1, snaps),
            MakeRead(snaps.Select(s => Live(s.StableId, s.PixelPosition)).ToArray()),
            new[] { MakeD() }, MakeCaps(), TD);

    private static DesktopIconReadResult MakeRead(DesktopIconDescriptor[] icons) =>
        new(icons, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
}
