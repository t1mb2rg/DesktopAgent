using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class PersistedRecoveryCoordinatorStagingTests
{
    private const string Monitor = @"\\.\DISPLAY1";
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1080);
    private static readonly PixelPoint A0 = new(100, 100);
    private static readonly PixelPoint B0 = new(300, 100);
    private static readonly PixelPoint Staging = new(700, 500);

    [Fact]
    public async Task RecoverAsync_StagingIntermediate_DelegatesToPrefixRecoveryAndRestores()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableWorld(
            Live("a", Staging),
            Live("b", B0)); // prefix 1
        var store = new Store(Checkpoint(source, plan));
        var coordinator = CreateCoordinator(store, world);

        var result = await coordinator.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        var call = Assert.Single(world.MoveCalls);
        Assert.Equal("a", call.StableId);
        Assert.Equal(A0, call.Target);
        Assert.Equal(RecoverySessionLifecycle.FailedAndRolledBack, store.Current.Lifecycle);
        Assert.NotEqual("RecoveryStagingNotSupported", store.Current.RecoveryFailureType);
    }

    [Fact]
    public async Task RecoverAsync_StagingCommitted_RecognizesCurrentFactWithoutWriter()
    {
        var (source, plan) = SwapPlan();
        var world = new MutableWorld(
            Live("a", B0),
            Live("b", A0)); // final prefix
        var store = new Store(Checkpoint(source, plan));
        var coordinator = CreateCoordinator(store, world);

        var result = await coordinator.RecoverAsync(store.Current);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Empty(world.MoveCalls);
        Assert.Equal(RecoverySessionLifecycle.Completed, store.Current.Lifecycle);
        Assert.NotEqual("RecoveryStagingNotSupported", store.Current.RecoveryFailureType);
    }

    private static PersistedRecoveryCoordinator CreateCoordinator(
        IRecoverySessionStore store,
        MutableWorld world)
        => new(
            store,
            new FakeDisplayTopologyReader(Monitor, WorkArea),
            world,
            world,
            new StaThreadExecutor());

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
        var items = new[]
        {
            MoveItem("a", A0, B0),
            MoveItem("b", B0, A0),
        };
        var source = new TargetDisplayLayoutRestorePlan(
            Monitor,
            snapshotTargetIconCount: 2,
            alreadyAtTargetCount: 0,
            moveRequiredCount: 2,
            missingCount: 0,
            blockedCount: 0,
            hasBlockingErrors: false,
            requiresTemporaryStaging: true,
            isNoOp: false,
            items,
            Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            new[] { new TargetDisplayLayoutRestoreCycle(new[] { "a", "b" }) },
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: null);

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

    private sealed class MutableWorld : IDesktopIconReader, IDesktopIconPositionWriter
    {
        private readonly Dictionary<string, DesktopIconDescriptor> _icons;

        public MutableWorld(params DesktopIconDescriptor[] icons)
        {
            _icons = icons.ToDictionary(icon => icon.StableId, StringComparer.Ordinal);
        }

        public List<(string StableId, PixelPoint Target)> MoveCalls { get; } = [];

        public DesktopIconReadResult ReadDesktopIcons()
            => new(
                _icons.Values.OrderBy(icon => icon.StableId, StringComparer.Ordinal).ToArray(),
                0,
                ConnectionMethod.FindWindowSW,
                TimeSpan.Zero,
                null);

        public DesktopIconPositioningCapabilities GetCapabilities()
            => new(
                false,
                new PixelPoint(75, 75),
                ConnectionMethod.FindWindowSW,
                null);

        public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
        {
            var current = _icons[stableId];
            var previous = current.PixelPosition;
            MoveCalls.Add((stableId, targetPosition));
            _icons[stableId] = current with
            {
                PixelPosition = targetPosition,
                MonitorDeviceName = Monitor,
            };

            return new DesktopIconMoveResult(
                stableId,
                previous,
                targetPosition,
                targetPosition,
                ConnectionMethod.FindWindowSW,
                null);
        }
    }

    private sealed class Store : IRecoverySessionStore
    {
        public Store(RecoverySessionRecord current)
        {
            Current = current;
        }

        public RecoverySessionRecord Current { get; private set; }

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
            if (record.SessionId != Current.SessionId || record.Revision != Current.Revision)
                throw new InvalidOperationException("CAS mismatch");

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
