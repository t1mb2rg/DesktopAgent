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

public sealed class DesktopWorkspaceDynamicExecutionCoordinatorTests
{
    [Fact]
    public async Task Execute_AuthorizedItemDriftsAfterEarlierWriter_ReobservesFreshFromAndCommits()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var snapshotStore = new InMemorySnapshotStore();
        var staticStore = new EmptyStaticJournalStore();
        var dynamicStore = new InMemoryDynamicJournalStore();
        var writer = new StatefulWriter(state, dynamicStore)
        {
            PassiveDriftAfterCall = 1,
            PassiveDriftStableId = "b",
            PassiveDriftPosition = new PixelPoint(240, 0),
        };
        using var sta = new StaThreadExecutor();
        var coordinator = CreateCoordinator(
            state, writer, sta, snapshotStore, staticStore, dynamicStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.Committed, result.Status);
        Assert.True(result.WriterStarted);
        var journal = Assert.IsType<DesktopWorkspaceDynamicExecutionJournal>(result.Journal);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.Committed, journal.Lifecycle);
        Assert.Equal(new PixelPoint(0, 0), state.GetPosition("a"));
        Assert.Equal(new PixelPoint(80, 0), state.GetPosition("b"));
        Assert.True(writer.CallCount >= 3);
        Assert.All(writer.LifecycleSeenBeforeCalls, lifecycle =>
            Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress, lifecycle));

        var bAttempt = Assert.Single(journal.ForwardAttempts.Where(static attempt => attempt.StableId == "b"));
        Assert.Equal(new PixelPoint(240, 0), bAttempt.FreshFromPixelPosition);
        Assert.Equal(new PixelPoint(80, 0), bAttempt.FrozenTargetPixelPosition);
        Assert.Equal(new PixelPoint(80, 0), bAttempt.RequestedPixelPosition);
        Assert.Equal(new PixelPoint(240, 0), bAttempt.ReportedPreviousPixelPosition);
        Assert.Equal(DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess, bAttempt.Outcome);
        Assert.Equal(journal.Authority.MoveGoalCount, journal.LastObservedSatisfiedGoalCount);
        Assert.NotNull(journal.FinalVerifiedAtUtc);
    }

    [Fact]
    public async Task Execute_SourceDriftsBeforeFirstWriter_StrictFreshCheckRejectsWithoutSnapshotJournalOrWriter()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        state.SetPosition("a", new PixelPoint(160, 0));

        var snapshotStore = new InMemorySnapshotStore();
        var staticStore = new EmptyStaticJournalStore();
        var dynamicStore = new InMemoryDynamicJournalStore();
        var writer = new StatefulWriter(state, dynamicStore);
        using var sta = new StaThreadExecutor();
        var coordinator = CreateCoordinator(
            state, writer, sta, snapshotStore, staticStore, dynamicStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.False(result.WriterStarted);
        Assert.Equal("WorkspaceFreshCheckRejected", result.ErrorType);
        Assert.Equal(0, writer.CallCount);
        Assert.Empty(snapshotStore.Snapshots);
        Assert.Empty(dynamicStore.History);
    }

    [Fact]
    public async Task Execute_InexactWriter_PersistsDynamicNeedsAttentionAndStopsNewWrites()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var snapshotStore = new InMemorySnapshotStore();
        var staticStore = new EmptyStaticJournalStore();
        var dynamicStore = new InMemoryDynamicJournalStore();
        var writer = new StatefulWriter(state, dynamicStore)
        {
            InexactOnCall = 2,
        };
        using var sta = new StaThreadExecutor();
        var coordinator = CreateCoordinator(
            state, writer, sta, snapshotStore, staticStore, dynamicStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.NeedsAttention, result.Status);
        Assert.True(result.WriterStarted);
        var journal = Assert.IsType<DesktopWorkspaceDynamicExecutionJournal>(result.Journal);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention, journal.Lifecycle);
        Assert.Equal(2, writer.CallCount);
        Assert.Equal(2, journal.ForwardAttempts.Count);
        Assert.Equal(
            DesktopWorkspaceExecutionAttemptOutcome.InexactKnown,
            journal.ForwardAttempts[^1].Outcome);
        Assert.Equal(1, journal.LastFailedDecisionIndex);
        Assert.Equal("InexactKnownPosition", journal.ForwardFailureType);
    }

    [Fact]
    public async Task Execute_UnresolvedV9Journal_BlocksBeforeFreshSnapshotAndWriter()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var snapshotStore = new InMemorySnapshotStore();
        var staticStore = new EmptyStaticJournalStore { HasUnresolved = true };
        var dynamicStore = new InMemoryDynamicJournalStore();
        var writer = new StatefulWriter(state, dynamicStore);
        using var sta = new StaThreadExecutor();
        var coordinator = CreateCoordinator(
            state, writer, sta, snapshotStore, staticStore, dynamicStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.False(result.WriterStarted);
        Assert.Equal("UnresolvedStaticWorkspaceExecution", result.ErrorType);
        Assert.Equal(0, writer.CallCount);
        Assert.Empty(snapshotStore.Snapshots);
        Assert.Empty(dynamicStore.History);
    }

    private static DesktopWorkspaceDynamicExecutionCoordinator CreateCoordinator(
        MutableDesktopState state,
        StatefulWriter writer,
        StaThreadExecutor sta,
        InMemorySnapshotStore snapshotStore,
        EmptyStaticJournalStore staticStore,
        InMemoryDynamicJournalStore dynamicStore)
        => new(
            new MutableObservationSource(state),
            writer,
            sta,
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            snapshotStore,
            staticStore,
            dynamicStore);

    private static DesktopWorkspaceFrozenPreview CreatePreview(DesktopObservation observation)
    {
        var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);
        Assert.True(
            geometryResult.Succeeded,
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
        Assert.True(
            compilation.Succeeded,
            string.Join("；", compilation.Issues.Select(static issue => issue.Message)));
        var plan = Assert.IsType<CompiledWorkspacePlan>(compilation.Plan);
        var equivalence = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            blueprint,
            geometry,
            plan,
            facts);
        Assert.True(
            equivalence.Equivalent,
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
            new PixelRect(0, 0, 320, 80),
            new PixelRect(0, 0, 320, 80),
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
        private readonly InMemoryDynamicJournalStore _journalStore;

        public int CallCount { get; private set; }
        public int? InexactOnCall { get; init; }
        public int? PassiveDriftAfterCall { get; init; }
        public string? PassiveDriftStableId { get; init; }
        public PixelPoint? PassiveDriftPosition { get; init; }
        public List<DesktopWorkspaceExecutionJournalLifecycle?> LifecycleSeenBeforeCalls { get; } = [];

        public StatefulWriter(
            MutableDesktopState state,
            InMemoryDynamicJournalStore journalStore)
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

            if (PassiveDriftAfterCall == CallCount &&
                PassiveDriftStableId is not null &&
                PassiveDriftPosition.HasValue)
            {
                _state.SetPosition(PassiveDriftStableId, PassiveDriftPosition.Value);
            }

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
            return Task.FromResult<IReadOnlyList<DesktopLayoutSnapshotSummary>>([]);
        }

        public Task<bool> DeleteAsync(Guid snapshotId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshots.Remove(snapshotId));
        }
    }

    private sealed class EmptyStaticJournalStore : IDesktopWorkspaceExecutionJournalStore
    {
        public bool HasUnresolved { get; init; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SaveAsync(
            DesktopWorkspaceExecutionJournal journal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DesktopWorkspaceExecutionJournal> UpdateAsync(
            DesktopWorkspaceExecutionJournal journal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DesktopWorkspaceExecutionJournal?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DesktopWorkspaceExecutionJournal?>(null);

        public Task<IReadOnlyList<DesktopWorkspaceExecutionJournal>> ListUnresolvedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DesktopWorkspaceExecutionJournal> result = HasUnresolved
                ? new DesktopWorkspaceExecutionJournal[] { null! }
                : Array.Empty<DesktopWorkspaceExecutionJournal>();
            return Task.FromResult(result);
        }
    }

    private sealed class InMemoryDynamicJournalStore : IDesktopWorkspaceDynamicExecutionJournalStore
    {
        private readonly Dictionary<Guid, DesktopWorkspaceDynamicExecutionJournal> _journals = [];

        public List<DesktopWorkspaceExecutionJournalLifecycle> History { get; } = [];
        public DesktopWorkspaceDynamicExecutionJournal? Current => _journals.Values.SingleOrDefault();

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SaveAsync(
            DesktopWorkspaceDynamicExecutionJournal journal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            journal.Validate();
            _journals.Add(journal.SessionId, journal);
            History.Add(journal.Lifecycle);
            return Task.CompletedTask;
        }

        public Task<DesktopWorkspaceDynamicExecutionJournal> UpdateAsync(
            DesktopWorkspaceDynamicExecutionJournal journal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            journal.Validate();
            if (!_journals.TryGetValue(journal.SessionId, out var existing))
                throw new InvalidOperationException("journal missing");
            if (existing.Revision != journal.Revision)
                throw new InvalidOperationException("revision conflict");

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

        public Task<DesktopWorkspaceDynamicExecutionJournal?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _journals.TryGetValue(sessionId, out var journal);
            return Task.FromResult(journal);
        }

        public Task<IReadOnlyList<DesktopWorkspaceDynamicExecutionJournal>> ListUnresolvedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DesktopWorkspaceDynamicExecutionJournal> result = _journals.Values
                .Where(static journal => !journal.IsResolved)
                .ToArray();
            return Task.FromResult(result);
        }
    }
}
