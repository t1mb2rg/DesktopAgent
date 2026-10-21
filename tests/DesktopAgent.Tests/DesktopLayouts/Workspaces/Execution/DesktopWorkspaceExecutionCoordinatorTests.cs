using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.DesktopLayouts.Workspaces.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Execution;

public sealed class DesktopWorkspaceExecutionCoordinatorTests
{
    [Fact]
    public async Task Execute_ExactSwap_PersistsAuthorityBeforeWriterAndCommitsAfterFreshVerification()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var snapshotStore = new InMemorySnapshotStore();
        var journalStore = new InMemoryJournalStore();
        var writer = new StatefulWriter(state, journalStore);
        using var sta = new StaThreadExecutor();
        var coordinator = CreateCoordinator(state, writer, sta, snapshotStore, journalStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.Committed, result.Status);
        Assert.True(result.WriterStarted);
        Assert.NotNull(result.Journal);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.Committed, result.Journal!.Lifecycle);
        Assert.Equal(3, writer.CallCount);
        Assert.Equal(new PixelPoint(0, 0), state.GetPosition("a"));
        Assert.Equal(new PixelPoint(80, 0), state.GetPosition("b"));
        Assert.NotNull(result.SafetySnapshotId);

        Assert.True(journalStore.History.Count >= 6);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.Prepared, journalStore.History[0]);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress, journalStore.History[1]);
        Assert.All(writer.LifecycleSeenBeforeCalls, lifecycle =>
            Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress, lifecycle));
        Assert.Equal(
            new[] { 0, 1, 2 },
            result.Journal.CompletedForwardStepIndexes);
        Assert.Empty(result.Journal.RollbackAttempts);
        Assert.Empty(result.VerificationIssues);
    }

    [Fact]
    public async Task Execute_SourceChangedAfterPreview_FailsBeforeSnapshotJournalAndWriter()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        state.SetPosition("a", new PixelPoint(160, 0));

        var snapshotStore = new InMemorySnapshotStore();
        var journalStore = new InMemoryJournalStore();
        var writer = new StatefulWriter(state, journalStore);
        using var sta = new StaThreadExecutor();
        var coordinator = CreateCoordinator(state, writer, sta, snapshotStore, journalStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.False(result.WriterStarted);
        Assert.Equal("WorkspaceExecutionPlanRejected", result.ErrorType);
        Assert.Equal(0, writer.CallCount);
        Assert.Empty(snapshotStore.Snapshots);
        Assert.Empty(journalStore.History);
    }

    [Fact]
    public async Task Execute_InexactThirdState_StopsAndPersistsNeedsAttentionWithoutBlindRollback()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var snapshotStore = new InMemorySnapshotStore();
        var journalStore = new InMemoryJournalStore();
        var writer = new StatefulWriter(state, journalStore)
        {
            InexactOnCall = 1,
        };
        using var sta = new StaThreadExecutor();
        var coordinator = CreateCoordinator(state, writer, sta, snapshotStore, journalStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.NeedsAttention, result.Status);
        Assert.True(result.WriterStarted);
        Assert.Equal(1, writer.CallCount);
        Assert.NotNull(result.Journal);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention, result.Journal!.Lifecycle);
        var attempt = Assert.Single(result.Journal.ForwardAttempts);
        Assert.Equal(DesktopWorkspaceExecutionAttemptOutcome.InexactKnown, attempt.Outcome);
        Assert.Empty(result.Journal.RollbackAttempts);
        Assert.Empty(result.Journal.CompletedForwardStepIndexes);
        Assert.Equal(new PixelPoint(161, 0), state.GetPosition("a"));
    }

    [Fact]
    public async Task Execute_CancelBetweenSteps_RollsBackOnlyKnownExactPrefixAndVerifiesSnapshot()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var snapshotStore = new InMemorySnapshotStore();
        var journalStore = new InMemoryJournalStore();
        using var cancellation = new CancellationTokenSource();
        var writer = new StatefulWriter(state, journalStore)
        {
            CancelAfterCall = 1,
            Cancellation = cancellation,
        };
        using var sta = new StaThreadExecutor();
        var coordinator = CreateCoordinator(state, writer, sta, snapshotStore, journalStore);

        var result = await coordinator.ExecuteAsync(confirmation, cancellation.Token);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.Restored, result.Status);
        Assert.True(result.WriterStarted);
        Assert.Equal(2, writer.CallCount);
        Assert.NotNull(result.Journal);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.Restored, result.Journal!.Lifecycle);
        Assert.Equal(new[] { 0 }, result.Journal.CompletedForwardStepIndexes);
        Assert.Equal(new[] { 0 }, result.Journal.CompletedRollbackSourceStepIndexes);
        Assert.Single(result.Journal.ForwardAttempts);
        Assert.Single(result.Journal.RollbackAttempts);
        Assert.Equal(DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess, result.Journal.RollbackAttempts[0].Outcome);
        Assert.Equal(new PixelPoint(80, 0), state.GetPosition("a"));
        Assert.Equal(new PixelPoint(0, 0), state.GetPosition("b"));
        Assert.Contains(DesktopWorkspaceExecutionJournalLifecycle.RollingBack, journalStore.History);
    }

    [Fact]
    public async Task Execute_PreparedJournalSaveFails_NeverStartsWriter()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var snapshotStore = new InMemorySnapshotStore();
        var journalStore = new InMemoryJournalStore { ThrowOnSave = true };
        var writer = new StatefulWriter(state, journalStore);
        using var sta = new StaThreadExecutor();
        var coordinator = CreateCoordinator(state, writer, sta, snapshotStore, journalStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.False(result.WriterStarted);
        Assert.Equal(0, writer.CallCount);
        Assert.Single(snapshotStore.Snapshots);
        Assert.Empty(journalStore.History);
    }

    private static DesktopWorkspaceExecutionCoordinator CreateCoordinator(
        MutableDesktopState state,
        StatefulWriter writer,
        StaThreadExecutor sta,
        InMemorySnapshotStore snapshotStore,
        InMemoryJournalStore journalStore)
    {
        return new DesktopWorkspaceExecutionCoordinator(
            new MutableObservationSource(state),
            writer,
            sta,
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            snapshotStore,
            journalStore);
    }

    private static DesktopWorkspaceFrozenPreview CreatePreview(DesktopObservation observation)
    {
        var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);
        Assert.True(geometryResult.Succeeded,
            string.Join("；", geometryResult.Issues.Select(static issue => issue.Message)));
        var geometry = geometryResult.Geometry!;
        var facts = DesktopWorkspaceLocalItemFactProjector.Project(observation, geometry);

        var blueprint = new DesktopWorkspaceBlueprint(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            revision: 1,
            designSummary: "交换两个开发项目的位置。",
            displayPolicies:
            [new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1),
                    DesktopWorkspaceAnchorKind.TopLeft,
                    DesktopWorkspacePackingOrder.RowMajor),
            ],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule("dev", "tools")]);
        var semantics = new DesktopSemanticUnderstanding(
            "两个开发项目。",
            [new DesktopSemanticCategory("dev", "开发", "开发工具")],
            facts.Select(fact => new DesktopSemanticAssignment(
                fact.StableId,
                "dev",
                1,
                "test")).ToArray());

        var compilation = DesktopWorkspaceCompiler.Compile(
            blueprint,
            semantics,
            geometry,
            facts);
        Assert.True(compilation.Succeeded,
            string.Join("；", compilation.Issues.Select(static issue => issue.Message)));
        var plan = Assert.IsType<CompiledWorkspacePlan>(compilation.Plan);
        var equivalence = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            blueprint,
            geometry,
            plan,
            facts);
        Assert.True(equivalence.Equivalent,
            string.Join("；", equivalence.Issues.Select(static issue => issue.Message)));
        var certificate = Assert.IsType<WorkspaceRealizationCertificate>(equivalence.Certificate);
        var source = new DesktopWorkspacePreviewSourceSnapshot(
            observation.ObservationId,
            facts,
            facts.Select(fact => new DesktopWorkspacePreviewItemPresentation(
                fact.StableId,
                fact.StableId.ToUpperInvariant())).ToArray());

        return DesktopWorkspaceFrozenPreviewFactory.Create(
            blueprint,
            geometry,
            plan,
            certificate,
            source);
    }

    private sealed class MutableDesktopState
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, PixelPoint> _positions;

        public DisplayDescriptor Display { get; } = new(
            "DISPLAY1",
            new PixelRect(0, 0, 240, 80),
            new PixelRect(0, 0, 240, 80),
            96,
            96,
            true);

        public DesktopIconPositioningCapabilities Capabilities { get; } = new(
            false,
            new PixelPoint(80, 80),
            ConnectionMethod.FindWindowSW,
            null);

        private MutableDesktopState(Dictionary<string, PixelPoint> positions)
        {
            _positions = positions;
        }

        public static MutableDesktopState CreateSwap()
            => new(new Dictionary<string, PixelPoint>(StringComparer.Ordinal)
            {
                ["a"] = new PixelPoint(80, 0),
                ["b"] = new PixelPoint(0, 0),
            });

        public PixelPoint GetPosition(string stableId)
        {
            lock (_gate)
                return _positions[stableId];
        }

        public void SetPosition(string stableId, PixelPoint position)
        {
            lock (_gate)
                _positions[stableId] = position;
        }

        public DesktopObservation Capture()
        {
            lock (_gate)
            {
                var icons = _positions
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new DesktopIconDescriptor(
                        pair.Key,
                        pair.Key.ToUpperInvariant(),
                        null,
                        ItemKind.File,
                        pair.Value,
                        Display.DeviceName,
                        false,
                        true))
                    .ToArray();
                return new DesktopObservation(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    [Display],
                    Capabilities,
                    icons,
                    ConnectionMethod.FindWindowSW,
                    TimeSpan.Zero);
            }
        }
    }

    private sealed class MutableObservationSource : IDesktopObservationSource
    {
        private readonly MutableDesktopState _state;

        public MutableObservationSource(MutableDesktopState state)
        {
            _state = state;
        }

        public Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(DesktopObservationCaptureResult.Captured(_state.Capture()));
        }
    }

    private sealed class StatefulWriter : IDesktopIconPositionWriter
    {
        private readonly MutableDesktopState _state;
        private readonly InMemoryJournalStore _journalStore;

        public int CallCount { get; private set; }
        public int? InexactOnCall { get; init; }
        public int? CancelAfterCall { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public List<DesktopWorkspaceExecutionJournalLifecycle?> LifecycleSeenBeforeCalls { get; } = [];

        public StatefulWriter(MutableDesktopState state, InMemoryJournalStore journalStore)
        {
            _state = state;
            _journalStore = journalStore;
        }

        public DesktopIconPositioningCapabilities GetCapabilities() => _state.Capabilities;

        public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
        {
            LifecycleSeenBeforeCalls.Add(_journalStore.Current?.Lifecycle);
            CallCount++;
            var previous = _state.GetPosition(stableId);
            var actual = InexactOnCall == CallCount
                ? new PixelPoint(targetPosition.X + 1, targetPosition.Y)
                : targetPosition;
            _state.SetPosition(stableId, actual);

            if (CancelAfterCall == CallCount)
                Cancellation?.Cancel();

            return new DesktopIconMoveResult(
                stableId,
                previous,
                targetPosition,
                actual,
                ConnectionMethod.FindWindowSW,
                null);
        }
    }

    private sealed class InMemorySnapshotStore : IDesktopLayoutSnapshotStore
    {
        public Dictionary<Guid, DesktopLayoutSnapshot> Snapshots { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SaveAsync(DesktopLayoutSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshots.Add(snapshot.SnapshotId, snapshot);
            return Task.CompletedTask;
        }

        public Task<DesktopLayoutSnapshot?> GetAsync(Guid snapshotId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshots.TryGetValue(snapshotId, out var snapshot);
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DesktopLayoutSnapshotSummary>>(Array.Empty<DesktopLayoutSnapshotSummary>());
        }

        public Task<bool> DeleteAsync(Guid snapshotId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshots.Remove(snapshotId));
        }
    }

    private sealed class InMemoryJournalStore : IDesktopWorkspaceExecutionJournalStore
    {
        private readonly Dictionary<Guid, DesktopWorkspaceExecutionJournal> _journals = [];

        public bool ThrowOnSave { get; init; }
        public List<DesktopWorkspaceExecutionJournalLifecycle> History { get; } = [];
        public DesktopWorkspaceExecutionJournal? Current => _journals.Values.SingleOrDefault();

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SaveAsync(
            DesktopWorkspaceExecutionJournal journal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnSave)
                throw new IOException("journal save failed");
            journal.Validate();
            _journals.Add(journal.SessionId, journal);
            History.Add(journal.Lifecycle);
            return Task.CompletedTask;
        }

        public Task<DesktopWorkspaceExecutionJournal> UpdateAsync(
            DesktopWorkspaceExecutionJournal journal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = _journals[journal.SessionId];
            if (current.Revision != journal.Revision)
                throw new InvalidOperationException("Revision conflict");
            journal.Validate();
            var updated = journal with
            {
                Revision = journal.Revision + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            updated.Validate();
            _journals[journal.SessionId] = updated;
            History.Add(updated.Lifecycle);
            return Task.FromResult(updated);
        }

        public Task<DesktopWorkspaceExecutionJournal?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _journals.TryGetValue(sessionId, out var journal);
            return Task.FromResult(journal);
        }

        public Task<IReadOnlyList<DesktopWorkspaceExecutionJournal>> ListUnresolvedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _journals.Values.Where(static journal => !journal.IsResolved).ToArray();
            return Task.FromResult<IReadOnlyList<DesktopWorkspaceExecutionJournal>>(
                Array.AsReadOnly(result));
        }
    }
}
