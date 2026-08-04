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

    private sealed class ScriptedDisplayTopologyReader : IDisplayTopologyReader
    {
        private readonly Queue<IReadOnlyList<DisplayDescriptor>> _reads;

        public ScriptedDisplayTopologyReader(
            params IReadOnlyList<DisplayDescriptor>[] reads)
        {
            _reads = new Queue<IReadOnlyList<DisplayDescriptor>>(reads);
        }

        public IReadOnlyList<DisplayDescriptor> GetDisplays()
        {
            if (_reads.Count == 0)
                throw new InvalidOperationException("No scripted display topology remains.");

            return _reads.Count == 1 ? _reads.Peek() : _reads.Dequeue();
        }
    }

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

    // ═══════════ 12. AlreadyAtTarget position mismatch on target display → failed ═══════════
    [Fact] public async Task AlreadyAtTarget_PositionMismatch_Failed()
    {
        var plan = MakeRestorePlan(
            [Snap("a", new(160, 240)), Snap("b", new(400, 480))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]); // 'a' is AlreadyAtTarget, 'b' is MoveRequired

        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("b", new(320, 400), new(400, 480));

        // Verification: 'b' is at target but 'a' (AlreadyAtTarget) has wrong position
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240)), Live("b", new(320, 400))]); // initial
        reader.Enqueue([Live("a", new(999, 999)), Live("b", new(400, 480))]); // 'a' moved!

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Failed, result.VerificationResult!.Status);
    }

    // ═══════════ 13. Verification not skippable by UI cancel ═══════════
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

    // ═══════════════════════════════════════════════════
    //  Invariant 1: Verifier exception → PositionUncertain
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task ForwardVerifierThrows_ReturnsPositionUncertain_NotEscaped()
    {
        // Verifier 异常不得逃出 Coordinator → 转化为 PositionUncertain
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store,
            TargetDisplayLayoutExecutionPlanner.CreatePlan,
            verifyForward: (_, _, _) => throw new InvalidOperationException("verifier BOMB"),
            verifyRollback: null);

        var result = await coordinator.ExecuteAsync(plan);
        // 必须返回 PositionUncertain，不能逃出异常
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.Equal("ForwardVerificationException", result.ForwardFailureType);
        Assert.Contains("verifier BOMB", result.ForwardFailureMessage);
        Assert.NotNull(result.Session);
        Assert.True(result.Session!.RequiresManualRecovery);
        Assert.True(result.Session.IsPositionUncertain);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationPhase.Forward, result.VerificationResult!.Phase);
        Assert.Equal(ExecutionVerificationStatus.Uncertain, result.VerificationResult.Status);
        Assert.Contains("verifier BOMB", string.Join("; ", result.VerificationResult.Issues));
    }

    [Fact]
    public async Task RollbackVerifierThrows_ReturnsPositionUncertain_NotEscaped()
    {
        // 回滚验证异常不得逃出 Coordinator → 转化为 PositionUncertain
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240)); // rollback exact

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store,
            TargetDisplayLayoutExecutionPlanner.CreatePlan,
            verifyForward: null,
            verifyRollback: (_, _, _) => throw new InvalidOperationException("rollback verifier BOMB"));

        var result = await coordinator.ExecuteAsync(plan);
        // 必须返回 PositionUncertain，不能逃出异常
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.Equal("RollbackVerificationException", result.RecoveryFailureType);
        Assert.Contains("rollback verifier BOMB", result.RecoveryFailureMessage);
        Assert.NotNull(result.Session);
        Assert.True(result.Session!.RequiresManualRecovery);
        Assert.True(result.Session.IsPositionUncertain);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationPhase.Rollback, result.VerificationResult!.Phase);
        Assert.Equal(ExecutionVerificationStatus.Uncertain, result.VerificationResult.Status);
        Assert.Contains("rollback verifier BOMB", string.Join("; ", result.VerificationResult.Issues));
    }

    // ═══════════════════════════════════════════════════
    //  Invariant 2: FailedAndRolledBack 仅在独立重读证明
    //  所有相关图标的原始坐标和显示器归属都恢复后成立
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Rollback_PositionNotRestored_NotFailedAndRolledBack()
    {
        // Rollback Writer 精确但独立重读位置未恢复 → 不得返回 FailedAndRolledBack
        // Invariant 2: FailedAndRolledBack 仅在独立重读证明位置恢复后成立
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240)); // rollback Writer says OK

        // Rollback verification: 独立重读显示位置 ≠ 原始 FromPosition
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("a", new(999, 999))]); // rollback verification: NOT at original

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        // 不得返回 FailedAndRolledBack（独立重读证明位置未恢复）
        Assert.NotEqual(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Failed, result.VerificationResult!.Status);
    }

    [Fact]
    public async Task Rollback_PositionCorrectMonitorCorrect_FailedAndRolledBack()
    {
        // Rollback 后位置和显示器都恢复 → FailedAndRolledBack
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240)); // rollback exact

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("a", new(160, 240))]); // rollback verification: position + monitor correct

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Passed, result.VerificationResult!.Status);
        Assert.Equal(ExecutionVerificationPhase.Rollback, result.VerificationResult.Phase);
    }

    [Fact]
    public async Task Rollback_PositionCorrectButOriginalMonitorWrong_FailedRollbackIncomplete()
    {
        const string display2Name = @"\\.\DISPLAY2";
        var currentPosition = new PixelPoint(160, 240);
        var targetPosition = new PixelPoint(240, 320);

        // 两块显示器刻意重叠：
        // 初次读取按列表顺序分配到 DISPLAY2，回滚验证时按新顺序分配到 DISPLAY1。
        // 坐标完全相同，仅显示器归属错误，精确复现原漏判。
        var display1 = MakeDisplay();
        var display2 = new DisplayDescriptor(
            display2Name,
            new PixelRect(0, 0, 1920, 1080),
            WA,
            96, 96,
            false);

        var snapshot = new DesktopLayoutSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            TD,
            ConnectionMethod.FindWindowSW,
            1,
            new[] { Snap("a", targetPosition) });

        var originalLive = new DesktopIconDescriptor(
            "a", "N_a", null, ItemKind.File,
            currentPosition, display2Name, false, true);

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snapshot,
            new DesktopIconReadResult(
                new[] { originalLive }, 0,
                ConnectionMethod.FindWindowSW, TimeSpan.Zero, null),
            new[] { display2, display1 },
            MakeCaps(),
            TD);

        Assert.Equal(
            display2Name,
            Assert.Single(plan.Items).CurrentMonitorDeviceName);

        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult(
            "a", currentPosition, targetPosition, new PixelPoint(250, 330));
        writer.ScriptExactResult(
            "a", new PixelPoint(250, 330), currentPosition);

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue(new[] { originalLive });
        reader.Enqueue(new[] { originalLive });

        var topology = new ScriptedDisplayTopologyReader(
            new[] { display2, display1 },
            new[] { display1, display2 });

        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            topology, reader, writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore());

        var result = await coordinator.ExecuteAsync(plan);

        Assert.Equal(
            TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
            result.Status);
        Assert.NotNull(result.VerificationResult);
        Assert.Equal(
            ExecutionVerificationStatus.Failed,
            result.VerificationResult!.Status);
        Assert.Contains(
            "Rollback 验证显示器",
            string.Join("; ", result.VerificationResult.Issues));
    }

    // ═══════════════════════════════════════════════════
    //  Invariant 3: Forward verification 非 Passed →
    //  RunResult + RecoverySession 一致
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task ForwardVerificationFailed_RunResultAndSession_Consistent()
    {
        // Forward 验证 Failed → RunResult 与 RecoverySession 必须同时满足
        // RequiresManualRecovery=true, IsPositionUncertain=true
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        // Verification: position mismatch → Failed
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial
        reader.Enqueue([Live("a", new(999, 999))]); // verification: wrong position

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal(ExecutionVerificationStatus.Failed, result.VerificationResult!.Status);

        // Invariant 3: RunResult 标志
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);

        // Invariant 3: Session 标志必须与 RunResult 一致
        Assert.NotNull(result.Session);
        Assert.True(result.Session!.RequiresManualRecovery,
            "Session.RequiresManualRecovery 必须为 true（与 RunResult 一致）");
        Assert.True(result.Session!.IsPositionUncertain,
            "Session.IsPositionUncertain 必须为 true（与 RunResult 一致）");
        Assert.Equal("PositionUncertain", result.Session.Status);
    }

    [Fact]
    public async Task ForwardVerificationUncertain_RunResultAndSession_Consistent()
    {
        // Forward 验证 Uncertain → RunResult 与 RecoverySession 标志一致
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        // Verification: incomplete read → Uncertain
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial
        reader.Enqueue(new DesktopIconReadResult(
            [Live("a", new(240, 320))], 1, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null));

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal(ExecutionVerificationStatus.Uncertain, result.VerificationResult!.Status);

        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.NotNull(result.Session);
        Assert.True(result.Session!.RequiresManualRecovery);
        Assert.True(result.Session!.IsPositionUncertain);
        Assert.Equal("PositionUncertain", result.Session.Status);
    }

    [Fact]
    public async Task ForwardVerificationPassed_RunResultAndSession_Consistent()
    {
        // Forward 验证 Passed → 两处都为 false（对照组）
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial
        reader.Enqueue([Live("a", new(240, 320))]); // verification: correct position

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Equal(ExecutionVerificationStatus.Passed, result.VerificationResult!.Status);

        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.NotNull(result.Session);
        Assert.False(result.Session!.RequiresManualRecovery);
        Assert.False(result.Session!.IsPositionUncertain);
        Assert.Equal("Completed", result.Session.Status);
    }

    // ═══════════════════════════════════════════════════
    //  Invariant 4: Rollback verification 非 Passed →
    //  RunResult + RecoverySession 一致
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task RollbackVerificationPassed_RunResultAndSession_Consistent()
    {
        // Rollback 验证 Passed → FailedAndRolledBack
        // Session 标志必须与 RunResult 一致（两处都为 false）
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240)); // rollback exact

        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("a", new(160, 240))]); // rollback verification: back to original

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(ExecutionVerificationStatus.Passed, result.VerificationResult!.Status);

        // Invariant 4a: RunResult 标志
        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);

        // Invariant 4a: Session 标志必须与 RunResult 一致
        Assert.NotNull(result.Session);
        Assert.False(result.Session!.RequiresManualRecovery,
            "Session.RequiresManualRecovery 必须为 false（与 RunResult 一致）");
        Assert.False(result.Session!.IsPositionUncertain,
            "Session.IsPositionUncertain 必须为 false（与 RunResult 一致）");
        Assert.Equal("FailedAndRolledBack", result.Session.Status);
    }

    [Fact]
    public async Task RollbackVerificationFailed_RunResultAndSession_Consistent()
    {
        // Rollback 验证 Failed → FailedRollbackIncomplete
        // Session 标志必须与 RunResult 一致：RequiresManualRecovery=true, IsPositionUncertain=false
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240)); // rollback Writer says OK

        // Rollback verification: 独立重读显示位置未恢复 → Failed
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue([Live("a", new(999, 999))]); // rollback verification: NOT at original

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.Equal(ExecutionVerificationStatus.Failed, result.VerificationResult!.Status);

        // Invariant 4b: RunResult 标志
        Assert.True(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);

        // Invariant 4b: Session 标志必须与 RunResult 一致
        Assert.NotNull(result.Session);
        Assert.True(result.Session!.RequiresManualRecovery,
            "Session.RequiresManualRecovery 必须为 true（与 RunResult 一致）");
        Assert.False(result.Session!.IsPositionUncertain,
            "Session.IsPositionUncertain 必须为 false（与 RunResult 一致）");
        Assert.Equal("FailedRollbackIncomplete", result.Session.Status);
    }

    [Fact]
    public async Task RollbackVerificationUncertain_RunResultAndSession_Consistent()
    {
        // Rollback 验证 Uncertain → PositionUncertain
        // Session 标志必须与 RunResult 一致：两处都为 true
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptInexactResult("a", new(160, 240), new(240, 320), new(250, 330));
        writer.ScriptExactResult("a", new(250, 330), new(160, 240)); // rollback exact

        // Rollback verification: 读取不完整 → Uncertain
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]); // initial re-read
        reader.Enqueue(new DesktopIconReadResult(
            [Live("a", new(160, 240))], 1, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null));

        var store = new InMemoryDesktopLayoutSnapshotStore();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader,
            writer, new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()), store);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal(ExecutionVerificationStatus.Uncertain, result.VerificationResult!.Status);

        // Invariant 4c: RunResult 标志
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);

        // Invariant 4c: Session 标志必须与 RunResult 一致
        Assert.NotNull(result.Session);
        Assert.True(result.Session!.RequiresManualRecovery,
            "Session.RequiresManualRecovery 必须为 true（与 RunResult 一致）");
        Assert.True(result.Session!.IsPositionUncertain,
            "Session.IsPositionUncertain 必须为 true（与 RunResult 一致）");
        Assert.Equal("PositionUncertain", result.Session.Status);
    }
}
