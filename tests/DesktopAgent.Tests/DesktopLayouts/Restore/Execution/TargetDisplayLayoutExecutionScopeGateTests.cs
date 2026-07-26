using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class TargetDisplayLayoutExecutionScopeGateTests
{
    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelPoint Sp = new(80, 80);
    private static readonly PixelRect WA = new(0, 0, 800, 600);
    private static int _ord;
    public TargetDisplayLayoutExecutionScopeGateTests() { _ord = 0; }

    private static DesktopIconSnapshotItem Snap(string sid, PixelPoint pos) =>
        new(_ord++, sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    private static DesktopIconDescriptor Live(string sid, PixelPoint pos) =>
        new(sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    private static DisplayDescriptor MakeD() =>
        new(TD, new PixelRect(0, 0, 1920, 1080), WA, 96, 96, true);

    private static DesktopIconPositioningCapabilities MakeCaps() =>
        new(false, Sp, ConnectionMethod.FindWindowSW, null);

    private (TargetDisplayLayoutRestorePlan, TargetDisplayLayoutExecutionPlan) MakePlans(
        DesktopIconSnapshotItem[] snaps, DesktopIconDescriptor[] lives)
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow,
            TD, ConnectionMethod.FindWindowSW, 1, snaps);
        var read = new DesktopIconReadResult(lives, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var displays = new[] { MakeD() };
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, read, displays, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read, displays, MakeCaps());
        return (rp, ep);
    }

    [Fact] public void SingleDirectMove_IsAllowed()
    {
        var (rp, ep) = MakePlans(
            [Snap("a", new(240, 320))],
            [Live("a", new(160, 240))]);
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, ep);
        Assert.True(result.IsAllowed);
        Assert.Empty(result.RejectionReasons);
    }

    [Fact] public void NoOp_IsRejected()
    {
        var (rp, ep) = MakePlans(
            [Snap("a", new(160, 240))],
            [Live("a", new(160, 240))]);
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, ep);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("IsNoOp"));
    }

    [Fact] public void MultipleMoveItems_AreRejected()
    {
        var (rp, ep) = MakePlans(
            [Snap("a", new(240, 320)), Snap("b", new(320, 400))],
            [Live("a", new(160, 240)), Live("b", new(240, 320))]);
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, ep);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("MoveRequiredCount"));
    }

    [Fact] public void TemporaryStaging_IsRejected()
    {
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
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, ep);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("UsesTemporaryStaging"));
    }

    [Fact] public void MultipleExecutionSteps_AreRejected()
    {
        var (rp, ep) = MakePlans(
            [Snap("a", new(160, 80)), Snap("b", new(240, 80))],
            [Live("a", new(80, 80)), Live("b", new(160, 80))]);
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, ep);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("Steps.Count"));
    }

    [Fact] public void MissingOrBlockedItems_AreRejected()
    {
        // Missing item
        var (rp, ep) = MakePlans([Snap("a", new(240, 320))], []);
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, ep);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("MissingCount"));
    }

    [Fact] public void NonFileSystemOrSystemItem_IsRejected()
    {
        // Construct a plan with a MoveRequired item that's a system item
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320), new(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, isFileSystemItem: false, isSystemItem: true),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a", new(160, 240), new(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 1, 0, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("IsFileSystemItem") || r.Contains("IsSystemItem"));
    }

    [Fact] public void StableIdMismatch_IsRejected()
    {
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320), new(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "WRONG", "N_a", new(160, 240), new(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 1, 0, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("StableId"));
    }

    [Fact] public void CoordinateMismatch_IsRejected()
    {
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320), new(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a", new(999, 999), new(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 1, 0, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("FromPosition"));
    }

    [Fact] public void DeclaredMoveRequiredCountDoesNotMatchItems_IsRejected()
    {
        // Declare MoveRequiredCount=2 but only 1 actual MoveRequired item
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320), new(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
            new TargetDisplayLayoutRestorePlanItem("b", "N_b", new(400, 480), new(400, 480), TD,
                TargetDisplayLayoutRestoreItemState.AlreadyAtTarget, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a", new(160, 240), new(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 2, 1, 2, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("MoveRequiredCount") && r.Contains("不一致"));
    }

    [Fact] public void DeclaredOneButActualZeroMoveItems_IsRejectedWithoutThrow()
    {
        // MoveRequiredCount=1 but 0 items with MoveRequired state
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320), new(240, 320), TD,
                TargetDisplayLayoutRestoreItemState.AlreadyAtTarget, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a", new(240, 320), new(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 1, 1, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a" });
        // Must not throw — uses .ToList() not First()
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, plan);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, r => r.Contains("实际") && r.Contains("预期 1"));
    }

    [Fact] public void DeclaredOneButActualTwoMoveItems_IsRejectedWithoutThrow()
    {
        // MoveRequiredCount=1 but 2 items with MoveRequired state
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320), new(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
            new TargetDisplayLayoutRestorePlanItem("b", "N_b", new(400, 480), new(320, 400), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, true, false),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N_a", new(160, 240), new(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var rp = new TargetDisplayLayoutRestorePlan(TD, 2, 0, 1, 0, 0, false, false, false,
            items, Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(), new[] { "a", "b" });
        // Must not throw — uses .ToList() not First()
        var result = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, plan);
        Assert.False(result.IsAllowed);
    }
}
