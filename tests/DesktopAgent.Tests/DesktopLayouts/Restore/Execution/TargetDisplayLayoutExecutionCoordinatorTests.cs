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
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

    private static TargetDisplayLayoutExecutionCoordinator MakeCoordinator(
        FakeDisplayTopologyReader topology, FakeDesktopIconReader reader,
        ScriptedDesktopIconPositionWriter writer, IDesktopLayoutSnapshotStore store,
        IDesktopLayoutSafetySnapshotFactory factory) =>
        new(topology, reader, writer, new StaThreadExecutor(), factory, store);

    private static TargetDisplayLayoutRestorePlanItem PlanItem(
        string sid, PixelPoint target, TargetDisplayLayoutRestoreItemState state,
        PixelPoint? currentPos = null, string? monitor = null) =>
        new(sid, $"N_{sid}", target, currentPos, monitor, state, true, false);

    // ═══════════ 1. NoOp ═══════════
    [Fact] public async Task NoOp_ReturnsCompletedNoOp_NoWriterCall()
    {
        var plan = MakeRestorePlan([Snap("a", new(160, 240))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.CompletedNoOp, result.Status);
        Assert.Equal(0, writer.AttemptCount);
    }

    // ═══════════ 2. Inexecutable ═══════════
    [Fact] public async Task InexecutablePlan_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var caps = new DesktopIconPositioningCapabilities(true, Sp, ConnectionMethod.FindWindowSW, null);
        var writer = new ScriptedDesktopIconPositionWriter(caps);
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal(0, writer.AttemptCount);
    }

    // ═══════════ 3-5. Snapshot failures ═══════════
    [Fact] public async Task SnapshotSaveFailure_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore
            { ShouldThrowOnSave = true, SaveException = new InvalidOperationException("disk full") };
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]), writer, store);
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Contains("disk full", result.ForwardFailureMessage);
        Assert.Equal(0, writer.AttemptCount);
    }

    [Fact] public async Task SnapshotReadBackNull_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new NullReadBackStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Contains("null", result.ForwardFailureMessage);
        Assert.Equal(0, writer.AttemptCount);
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
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new CorruptingStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Contains("DisplayName", result.ForwardFailureMessage);
        Assert.NotNull(result.SafetySnapshotId); // snapshot was saved
        Assert.Equal(0, writer.AttemptCount);
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

    // ═══════════ 6-7. Success ═══════════
    [Fact] public async Task SingleStepExact_ReturnsCompleted()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Single(result.ForwardAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess, result.ForwardAttempts[0].Outcome);
        Assert.NotNull(result.Session);
        Assert.Single(result.Session!.CompletedForwardStepIndexes);
        Assert.Contains(0, result.Session.CompletedForwardStepIndexes);
    }

    [Fact] public async Task TwoStepChain_ExecutesInOrder_ReturnsCompleted()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(160, 80)), Snap("b", new(240, 80))],
            [Live("a", new(80, 80)), Live("b", new(160, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("b", new(160, 80), new(240, 80));
        writer.ScriptExactResult("a", new(80, 80), new(160, 80));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(80, 80)), Live("b", new(160, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Equal(2, result.ForwardAttempts.Count);
    }

    // ═══════════ 8. Writer exception → PositionUncertain ═══════════
    [Fact] public async Task ForwardWriterException_ReturnsPositionUncertain_NoRollback()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 80), new(240, 80));
        writer.ScriptException("b", new(400, 80), new InvalidOperationException("BOOM"));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.Empty(result.RollbackAttempts);
        Assert.Equal(2, writer.AttemptCount); // a success + b exception — both counted
    }

    [Fact] public async Task ForwardException_NoPreviousSteps_ReturnsPositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptException("a", new(240, 320), new InvalidOperationException("BOOM"));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Empty(result.RollbackAttempts);
    }

    // ═══════════ 9. Wrong StableId → PositionUncertain ═══════════
    [Fact] public async Task WriterWrongStableId_ReturnsPositionUncertain_NoRollback()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptWrongStableId("a", new(160, 240), new(240, 320), "WRONG_ID");
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Empty(result.RollbackAttempts);
        // Verify attempt outcome and trust
        var attempt = Assert.Single(result.ForwardAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.StableIdMismatch, attempt.Outcome);
        Assert.False(attempt.HasTrustedActualPosition);
        Assert.False(attempt.IsExact);
        // Not in completed
        Assert.NotNull(result.Session);
        Assert.Empty(result.Session!.CompletedForwardStepIndexes);
    }

    // ═══════════ 10. Wrong PreviousPosition → PositionUncertain ═══════════
    [Fact] public async Task WriterPreviousPositionMismatch_ReturnsPositionUncertain_NoRollback()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptWrongPreviousPosition("a", new(160, 240), new(240, 320), new(999, 999));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Empty(result.RollbackAttempts);
    }

    // ═══════════ 11. Inexact → rollback ═══════════
    [Fact] public async Task ForwardInexactKnownActual_RollsBackCurrentStep()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(2, writer.AttemptCount);
        Assert.Single(result.RollbackAttempts);
    }

    // ═══════════ 15. Rollback all exact ═══════════
    [Fact] public async Task RollbackAllExact_ReturnsFailedAndRolledBack()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 80), new(240, 80));
        writer.ScriptInexactResult("b", new(320, 80), new(400, 80), new(410, 85));
        writer.ScriptExactResult("b", new(410, 85), new(320, 80));
        writer.ScriptExactResult("a", new(240, 80), new(160, 80));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.False(result.RequiresManualRecovery);
        // Check rollback source step indices in session
        Assert.NotNull(result.Session);
        var rbSrc = result.Session!.CompletedRollbackSourceStepIndexes;
        Assert.Equal(2, rbSrc.Count); // steps 1 and 0 rolled back
    }

    // ═══════════ 16. Rollback inexact → FailedRollbackIncomplete ═══════════
    [Fact] public async Task RollbackInexact_ReturnsFailedRollbackIncomplete()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptInexactResult("a", new(250, 330), new(160, 240), new(170, 250));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.NotNull(result.RecoveryFailureMessage);
        Assert.Contains("非精确", result.RecoveryFailureMessage);
    }

    // ═══════════ 17. Rollback exception → PositionUncertain ═══════════
    [Fact] public async Task RollbackException_ReturnsPositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptException("a", new(160, 240), new InvalidOperationException("rollback fail"));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.NotNull(result.RecoveryFailureType);
        Assert.Contains("rollback fail", result.RecoveryFailureMessage);
    }

    // ═══════════ Rollback mismatch tests ═══════════
    [Fact] public async Task RollbackWrongStableId_IsNotMarkedCompleted()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptWrongStableId("a", new(250, 330), new(160, 240), "WRONG");
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        var rb = Assert.Single(result.RollbackAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.StableIdMismatch, rb.Outcome);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptPhase.Rollback, rb.Phase);
        Assert.False(rb.HasTrustedActualPosition);
        Assert.NotNull(result.Session);
        Assert.Empty(result.Session!.CompletedRollbackSourceStepIndexes);
        Assert.Equal("RollbackStableIdMismatch", result.RecoveryFailureType);
    }

    [Fact] public async Task RollbackWrongPreviousPosition_IsNotMarkedCompleted()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptWrongPreviousPosition("a", new(250, 330), new(160, 240), new(999, 999));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        var rb = Assert.Single(result.RollbackAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.PreviousPositionMismatch, rb.Outcome);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptPhase.Rollback, rb.Phase);
        Assert.Empty(result.Session!.CompletedRollbackSourceStepIndexes);
        Assert.Equal("RollbackPreviousPositionMismatch", result.RecoveryFailureType);
    }

    [Fact] public async Task RollbackInexact_ReturnsFailedRollbackIncomplete_WithType()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptInexactResult("a", new(250, 330), new(160, 240), new(170, 250));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.Equal("RollbackInexact", result.RecoveryFailureType);
        Assert.NotNull(result.RecoveryFailureMessage);
        Assert.Contains("170", result.RecoveryFailureMessage); // actualPosition
    }

    [Fact] public async Task PriorRollbackMismatch_DoesNotMarkFailedRollbackCompleted()
    {
        // 2 steps: a exact, b inexact → rollback step 1(a→b) then step 0(a→a′)
        // Step 0 exact, step 1 inexact → triggers rollback
        // Rollback step 1: exact success
        // Rollback step 0: wrong StableId
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 80), new(240, 80));           // fwd step 0
        writer.ScriptInexactResult("b", new(320, 80), new(400, 80), new(410, 85)); // fwd step 1 inexact
        writer.ScriptExactResult("b", new(410, 85), new(320, 80));           // rb step 1 exact
        writer.ScriptWrongStableId("a", new(240, 80), new(160, 80), "WRONG"); // rb step 0 wrong id
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RollbackStableIdMismatch", result.RecoveryFailureType);
        Assert.Equal(2, result.RollbackAttempts.Count);
        // First rollback: source step 1 (b), exact
        Assert.Equal(1, result.RollbackAttempts[0].SourceStepIndex);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess, result.RollbackAttempts[0].Outcome);
        // Second rollback: source step 0 (a), mismatch
        Assert.Equal(0, result.RollbackAttempts[1].SourceStepIndex);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.StableIdMismatch, result.RollbackAttempts[1].Outcome);
        Assert.False(result.RollbackAttempts[1].HasTrustedActualPosition);
        // Completed only contains step 1, NOT step 0
        Assert.NotNull(result.Session);
        Assert.Single(result.Session!.CompletedRollbackSourceStepIndexes);
        Assert.Contains(1, result.Session.CompletedRollbackSourceStepIndexes);
        Assert.DoesNotContain(0, result.Session.CompletedRollbackSourceStepIndexes);
    }

    // ═══════════ 19. Collections ═══════════
    [Fact] public async Task PublicCollections_AreReadOnly()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TargetDisplayLayoutExecutionAttemptResult>)result.ForwardAttempts).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TargetDisplayLayoutExecutionAttemptResult>)result.RollbackAttempts).Clear());
    }

    // ═══════════ 20. Cancellation before Writer ═══════════
    [Fact] public async Task CancellationBeforeWriter_NoWriteOccurs()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ExecuteAsync(plan, cts.Token));
        Assert.Equal(0, writer.AttemptCount);
    }

    // ═══════════ OperatorCanceledException from Writer ═══════════
    [Fact] public async Task ForwardWriterThrowsOCE_ReturnsPositionUncertain_NoRollback()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 80), new(240, 80));
        writer.ScriptException("b", new(400, 80), new OperationCanceledException("fake writer OCE"));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        // OCE from Writer is NOT a clean cancellation — it means unknown side effects
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.Empty(result.RollbackAttempts);
    }

    [Fact] public async Task RollbackWriterThrowsOCE_ReturnsPositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptException("a", new(160, 240), new OperationCanceledException("rollback OCE"));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.NotNull(result.RecoveryFailureType);
    }

    // ═══════════ Real cancellation timing tests ═══════════

    [Fact] public async Task CancellationDuringForwardWriter_WaitsForWriterResult()
    {
        // Plan with 2 independent steps so second step exists
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.ScriptBlockingExact("a", new(240, 80), new(160, 80), started, release);
        // Rollback for step 0
        writer.ScriptExactResult("a", new(240, 80), new(160, 80));

        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());

        using var cts = new CancellationTokenSource();
        var task = coordinator.ExecuteAsync(plan, cts.Token);

        // Wait for writer to start
        await started.Task;
        // Cancel while writer is blocked
        cts.Cancel();
        // Task should NOT be complete yet (still waiting for writer)
        Assert.False(task.IsCompleted);
        // Release writer
        release.TrySetResult();

        var result = await task;
        // Step 0 completed, then cancelled → rollback step 0
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Single(result.RollbackAttempts); // step 0 rolled back
        Assert.Equal(2, writer.AttemptCount); // a forward + a rollback
    }

    [Fact] public async Task CancellationBetweenForwardSteps_RollsBackKnownSteps()
    {
        // Step 0 blocks, cancel during step 0 writer, then release.
        // Step 0 completes, then at top of step 1 loop the cancellation is detected
        // and prior step 0 is rolled back.
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var started0 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release0 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.ScriptBlockingExact("a", new(240, 80), new(160, 80), started0, release0);
        // Rollback for a when cancellation detected between steps
        writer.ScriptExactResult("a", new(240, 80), new(160, 80));

        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 80)), Live("b", new(320, 80))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());

        using var cts = new CancellationTokenSource();
        var task = coordinator.ExecuteAsync(plan, cts.Token);

        await started0.Task; // writer is blocked
        cts.Cancel();        // cancel while blocked
        release0.TrySetResult(); // writer returns exact

        var result = await task;
        // Step 0 completed, token cancelled, rollback at top of step 1's iteration
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Contains(result.RollbackAttempts, r => r.SourceStepIndex == 0);
    }

    [Fact] public async Task CancellationDuringRollback_DoesNotAbandonRollback()
    {
        // Forward inexact → rollback. Rollback blocks, cancel token, rollback must complete.
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        // Forward inexact
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        // Rollback blocking
        var startedRb = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRb = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.ScriptBlockingExact("a", new(160, 240), new(250, 330), startedRb, releaseRb);

        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());

        using var cts = new CancellationTokenSource();
        var task = coordinator.ExecuteAsync(plan, cts.Token);

        // Wait for rollback to start
        await startedRb.Task;
        // Cancel during rollback
        cts.Cancel();
        Assert.False(task.IsCompleted); // must not be done
        releaseRb.TrySetResult();

        var result = await task;
        // Rollback completed despite cancellation
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.NotEmpty(result.RollbackAttempts);
    }

    // ═══════════ ExecutionPlan structure validation ═══════════

    // ═══════════ SafetySnapshotId preserved on read-back failure ═══════════
    [Fact] public async Task SnapshotReadBackException_ReturnsFailedBeforeWrite_WithSnapshotId()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var store = new ThrowOnGetStore();
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, store);
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.NotNull(result.SafetySnapshotId);
        Assert.Equal(0, writer.AttemptCount);
        Assert.Null(result.Session);
        Assert.False(result.RequiresManualRecovery);
    }

    private sealed class ThrowOnGetStore : IDesktopLayoutSnapshotStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopLayoutSnapshot s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DesktopLayoutSnapshot?> GetAsync(Guid id, CancellationToken ct = default) =>
            throw new InvalidOperationException("GET BOMB");
        public Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(int l = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DesktopLayoutSnapshotSummary>>(Array.Empty<DesktopLayoutSnapshotSummary>());
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact] public async Task SnapshotReadBackNull_ReturnsFailedBeforeWrite_WithSnapshotId()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new NullReadBackStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.NotNull(result.SafetySnapshotId);
        Assert.Equal(0, writer.AttemptCount);
    }

    // ═══════════ 21. RecoverySession freeze + full plans ═══════════
    [Fact] public async Task RecoverySession_FreezesSourceRestorePlan()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.NotNull(result.Session);

        var sp = result.Session!.SourceRestorePlan;
        var items = Assert.IsAssignableFrom<IList<TargetDisplayLayoutRestorePlanItem>>(sp.Items);
        Assert.Throws<NotSupportedException>(() => items.Clear());
        Assert.Throws<NotSupportedException>(() => items.Add(items[0]));
        Assert.Throws<NotSupportedException>(() => items[0] = items[0]);
        Assert.False(sp.Items is TargetDisplayLayoutRestorePlanItem[]);

        // Verify ExecutionPlan is frozen
        var ep = result.Session!.ExecutionPlan;
        var steps = Assert.IsAssignableFrom<IList<TargetDisplayLayoutExecutionStep>>(ep.Steps);
        Assert.Throws<NotSupportedException>(() => steps.Clear());
        Assert.Throws<NotSupportedException>(() => steps[0] = steps[0]);

        // CompletedForwardStepIndexes frozen
        var fwd = Assert.IsAssignableFrom<IList<int>>(result.Session.CompletedForwardStepIndexes);
        Assert.Throws<NotSupportedException>(() => fwd[0] = 99);
    }

    [Fact] public async Task RecoverySession_FreezesNestedCycleStableIds()
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
        writer.ScriptExactResult("a", new(0, 0), new(160, 0));
        writer.ScriptExactResult("b", new(80, 0), new(0, 0));
        writer.ScriptExactResult("a", new(160, 0), new(80, 0));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, new PixelRect(0, 0, 240, 80)),
            new FakeDesktopIconReader(live), writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.NotNull(result.Session);

        var cycles = result.Session!.SourceRestorePlan.Cycles;
        Assert.NotEmpty(cycles);
        var cycleIds = Assert.IsAssignableFrom<IList<string>>(cycles[0].StableIds);
        Assert.Throws<NotSupportedException>(() => cycleIds[0] = "tampered");
        Assert.False(cycles[0].StableIds is string[]);
    }

    [Fact] public async Task RecoverySession_FreezesExecutionPlan()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        var ep = result.Session!.ExecutionPlan;
        // Steps
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TargetDisplayLayoutExecutionStep>)ep.Steps).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TargetDisplayLayoutExecutionStep>)ep.Steps)[0] = ep.Steps[0]);
        // StagingAssignments
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TargetDisplayLayoutStagingAssignment>)ep.StagingAssignments).Clear());
        // Issues
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TargetDisplayLayoutExecutionPlanIssue>)ep.Issues).Clear());
    }

    // ═══════════ Raw monitor null assignment ═══════════
    [Fact] public async Task RawReaderMonitorNamesAreNull_CoordinatorAssignsMonitors()
    {
        // Create icons with null MonitorDeviceName (simulating real reader)
        var rawIcon = new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new(160, 240), null, false, true);
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([rawIcon]), writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
    }

    // ═══════════ Planner exception → FailedBeforeWrite ═══════════
    [Fact] public async Task PlannerException_ReturnsFailedBeforeWrite_NoWriterCall()
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

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal(nameof(InvalidOperationException), result.ForwardFailureType);
        Assert.Contains("CreatePlan 异常：planner BOMB", result.ForwardFailureMessage);
        Assert.Null(result.Session);
        Assert.Null(result.SafetySnapshotId);
        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.Empty(result.ForwardAttempts);
        Assert.Empty(result.RollbackAttempts);
        Assert.Equal(0, writer.AttemptCount);
        Assert.Equal(0, store.SaveCount);
    }

    // ═══════════ 畸形 ExecutionPlan 在快照前拒绝 ═══════════
    [Fact] public async Task MalformedExecutionPlan_IsRejectedBeforeSnapshotAndWriter()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var store = new InMemoryDesktopLayoutSnapshotStore();
        // Inject a planner that returns IsExecutable=true, IsNoOp=false, but Steps=empty
        var emptyPlan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            store,
            (_, _, _, _) => emptyPlan);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Contains("EmptySteps", result.ForwardFailureType);
        Assert.Null(result.Session);
        Assert.Null(result.SafetySnapshotId);
        Assert.Equal(0, writer.AttemptCount);
        Assert.Equal(0, store.SaveCount);
    }

    // ═══════════ InvalidDependencies → FailedBeforeWrite (plan not executable) ═══════════
    [Fact] public async Task InvalidDependencies_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var items = new[]
        {
            PlanItem("a", new(240, 320), TargetDisplayLayoutRestoreItemState.MoveRequired, new(160, 240), TD),
        };
        var plan = new TargetDisplayLayoutRestorePlan(
            TD, 1, 0, 1, 0, 0, false, false, false,
            items,
            (IReadOnlyList<TargetDisplayLayoutRestoreDependency>)null!,
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            new[] { "a" });
        var writer = new ScriptedDesktopIconPositionWriter();
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore());
        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(0, writer.AttemptCount);
    }

    // ═══════════ SnapshotFactory failure → FailedBeforeWrite ═══════════
    private sealed class ThrowingSnapshotFactory : IDesktopLayoutSafetySnapshotFactory
    {
        public DesktopLayoutSnapshot Create(IReadOnlyList<DesktopIconDescriptor> icons,
            string? targetMonitorDeviceName, ConnectionMethod connectionMethod) =>
            throw new InvalidOperationException("factory BOMB");
    }

    [Fact] public async Task SnapshotFactoryFailure_ReturnsFailedBeforeWrite_NoWriterCall()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var coordinator = MakeCoordinator(new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new InMemoryDesktopLayoutSnapshotStore(),
            new ThrowingSnapshotFactory());

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(0, writer.AttemptCount);
        Assert.False(result.RequiresManualRecovery);
        Assert.Contains("factory BOMB", result.ForwardFailureMessage);
    }

    // ═══════════ ExecutionPlan structure validation (direct unit tests) ═══════════
    [Fact] public void ExecutableNonNoOpEmptySteps_IsRejected()
    {
        var plan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var err = TargetDisplayLayoutExecutionCoordinator.ValidateExecutionPlanStructure(plan);
        Assert.NotNull(err);
        Assert.Equal("EmptySteps", err.Value.ErrorType);
    }

    [Fact] public void BlankStableIdStep_IsRejected()
    {
        var plan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "  ", "N", new(0, 0), new(80, 0),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var err = TargetDisplayLayoutExecutionCoordinator.ValidateExecutionPlanStructure(plan);
        Assert.NotNull(err);
        Assert.Equal("BlankStableId", err.Value.ErrorType);
    }

    [Fact] public void NonSequentialStepIndex_IsRejected()
    {
        var plan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(5, "a", "N", new(0, 0), new(80, 0),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var err = TargetDisplayLayoutExecutionCoordinator.ValidateExecutionPlanStructure(plan);
        Assert.NotNull(err);
        Assert.Equal("StepIndexMismatch", err.Value.ErrorType);
    }

    [Fact] public void ZeroLengthStep_IsRejected()
    {
        var plan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N", new(80, 0), new(80, 0),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var err = TargetDisplayLayoutExecutionCoordinator.ValidateExecutionPlanStructure(plan);
        Assert.NotNull(err);
        Assert.Equal("ZeroLengthStep", err.Value.ErrorType);
    }

    [Fact] public void ValidPlan_PassesValidation()
    {
        var plan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            new[] { new TargetDisplayLayoutExecutionStep(0, "a", "N", new(0, 0), new(80, 0),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD) },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var err = TargetDisplayLayoutExecutionCoordinator.ValidateExecutionPlanStructure(plan);
        Assert.Null(err);
    }

    // ═══════════ Scope gate integration tests ═══════════

    [Fact]
    public async Task FreshExecutionPlanRejectedByScopeGate_NoSnapshotAndNoWriter()
    {
        // Preview SourcePlan looks like single move (1 item),
        // but Coordinator's fresh execution plan has 2 steps → gate rejects.
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var store = new InMemoryDesktopLayoutSnapshotStore();

        // Inject planner that returns a multi-step plan (2 steps)
        var multiStepPlan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            new[]
            {
                new TargetDisplayLayoutExecutionStep(0, "a", "N_a", new(160, 240), new(200, 280),
                    TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD),
                new TargetDisplayLayoutExecutionStep(1, "a", "N_a", new(200, 280), new(240, 320),
                    TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD),
            },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            store,
            (_, _, _, _) => multiStepPlan);

        var result = await coordinator.ExecuteWithScopeGateAsync(
            plan, TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal("ExecutionScopeRejected", result.ForwardFailureType);
        Assert.Contains("Steps.Count", result.ForwardFailureMessage);
        Assert.Equal(0, writer.AttemptCount);
        Assert.Equal(0, store.SaveCount);
        Assert.Null(result.SafetySnapshotId);
        Assert.Null(result.Session);
        Assert.Empty(result.ForwardAttempts);
        Assert.Empty(result.RollbackAttempts);
        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
    }

    [Fact]
    public async Task FreshSingleDirectMoveAllowedByScopeGate_Executes()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore();

        // Planner returns a valid single-step plan
        var singleStepPlan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            new[]
            {
                new TargetDisplayLayoutExecutionStep(0, "a", "N_a", new(160, 240), new(240, 320),
                    TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD),
            },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            store,
            (_, _, _, _) => singleStepPlan);

        var result = await coordinator.ExecuteWithScopeGateAsync(
            plan, TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Equal(1, writer.AttemptCount);
        Assert.NotNull(result.Session);
        Assert.NotEqual(Guid.Empty, result.SafetySnapshotId);
    }

    [Fact]
    public async Task ScopeGateException_ReturnsFailedBeforeWrite()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var store = new InMemoryDesktopLayoutSnapshotStore();

        var singleStepPlan = new TargetDisplayLayoutExecutionPlan(
            TD, true, false,
            new[]
            {
                new TargetDisplayLayoutExecutionStep(0, "a", "N_a", new(160, 240), new(240, 320),
                    TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD),
            },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader([Live("a", new(160, 240))]),
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            store,
            (_, _, _, _) => singleStepPlan);

        var result = await coordinator.ExecuteWithScopeGateAsync(
            plan,
            (_, _) => throw new InvalidOperationException("gate BOMB"));

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal("ExecutionScopeGateException", result.ForwardFailureType);
        Assert.Contains("gate BOMB", result.ForwardFailureMessage);
        Assert.Null(result.SafetySnapshotId);
        Assert.Null(result.Session);
        Assert.Equal(0, writer.AttemptCount);
        Assert.Equal(0, store.SaveCount);
    }
}
