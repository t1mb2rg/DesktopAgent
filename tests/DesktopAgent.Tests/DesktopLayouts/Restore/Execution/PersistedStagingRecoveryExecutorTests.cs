using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class PersistedStagingRecoveryExecutorTests
{
    private const string Monitor = @"\\.\DISPLAY1";
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1080);
    private static readonly PixelPoint A0 = new(100, 100);
    private static readonly PixelPoint B0 = new(300, 100);
    private static readonly PixelPoint C0 = new(500, 100);
    private static readonly PixelPoint C1 = new(900, 100);
    private static readonly PixelPoint Staging = new(700, 500);

    [Fact]
    public async Task PrefixZero_PersistsRestoredWithoutWriter()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableDesktopWorld(
            Live("a", A0),
            Live("b", B0));
        var store = new InMemoryRecoveryStore(Checkpoint(source, plan));
        var executor = CreateExecutor(store, world);

        var result = await executor.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Empty(world.MoveCalls);
        Assert.Equal(RecoverySessionLifecycle.FailedAndRolledBack, store.Current.Lifecycle);
        Assert.Equal(ExecutionVerificationStatus.Passed, store.Current.VerificationResult?.Status);
        Assert.Equal(ExecutionVerificationPhase.Rollback, store.Current.VerificationResult?.Phase);
    }

    [Fact]
    public async Task CommittedPrefix_PersistsCompletedWithoutRollbackWriter()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableDesktopWorld(
            Live("a", B0),
            Live("b", A0));
        var store = new InMemoryRecoveryStore(Checkpoint(source, plan));
        var executor = CreateExecutor(store, world);

        var result = await executor.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Empty(world.MoveCalls);
        Assert.Equal(RecoverySessionLifecycle.Completed, store.Current.Lifecycle);
        Assert.Equal(ExecutionVerificationPhase.Forward, store.Current.VerificationResult?.Phase);
    }

    [Fact]
    public async Task PrefixTwo_RollsBackExactlyStepOneThenStepZero()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableDesktopWorld(
            Live("a", Staging),
            Live("b", A0));
        var store = new InMemoryRecoveryStore(Checkpoint(source, plan));
        var executor = CreateExecutor(store, world);

        var result = await executor.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(
            new[] { ("b", B0), ("a", A0) },
            world.MoveCalls.Select(call => (call.StableId, call.Target)).ToArray());
        Assert.Equal(A0, world.GetPosition("a"));
        Assert.Equal(B0, world.GetPosition("b"));
        Assert.Equal(new[] { 0, 1 }, store.Current.CompletedRollbackSourceStepIndexes);
        Assert.Equal(2, store.Current.RollbackAttempts.Count);
    }

    [Fact]
    public async Task IntermediateAfterStagingExit_CanRollbackSameStableIdTwiceByDifferentStepIndex()
    {
        var (source, plan) = SwapPlusPendingPlan();
        // prefix 3: A 已从 staging 进入 Desired，B 已让位，C 尚未执行最后一步。
        var world = new MutableDesktopWorld(
            Live("a", B0),
            Live("b", A0),
            Live("c", C0));
        var store = new InMemoryRecoveryStore(Checkpoint(source, plan));
        var executor = CreateExecutor(store, world);

        var result = await executor.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(
            new[]
            {
                ("a", Staging),
                ("b", B0),
                ("a", A0),
            },
            world.MoveCalls.Select(call => (call.StableId, call.Target)).ToArray());
        Assert.Equal(2, world.MoveCalls.Count(call => call.StableId == "a"));
        Assert.Equal(A0, world.GetPosition("a"));
        Assert.Equal(B0, world.GetPosition("b"));
        Assert.Equal(C0, world.GetPosition("c"));
    }

    [Fact]
    public async Task WriterThrowsAfterPhysicalEffect_PostObservationAuthorizesRestored()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableDesktopWorld(
            Live("a", Staging),
            Live("b", B0));
        world.EnqueueBehavior(WriterBehavior.ThrowAfterEffect);
        var store = new InMemoryRecoveryStore(Checkpoint(source, plan));
        var executor = CreateExecutor(store, world);

        var result = await executor.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Single(world.MoveCalls);
        Assert.Equal(A0, world.GetPosition("a"));
        var attempt = Assert.Single(store.Current.RollbackAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.WriterException, attempt.Outcome);
        Assert.Empty(store.Current.CompletedRollbackSourceStepIndexes);
        Assert.Equal(ExecutionVerificationStatus.Passed, store.Current.VerificationResult?.Status);
    }

    [Fact]
    public async Task WriterThrowsBeforeEffect_KnownPrefixStopsAsRollbackIncomplete()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableDesktopWorld(
            Live("a", Staging),
            Live("b", B0));
        world.EnqueueBehavior(WriterBehavior.ThrowBeforeEffect);
        var store = new InMemoryRecoveryStore(Checkpoint(source, plan));
        var executor = CreateExecutor(store, world);

        var result = await executor.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.Equal(RecoverySessionLifecycle.FailedRollbackIncomplete, store.Current.Lifecycle);
        Assert.Equal("StagingRecoveryWriteDidNotRollbackPrefix", store.Current.RecoveryFailureType);
        Assert.Equal(Staging, world.GetPosition("a"));
    }

    [Fact]
    public async Task AutoArrangeEnabled_KnownPrefixStopsWithoutWriter()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableDesktopWorld(
            Live("a", Staging),
            Live("b", B0))
        {
            AutoArrangeEnabled = true,
        };
        var store = new InMemoryRecoveryStore(Checkpoint(source, plan));
        var executor = CreateExecutor(store, world);

        var result = await executor.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.Empty(world.MoveCalls);
        Assert.Equal("StagingRecoveryAutoArrangeEnabled", store.Current.RecoveryFailureType);
    }

    [Fact]
    public async Task TopologyChangesBeforeWriter_FailsClosedWithoutMove()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableDesktopWorld(
            Live("a", Staging),
            Live("b", B0));
        var topology = new SequenceTopologyReader(
            Display(WorkArea),
            Display(WorkArea),
            Display(new PixelRect(0, 0, 1600, 900)));
        var store = new InMemoryRecoveryStore(Checkpoint(source, plan));
        var executor = CreateExecutor(store, world, topology);

        var result = await executor.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Empty(world.MoveCalls);
        Assert.Equal("StagingRecoveryTopologyChangedBeforeWrite", store.Current.RecoveryFailureType);
    }

    [Fact]
    public async Task ExactPhysicalRollbackButCheckpointPersistenceFails_ReturnsUnpersistedUncertain()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableDesktopWorld(
            Live("a", Staging),
            Live("b", A0)); // prefix 2，第一步只会回到 prefix 1，不是终态
        var store = new InMemoryRecoveryStore(Checkpoint(source, plan))
        {
            FailNextUpdate = true,
        };
        var executor = CreateExecutor(store, world);

        var result = await executor.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.Equal(B0, world.GetPosition("b"));
        Assert.Equal(RecoverySessionLifecycle.Executing, store.Current.Lifecycle);
        Assert.Equal("StagingCheckpointPersistFailed", result.RecoveryFailureType);
    }

    private static PersistedStagingRecoveryExecutor CreateExecutor(
        IRecoverySessionStore store,
        MutableDesktopWorld world,
        IDisplayTopologyReader? topology = null)
    {
        var sta = new StaThreadExecutor();
        var actualTopology = topology ?? new FakeDisplayTopologyReader(Monitor, WorkArea);
        var observer = new RecoveryDesktopStateObserver(actualTopology, world, sta);
        return new PersistedStagingRecoveryExecutor(store, world, sta, observer);
    }

    private static RecoverySessionRecord Checkpoint(
        TargetDisplayLayoutRestorePlan source,
        TargetDisplayLayoutExecutionPlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = Guid.NewGuid(),
            TargetDeviceName = Monitor,
            StartedAtUtc = now,
            Lifecycle = RecoverySessionLifecycle.Executing,
            SourceRestorePlan = source,
            ExecutionPlan = plan,
            ForwardAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            RollbackAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            CompletedForwardStepIndexes = Array.Empty<int>(),
            CompletedRollbackSourceStepIndexes = Array.Empty<int>(),
            Revision = 1,
            UpdatedAtUtc = now,
        };
        record.Validate();
        return record;
    }

    private static (TargetDisplayLayoutRestorePlan Source, TargetDisplayLayoutExecutionPlan Plan) SwapPlan()
    {
        var source = SourcePlan(
            new[]
            {
                MoveItem("a", A0, B0),
                MoveItem("b", B0, A0),
            });
        var plan = new TargetDisplayLayoutExecutionPlan(
            Monitor,
            true,
            false,
            new[]
            {
                Step(0, "a", A0, Staging, TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, 0),
                Step(1, "b", B0, A0, TargetDisplayLayoutExecutionStepKind.MoveToTarget),
                Step(2, "a", Staging, B0, TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget),
            },
            new[] { new TargetDisplayLayoutStagingAssignment("a", Staging, 0, 2) },
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        return (source, plan);
    }

    private static (TargetDisplayLayoutRestorePlan Source, TargetDisplayLayoutExecutionPlan Plan) SwapPlusPendingPlan()
    {
        var source = SourcePlan(
            new[]
            {
                MoveItem("a", A0, B0),
                MoveItem("b", B0, A0),
                MoveItem("c", C0, C1),
            });
        var plan = new TargetDisplayLayoutExecutionPlan(
            Monitor,
            true,
            false,
            new[]
            {
                Step(0, "a", A0, Staging, TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, 0),
                Step(1, "b", B0, A0, TargetDisplayLayoutExecutionStepKind.MoveToTarget),
                Step(2, "a", Staging, B0, TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget),
                Step(3, "c", C0, C1, TargetDisplayLayoutExecutionStepKind.MoveToTarget),
            },
            new[] { new TargetDisplayLayoutStagingAssignment("a", Staging, 0, 2) },
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        return (source, plan);
    }

    private static TargetDisplayLayoutRestorePlan SourcePlan(
        IReadOnlyList<TargetDisplayLayoutRestorePlanItem> items)
        => new(
            Monitor,
            snapshotTargetIconCount: items.Count,
            alreadyAtTargetCount: 0,
            moveRequiredCount: items.Count,
            missingCount: 0,
            blockedCount: 0,
            hasBlockingErrors: false,
            requiresTemporaryStaging: true,
            isNoOp: false,
            items,
            Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            new[] { new TargetDisplayLayoutRestoreCycle(items.Select(item => item.StableId).Take(2).ToArray()) },
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);

    private static TargetDisplayLayoutRestorePlanItem MoveItem(
        string stableId,
        PixelPoint current,
        PixelPoint target)
        => new(
            stableId,
            stableId.ToUpperInvariant(),
            target,
            current,
            Monitor,
            TargetDisplayLayoutRestoreItemState.MoveRequired,
            true,
            false);

    private static TargetDisplayLayoutExecutionStep Step(
        int index,
        string stableId,
        PixelPoint from,
        PixelPoint to,
        TargetDisplayLayoutExecutionStepKind kind,
        int? stagingSequence = null)
        => new(
            index,
            stableId,
            stableId.ToUpperInvariant(),
            from,
            to,
            kind,
            Monitor,
            stagingSequence);

    private static DesktopIconDescriptor Live(string stableId, PixelPoint position)
        => new(
            stableId,
            stableId.ToUpperInvariant(),
            null,
            ItemKind.File,
            position,
            Monitor,
            false,
            true);

    private static DisplayDescriptor Display(PixelRect workArea)
        => new(Monitor, workArea, workArea, 96, 96, true);

    private enum WriterBehavior
    {
        Exact,
        ThrowBeforeEffect,
        ThrowAfterEffect,
    }

    private sealed class MutableDesktopWorld : IDesktopIconReader, IDesktopIconPositionWriter
    {
        private readonly Dictionary<string, DesktopIconDescriptor> _icons;
        private readonly Queue<WriterBehavior> _behaviors = new();

        public MutableDesktopWorld(params DesktopIconDescriptor[] icons)
        {
            _icons = icons.ToDictionary(icon => icon.StableId, StringComparer.Ordinal);
        }

        public bool AutoArrangeEnabled { get; init; }
        public List<(string StableId, PixelPoint Target)> MoveCalls { get; } = [];

        public void EnqueueBehavior(WriterBehavior behavior) => _behaviors.Enqueue(behavior);

        public PixelPoint GetPosition(string stableId) => _icons[stableId].PixelPosition;

        public DesktopIconReadResult ReadDesktopIcons()
            => new(
                _icons.Values.OrderBy(icon => icon.StableId, StringComparer.Ordinal).ToArray(),
                0,
                ConnectionMethod.FindWindowSW,
                TimeSpan.Zero,
                null);

        public DesktopIconPositioningCapabilities GetCapabilities()
            => new(
                AutoArrangeEnabled,
                new PixelPoint(75, 75),
                ConnectionMethod.FindWindowSW,
                null);

        public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
        {
            MoveCalls.Add((stableId, targetPosition));
            var behavior = _behaviors.Count > 0 ? _behaviors.Dequeue() : WriterBehavior.Exact;
            var current = _icons[stableId];
            var previous = current.PixelPosition;

            if (behavior == WriterBehavior.ThrowBeforeEffect)
                throw new InvalidOperationException("before effect");

            _icons[stableId] = current with
            {
                PixelPosition = targetPosition,
                MonitorDeviceName = Monitor,
            };

            if (behavior == WriterBehavior.ThrowAfterEffect)
                throw new InvalidOperationException("after effect");

            return new DesktopIconMoveResult(
                stableId,
                previous,
                targetPosition,
                targetPosition,
                ConnectionMethod.FindWindowSW,
                null);
        }
    }

    private sealed class SequenceTopologyReader : IDisplayTopologyReader
    {
        private readonly Queue<IReadOnlyList<DisplayDescriptor>> _samples;
        private IReadOnlyList<DisplayDescriptor> _last;

        public SequenceTopologyReader(params DisplayDescriptor[] samples)
        {
            _samples = new Queue<IReadOnlyList<DisplayDescriptor>>(
                samples.Select(sample => (IReadOnlyList<DisplayDescriptor>)new[] { sample }));
            _last = _samples.Count > 0
                ? _samples.Peek()
                : Array.Empty<DisplayDescriptor>();
        }

        public IReadOnlyList<DisplayDescriptor> GetDisplays()
        {
            if (_samples.Count > 0)
                _last = _samples.Dequeue();
            return _last;
        }
    }

    private sealed class InMemoryRecoveryStore : IRecoverySessionStore
    {
        public InMemoryRecoveryStore(RecoverySessionRecord current)
        {
            Current = current;
        }

        public RecoverySessionRecord Current { get; private set; }
        public bool FailNextUpdate { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RecoverySessionRecord> UpdateAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextUpdate)
            {
                FailNextUpdate = false;
                throw new InvalidOperationException("simulated persist failure");
            }

            if (record.SessionId != Current.SessionId)
                throw new InvalidOperationException("wrong session");
            if (record.Revision != Current.Revision)
                throw new InvalidOperationException("revision mismatch");

            var updated = record with
            {
                Revision = Current.Revision + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            updated.Validate();
            Current = updated;
            return Task.FromResult(updated);
        }

        public Task<RecoverySessionRecord?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<RecoverySessionRecord?>(
                sessionId == Current.SessionId ? Current : null);

        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecoverySessionRecord>>(
                Current.GetDisposition().IsUnresolved
                    ? new[] { Current }
                    : Array.Empty<RecoverySessionRecord>());
    }
}
