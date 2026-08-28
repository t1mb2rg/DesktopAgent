using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.DesktopLayouts.Workspaces.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Execution;

public sealed class DesktopWorkspaceExecutionCoordinatorContainmentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public DesktopWorkspaceExecutionCoordinatorContainmentTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"DesktopAgent.Tests.WorkspaceExecutionContainment.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 尽力清理测试目录。
        }
    }

    [Fact]
    public async Task Execute_FinalVerificationThrowsAfterWriters_NeverReportsFailedBeforeWrite()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var source = new ThrowOnCaptureNumberObservationSource(state, throwOnCapture: 2);
        var snapshotStore = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        var journalStore = new SqliteDesktopWorkspaceExecutionJournalStore(_dbPath);
        var writer = new StatefulWriter(state);
        using var sta = new StaThreadExecutor();
        var coordinator = new DesktopWorkspaceExecutionCoordinator(
            source,
            writer,
            sta,
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            snapshotStore,
            journalStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.NeedsAttention, result.Status);
        Assert.NotEqual(DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.True(result.WriterStarted);
        Assert.Equal(3, writer.CallCount);
        Assert.Equal("UnhandledExecutionException", result.ErrorType);
        Assert.NotNull(result.Journal);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention, result.Journal!.Lifecycle);
        Assert.Equal(new[] { 0, 1, 2 }, result.Journal.CompletedForwardStepIndexes);
        Assert.Empty(result.Journal.RollbackAttempts);

        var persisted = await journalStore.GetAsync(result.Journal.SessionId);
        Assert.NotNull(persisted);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention, persisted!.Lifecycle);
        Assert.Equal("UnhandledExecutionException", persisted.ForwardFailureType);
        Assert.Contains("verification exploded", persisted.ForwardFailureMessage);
    }

    [Fact]
    public async Task Execute_FirstFreshObservationThrows_RemainsTruthfulFailedBeforeWrite()
    {
        var state = MutableDesktopState.CreateSwap();
        var preview = CreatePreview(state.Capture());
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        var source = new ThrowOnCaptureNumberObservationSource(state, throwOnCapture: 1);
        var snapshotStore = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        var journalStore = new SqliteDesktopWorkspaceExecutionJournalStore(_dbPath);
        var writer = new StatefulWriter(state);
        using var sta = new StaThreadExecutor();
        var coordinator = new DesktopWorkspaceExecutionCoordinator(
            source,
            writer,
            sta,
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            snapshotStore,
            journalStore);

        var result = await coordinator.ExecuteAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.False(result.WriterStarted);
        Assert.Equal(0, writer.CallCount);
        Assert.Null(result.Journal);
        Assert.Equal("InvalidOperationException", result.ErrorType);
        Assert.Empty(await journalStore.ListUnresolvedAsync());
        Assert.Empty(await snapshotStore.ListAsync());
    }

    private static DesktopWorkspaceFrozenPreview CreatePreview(DesktopObservation observation)
    {
        var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);
        Assert.True(
            geometryResult.Succeeded,
            string.Join("；", geometryResult.Issues.Select(static issue => issue.Message)));
        var geometry = geometryResult.Geometry!;
        var facts = DesktopWorkspaceLocalItemFactProjector.Project(observation, geometry);

        var blueprint = new DesktopWorkspaceBlueprint(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            revision: 1,
            designSummary: "交换两个项目的位置。",
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

    private sealed class ThrowOnCaptureNumberObservationSource : IDesktopObservationSource
    {
        private readonly MutableDesktopState _state;
        private readonly int _throwOnCapture;
        private int _captureCount;

        public ThrowOnCaptureNumberObservationSource(
            MutableDesktopState state,
            int throwOnCapture)
        {
            _state = state;
            _throwOnCapture = throwOnCapture;
        }

        public Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _captureCount++;
            if (_captureCount == _throwOnCapture)
                throw new InvalidOperationException("verification exploded");

            return Task.FromResult(
                DesktopObservationCaptureResult.Captured(_state.Capture()));
        }
    }

    private sealed class StatefulWriter : IDesktopIconPositionWriter
    {
        private readonly MutableDesktopState _state;

        public int CallCount { get; private set; }

        public StatefulWriter(MutableDesktopState state)
        {
            _state = state;
        }

        public DesktopIconPositioningCapabilities GetCapabilities() => _state.Capabilities;

        public DesktopIconMoveResult MoveDesktopIcon(
            string stableId,
            PixelPoint targetPosition)
        {
            CallCount++;
            var previous = _state.GetPosition(stableId);
            _state.SetPosition(stableId, targetPosition);
            return new DesktopIconMoveResult(
                stableId,
                previous,
                targetPosition,
                targetPosition,
                ConnectionMethod.FindWindowSW,
                null);
        }
    }
}
