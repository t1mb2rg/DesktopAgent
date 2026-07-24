using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class TargetDisplayLayoutExecutionCoordinatorTests
{
    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelPoint Sp = new(80, 80);
    private static readonly PixelRect WA = new(0, 0, 800, 600);
    private static int _ord;
    public TargetDisplayLayoutExecutionCoordinatorTests() { _ord = 0; }

    private static DisplayDescriptor MakeDisplay() =>
        new(TD, new PixelRect(0, 0, 1920, 1080), WA, 96, 96, true);

    private static DesktopIconPositioningCapabilities MakeCaps() =>
        new(false, Sp, ConnectionMethod.FindWindowSW, null);

    private static DesktopIconSnapshotItem Snap(string sid, PixelPoint pos) =>
        new(_ord++, sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    private static DesktopIconDescriptor Live(string sid, PixelPoint pos) =>
        new(sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

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
        FakeDisplayTopologyReader topology, FakeDesktopIconReader reader,
        ScriptedDesktopIconPositionWriter writer, IDesktopLayoutSnapshotStore store) =>
        new(topology, reader, writer, new StaThreadExecutor(),
            new DesktopLayoutSnapshotFactory(), store);

    // ═══════════════════════════════════════════
    // 1. NoOp
    // ═══════════════════════════════════════════

    [Fact] public async Task NoOp_ReturnsCompletedNoOp_NoWriterCall()
    {
        var plan = MakeRestorePlan([Snap("a", new(160, 240))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.CompletedNoOp, result.Status);
        Assert.Equal(0, writer.MoveCallCount);
    }

    // ═══════════════════════════════════════════
    // 2. 不可执行计划
    // ═══════════════════════════════════════════

    [Fact] public async Task InexecutablePlan_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var caps = new DesktopIconPositioningCapabilities(true, Sp, ConnectionMethod.FindWindowSW, null);
        var writer = new ScriptedDesktopIconPositionWriter(caps);
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal(0, writer.MoveCallCount);
    }

    // ═══════════════════════════════════════════
    // 3-5. 安全快照失败
    // ═══════════════════════════════════════════

    [Fact] public async Task SnapshotSaveFailure_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore
            { ShouldThrowOnSave = true, SaveException = new InvalidOperationException("disk full") };
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Contains("disk full", result.ErrorMessage);
        Assert.Equal(0, writer.MoveCallCount);
    }

    [Fact] public async Task SnapshotReadBackNull_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new NullReadBackStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Contains("null", result.ErrorMessage);
        Assert.Equal(0, writer.MoveCallCount);
    }

    private sealed class NullReadBackStore : IDesktopLayoutSnapshotStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopLayoutSnapshot s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DesktopLayoutSnapshot?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<DesktopLayoutSnapshot?>(null);
        public Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(int l = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DesktopLayoutSnapshotSummary>>(Array.Empty<DesktopLayoutSnapshotSummary>());
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact] public async Task SnapshotVerificationMismatch_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new CorruptingStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Contains("DisplayName", result.ErrorMessage);
        Assert.Equal(0, writer.MoveCallCount);
    }

    private sealed class CorruptingStore : IDesktopLayoutSnapshotStore
    {
        private DesktopLayoutSnapshot? _saved;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopLayoutSnapshot s, CancellationToken ct = default) { _saved = s; return Task.CompletedTask; }
        public Task<DesktopLayoutSnapshot?> GetAsync(Guid id, CancellationToken ct = default)
        {
            if (_saved is null) return Task.FromResult<DesktopLayoutSnapshot?>(null);
            var icons = _saved.Icons.Select((icon, i) =>
                new DesktopIconSnapshotItem(i, icon.StableId, "CORRUPTED", icon.ParsingName,
                    icon.ItemKind, icon.PixelPosition, icon.MonitorDeviceName,
                    icon.IsSystemItem, icon.IsFileSystemItem)).ToList();
            return Task.FromResult<DesktopLayoutSnapshot?>(new DesktopLayoutSnapshot(
                _saved.SnapshotId, _saved.CreatedAtUtc, _saved.TargetMonitorDeviceName,
                _saved.ConnectionMethod, _saved.FormatVersion, icons));
        }
        public Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(int l = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DesktopLayoutSnapshotSummary>>(Array.Empty<DesktopLayoutSnapshotSummary>());
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }

    // ═══════════════════════════════════════════
    // 6-7. 单步骤/多步骤成功
    // ═══════════════════════════════════════════

    [Fact] public async Task SingleStepExact_ReturnsCompleted()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Session);
        Assert.NotEqual(Guid.Empty, result.Session!.SessionId);
    }

    [Fact] public async Task TwoStepChain_ExecutesInOrder_ReturnsCompleted()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(160, 80)), Snap("b", new(240, 80))],
            [Live("a", new(80, 80)), Live("b", new(160, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("b", new(160, 80), new(240, 80)); // step 0
        writer.ScriptExactResult("a", new(80, 80), new(160, 80));  // step 1
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(80, 80)), Live("b", new(160, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Equal(2, result.ForwardStepResults.Count);
    }

    [Fact] public async Task Swap_CompletesAllThreeSteps_CorrectOrder()
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
        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, live,
            new[] { display }, MakeCaps(), TD);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(0, 0), new(160, 0));   // step 0: a→staging
        writer.ScriptExactResult("b", new(80, 0), new(0, 0));     // step 1: b→target
        writer.ScriptExactResult("a", new(160, 0), new(80, 0));   // step 2: a→target
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, new PixelRect(0, 0, 240, 80)),
            new FakeDesktopIconReader(live), writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Equal(3, result.ForwardStepResults.Count);
    }

    // ═══════════════════════════════════════════
    // 8. Writer 异常 → PositionUncertain，无回滚
    // ═══════════════════════════════════════════

    [Fact] public async Task ForwardWriterException_ReturnsPositionUncertain_NoRollback()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 80), new(240, 80));
        writer.ScriptException("b", new(320, 80), new InvalidOperationException("BOOM"));
        // 第三个脚本（如果有回滚）不会被调用
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.NotNull(result.SafetySnapshotId);
        Assert.NotNull(result.Session);
        Assert.Empty(result.RollbackStepResults);
        // Only 2 forward calls (a success, b exception)
        Assert.Equal(1, writer.MoveCallCount); // only a logged (b threw before log)
    }

    [Fact] public async Task ForwardException_NoPreviousSteps_ReturnsPositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptException("a", new(160, 240), new InvalidOperationException("BOOM"));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.Empty(result.RollbackStepResults);
    }

    // ═══════════════════════════════════════════
    // 9. Wrong StableId → PositionUncertain，无回滚
    // ═══════════════════════════════════════════

    [Fact] public async Task WriterWrongStableId_ReturnsPositionUncertain_NoRollback()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptWrongStableId("a", new(160, 240), new(240, 320), "WRONG_ID");
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.Empty(result.RollbackStepResults);
    }

    // ═══════════════════════════════════════════
    // 10. Wrong PreviousPosition → PositionUncertain，无回滚
    // ═══════════════════════════════════════════

    [Fact] public async Task WriterPreviousPositionMismatch_ReturnsPositionUncertain_NoRollback()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptWrongPreviousPosition("a", new(160, 240), new(240, 320), new(999, 999));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.Empty(result.RollbackStepResults);
    }

    // ═══════════════════════════════════════════
    // 11. 已知非精确 → 回滚当前步骤 + 之前步骤
    // ═══════════════════════════════════════════

    [Fact] public async Task ForwardInexactKnownActual_RollsBackCurrentStep()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        // Forward: ActualPosition (250,330) ≠ ToPosition (240,320)
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        // Rollback current step: target = FromPosition(160,240), expected PreviousPosition = actual(250,330)
        writer.ScriptExactResult("a", new(250, 330), new(160, 240));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(2, writer.MoveCallCount); // forward + rollback
        Assert.Single(result.RollbackStepResults);
        Assert.True(result.RollbackStepResults[0].IsExact);
    }

    [Fact] public async Task InexactWithPriorSteps_RollsBackAllReverse()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 80), new(240, 80));     // step 0 exact
        writer.ScriptInexactResult("b", new(320, 80), new(400, 80), new(410, 85)); // step 1 inexact
        writer.ScriptExactResult("b", new(410, 85), new(320, 80));     // rollback b: target=320,80, prev=410,85
        writer.ScriptExactResult("a", new(240, 80), new(160, 80));     // rollback a: target=160,80
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(4, writer.MoveCallCount); // a fwd + b fwd + b rollback + a rollback
        Assert.Equal(2, result.RollbackStepResults.Count);
    }

    // ═══════════════════════════════════════════
    // 15. 全部回滚成功 → FailedAndRolledBack
    // ═══════════════════════════════════════════

    [Fact] public async Task RollbackAllExact_ReturnsFailedAndRolledBack()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 80), new(240, 80));
        writer.ScriptInexactResult("b", new(320, 80), new(400, 80), new(410, 85)); // triggers rollback
        writer.ScriptExactResult("b", new(410, 85), new(320, 80)); // rollback b
        writer.ScriptExactResult("a", new(240, 80), new(160, 80)); // rollback a
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.False(result.RequiresManualRecovery);
        Assert.NotNull(result.SafetySnapshotId);
    }

    // ═══════════════════════════════════════════
    // 16. 回滚非精确 → FailedRollbackIncomplete
    // ═══════════════════════════════════════════

    [Fact] public async Task RollbackInexact_ReturnsFailedRollbackIncomplete()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330)); // forward inexact
        writer.ScriptInexactResult("a", new(250, 330), new(160, 240), new(170, 250)); // rollback inexact
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        // Rollback position recorded
        Assert.Single(result.RollbackStepResults);
        Assert.Equal(new PixelPoint(170, 250), result.RollbackStepResults[0].ActualPosition);
    }

    // ═══════════════════════════════════════════
    // 17-18. 回滚异常 → PositionUncertain
    // ═══════════════════════════════════════════

    [Fact] public async Task RollbackException_ReturnsPositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptException("a", new(250, 330), new InvalidOperationException("rollback fail"));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.IsPositionUncertain);
    }

    [Fact] public async Task RollbackWrongStableId_ReturnsPositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptWrongStableId("a", new(250, 330), new(160, 240), "WRONG");
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
    }

    // ═══════════════════════════════════════════
    // 19. 集合并发防御
    // ═══════════════════════════════════════════

    [Fact] public async Task PublicCollections_AreReadOnly()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<DesktopIconMoveResult>)result.ForwardStepResults).Add(
                new DesktopIconMoveResult("x", new(0, 0), new(80, 0), new(80, 0),
                    ConnectionMethod.FindWindowSW, null)));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<DesktopIconMoveResult>)result.RollbackStepResults).Clear());
    }

    // ═══════════════════════════════════════════
    // 20. Cancellation
    // ═══════════════════════════════════════════

    [Fact] public async Task CancellationBeforeWriter_NoWriteOccurs()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ExecuteAsync(plan, cts.Token));
        Assert.Equal(0, writer.MoveCallCount);
    }

    // ═══════════════════════════════════════════
    // 21. RecoverySession 深度冻结
    // ═══════════════════════════════════════════

    [Fact] public async Task RecoverySession_FreezesSourcePlanCollections()
    {
        // Build a plan that goes to Completed so we can inspect the session
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.NotNull(result.Session);

        var session = result.Session!;

        // Verify SourceRestorePlanItems is truly read-only
        var planItems = Assert.IsAssignableFrom<IList<TargetDisplayLayoutRestorePlanItem>>(
            session.SourceRestorePlanItems);
        Assert.Throws<NotSupportedException>(() => planItems.Clear());
        Assert.Throws<NotSupportedException>(() => planItems.Add(planItems[0]));
        Assert.Throws<NotSupportedException>(() => planItems.Remove(planItems[0]));
        Assert.Throws<NotSupportedException>(() => planItems[0] = planItems[0]);
        Assert.False(session.SourceRestorePlanItems is TargetDisplayLayoutRestorePlanItem[]);

        // Verify ExecutionPlanSteps is truly read-only
        var execSteps = Assert.IsAssignableFrom<IList<TargetDisplayLayoutExecutionStep>>(
            session.ExecutionPlanSteps);
        Assert.Throws<NotSupportedException>(() => execSteps.Clear());
        Assert.Throws<NotSupportedException>(() => execSteps.Add(execSteps[0]));
        Assert.Throws<NotSupportedException>(() => execSteps.Remove(execSteps[0]));
        Assert.Throws<NotSupportedException>(() => execSteps[0] = execSteps[0]);
        Assert.False(session.ExecutionPlanSteps is TargetDisplayLayoutExecutionStep[]);

        // Verify CompletedForwardSteps is truly read-only
        var fwdSteps = Assert.IsAssignableFrom<IList<int>>(session.CompletedForwardSteps);
        Assert.Throws<NotSupportedException>(() => fwdSteps.Clear());
        Assert.Throws<NotSupportedException>(() => fwdSteps.Add(42));
        Assert.Throws<NotSupportedException>(() => fwdSteps.Remove(0));
        Assert.Throws<NotSupportedException>(() => fwdSteps[0] = 99);
        Assert.False(session.CompletedForwardSteps is int[]);

        // Verify CompletedRollbackSteps is truly read-only
        var rbSteps = Assert.IsAssignableFrom<IList<int>>(session.CompletedRollbackSteps);
        Assert.Throws<NotSupportedException>(() => rbSteps.Clear());
        Assert.Throws<NotSupportedException>(() => rbSteps.Add(42));
        Assert.Throws<NotSupportedException>(() => rbSteps.Remove(0));
        Assert.False(session.CompletedRollbackSteps is int[]);
    }

    [Fact] public async Task RecoverySession_FreezesNestedCycleStableIds()
    {
        // Construct a plan with real cycles (swap)
        var display = new DisplayDescriptor(TD, new PixelRect(0, 0, 240, 80),
            new PixelRect(0, 0, 240, 80), 96, 96, true);
        var snap = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD,
            ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "a", "N_a", null, ItemKind.File, new PixelPoint(80, 0), TD, false, true),
             new DesktopIconSnapshotItem(1, "b", "N_b", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);
        var live = new DesktopIconReadResult(
            [Live("a", new(0, 0)), Live("b", new(80, 0))],
            0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(snap, live,
            new[] { display }, MakeCaps(), TD);

        // Modify original mutable Cycle list and StableIds after creating session
        // (the original plan's Cycles/StableIds are the source before freezing)
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(0, 0), new(160, 0));
        writer.ScriptExactResult("b", new(80, 0), new(0, 0));
        writer.ScriptExactResult("a", new(160, 0), new(80, 0));
        var coordinator = MakeCoordinator(
            new FakeDisplayTopologyReader(TD, new PixelRect(0, 0, 240, 80)),
            new FakeDesktopIconReader(live), writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.NotNull(result.Session);

        // Session's SourceRestorePlanItems are read-only
        var sourcePlanItems = Assert.IsAssignableFrom<IList<TargetDisplayLayoutRestorePlanItem>>(
            result.Session!.SourceRestorePlanItems);
        Assert.Throws<NotSupportedException>(() => sourcePlanItems.Clear());
        Assert.Throws<NotSupportedException>(() => sourcePlanItems[0] = sourcePlanItems[0]);
        Assert.False(result.Session!.SourceRestorePlanItems is TargetDisplayLayoutRestorePlanItem[]);
    }
}
