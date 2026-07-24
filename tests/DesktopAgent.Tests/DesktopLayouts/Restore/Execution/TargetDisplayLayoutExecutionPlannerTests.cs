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
    private static DesktopIconPositioningCapabilities MakeCaps() =>
        new(false, Sp, ConnectionMethod.FindWindowSW, null);

    private static DesktopIconSnapshotItem Snap(string sid, PixelPoint pos) =>
        new(_ord++, sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);
    private static DesktopIconDescriptor Live(string sid, PixelPoint pos) =>
        new(sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    // Helper: plan → execution via M1-05A (normal cases)
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

    // Helper: execution from a manually-constructed restore plan (for inconsistency tests)
    private static TargetDisplayLayoutExecutionPlan ExecRaw(
        TargetDisplayLayoutRestorePlan plan,
        DesktopIconReadResult read,
        DisplayDescriptor[] displays = null!,
        DesktopIconPositioningCapabilities caps = null!)
    {
        return TargetDisplayLayoutExecutionPlanner.CreatePlan(
            plan, read, displays ?? new[] { MakeD() }, caps ?? MakeCaps());
    }

    // Helper: construct a consistent plan item
    private static TargetDisplayLayoutRestorePlanItem PlanItem(
        string sid, PixelPoint target, TargetDisplayLayoutRestoreItemState state,
        PixelPoint? currentPos = null, string? monitor = null) =>
        new(sid, $"N_{sid}", target, currentPos, monitor, state, true, false);

    // ═══════════════════════════════════════════
    // 参数验证
    // ═══════════════════════════════════════════

    [Fact] public void NullRestorePlan_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutExecutionPlanner.CreatePlan(null!, MakeRead([]), new[] { MakeD() }, MakeCaps()));
    [Fact] public void NullCurrentRead_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutExecutionPlanner.CreatePlan(MakeRestore([]), null!, new[] { MakeD() }, MakeCaps()));
    [Fact] public void NullDisplays_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutExecutionPlanner.CreatePlan(MakeRestore([]), MakeRead([]), null!, MakeCaps()));
    [Fact] public void NullCapabilities_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutExecutionPlanner.CreatePlan(MakeRestore([]), MakeRead([]), new[] { MakeD() }, null!));

    // ═══════════════════════════════════════════
    // 前置验证
    // ═══════════════════════════════════════════

    [Fact] public void IncompleteRead_Issue()
    {
        var rp = MakeRestore([Snap("a", new(160, 240))]);
        var rd = new DesktopIconReadResult([Live("a", new(160, 240))], 3, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, rd, new[] { MakeD() }, MakeCaps());
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.CurrentReadIncomplete);
        Assert.Empty(ep.Steps);
        Assert.Empty(ep.StagingAssignments);
    }

    [Fact] public void AutoArrange_Issue()
    {
        var rp = MakeRestore([]);
        var caps = new DesktopIconPositioningCapabilities(true, Sp, ConnectionMethod.FindWindowSW, null);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, MakeRead([]), new[] { MakeD() }, caps);
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.AutoArrangeEnabled);
    }

    [Fact] public void SourcePlanBlocking_NoSteps()
    {
        var ep = Exec([Snap("a", new(160, 240)), Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanHasBlockingErrors);
        Assert.Empty(ep.Steps);
    }

    // ═══════════════════════════════════════════
    // 当前桌面防陈旧验证
    // ═══════════════════════════════════════════

    [Fact] public void CurrentStableIdDuplicate_Issue()
    {
        // 1. 用唯一 StableId 生成合法 RestorePlan
        var rp = MakeRestore([Snap("a", new(160, 240))]);
        // 2. 创建新的 currentReadResult，其中同一 StableId 出现两次
        var rd = new DesktopIconReadResult(
            [Live("a", new(160, 240)), Live("a", new(320, 400))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        // 3. 将合法 RestorePlan 和重复的新 currentReadResult 传给 ExecutionPlanner
        var ep = ExecRaw(rp, rd);
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.DuplicateCurrentStableId);
        Assert.Empty(ep.Steps);
        Assert.Empty(ep.StagingAssignments);
    }

    [Fact] public void CurrentPositionChanged_Stale()
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1, [Snap("a", new(240, 320))]);
        var rd1 = new DesktopIconReadResult([Live("a", new(160, 240))], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, rd1, new[] { MakeD() }, MakeCaps(), TD);
        var rd2 = new DesktopIconReadResult([Live("a", new(200, 300))], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, rd2, new[] { MakeD() }, MakeCaps());
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanStale);
    }

    [Fact] public void StableIdComparison_IsOrdinal()
    {
        var ep = Exec([Snap("SID", new(240, 320))], [Live("sid", new(160, 240))]);
        Assert.True(ep.IsExecutable); Assert.False(ep.IsNoOp); Assert.Empty(ep.Steps);
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

    [Fact] public void OverlappingFinalTargets_Issue()
    {
        var ep = Exec(
            [Snap("a", new(160, 240)), Snap("b", new(200, 240))],
            [Live("a", new(80, 240)), Live("b", new(400, 400))]);
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.OverlappingFinalTargets);
    }

    // ═══════════════════════════════════════════
    // 源计划摘要重新计算验证
    // ═══════════════════════════════════════════

    [Fact] public void SourcePlanItemCountMismatch_ReturnsIssue()
    {
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 99, alreadyAtTargetCount: 0, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: true, items: Array.Empty<TargetDisplayLayoutRestorePlanItem>(),
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("SnapshotTargetIconCount"));
    }

    [Fact] public void SourcePlanAlreadyCountMismatch_ReturnsIssue()
    {
        var items = new[] { PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.AlreadyAtTarget,
            new(160, 240), TD) };
        // AlreadyAtTargetCount should be 1 but we pass 0
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("AlreadyAtTargetCount"));
    }

    [Fact] public void SourcePlanMoveCountMismatch_ReturnsIssue()
    {
        var items = new[] { PlanItem("a", new(240, 320), TargetDisplayLayoutRestoreItemState.MoveRequired,
            new(160, 240), TD) };
        // MoveRequiredCount should be 1 but we pass 99
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 99,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a" });
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("MoveRequiredCount"));
    }

    [Fact] public void SourcePlanMissingCountMismatch_ReturnsIssue()
    {
        var items = new[] { PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.MissingFromCurrent) };
        // MissingCount should be 1 but we pass 0
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("MissingCount"));
    }

    [Fact] public void SourcePlanBlockedCountMismatch_ReturnsIssue()
    {
        var items = new[] { PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.DuplicateCurrentStableId) };
        // BlockedCount should be 1 but we pass 0
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: true, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("BlockedCount"));
    }

    [Fact] public void SourcePlanBlockingStateButFlagFalse_ReturnsIssue()
    {
        var items = new[] { PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.DuplicateCurrentStableId) };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 1, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("HasBlockingErrors"));
    }

    // ═══════════════════════════════════════════
    // AlreadyAtTarget 内部一致性
    // ═══════════════════════════════════════════

    [Fact] public void AlreadyAtTargetMissingCurrentPosition_ReturnsIssue()
    {
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.AlreadyAtTarget,
                currentPos: null, monitor: TD)
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 1, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("CurrentPosition"));
    }

    [Fact] public void AlreadyAtTargetMissingMonitor_ReturnsIssue()
    {
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.AlreadyAtTarget,
                currentPos: new(160, 240), monitor: null)
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 1, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("CurrentMonitorDeviceName"));
    }

    [Fact] public void AlreadyAtTargetPositionNotAtTarget_ReturnsIssue()
    {
        var items = new[]
        {
            PlanItem("a", new(240, 320), TargetDisplayLayoutRestoreItemState.AlreadyAtTarget,
                currentPos: new(160, 240), monitor: TD)
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 1, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("AlreadyAtTarget") && i.Message.Contains("SnapshotTargetPosition"));
    }

    [Fact] public void AlreadyAtTargetMonitorNotTarget_ReturnsIssue()
    {
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.AlreadyAtTarget,
                currentPos: new(160, 240), monitor: @"\\.\DISPLAY2")
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 1, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("TargetDeviceName"));
    }

    [Fact] public void MoveRequiredActuallyAtTarget_ReturnsIssue()
    {
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(160, 240), monitor: TD)
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 1,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a" });
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("MoveRequired") && i.Message.Contains("已经在目标位置"));
    }

    // ═══════════════════════════════════════════
    // IsNoOp 一致性
    // ═══════════════════════════════════════════

    [Fact] public void SourcePlanIsNoOpFalseButEverythingAtTarget_ReturnsIssue()
    {
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.AlreadyAtTarget,
                currentPos: new(160, 240), monitor: TD)
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 1, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("IsNoOp"));
        Assert.Empty(ep.Steps);
        Assert.Empty(ep.StagingAssignments);
    }

    [Fact] public void SourcePlanIsNoOpTrueButHasMissingItem_ReturnsIssue()
    {
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.MissingFromCurrent)
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 0,
            missingCount: 1, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: true, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("IsNoOp"));
        Assert.Empty(ep.Steps);
        Assert.Empty(ep.StagingAssignments);
    }

    // ═══════════════════════════════════════════
    // MoveRequired 资格
    // ═══════════════════════════════════════════

    [Fact] public void MoveRequired_NonFileSystemPlanItem_ReturnsInconsistent()
    {
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320), new(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, isFileSystemItem: false, isSystemItem: false),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 1,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a" });
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("IsFileSystemItem"));
        Assert.Empty(ep.Steps);
        Assert.Empty(ep.StagingAssignments);
    }

    [Fact] public void MoveRequired_SystemPlanItem_ReturnsInconsistent()
    {
        var items = new[]
        {
            new TargetDisplayLayoutRestorePlanItem("a", "N_a", new(240, 320), new(160, 240), TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired, isFileSystemItem: true, isSystemItem: true),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 1,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a" });
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("IsSystemItem"));
        Assert.Empty(ep.Steps);
        Assert.Empty(ep.StagingAssignments);
    }

    [Fact] public void CurrentItemBecameNonFileSystem_ReturnsStale()
    {
        var items = new[]
        {
            PlanItem("a", new(240, 320), TargetDisplayLayoutRestoreItemState.MoveRequired,
                new(160, 240), TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 1,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a" });
        // 当前图标不是文件系统项目
        var nonFs = new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new(160, 240), TD,
            IsSystemItem: false, IsFileSystemItem: false);
        var ep = ExecRaw(plan, MakeRead([nonFs]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanStale
            && i.Message.Contains("文件系统"));
        Assert.Empty(ep.Steps);
    }

    [Fact] public void CurrentItemBecameSystemItem_ReturnsStale()
    {
        var items = new[]
        {
            PlanItem("a", new(240, 320), TargetDisplayLayoutRestoreItemState.MoveRequired,
                new(160, 240), TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 1,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a" });
        // 当前图标是系统项目
        var sys = new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new(160, 240), TD,
            IsSystemItem: true, IsFileSystemItem: true);
        var ep = ExecRaw(plan, MakeRead([sys]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanStale
            && i.Message.Contains("系统项目"));
        Assert.Empty(ep.Steps);
    }

    // ═══════════════════════════════════════════
    // 无环路径

    [Fact] public void AcyclicPlan_StrictlyFollowsMoveExecutionOrder()
    {
        // a current(80,80)→(160,80), b current(160,80)→(240,80)
        // MoveExecutionOrder = [b, a] — b 必须先移动
        var ep = Exec(
            [Snap("a", new(160, 80)), Snap("b", new(240, 80))],
            [Live("a", new(80, 80)), Live("b", new(160, 80))]);
        Assert.True(ep.IsExecutable);
        Assert.False(ep.UsesTemporaryStaging);
        Assert.Equal(2, ep.Steps.Count);
        Assert.Equal("b", ep.Steps[0].StableId);
        Assert.Equal("a", ep.Steps[1].StableId);
        Assert.All(ep.Steps, s => Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, s.Kind));
        Assert.Equal(TD, ep.Steps[0].ExpectedMonitorDeviceName);
        Assert.Equal(TD, ep.Steps[1].ExpectedMonitorDeviceName);
    }

    [Fact] public void AcyclicPlan_DoesNotSkipBlockedFirstItem()
    {
        // 手工构造：MoveExecutionOrder = ["a", "b"]，但 a 的目标被 b 占用
        // 无环路径应该返回 SimulationDeadlock，而不是跳过 a
        var items = new[]
        {
            PlanItem("a", new(160, 80), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(80, 80), monitor: TD),
            PlanItem("b", new(240, 80), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(160, 80), monitor: TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 2, alreadyAtTargetCount: 0, moveRequiredCount: 2,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a", "b" }); // 错误顺序：a 先，但 b 占用 a 的目标
        var ep = ExecRaw(plan, MakeRead([Live("a", new(80, 80)), Live("b", new(160, 80))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SimulationDeadlock);
        Assert.Empty(ep.Steps);
    }

    [Fact] public void AcyclicPlan_NeverIntroducesStaging()
    {
        // 正常链：MoveExecutionOrder 正确时不应生成 staging
        var items = new[]
        {
            PlanItem("a", new(160, 80), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(80, 80), monitor: TD),
            PlanItem("b", new(240, 80), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(160, 80), monitor: TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 2, alreadyAtTargetCount: 0, moveRequiredCount: 2,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "b", "a" });
        var ep = ExecRaw(plan, MakeRead([Live("a", new(80, 80)), Live("b", new(160, 80))]));
        Assert.True(ep.IsExecutable);
        Assert.False(ep.UsesTemporaryStaging);
        Assert.Equal(0, ep.StagingMoveStepCount);
        Assert.Empty(ep.StagingAssignments);
        Assert.All(ep.Steps, s => Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, s.Kind));
    }

    [Fact] public void AcyclicPlan_MissingExecutionOrder_ReturnsInconsistent()
    {
        var items = new[]
        {
            PlanItem("a", new(240, 320), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(160, 240), monitor: TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 1,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null); // 缺失
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("MoveExecutionOrder 为 null"));
    }

    [Fact] public void AcyclicPlan_DuplicateExecutionOrderId_ReturnsInconsistent()
    {
        var items = new[]
        {
            PlanItem("a", new(240, 320), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(160, 240), monitor: TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 1,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a", "a" }); // 重复
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("重复"));
    }

    [Fact] public void AcyclicPlan_ExecutionOrderMissingItem_ReturnsInconsistent()
    {
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(80, 240), monitor: TD),
            PlanItem("b", new(320, 400), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(160, 240), monitor: TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 2, alreadyAtTargetCount: 0, moveRequiredCount: 2,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a" }); // 缺失 b
        var ep = ExecRaw(plan, MakeRead([Live("a", new(80, 240)), Live("b", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("数量"));
    }

    [Fact] public void AcyclicPlan_ExecutionOrderHasExtraItem_ReturnsInconsistent()
    {
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(80, 240), monitor: TD),
            PlanItem("b", new(320, 400), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(160, 240), monitor: TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 2, alreadyAtTargetCount: 0, moveRequiredCount: 2,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a", "x" }); // x 不在 MoveRequired 中
        var ep = ExecRaw(plan, MakeRead([Live("a", new(80, 240)), Live("b", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("额外"));
    }

    // ═══════════════════════════════════════════
    // 有环标志一致性
    // ═══════════════════════════════════════════

    [Fact] public void CycleFlag_WithExecutionOrder_ReturnsInconsistent()
    {
        // a(0,0)→(80,0), b(80,0)→(0,0): 互为依赖形成环
        var items = new[]
        {
            PlanItem("a", new(80, 0), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(0, 0), monitor: TD),
            PlanItem("b", new(0, 0), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(80, 0), monitor: TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 2, alreadyAtTargetCount: 0, moveRequiredCount: 2,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: true,
            isNoOp: false, items: items,
            dependencies: new TargetDisplayLayoutRestoreDependency[]
            {
                new("b", "a"), new("a", "b"),
            },
            cycles: new[] { new TargetDisplayLayoutRestoreCycle(new[] { "a", "b" }) },
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a", "b" }); // 有环但提供了 MoveExecutionOrder
        var ep = ExecRaw(plan, MakeRead([Live("a", new(0, 0)), Live("b", new(80, 0))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("MoveExecutionOrder 不为 null"));
    }

    [Fact] public void CycleFlag_WithoutCycles_ReturnsInconsistent()
    {
        // a(0,0)→(80,0), b(80,0)→(0,0): 互为依赖形成环
        var items = new[]
        {
            PlanItem("a", new(80, 0), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(0, 0), monitor: TD),
            PlanItem("b", new(0, 0), TargetDisplayLayoutRestoreItemState.MoveRequired,
                currentPos: new(80, 0), monitor: TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 2, alreadyAtTargetCount: 0, moveRequiredCount: 2,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: true,
            isNoOp: false, items: items,
            dependencies: new TargetDisplayLayoutRestoreDependency[]
            {
                new("b", "a"), new("a", "b"),
            },
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(), // 空环列表（不一致）
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(0, 0)), Live("b", new(80, 0))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("Cycles") && i.Message.Contains("SCC"));
    }

    // ═══════════════════════════════════════════
    // SCC 重新验证
    // ═══════════════════════════════════════════

    [Fact] public void AcyclicFlag_WithCycles_ReturnsInconsistent()
    {
        // 有效环数据但 RequiresTemporaryStaging=false
        var items = new[]
        {
            PlanItem("a", new(80, 0), TargetDisplayLayoutRestoreItemState.MoveRequired,
                new(0, 0), TD),
            PlanItem("b", new(0, 0), TargetDisplayLayoutRestoreItemState.MoveRequired,
                new(80, 0), TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 2, alreadyAtTargetCount: 0, moveRequiredCount: 2,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: new TargetDisplayLayoutRestoreDependency[]
            {
                new("b", "a"), new("a", "b"),
            },
            cycles: new[] { new TargetDisplayLayoutRestoreCycle(new[] { "a", "b" }) },
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(0, 0)), Live("b", new(80, 0))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("RequiresTemporaryStaging"));
    }

    [Fact] public void NoMovePlan_WithCycleFlag_ReturnsInconsistent()
    {
        // 无 MoveRequired 项但 RequiresTemporaryStaging=true
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.AlreadyAtTarget,
                new(160, 240), TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 1, moveRequiredCount: 0,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: true,
            isNoOp: true, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: new[] { new TargetDisplayLayoutRestoreCycle(new[] { "a" }) },
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(160, 240))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("RequiresTemporaryStaging"));
    }

    [Fact] public void CycleFlag_WithNonCyclicDependencies_ReturnsInconsistent()
    {
        // 链依赖（非环）但 RequiresTemporaryStaging=true
        var items = new[]
        {
            PlanItem("a", new(160, 80), TargetDisplayLayoutRestoreItemState.MoveRequired,
                new(80, 80), TD),
            PlanItem("b", new(240, 80), TargetDisplayLayoutRestoreItemState.MoveRequired,
                new(160, 80), TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 2, alreadyAtTargetCount: 0, moveRequiredCount: 2,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: true,
            isNoOp: false, items: items,
            dependencies: new TargetDisplayLayoutRestoreDependency[]
            {
                new("b", "a"), // b→a: b must move first → a can move — no cycle
            },
            cycles: new[] { new TargetDisplayLayoutRestoreCycle(new[] { "a", "b" }) },
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(80, 80)), Live("b", new(160, 80))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("RequiresTemporaryStaging"));
    }

    [Fact] public void CycleMetadata_DiffersFromRecomputedScc_ReturnsInconsistent()
    {
        // 有效环，但 Cycles 元数据与重新计算不一致（缺少一个节点）
        var items = new[]
        {
            PlanItem("a", new(80, 0), TargetDisplayLayoutRestoreItemState.MoveRequired,
                new(0, 0), TD),
            PlanItem("b", new(0, 0), TargetDisplayLayoutRestoreItemState.MoveRequired,
                new(80, 0), TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 2, alreadyAtTargetCount: 0, moveRequiredCount: 2,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: true,
            isNoOp: false, items: items,
            dependencies: new TargetDisplayLayoutRestoreDependency[]
            {
                new("b", "a"), new("a", "b"),
            },
            cycles: new[] { new TargetDisplayLayoutRestoreCycle(new[] { "a" }) }, // 只有 "a"，缺少 "b"
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);
        var ep = ExecRaw(plan, MakeRead([Live("a", new(0, 0)), Live("b", new(80, 0))]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanInconsistent
            && i.Message.Contains("restorePlan.Cycles"));
    }

    [Fact] public void ValidCycles_MatchSharedAnalyzer()
    {
        // 验证有效环通过 SCC 重新验证
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 240, 80), new PixelRect(0, 0, 240, 80), 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, new PixelPoint(80, 0), TD, false, true),
             new DesktopIconSnapshotItem(1, "b", "N_b", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);
        var read = new DesktopIconReadResult([Live("a", new(0, 0)), Live("b", new(80, 0))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, read, new[] { display }, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read, new[] { display }, MakeCaps());
        Assert.True(ep.IsExecutable);
        Assert.True(ep.UsesTemporaryStaging);
        Assert.Equal(3, ep.Steps.Count);
    }

    // ═══════════════════════════════════════════
    // 同坐标不同显示器
    // ═══════════════════════════════════════════

    [Fact] public void AcyclicSamePositionDifferentMonitor_ReturnsFailure()
    {
        var items = new[]
        {
            PlanItem("a", new(160, 240), TargetDisplayLayoutRestoreItemState.MoveRequired,
                new(160, 240), @"\\.\DISPLAY2"), // 同坐标不同显示器
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, snapshotTargetIconCount: 1, alreadyAtTargetCount: 0, moveRequiredCount: 1,
            missingCount: 0, blockedCount: 0, hasBlockingErrors: false, requiresTemporaryStaging: false,
            isNoOp: false, items: items,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: new[] { "a" });
        // 当前在 DISPLAY2
        var live = new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new(160, 240), @"\\.\DISPLAY2",
            false, true);
        var ep = ExecRaw(plan, MakeRead([live]));
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SimulationDeadlock
            && i.Message.Contains("同坐标跨显示器"));
        Assert.Equal("a", ep.Issues.First(i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SimulationDeadlock).RelatedStableId);
        Assert.Empty(ep.Steps);
        Assert.Empty(ep.StagingAssignments);
    }

    [Fact] public void FinalSimulation_VerifiesTargetMonitor()
    {
        // 正常两图标交换，最终验证包含显示器
        var ep = Exec(
            [Snap("a", new(320, 400)), Snap("b", new(160, 240))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        Assert.True(ep.IsExecutable);
        // 所有步骤的目标显示器都是 TargetDeviceName
        Assert.All(ep.Steps, s => Assert.Equal(TD, s.ExpectedMonitorDeviceName));
        // 每个 MoveToTarget / MoveFromTemporaryStaging 的 ToPosition 不等于 FromPosition
        Assert.All(ep.Steps, s => Assert.NotEqual(s.FromPosition, s.ToPosition));
    }

    // ═══════════════════════════════════════════
    // 基础执行
    // ═══════════════════════════════════════════

    [Fact] public void NoMoveItems_IsNoOp()
    {
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
        var stagedId = ep.Steps[0].StableId;
        var stagedInitial = stagedId == "a" ? new PixelPoint(160, 240) : new PixelPoint(320, 400);
        Assert.Equal(stagedInitial, ep.Steps[0].FromPosition);
        var otherId = ep.Steps[1].StableId;
        var otherInitial = otherId == "a" ? new PixelPoint(160, 240) : new PixelPoint(320, 400);
        Assert.Equal(otherInitial, ep.Steps[1].FromPosition);
        var sa = ep.StagingAssignments[0];
        Assert.Equal(sa.StagingPosition, ep.Steps[2].FromPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, ep.Steps[2].Kind);
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

    // ═══════════════════════════════════════════
    // 环与中转
    // ═══════════════════════════════════════════

    [Fact] public void TwoItemSwap_ProducesThreeSteps()
    {
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
        Assert.Equal(TD, ep.Steps[0].ExpectedMonitorDeviceName);
        // Step 1: b → target
        Assert.Equal(1, ep.Steps[1].StepIndex); Assert.Equal("b", ep.Steps[1].StableId);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[1].FromPosition);
        Assert.Equal(new PixelPoint(0, 0), ep.Steps[1].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[1].Kind);
        Assert.Equal(TD, ep.Steps[1].ExpectedMonitorDeviceName);
        // Step 2: a → target
        Assert.Equal(2, ep.Steps[2].StepIndex); Assert.Equal("a", ep.Steps[2].StableId);
        Assert.Equal(new PixelPoint(160, 0), ep.Steps[2].FromPosition);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[2].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, ep.Steps[2].Kind);
        Assert.Equal(TD, ep.Steps[2].ExpectedMonitorDeviceName);
        // Staging
        var sa = ep.StagingAssignments[0];
        Assert.Equal("a", sa.StableId); Assert.Equal(new PixelPoint(160, 0), sa.StagingPosition);
        Assert.Equal(0, sa.EnterStepIndex); Assert.Equal(2, sa.ExitStepIndex);
    }

    [Fact] public void ThreeItemCycle_FourSteps()
    {
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
        Assert.Equal(4, ep.Steps.Count); Assert.Single(ep.StagingAssignments);
        // Step 0: a → staging (240,0)
        Assert.Equal(0, ep.Steps[0].StepIndex); Assert.Equal("a", ep.Steps[0].StableId);
        Assert.Equal("N_a", ep.Steps[0].DisplayName);
        Assert.Equal(new PixelPoint(0, 0), ep.Steps[0].FromPosition);
        Assert.Equal(new PixelPoint(240, 0), ep.Steps[0].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, ep.Steps[0].Kind);
        Assert.Equal(TD, ep.Steps[0].ExpectedMonitorDeviceName);
        // Step 1: c → target (0,0)
        Assert.Equal(1, ep.Steps[1].StepIndex); Assert.Equal("c", ep.Steps[1].StableId);
        Assert.Equal("N_c", ep.Steps[1].DisplayName);
        Assert.Equal(new PixelPoint(160, 0), ep.Steps[1].FromPosition);
        Assert.Equal(new PixelPoint(0, 0), ep.Steps[1].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[1].Kind);
        Assert.Equal(TD, ep.Steps[1].ExpectedMonitorDeviceName);
        // Step 2: b → target (160,0)
        Assert.Equal(2, ep.Steps[2].StepIndex); Assert.Equal("b", ep.Steps[2].StableId);
        Assert.Equal("N_b", ep.Steps[2].DisplayName);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[2].FromPosition);
        Assert.Equal(new PixelPoint(160, 0), ep.Steps[2].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[2].Kind);
        Assert.Equal(TD, ep.Steps[2].ExpectedMonitorDeviceName);
        // Step 3: a → target (80,0)
        Assert.Equal(3, ep.Steps[3].StepIndex); Assert.Equal("a", ep.Steps[3].StableId);
        Assert.Equal("N_a", ep.Steps[3].DisplayName);
        Assert.Equal(new PixelPoint(240, 0), ep.Steps[3].FromPosition);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[3].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, ep.Steps[3].Kind);
        Assert.Equal(TD, ep.Steps[3].ExpectedMonitorDeviceName);
        // StagingAssignment
        var sa = ep.StagingAssignments[0];
        Assert.Equal("a", sa.StableId);
        Assert.Equal(new PixelPoint(240, 0), sa.StagingPosition);
        Assert.Equal(0, sa.EnterStepIndex); Assert.Equal(3, sa.ExitStepIndex);
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
        Assert.Equal(new PixelPoint(320, 0), ep.Steps[0].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, ep.Steps[0].Kind);
        Assert.Equal(TD, ep.Steps[0].ExpectedMonitorDeviceName);
        // Step 1: d → target (0,0)
        Assert.Equal(1, ep.Steps[1].StepIndex); Assert.Equal("d", ep.Steps[1].StableId);
        Assert.Equal(new PixelPoint(240, 0), ep.Steps[1].FromPosition);
        Assert.Equal(new PixelPoint(0, 0), ep.Steps[1].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[1].Kind);
        Assert.Equal(TD, ep.Steps[1].ExpectedMonitorDeviceName);
        // Step 2: c → target (240,0)
        Assert.Equal(2, ep.Steps[2].StepIndex); Assert.Equal("c", ep.Steps[2].StableId);
        Assert.Equal(new PixelPoint(160, 0), ep.Steps[2].FromPosition);
        Assert.Equal(new PixelPoint(240, 0), ep.Steps[2].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[2].Kind);
        Assert.Equal(TD, ep.Steps[2].ExpectedMonitorDeviceName);
        // Step 3: b → target (160,0)
        Assert.Equal(3, ep.Steps[3].StepIndex); Assert.Equal("b", ep.Steps[3].StableId);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[3].FromPosition);
        Assert.Equal(new PixelPoint(160, 0), ep.Steps[3].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveToTarget, ep.Steps[3].Kind);
        Assert.Equal(TD, ep.Steps[3].ExpectedMonitorDeviceName);
        // Step 4: a → target (80,0) from staging
        Assert.Equal(4, ep.Steps[4].StepIndex); Assert.Equal("a", ep.Steps[4].StableId);
        Assert.Equal(new PixelPoint(320, 0), ep.Steps[4].FromPosition);
        Assert.Equal(new PixelPoint(80, 0), ep.Steps[4].ToPosition);
        Assert.Equal(TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, ep.Steps[4].Kind);
        Assert.Equal(TD, ep.Steps[4].ExpectedMonitorDeviceName);
        // StagingAssignment
        var sa = ep.StagingAssignments[0];
        Assert.Equal("a", sa.StableId);
        Assert.Equal(new PixelPoint(320, 0), sa.StagingPosition);
        Assert.Equal(0, sa.EnterStepIndex); Assert.Equal(4, sa.ExitStepIndex);
    }

    [Fact] public void NoSafeStagingPosition_ReturnsIssue()
    {
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

    [Fact] public void ReleasedStaging_Reused()
    {
        // Two disjoint swaps: a↔b and c↔d
        // WA=(0,0)-(320,320), Spacing=(80,80)
        var wa = new PixelRect(0, 0, 320, 320);
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 320, 320), wa, 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, new PixelPoint(160, 80), TD, false, true),
             new DesktopIconSnapshotItem(1, "b", "N_b", null, ItemKind.File, new PixelPoint(80, 80), TD, false, true),
             new DesktopIconSnapshotItem(2, "c", "N_c", null, ItemKind.File, new PixelPoint(160, 160), TD, false, true),
             new DesktopIconSnapshotItem(3, "d", "N_d", null, ItemKind.File, new PixelPoint(80, 160), TD, false, true)]);
        var read = new DesktopIconReadResult(
            [Live("a", new(80, 80)), Live("b", new(160, 80)),
             Live("c", new(80, 160)), Live("d", new(160, 160))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, read, new[] { display }, MakeCaps(), TD);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, read, new[] { display }, MakeCaps());
        Assert.True(ep.IsExecutable); Assert.True(ep.UsesTemporaryStaging);
        // 恰好两个 StagingAssignment
        Assert.Equal(2, ep.StagingAssignments.Count);
        var sa0 = ep.StagingAssignments[0];
        var sa1 = ep.StagingAssignments[1];
        // 第一个 ExitStepIndex 小于第二个 EnterStepIndex
        Assert.True(sa0.ExitStepIndex < sa1.EnterStepIndex,
            $"First SA exit={sa0.ExitStepIndex} should be < second SA enter={sa1.EnterStepIndex}");
        // 两者 StagingPosition 完全相同
        Assert.Equal(sa0.StagingPosition, sa1.StagingPosition);
        // 6 steps total (3 + 3 for two swaps)
        Assert.Equal(6, ep.Steps.Count);
    }

    // ═══════════════════════════════════════════
    // Staging 安全
    // ═══════════════════════════════════════════

    [Fact] public void StagingPosition_NeverEqualsCurrentPosition()
    {
        // Two-item swap: staging must not be current position of the staged icon
        var ep = Exec(
            [Snap("a", new(320, 400)), Snap("b", new(160, 240))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        Assert.True(ep.IsExecutable);
        foreach (var sa in ep.StagingAssignments)
        {
            // Find the step that moves this icon to staging
            var stagingStep = ep.Steps.First(s =>
                s.StableId == sa.StableId && s.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging);
            Assert.NotEqual(stagingStep.FromPosition, stagingStep.ToPosition);
        }
    }

    [Fact] public void EveryExecutionStep_ChangesPosition()
    {
        var ep = Exec(
            [Snap("a", new(320, 400)), Snap("b", new(160, 240))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        Assert.True(ep.IsExecutable);
        foreach (var step in ep.Steps)
        {
            Assert.NotEqual(step.FromPosition, step.ToPosition);
        }
    }

    [Fact] public void StagingPosition_Deterministic()
    {
        var p1 = Exec([Snap("a", new(320, 400)), Snap("b", new(160, 240))], [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        var p2 = Exec([Snap("a", new(320, 400)), Snap("b", new(160, 240))], [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        for (var i = 0; i < p1.Steps.Count; i++)
        { Assert.Equal(p1.Steps[i].StableId, p2.Steps[i].StableId); Assert.Equal(p1.Steps[i].Kind, p2.Steps[i].Kind); }
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

    // ═══════════════════════════════════════════
    // MissingFromCurrent 语义
    // ═══════════════════════════════════════════

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
        var rd1 = new DesktopIconReadResult([], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, rd1, new[] { MakeD() }, MakeCaps(), TD);
        var rd2 = new DesktopIconReadResult([Live("a", new(80, 240))], 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(rp, rd2, new[] { MakeD() }, MakeCaps());
        Assert.False(ep.IsExecutable);
        Assert.Contains(ep.Issues, i => i.Code == TargetDisplayLayoutExecutionPlanIssueCode.SourcePlanStale);
        Assert.Empty(ep.Steps);
    }

    // ═══════════════════════════════════════════
    // 失败后半截步骤
    // ═══════════════════════════════════════════

    [Fact] public void FailureAfterInternalProgress_ReturnsNoPartialSteps()
    {
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

    // ═══════════════════════════════════════════
    // 防御性复制
    // ═══════════════════════════════════════════

    [Fact] public void ExecutionPlan_DefensivelyCopiesSteps()
    {
        var originalSteps = new List<TargetDisplayLayoutExecutionStep>
        {
            new(0, "a", "N_a", new(0, 0), new(80, 0), TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false, originalSteps,
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var steps = plan.Steps;
        Assert.NotEmpty(steps);
        // 修改原始 List 不影响 Plan 中的集合
        originalSteps.Clear();
        Assert.NotEmpty(plan.Steps);
        // 通过 IList 修改公开集合应抛异常
        var list = (IList<TargetDisplayLayoutExecutionStep>)steps;
        Assert.Throws<NotSupportedException>(() => list[0] = list[0]);
        Assert.Throws<NotSupportedException>(() => list.Add(list[0]));
        Assert.Throws<NotSupportedException>(() => list.Remove(list[0]));
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact] public void ExecutionPlan_DefensivelyCopiesStagingAssignments()
    {
        var originalSA = new List<TargetDisplayLayoutStagingAssignment>
        {
            new("a", new(160, 0), 0, 2),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            new TargetDisplayLayoutExecutionStep[] { new(0, "a", "N_a", new(0, 0), new(80, 0), TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, TD) },
            originalSA,
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        Assert.NotEmpty(plan.StagingAssignments);
        // 修改原始 List 不影响 Plan 中的集合
        originalSA.Clear();
        Assert.NotEmpty(plan.StagingAssignments);
        // 通过 IList 修改公开集合应抛异常
        var list = (IList<TargetDisplayLayoutStagingAssignment>)plan.StagingAssignments;
        Assert.Throws<NotSupportedException>(() => list[0] = list[0]);
        Assert.Throws<NotSupportedException>(() => list.Add(list[0]));
        Assert.Throws<NotSupportedException>(() => list.Remove(list[0]));
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact] public void ExecutionPlan_DefensivelyCopiesIssues()
    {
        var originalIssues = new List<TargetDisplayLayoutExecutionPlanIssue>
        {
            new(TargetDisplayLayoutExecutionPlanIssueCode.CurrentReadIncomplete, "test"),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(
            TD, false, false,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            originalIssues);
        Assert.NotEmpty(plan.Issues);
        // 修改原始 List 不影响 Plan 中的集合
        originalIssues.Clear();
        Assert.NotEmpty(plan.Issues);
        // 通过 IList 修改公开集合应抛异常
        var list = (IList<TargetDisplayLayoutExecutionPlanIssue>)plan.Issues;
        Assert.Throws<NotSupportedException>(() => list[0] = list[0]);
        Assert.Throws<NotSupportedException>(() => list.Add(list[0]));
        Assert.Throws<NotSupportedException>(() => list.Remove(list[0]));
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact] public void ExecutionPlan_CollectionsAreReadOnly()
    {
        // NoOp plan with empty collections — verify they are read-only
        var plan = new TargetDisplayLayoutExecutionPlan(
            TD, true, true,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        // 通过 IList 修改应抛异常
        Assert.Throws<NotSupportedException>(() => ((IList<TargetDisplayLayoutExecutionStep>)plan.Steps).Add(
            new(0, "x", "X", new(0, 0), new(80, 0), TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD)));
        Assert.Throws<NotSupportedException>(() => ((IList<TargetDisplayLayoutStagingAssignment>)plan.StagingAssignments).Add(
            new("x", new(0, 0), 0, 1)));
        Assert.Throws<NotSupportedException>(() => ((IList<TargetDisplayLayoutExecutionPlanIssue>)plan.Issues).Add(
            new(TargetDisplayLayoutExecutionPlanIssueCode.CurrentReadIncomplete, "x")));
    }

    // ═══════════════════════════════════════════
    // 确定性
    // ═══════════════════════════════════════════

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
            Assert.Equal(s1.StagingSequence, s2.StagingSequence);
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

    // ═══════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════

    private static TargetDisplayLayoutRestorePlan MakeRestore(DesktopIconSnapshotItem[] snaps) =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(
            new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1, snaps),
            MakeRead(snaps.Select(s => Live(s.StableId, s.PixelPosition)).ToArray()),
            new[] { MakeD() }, MakeCaps(), TD);

    private static DesktopIconReadResult MakeRead(DesktopIconDescriptor[] icons) =>
        new(icons, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
}
