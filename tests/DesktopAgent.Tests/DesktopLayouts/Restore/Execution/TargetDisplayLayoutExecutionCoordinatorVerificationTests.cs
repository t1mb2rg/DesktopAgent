using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class TargetDisplayLayoutExecutionCoordinatorVerificationTests
{
    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelPoint Sp = new(80, 80);
    private static readonly PixelRect WA = new(0, 0, 800, 600);
    private static int _ord;
    public TargetDisplayLayoutExecutionCoordinatorVerificationTests() { _ord = 0; }

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

    // ═══════════ 1. All exact + verification reader returns targets → Completed ═══════════
    [Fact] public async Task AllExact_ReaderReturnsTargets_Completed()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 320)), Snap("b", new(400, 480))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);

        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        writer.ScriptExactResult("b", new(320, 400), new(400, 480));

        // Scriptable: first read = pre-move, second read = post-move
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240)), Live("b", new(320, 400))]); // initial re-read
        reader.Enqueue([Live("a", new(240, 320)), Live("b", new(400, 480))]); // verification

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Passed, result.VerificationResult!.Status);
        Assert.Equal(ExecutionVerificationPhase.Forward, result.VerificationResult.Phase);
    }

    // ═══════════ 2. Verification read incomplete → PositionUncertain ═══════════
    [Fact] public async Task AllExact_VerificationIncomplete_PositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read OK
        reader.Enqueue(new DesktopIconReadResult(
            [Live("a", new(240, 320))], 1, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null)); // verification incomplete

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Uncertain, result.VerificationResult!.Status);
    }

    // ═══════════ 3. Reader throws during verification → PositionUncertain ═══════════
    [Fact] public async Task AllExact_ReaderThrows_PositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        // No second enqueue — will throw on verification read

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
    }

    // ═══════════ 4. StableId missing in verification → PositionUncertain ═══════════
    [Fact] public async Task AllExact_StableIdMissing_PositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("b", new(240, 320))]); // verification: wrong StableId

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
    }

    // ═══════════ 5. Duplicate StableId → PositionUncertain ═══════════
    [Fact] public async Task AllExact_DuplicateStableId_PositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("a", new(240, 320)), Live("a", new(400, 480))]); // duplicate

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
    }

    // ═══════════ 6. Position mismatch → PositionUncertain ═══════════
    [Fact] public async Task AllExact_PositionMismatch_PositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("a", new(999, 999))]); // wrong position

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Contains("验证位置", result.ForwardFailureMessage);
    }

    // ═══════════ 7. Two-Step all exact + verification returns targets → Completed ═══════════
    [Fact] public async Task TwoStep_AllExact_VerificationPasses_Completed()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 320)), Snap("b", new(400, 480))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        writer.ScriptExactResult("b", new(320, 400), new(400, 480));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240)), Live("b", new(320, 400))]); // initial
        reader.Enqueue([Live("a", new(240, 320)), Live("b", new(400, 480))]); // verification

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Passed, result.VerificationResult!.Status);
    }

    // ═══════════ 8. Rollback all exact + reader returns originals → FailedAndRolledBack ═══════════
    [Fact] public async Task RollbackExact_ReaderReturnsOriginals_FailedAndRolledBack()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240)); // rollback exact

        // Initial re-read (pre-move state), then rollback verification (back to origin)
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("a", new(160, 240))]); // rollback verification = back to original

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationPhase.Rollback, result.VerificationResult!.Phase);
        Assert.Equal(ExecutionVerificationStatus.Passed, result.VerificationResult.Status);
    }

    // ═══════════ 9. Rollback Writer exact but not restored → FailedRollbackIncomplete ═══════════
    [Fact] public async Task RollbackExact_NotRestored_FailedRollbackIncomplete()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240)); // rollback Writer says OK

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("a", new(999, 999))]); // rollback verification: not at original

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Failed, result.VerificationResult!.Status);
    }

    // ═══════════ 10. Rollback reader incomplete → PositionUncertain ═══════════
    [Fact] public async Task RollbackExact_ReadIncomplete_PositionUncertain()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read OK
        reader.Enqueue(new DesktopIconReadResult(
            [Live("a", new(160, 240))], 1, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null)); // rollback verification incomplete

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Uncertain, result.VerificationResult!.Status);
    }

    // ═══════════ 11. Writer boundary does not use WaitAsync(token) ═══════════
    [Fact]
    public async Task WriterWait_UsesRunAsync_NotWaitAsync()
    {
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("a", new(240, 320))]); // verification succeeds

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
    }

    // ═══════════ 12. Verification not skippable by UI cancel ═══════════
    [Fact]
    public async Task VerificationNotSkippableByUICancel()
    {
        // 2-step plan: step 0 blocks → cancel while blocked → release
        // → step 0 completes exact → step 1 pre-writer check detects cancel
        // → rolls back step 0 → rollback verification uses CancellationToken.None
        // → verification still completes despite cancelled token
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);

        var writer = new ScriptedDesktopIconPositionWriter();
        var started0 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release0 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.ScriptBlockingExact("a", new(240, 80), new(160, 80), started0, release0);
        // Rollback for step 0
        writer.ScriptExactResult("a", new(240, 80), new(160, 80));

        // Reader: initial re-read then rollback verification (returns original positions)
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80))]); // initial re-read
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80))]); // rollback verification: both at original

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        using var cts = new CancellationTokenSource();
        var task = coordinator.ExecuteAsync(plan, cts.Token);

        // Wait for writer step 0 to start
        await started0.Task;
        // Cancel while writer is blocked
        cts.Cancel();
        // Release writer
        release0.TrySetResult();

        var result = await task;
        // Rollback completed + verification ran despite cancelled token
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationPhase.Rollback, result.VerificationResult!.Phase);
    }
}
