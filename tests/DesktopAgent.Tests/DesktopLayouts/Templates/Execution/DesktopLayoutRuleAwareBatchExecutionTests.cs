using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Execution;

public sealed class DesktopLayoutRuleAwareBatchExecutionTests
{
    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelPoint Sp = new(80, 80);
    private static readonly PixelRect WA = new(0, 0, 800, 600);
    private static int _ord;
    public DesktopLayoutRuleAwareBatchExecutionTests() { _ord = 0; }

    private static DisplayDescriptor MakeDisplay() =>
        new(TD, new PixelRect(0, 0, 1920, 1080), WA, 96, 96, true);

    private static DesktopIconPositioningCapabilities MakeCaps() =>
        new(false, Sp, ConnectionMethod.FindWindowSW, null);

    private static DesktopIconSnapshotItem Snap(string sid, PixelPoint pos) =>
        new(_ord++, sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    private static DesktopIconDescriptor Live(string sid, PixelPoint pos) =>
        new(sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    private static DesktopIconDescriptor LiveOnMonitor(string sid, PixelPoint pos, string monitor) =>
        new(sid, $"N_{sid}", null, ItemKind.File, pos, monitor, false, true);

    private TargetDisplayLayoutRestorePlan MakeRestorePlan(
        DesktopIconSnapshotItem[] snaps, DesktopIconDescriptor[] lives)
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow,
            TD, ConnectionMethod.FindWindowSW, 1, snaps);
        var read = new DesktopIconReadResult(lives, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        return TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, read,
            new[] { MakeDisplay() }, MakeCaps(), TD);
    }

    private static TargetDisplayLayoutExecutionCoordinator MakeCoordinator(
        FakeDesktopIconReader reader, ScriptedDesktopIconPositionWriter writer,
        InMemoryDesktopLayoutSnapshotStore store) =>
        new(new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

    // ═══════════════════════════════════════════════
    //  ScopeGate: Single icon direct move
    // ═══════════════════════════════════════════════

    [Fact]
    public void ScopeGate_SingleDirectMove_IsAllowed()
    {
        var (rp, ep) = MakePlans(
            [Snap("a", new(240, 320))],
            [Live("a", new(160, 240))]);
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
        Assert.True(result.IsAllowed);
        Assert.Empty(result.RejectionReasons);
    }

    [Fact]
    public void ScopeGate_StagingPlan_IsRejected()
    {
        // Two icons that form a cycle → staging required
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 240, 80),
            new PixelRect(0, 0, 240, 80), 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD,
            ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, new PixelPoint(80, 0), TD, false, true),
             new DesktopIconSnapshotItem(1, "b", "N_b", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);
        var live = new DesktopIconReadResult(
            [Live("a", new(0, 0)), Live("b", new(80, 0))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, live, new[] { display }, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, live, new[] { display }, MakeCaps());

        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("RequiresTemporaryStaging")
            || r.Contains("UsesTemporaryStaging"));
    }

    [Fact]
    public void ScopeGate_NoOp_IsRejected()
    {
        var (rp, ep) = MakePlans(
            [Snap("a", new(160, 240))],
            [Live("a", new(160, 240))]);
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("IsNoOp"));
    }

    [Fact]
    public void ScopeGate_HasBlockingErrors_IsRejected()
    {
        // Create a plan with a system item that will be blocked
        var snap = new DesktopIconSnapshotItem(_ord++, "sys", "N_sys", null,
            ItemKind.File, new PixelPoint(240, 320), TD, true, false);
        var descriptor = new DesktopIconDescriptor("sys", "N_sys", null, ItemKind.File,
            new PixelPoint(160, 240), TD, true, false);
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow,
            TD, ConnectionMethod.FindWindowSW, 1, [snap]);
        var read = new DesktopIconReadResult([descriptor], 0,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, read,
            new[] { MakeDisplay() }, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read,
            new[] { MakeDisplay() }, MakeCaps());

        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("HasBlockingErrors"));
    }

    [Fact]
    public void ScopeGate_MissingItem_IsRejected()
    {
        var (rp, ep) = MakePlans([Snap("a", new(240, 320))], []);
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("MissingCount"));
    }

    // ═══════════════════════════════════════════════
    //  ScopeGate: Multiple independent moves
    // ═══════════════════════════════════════════════

    [Fact]
    public void ScopeGate_TwoIndependentMoves_IsAllowed()
    {
        var (rp, ep) = MakePlans(
            [Snap("a", new(240, 320)), Snap("b", new(320, 400))],
            [Live("a", new(160, 240)), Live("b", new(240, 320))]);
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void ScopeGate_ThreeIndependentMoves_IsAllowed()
    {
        var (rp, ep) = MakePlans(
            [Snap("a", new(160, 80)), Snap("b", new(240, 80)), Snap("c", new(320, 80))],
            [Live("a", new(80, 80)), Live("b", new(160, 80)), Live("c", new(240, 80))]);
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
        Assert.True(result.IsAllowed);
    }

    // ═══════════════════════════════════════════════
    //  ScopeGate: DAG without staging
    // ═══════════════════════════════════════════════

    [Fact]
    public void ScopeGate_DagChain_IsAllowed()
    {
        // a→b→c: each depends on the previous moving first
        // a: (80,80)→(160,80), b: (160,80)→(240,80), c: (240,80)→(320,80)
        var (rp, ep) = MakePlans(
            [Snap("a", new(160, 80)), Snap("b", new(240, 80)), Snap("c", new(320, 80))],
            [Live("a", new(80, 80)), Live("b", new(160, 80)), Live("c", new(240, 80))]);
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void ScopeGate_NonFileSystemItem_IsRejected()
    {
        // Construct a plan with a MoveRequired item that's not a file system item
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320),
                new PixelPoint(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired,
                isFileSystemItem: false, isSystemItem: false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
                new PixelPoint(160, 240), new PixelPoint(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 1, 0, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("IsFileSystemItem"));
    }

    [Fact]
    public void ScopeGate_MoveRequiredCountMismatch_IsRejected()
    {
        // Declare MoveRequiredCount=2 but only 1 actual item with MoveRequired state
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320),
                new PixelPoint(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
            new TargetDisplayLayoutRestorePlanItem("b", "N_b", new(400, 480),
                new PixelPoint(400, 480), TD,
                TargetDisplayLayoutRestoreItemState.AlreadyAtTarget, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
                new PixelPoint(160, 240), new PixelPoint(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 2, 1, 2, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("不一致"));
    }

    [Fact]
    public void ScopeGate_StableIdMismatchBetweenStepAndItem_IsRejected()
    {
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320),
                new PixelPoint(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "WRONG", "N_a",
                new PixelPoint(160, 240), new PixelPoint(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 1, 0, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("WRONG"));
    }

    [Fact]
    public void ScopeGate_StepKindNotMoveToTarget_IsRejected()
    {
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320),
                new PixelPoint(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
                new PixelPoint(160, 240), new PixelPoint(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 1, 0, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("MoveToTarget"));
    }

    [Fact]
    public void ScopeGate_StepCountMismatch_IsRejected()
    {
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320),
                new PixelPoint(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
            new TargetDisplayLayoutRestorePlanItem("b", "N_b", new(400, 480),
                new PixelPoint(320, 400), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
                new PixelPoint(160, 240), new PixelPoint(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 2, 0, 2, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a", "b" });
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("Steps.Count"));
    }

    [Fact]
    public void ScopeGate_ZeroLengthStep_IsRejected()
    {
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320),
                new PixelPoint(240, 320), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
                new PixelPoint(240, 320), new PixelPoint(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 1, 0, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("零长度"));
    }

    // ═══════════════════════════════════════════════
    //  ScopeGate: DAG simulation — blocked target
    // ═══════════════════════════════════════════════

    [Fact]
    public void ScopeGate_DagSimulation_TargetBlockedByAlreadyAtTarget_IsRejected()
    {
        // "a" is AlreadyAtTarget at (160,80), "b" wants to move to (160,80)
        // → target occupied by non-moving icon → simulation should reject
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(160, 80),
                new PixelPoint(160, 80), TD,
                TargetDisplayLayoutRestoreItemState.AlreadyAtTarget, true, false),
            new TargetDisplayLayoutRestorePlanItem("b", "N_b", new(160, 80),
                new PixelPoint(80, 80), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "b", "N_b",
                new PixelPoint(80, 80), new PixelPoint(160, 80),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 2, 1, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "b" });
        var result = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("占用") && r.Contains("无法直接移动"));
    }

    // ═══════════════════════════════════════════════
    //  Coordinator: Single icon execution succeeds
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_SingleIcon_WithBatchGate_Succeeds()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Single(result.ForwardAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess,
            result.ForwardAttempts[0].Outcome);
        Assert.NotNull(result.Session);
        Assert.NotEqual(Guid.Empty, result.SafetySnapshotId);
    }

    // ═══════════════════════════════════════════════
    //  Coordinator: Multi-icon independent execution
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_TwoIndependentIcons_WithBatchGate_Succeeds()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 80), new(240, 80));
        writer.ScriptExactResult("b", new(320, 80), new(400, 80));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Equal(2, result.ForwardAttempts.Count);
        Assert.All(result.ForwardAttempts,
            a => Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess, a.Outcome));
    }

    // ═══════════════════════════════════════════════
    //  Coordinator: DAG chain execution
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_DagChain_WithBatchGate_Succeeds()
    {
        // a→b: b's target is a's current position
        var plan = MakeRestorePlan(
            [Snap("a", new(160, 80)), Snap("b", new(240, 80))],
            [Live("a", new(80, 80)), Live("b", new(160, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        // Execution order: b first (frees a's target), then a
        writer.ScriptExactResult("b", new(160, 80), new(240, 80));
        writer.ScriptExactResult("a", new(80, 80), new(160, 80));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(80, 80)), Live("b", new(160, 80))]),
            writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Equal(2, result.ForwardAttempts.Count);
    }

    // ═══════════════════════════════════════════════
    //  Staging plan: rejected by batch gate
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_StagingPlan_RejectedByBatchGate_NoWriterCall()
    {
        // Two icons that form a cycle → staging
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 240, 80),
            new PixelRect(0, 0, 240, 80), 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD,
            ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, new PixelPoint(80, 0), TD, false, true),
             new DesktopIconSnapshotItem(1, "b", "N_b", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);
        var live = new DesktopIconReadResult(
            [Live("a", new(0, 0)), Live("b", new(80, 0))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, live, new[] { display }, MakeCaps(), TD);

        var writer = new ScriptedDesktopIconPositionWriter();
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader(live), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(rp,
            (rp2, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp2, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal("ExecutionScopeRejected", result.ForwardFailureType);
        Assert.Equal(0, writer.AttemptCount);
        Assert.Equal(0, store.SaveCount);
    }

    // ═══════════════════════════════════════════════
    //  Stale input → Writer doesn't start
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_FreshReadHasStaleInput_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        // RestorePlan was built for icon at (160,240), but fresh reader returns (999,999)
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        // Fresh reader returns different position → ExecutionPlanner will reject as stale
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(999, 999))]),
            writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal(0, writer.AttemptCount);
    }

    // ═══════════════════════════════════════════════
    //  Safety snapshot failure → Writer doesn't start
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_SnapshotSaveFailure_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore
            { ShouldThrowOnSave = true, SaveException = new InvalidOperationException("disk full") };
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal(0, writer.AttemptCount);
    }

    // ═══════════════════════════════════════════════
    //  Forward exact + independent verification
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_AllExact_VerifierConfirmsPositions()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);

        // Independent verification: fresh read shows icon at target position
        var freshRead = new DesktopIconReadResult(
            [Live("a", new(240, 320))], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };
        var safetySnapshot = new DesktopLayoutSnapshot(
            result.SafetySnapshotId!.Value, DateTimeOffset.UtcNow, TD,
            ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File,
                new PixelPoint(160, 240), TD, false, true)]);

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, safetySnapshot);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Passed,
            verification.Outcome);
        Assert.True(verification.AllPassed);
    }

    // ═══════════════════════════════════════════════
    //  Exact but final position or monitor wrong
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Verifier_WriterExactButPositionWrong_ReportsFailed()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);

        // Verify with wrong position in fresh read
        var freshRead = new DesktopIconReadResult(
            [Live("a", new(999, 999))], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, null);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Failed,
            verification.Outcome);
        Assert.False(verification.AllPassed);
    }

    [Fact]
    public async Task Verifier_WriterExactButMonitorWrong_ReportsFailed()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);

        // Verify with wrong monitor in fresh read
        var freshRead = new DesktopIconReadResult(
            [LiveOnMonitor("a", new(240, 320), @"\\.\DISPLAY2")],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, null);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Failed,
            verification.Outcome);
    }

    // ═══════════════════════════════════════════════
    //  Forward failure + exact rollback
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_ForwardInexact_RollsBackAndReportsFailedAndRolledBack()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Single(result.RollbackAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess,
            result.RollbackAttempts[0].Outcome);
        Assert.False(result.RequiresManualRecovery);
    }

    // ═══════════════════════════════════════════════
    //  Rollback exact but position/monitor not restored
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Verifier_RollbackExactButPositionNotRestored_ReportsFailed()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);

        // Verify with wrong position in fresh read
        var freshRead = new DesktopIconReadResult(
            [Live("a", new(999, 999))], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };
        var safetySnapshot = new DesktopLayoutSnapshot(
            result.SafetySnapshotId!.Value, DateTimeOffset.UtcNow, TD,
            ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File,
                new PixelPoint(160, 240), TD, false, true)]);

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, safetySnapshot);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Failed,
            verification.Outcome);
    }

    // ═══════════════════════════════════════════════
    //  Verifier: incomplete read, missing StableId, duplicate StableId
    // ═══════════════════════════════════════════════

    [Fact]
    public void Verifier_IncompleteRead_ReturnsInconclusive()
    {
        var result = TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite("X", "Y");
        var freshRead = new DesktopIconReadResult(
            new List<DesktopIconDescriptor>(), 3,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, null);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Inconclusive,
            verification.Outcome);
        Assert.Contains(verification.GlobalIssues,
            i => i.Contains("不完整"));
    }

    [Fact]
    public async Task Verifier_CompletedButIconMissing_ReportsFailed()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);

        // Verify with missing icon in fresh read
        var freshRead = new DesktopIconReadResult(
            new List<DesktopIconDescriptor>(), 0,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, null);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Failed,
            verification.Outcome);
        Assert.Contains(verification.Evidences,
            e => e.StableId == "a" && !e.Found);
    }

    [Fact]
    public void Verifier_DuplicateStableId_ReportsGlobalIssue()
    {
        // Create a Completed RunResult manually
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320),
                new PixelPoint(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
        };
        var rp = new TargetDisplayLayoutRestorePlan(TD, 1, 0, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var ep = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
                new PixelPoint(160, 240), new PixelPoint(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var session = new TargetDisplayLayoutRecoverySession(
            Guid.NewGuid(), Guid.NewGuid(), TD, DateTimeOffset.UtcNow,
            rp, ep, new[] { 0 }, Array.Empty<int>(), null, false, false, "Completed");
        var result = new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.Completed, true, false,
            session, Guid.NewGuid(),
            new[] { TargetDisplayLayoutExecutionAttemptResult.FromExactForward(0, 0,
                ep.Steps[0],
                new DesktopIconMoveResult("a", new PixelPoint(160, 240),
                    new PixelPoint(240, 320), new PixelPoint(240, 320),
                    ConnectionMethod.FindWindowSW, null)) },
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            null, false, false, null, null, null, null);

        // Duplicate StableId in fresh read
        var freshRead = new DesktopIconReadResult(
            [Live("a", new(240, 320)), Live("a", new(999, 999))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, null);

        Assert.Contains(verification.GlobalIssues,
            i => i.Contains("重复"));
    }

    // ═══════════════════════════════════════════════
    //  Verifier: PositionUncertain → Inconclusive
    // ═══════════════════════════════════════════════

    [Fact]
    public void Verifier_PositionUncertain_ReturnsInconclusive()
    {
        var result = new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
            false, false, null, Guid.NewGuid(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            0, true, true, "TestException", "boom", null, null);
        var freshRead = new DesktopIconReadResult(
            [Live("a", new(240, 320))], 0,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, null);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Inconclusive,
            verification.Outcome);
        Assert.Contains(verification.GlobalIssues,
            i => i.Contains("PositionUncertain"));
    }

    [Fact]
    public void Verifier_FailedBeforeWrite_ReturnsNotNeeded()
    {
        var result = TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite("X", "Y");
        var freshRead = new DesktopIconReadResult(
            new List<DesktopIconDescriptor>(), 0,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, null);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.NotNeeded,
            verification.Outcome);
    }

    // ═══════════════════════════════════════════════
    //  Verifier: FailedRollbackIncomplete → Inconclusive
    // ═══════════════════════════════════════════════

    [Fact]
    public void Verifier_FailedRollbackIncomplete_ReturnsInconclusive()
    {
        var result = new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
            false, false, null, Guid.NewGuid(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            0, true, false, "Fwd", "fwd msg", "Rb", "rb msg");
        var freshRead = new DesktopIconReadResult(
            [Live("a", new(240, 320))], 0,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, null);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Inconclusive,
            verification.Outcome);
    }

    // ═══════════════════════════════════════════════
    //  Rollback verifier confirmation (happy path)
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Verifier_RollbackAllExact_ConfirmsRestoration()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);

        // Fresh read confirms icon at original position
        var freshRead = new DesktopIconReadResult(
            [Live("a", new(160, 240))], 0,
            ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };
        var safetySnapshot = new DesktopLayoutSnapshot(
            result.SafetySnapshotId!.Value, DateTimeOffset.UtcNow, TD,
            ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File,
                new PixelPoint(160, 240), TD, false, true)]);

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, safetySnapshot);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Passed,
            verification.Outcome);
    }

    // ═══════════════════════════════════════════════
    //  Writer exception → PositionUncertain (no rollback)
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_ForwardWriterException_ReturnsPositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptException("a", new(240, 320), new InvalidOperationException("BOOM"));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.Empty(result.RollbackAttempts);
    }

    // ═══════════════════════════════════════════════
    //  Rollback exception → PositionUncertain
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_RollbackException_ReturnsPositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptException("a", new(160, 240), new InvalidOperationException("rollback fail"));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
    }

    // ═══════════════════════════════════════════════
    //  Pre-Writer exception → FailedBeforeWrite (NOT PositionUncertain)
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_PlannerException_ReturnsFailedBeforeWrite_NotPositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            store,
            (_, _, _, _) => throw new InvalidOperationException("planner BOMB"));

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.Equal(0, writer.AttemptCount);
    }

    // ═══════════════════════════════════════════════
    //  Cancellation after writer completes
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_CancellationDuringForwardWriter_RollsBack()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.ScriptBlockingExact("a", new(240, 80), new(160, 80), started, release);
        writer.ScriptExactResult("a", new(240, 80), new(160, 80));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, store);

        using var cts = new CancellationTokenSource();
        var task = coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            },
            cts.Token);

        await started.Task;
        cts.Cancel();
        release.TrySetResult();

        var result = await task;
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
    }

    // ═══════════════════════════════════════════════
    //  Previous diagnostics don't pollute current
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Coordinator_PreviousFailureDoesNotPolluteNextSuccess()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);

        // First run: failure
        var writer1 = new ScriptedDesktopIconPositionWriter();
        writer1.ScriptException("a", new(240, 320), new InvalidOperationException("BOOM"));
        var store1 = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator1 = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer1, store1);
        var result1 = await coordinator1.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result1.Status);

        // Second run: success (fresh coordinator, fresh writer)
        var writer2 = new ScriptedDesktopIconPositionWriter();
        writer2.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store2 = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator2 = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer2, store2);
        var result2 = await coordinator2.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result2.Status);
        // Verify no stale state from first run
        Assert.NotEqual(result1.SafetySnapshotId, result2.SafetySnapshotId);
        Assert.NotEqual(result1.Session?.SessionId, result2.Session?.SessionId);
    }

    // ═══════════════════════════════════════════════
    //  AlreadyAtTarget externally moved during execution (verifier detects)
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task Verifier_AlreadyAtTargetMoved_IsDetected()
    {
        // Plan: "a" is at target, "b" needs to move
        var plan = MakeRestorePlan(
            [Snap("a", new(160, 240)), Snap("b", new(320, 400))],
            [Live("a", new(160, 240)), Live("b", new(240, 320))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("b", new(240, 320), new(320, 400));
        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = MakeCoordinator(
            new FakeDesktopIconReader([Live("a", new(160, 240)), Live("b", new(240, 320))]),
            writer, store);

        var result = await coordinator.ExecuteWithScopeGateAsync(plan,
            (rp, ep) =>
            {
                var core = DesktopLayoutRuleAwareExecutionScopeGate.EvaluateDirectExecution(rp, ep);
                return core.IsAllowed
                    ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                    : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                        core.RejectionReasons.ToList());
            });

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);

        // "b" moved to target, but "a" was moved externally to a different position
        var freshRead = new DesktopIconReadResult(
            [Live("a", new(999, 999)), Live("b", new(320, 400))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };

        var verification = DesktopLayoutPostExecutionVerifier.Verify(
            result, freshRead, displays, null);

        Assert.Equal(DesktopLayoutPostExecutionVerifier.VerificationOutcome.Failed,
            verification.Outcome);
        Assert.Contains(verification.Evidences,
            e => e.StableId == "a" && !e.Passed);
    }

    // ═══════════════════════════════════════════════
    //  Helper: plan builder
    // ═══════════════════════════════════════════════

    private (TargetDisplayLayoutRestorePlan, TargetDisplayLayoutExecutionPlan) MakePlans(
        DesktopIconSnapshotItem[] snaps, DesktopIconDescriptor[] lives)
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow,
            TD, ConnectionMethod.FindWindowSW, 1, snaps);
        var read = new DesktopIconReadResult(lives, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeDisplay() };
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, read, displays, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read, displays, MakeCaps());
        return (rp, ep);
    }
}
