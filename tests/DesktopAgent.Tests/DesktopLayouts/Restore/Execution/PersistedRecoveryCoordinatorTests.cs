using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class PersistedRecoveryCoordinatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    private const string TD = @"\\.\DISPLAY1";
    private static readonly PixelRect WA = new(0, 0, 1920, 1080);
    private static readonly PixelPoint A0 = new(160, 80);
    private static readonly PixelPoint A1 = new(240, 80);
    private static readonly PixelPoint B0 = new(160, 160);
    private static readonly PixelPoint B1 = new(240, 160);
    private static readonly PixelPoint C0 = new(160, 240);
    private static readonly PixelPoint C1 = new(240, 240);

    public PersistedRecoveryCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Tests.RecoveryR2.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ObservedDesired_WithoutForwardCheckpoint_CommitsWithoutWriter()
    {
        var step = Step(0, "a", A0, A1);
        var seed = await SeedAsync(Plan(step));

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A1));
        var writer = new MutableWriter(events, desktop);
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(events, e => e.StartsWith("Writer:", StringComparison.Ordinal));

        var fresh = await CreateStore().GetAsync(seed.SessionId);
        Assert.NotNull(fresh);
        Assert.Equal(RecoverySessionLifecycle.Completed, fresh.Lifecycle);
        Assert.Empty(fresh.ForwardAttempts);
        Assert.Empty(fresh.CompletedForwardStepIndexes);
        Assert.NotNull(fresh.VerificationResult);
        Assert.Equal(ExecutionVerificationPhase.Forward, fresh.VerificationResult.Phase);
        Assert.Equal(ExecutionVerificationStatus.Passed, fresh.VerificationResult.Status);
    }

    [Fact]
    public async Task ObservedOriginal_WithStaleForwardCheckpoint_RestoresWithoutWriter()
    {
        var step = Step(0, "a", A0, A1);
        var fwd = TargetDisplayLayoutExecutionAttemptResult.FromExactForward(
            0,
            0,
            step,
            Move("a", A0, A1));

        var seed = await SeedAsync(
            Plan(step),
            forwardAttempts: new[] { fwd },
            completedForward: new[] { 0 },
            forwardFailureType: "Interrupted",
            forwardFailureMessage: "crash");

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A0));
        var writer = new MutableWriter(events, desktop);
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.DoesNotContain(events, e => e.StartsWith("Writer:", StringComparison.Ordinal));

        var fresh = await CreateStore().GetAsync(seed.SessionId);
        Assert.NotNull(fresh);
        Assert.Equal(RecoverySessionLifecycle.FailedAndRolledBack, fresh.Lifecycle);
        Assert.Empty(fresh.RollbackAttempts);
        Assert.Empty(fresh.CompletedRollbackSourceStepIndexes);
        Assert.Equal(ExecutionVerificationPhase.Rollback, fresh.VerificationResult?.Phase);
        Assert.Equal(ExecutionVerificationStatus.Passed, fresh.VerificationResult?.Status);
    }

    [Fact]
    public async Task ObservedThirdPosition_PersistsPositionUncertainWithoutWriter()
    {
        var seed = await SeedAsync(Plan(Step(0, "a", A0, A1)));

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", new PixelPoint(999, 999)));
        var writer = new MutableWriter(events, desktop);
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.Equal("RecoveryObservationNeedsAttention", result.RecoveryFailureType);
        Assert.DoesNotContain(events, e => e.StartsWith("Writer:", StringComparison.Ordinal));

        var fresh = await CreateStore().GetAsync(seed.SessionId);
        Assert.NotNull(fresh);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, fresh.Lifecycle);
        Assert.Equal("RecoveryObservationNeedsAttention", fresh.RecoveryFailureType);
    }

    [Fact]
    public async Task IncompleteRead_PersistsPositionUncertainWithoutWriter()
    {
        var seed = await SeedAsync(Plan(Step(0, "a", A0, A1)));

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A1))
        {
            FailedItemCount = 1,
        };
        var writer = new MutableWriter(events, desktop);
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RecoveryObservationNeedsAttention", result.RecoveryFailureType);
        Assert.DoesNotContain(events, e => e.StartsWith("Writer:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MixedKnown_RollsBackOnlyObservedDesired_ThenRestores()
    {
        var plan = Plan(
            Step(0, "a", A0, A1),
            Step(1, "b", B0, B1));
        var seed = await SeedAsync(plan);

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A1), ("b", B0));
        var writer = new MutableWriter(events, desktop);
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(new[] { "a" }, writer.WrittenStableIds);
        Assert.Equal(A0, desktop.GetPosition("a"));
        Assert.Equal(B0, desktop.GetPosition("b"));

        var fresh = await CreateStore().GetAsync(seed.SessionId);
        Assert.NotNull(fresh);
        Assert.Equal(RecoverySessionLifecycle.FailedAndRolledBack, fresh.Lifecycle);
        var attempt = Assert.Single(fresh.RollbackAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess, attempt.Outcome);
        Assert.Equal(new[] { 0 }, fresh.CompletedRollbackSourceStepIndexes);
    }

    [Fact]
    public async Task WriterThrowsAfterSideEffect_ObservationWinsAndRestores()
    {
        var plan = Plan(
            Step(0, "a", A0, A1),
            Step(1, "b", B0, B1));
        var seed = await SeedAsync(plan);

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A1), ("b", B0));
        var writer = new MutableWriter(events, desktop)
        {
            Behavior = WriterBehavior.ThrowAfterMove,
        };
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.False(result.IsPositionUncertain);
        Assert.Equal(A0, desktop.GetPosition("a"));

        var fresh = await CreateStore().GetAsync(seed.SessionId);
        Assert.NotNull(fresh);
        Assert.Equal(RecoverySessionLifecycle.FailedAndRolledBack, fresh.Lifecycle);
        var attempt = Assert.Single(fresh.RollbackAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.WriterException, attempt.Outcome);
        Assert.Empty(fresh.CompletedRollbackSourceStepIndexes);
    }

    [Fact]
    public async Task WriterThrowsBeforeSideEffect_RemainsKnownIncompleteNotUncertain()
    {
        var plan = Plan(
            Step(0, "a", A0, A1),
            Step(1, "b", B0, B1));
        var seed = await SeedAsync(plan);

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A1), ("b", B0));
        var writer = new MutableWriter(events, desktop)
        {
            Behavior = WriterBehavior.ThrowBeforeMove,
        };
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.Equal("RecoveryWriteDidNotRestore", result.RecoveryFailureType);

        var fresh = await CreateStore().GetAsync(seed.SessionId);
        Assert.NotNull(fresh);
        Assert.Equal(RecoverySessionLifecycle.FailedRollbackIncomplete, fresh.Lifecycle);
        Assert.False(fresh.IsPositionUncertain);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.WriterException,
            Assert.Single(fresh.RollbackAttempts).Outcome);
    }

    [Fact]
    public async Task WriterStableIdMismatch_FailsClosedEvenIfPlannedPositionsLookRestored()
    {
        var plan = Plan(
            Step(0, "a", A0, A1),
            Step(1, "b", B0, B1));
        var seed = await SeedAsync(plan);

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A1), ("b", B0));
        var writer = new MutableWriter(events, desktop)
        {
            Behavior = WriterBehavior.WrongStableIdAfterMove,
        };
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.IsPositionUncertain);
        Assert.Equal("RollbackStableIdMismatch", result.RecoveryFailureType);
        Assert.Equal(A0, desktop.GetPosition("a"));

        var fresh = await CreateStore().GetAsync(seed.SessionId);
        Assert.NotNull(fresh);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, fresh.Lifecycle);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.StableIdMismatch,
            Assert.Single(fresh.RollbackAttempts).Outcome);
    }

    [Fact]
    public async Task AutoArrangeEnabled_LeavesMixedStateKnownAndDoesNotWrite()
    {
        var plan = Plan(
            Step(0, "a", A0, A1),
            Step(1, "b", B0, B1));
        var seed = await SeedAsync(plan);

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A1), ("b", B0));
        var writer = new MutableWriter(events, desktop)
        {
            IsAutoArrangeEnabled = true,
        };
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.False(result.IsPositionUncertain);
        Assert.Equal("RecoveryAutoArrangeEnabled", result.RecoveryFailureType);
        Assert.Empty(writer.WrittenStableIds);
    }

    [Fact]
    public async Task MultipleDesiredItems_CheckpointsBetweenWritesUsingFreshRevision()
    {
        var plan = Plan(
            Step(0, "a", A0, A1),
            Step(1, "b", B0, B1),
            Step(2, "c", C0, C1));
        var seed = await SeedAsync(plan);

        var events = new List<string>();
        var desktop = new MutableDesktopReader(
            events,
            ("a", A1),
            ("b", B1),
            ("c", C0));
        var writer = new MutableWriter(events, desktop);
        var store = new EventStore(_dbPath, events);
        var coordinator = Coordinator(store, desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.Equal(new[] { "b", "a" }, writer.WrittenStableIds);
        Assert.Contains("Update:Executing:Revision1", events);
        Assert.Contains("Update:FailedAndRolledBack:Revision2", events);

        var fresh = await CreateStore().GetAsync(seed.SessionId);
        Assert.NotNull(fresh);
        Assert.Equal(3, fresh.Revision);
        Assert.Equal(2, fresh.RollbackAttempts.Count);
        Assert.Equal(new[] { 0, 1 }, fresh.CompletedRollbackSourceStepIndexes);
    }

    [Fact]
    public async Task SafeTerminalPersistFailure_ReturnsUnpersistedPositionUncertain()
    {
        var seed = await SeedAsync(Plan(Step(0, "a", A0, A1)));

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A1));
        var writer = new MutableWriter(events, desktop);
        var store = new EventStore(_dbPath, events)
        {
            FailUpdate = true,
        };
        var coordinator = Coordinator(store, desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("CompletedTerminalPersistFailed", result.RecoveryFailureType);
        Assert.DoesNotContain(events, e => e.StartsWith("Writer:", StringComparison.Ordinal));

        var fresh = await CreateStore().GetAsync(seed.SessionId);
        Assert.NotNull(fresh);
        Assert.Equal(RecoverySessionLifecycle.Executing, fresh.Lifecycle);
    }

    [Fact]
    public async Task StagingPlan_FailsClosedWithoutWriter()
    {
        var staging = new PixelPoint(600, 500);
        var steps = new[]
        {
            new TargetDisplayLayoutExecutionStep(
                0, "a", "N_a", A0, staging,
                TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, TD, 0),
            new TargetDisplayLayoutExecutionStep(
                1, "a", "N_a", staging, A1,
                TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, TD, 0),
        };
        var execPlan = new TargetDisplayLayoutExecutionPlan(
            TD,
            true,
            false,
            steps,
            new[] { new TargetDisplayLayoutStagingAssignment("a", staging, 0, 1) },
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        var seed = await SeedAsync(execPlan);

        var events = new List<string>();
        var desktop = new MutableDesktopReader(events, ("a", A1));
        var writer = new MutableWriter(events, desktop);
        var coordinator = Coordinator(new EventStore(_dbPath, events), desktop, writer);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RecoveryStagingNotSupported", result.RecoveryFailureType);
        Assert.Empty(writer.WrittenStableIds);
    }

    private PersistedRecoveryCoordinator Coordinator(
        IRecoverySessionStore store,
        IDesktopIconReader reader,
        IDesktopIconPositionWriter writer)
        => new(
            store,
            new FakeDisplayTopologyReader(TD, WA),
            reader,
            writer,
            new StaThreadExecutor());

    private SqliteRecoverySessionStore CreateStore() => new(_dbPath);

    private async Task<RecoverySessionRecord> SeedAsync(
        TargetDisplayLayoutExecutionPlan executionPlan,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult>? forwardAttempts = null,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult>? rollbackAttempts = null,
        IReadOnlyList<int>? completedForward = null,
        IReadOnlyList<int>? completedRollback = null,
        string? forwardFailureType = null,
        string? forwardFailureMessage = null)
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = Guid.NewGuid(),
            TargetDeviceName = TD,
            StartedAtUtc = DateTimeOffset.UtcNow,
            SourceRestorePlan = RestorePlanFor(executionPlan),
            ExecutionPlan = executionPlan,
            Lifecycle = RecoverySessionLifecycle.Executing,
            RequiresManualRecovery = false,
            IsPositionUncertain = false,
            ForwardAttempts = forwardAttempts ?? Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            RollbackAttempts = rollbackAttempts ?? Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            CompletedForwardStepIndexes = completedForward ?? Array.Empty<int>(),
            CompletedRollbackSourceStepIndexes = completedRollback ?? Array.Empty<int>(),
            ForwardFailureType = forwardFailureType,
            ForwardFailureMessage = forwardFailureMessage,
            Revision = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        record.Validate();
        await store.SaveAsync(record);
        return (await store.GetAsync(record.SessionId))!;
    }

    private static TargetDisplayLayoutRestorePlan RestorePlanFor(
        TargetDisplayLayoutExecutionPlan executionPlan)
    {
        var directItems = executionPlan.Steps
            .Where(step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTarget)
            .Select(step => new TargetDisplayLayoutRestorePlanItem(
                step.StableId,
                step.DisplayName,
                step.ToPosition,
                step.FromPosition,
                TD,
                TargetDisplayLayoutRestoreItemState.MoveRequired,
                true,
                false))
            .ToArray();

        // Staging 测试只需要一个结构合法的 SourceRestorePlan；具体 staging 语义由 ExecutionPlan 冻结副本承担。
        if (directItems.Length == 0 && executionPlan.Steps.Count > 0)
        {
            var first = executionPlan.Steps[0];
            directItems =
            [
                new TargetDisplayLayoutRestorePlanItem(
                    first.StableId,
                    first.DisplayName,
                    executionPlan.Steps[^1].ToPosition,
                    first.FromPosition,
                    TD,
                    TargetDisplayLayoutRestoreItemState.MoveRequired,
                    true,
                    false)
            ];
        }

        return new TargetDisplayLayoutRestorePlan(
            TD,
            snapshotTargetIconCount: directItems.Length,
            alreadyAtTargetCount: 0,
            moveRequiredCount: directItems.Length,
            missingCount: 0,
            blockedCount: 0,
            hasBlockingErrors: false,
            requiresTemporaryStaging: executionPlan.UsesTemporaryStaging,
            isNoOp: directItems.Length == 0,
            items: directItems,
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: directItems.Select(item => item.StableId).ToArray());
    }

    private static TargetDisplayLayoutExecutionStep Step(
        int index,
        string stableId,
        PixelPoint from,
        PixelPoint to)
        => new(
            index,
            stableId,
            $"N_{stableId}",
            from,
            to,
            TargetDisplayLayoutExecutionStepKind.MoveToTarget,
            TD);

    private static TargetDisplayLayoutExecutionPlan Plan(
        params TargetDisplayLayoutExecutionStep[] steps)
        => new(
            TD,
            true,
            steps.Length == 0,
            steps,
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

    private static DesktopIconMoveResult Move(
        string stableId,
        PixelPoint previous,
        PixelPoint target)
        => new(
            stableId,
            previous,
            target,
            target,
            ConnectionMethod.FindWindowSW,
            null);

    private sealed class EventStore : IRecoverySessionStore
    {
        private readonly SqliteRecoverySessionStore _inner;
        public List<string> Events { get; }
        public bool FailUpdate { get; init; }

        public EventStore(string dbPath, List<string> events)
        {
            _inner = new SqliteRecoverySessionStore(dbPath);
            Events = events;
        }

        public Task InitializeAsync(CancellationToken ct = default)
            => _inner.InitializeAsync(ct);

        public Task SaveAsync(RecoverySessionRecord record, CancellationToken ct = default)
            => _inner.SaveAsync(record, ct);

        public async Task<RecoverySessionRecord> UpdateAsync(
            RecoverySessionRecord record,
            CancellationToken ct = default)
        {
            Events.Add($"Update:{record.Lifecycle}:Revision{record.Revision}");
            if (FailUpdate)
                throw new InvalidOperationException("injected update failure");

            return await _inner.UpdateAsync(record, ct);
        }

        public async Task<RecoverySessionRecord?> GetAsync(
            Guid sessionId,
            CancellationToken ct = default)
        {
            Events.Add("Get");
            return await _inner.GetAsync(sessionId, ct);
        }

        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken ct = default)
            => _inner.ListUnresolvedAsync(limit, ct);
    }

    private sealed class MutableDesktopReader : IDesktopIconReader
    {
        private readonly Dictionary<string, PixelPoint> _positions =
            new(StringComparer.Ordinal);
        private readonly List<string> _events;

        public int FailedItemCount { get; set; }

        public MutableDesktopReader(
            List<string> events,
            params (string StableId, PixelPoint Position)[] items)
        {
            _events = events;
            foreach (var item in items)
                _positions[item.StableId] = item.Position;
        }

        public PixelPoint GetPosition(string stableId) => _positions[stableId];

        public void SetPosition(string stableId, PixelPoint position)
            => _positions[stableId] = position;

        public DesktopIconReadResult ReadDesktopIcons()
        {
            _events.Add("Read");
            var icons = _positions
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new DesktopIconDescriptor(
                    pair.Key,
                    $"N_{pair.Key}",
                    null,
                    ItemKind.File,
                    pair.Value,
                    TD,
                    false,
                    true))
                .ToArray();

            return new DesktopIconReadResult(
                icons,
                FailedItemCount,
                ConnectionMethod.FindWindowSW,
                TimeSpan.Zero,
                null);
        }
    }

    private enum WriterBehavior
    {
        Exact,
        ThrowBeforeMove,
        ThrowAfterMove,
        WrongStableIdAfterMove,
    }

    private sealed class MutableWriter : IDesktopIconPositionWriter
    {
        private readonly List<string> _events;
        private readonly MutableDesktopReader _desktop;

        public WriterBehavior Behavior { get; init; } = WriterBehavior.Exact;
        public bool IsAutoArrangeEnabled { get; init; }
        public List<string> WrittenStableIds { get; } = new();

        public MutableWriter(List<string> events, MutableDesktopReader desktop)
        {
            _events = events;
            _desktop = desktop;
        }

        public DesktopIconPositioningCapabilities GetCapabilities()
        {
            _events.Add("Capabilities");
            return new DesktopIconPositioningCapabilities(
                IsAutoArrangeEnabled,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                null);
        }

        public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint target)
        {
            _events.Add($"Writer:{stableId}");
            WrittenStableIds.Add(stableId);

            var previous = _desktop.GetPosition(stableId);

            if (Behavior == WriterBehavior.ThrowBeforeMove)
                throw new InvalidOperationException("Writer failed before move");

            _desktop.SetPosition(stableId, target);

            if (Behavior == WriterBehavior.ThrowAfterMove)
                throw new InvalidOperationException("Writer failed after move");

            if (Behavior == WriterBehavior.WrongStableIdAfterMove)
            {
                return new DesktopIconMoveResult(
                    "WRONG",
                    previous,
                    target,
                    target,
                    ConnectionMethod.FindWindowSW,
                    null);
            }

            return new DesktopIconMoveResult(
                stableId,
                previous,
                target,
                target,
                ConnectionMethod.FindWindowSW,
                null);
        }
    }
}
