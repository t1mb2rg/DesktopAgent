using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class StagingForwardRecoveryIntegrationTests : IDisposable
{
    private const string Monitor = @"\\.\DISPLAY1";
    private static readonly PixelRect WorkArea = new(0, 0, 800, 600);
    private static readonly PixelPoint Spacing = new(80, 80);
    private static readonly PixelPoint A0 = new(80, 80);
    private static readonly PixelPoint B0 = new(160, 80);

    private readonly string _tempDir;
    private readonly string _dbPath;

    public StagingForwardRecoveryIntegrationTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"DesktopAgent.StagingForwardRecovery.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "desktop-agent.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 测试清理失败不覆盖真正断言结果。
        }
    }

    [Fact]
    public async Task StagingSwap_WithSqliteRecoveryStore_CompletesAndPersistsCompleted()
    {
        var world = CreateOriginalWorld();
        var restorePlan = CreateSwapRestorePlan(world);
        var recoveryStore = new SqliteRecoverySessionStore(_dbPath);
        await recoveryStore.InitializeAsync();

        Assert.True(restorePlan.RequiresTemporaryStaging);
        Assert.NotEmpty(restorePlan.Cycles);

        var coordinator = CreateExecutionCoordinator(world, recoveryStore);
        var result = await coordinator.ExecuteWithScopeGateAsync(
            restorePlan,
            TargetDisplayLayoutStagingExecutionScopeGate.Evaluate);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.Equal(B0, world.GetPosition("a"));
        Assert.Equal(A0, world.GetPosition("b"));

        Assert.NotNull(result.Session);
        var executionPlan = result.Session!.ExecutionPlan;
        Assert.True(executionPlan.UsesTemporaryStaging);
        Assert.Equal(3, executionPlan.Steps.Count);
        var stagingAssignment = Assert.Single(executionPlan.StagingAssignments);
        Assert.Equal(3, result.ForwardAttempts.Count);
        Assert.Empty(result.RollbackAttempts);
        Assert.Equal(3, world.MoveCalls.Count);
        Assert.Equal(
            executionPlan.Steps.Select(step => (step.StableId, step.ToPosition)),
            world.MoveCalls);
        Assert.Equal(
            2,
            world.MoveCalls.Count(call =>
                string.Equals(call.StableId, stagingAssignment.StableId, StringComparison.Ordinal)));

        var stored = await recoveryStore.GetAsync(result.Session.SessionId);
        Assert.NotNull(stored);
        Assert.Equal(RecoverySessionLifecycle.Completed, stored!.Lifecycle);
        Assert.Equal(new[] { 0, 1, 2 }, stored.CompletedForwardStepIndexes);
        Assert.Empty(await recoveryStore.ListUnresolvedAsync());
    }

    [Fact]
    public async Task StagingSwap_PhysicalPrefixWithReportedPreviousMismatch_PersistsThenRecoversOriginal()
    {
        var world = CreateOriginalWorld();
        var restorePlan = CreateSwapRestorePlan(world);
        var recoveryStore = new SqliteRecoverySessionStore(_dbPath);
        await recoveryStore.InitializeAsync();

        Assert.True(restorePlan.RequiresTemporaryStaging);

        // 第 2 次 Writer 调用先真实移动到目标，再故意错误回报 PreviousPosition。
        // Forward 因证据不可信必须停止并持久化 PositionUncertain；
        // 但真实桌面此时恰好处于合法 execution prefix，可由 observation-first recovery 证明并回退。
        world.CorruptPreviousOnCallIndex = 1;

        var executionCoordinator = CreateExecutionCoordinator(world, recoveryStore);
        var forward = await executionCoordinator.ExecuteWithScopeGateAsync(
            restorePlan,
            TargetDisplayLayoutStagingExecutionScopeGate.Evaluate);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, forward.Status);
        Assert.True(forward.RequiresManualRecovery);
        Assert.True(forward.IsPositionUncertain);
        Assert.Equal("PreviousPositionMismatch", forward.ForwardFailureType);
        Assert.Equal(2, world.MoveCalls.Count);
        Assert.NotNull(forward.Session);

        var persistedAfterForward = await recoveryStore.GetAsync(forward.Session!.SessionId);
        Assert.NotNull(persistedAfterForward);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, persistedAfterForward!.Lifecycle);
        Assert.Single(await recoveryStore.ListUnresolvedAsync());

        var recoveryCoordinator = new PersistedRecoveryCoordinator(
            recoveryStore,
            new FakeDisplayTopologyReader(Monitor, WorkArea),
            world,
            world,
            new StaThreadExecutor());

        var recovered = await recoveryCoordinator.RecoverAsync(persistedAfterForward);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, recovered.Status);
        Assert.False(recovered.RequiresManualRecovery);
        Assert.False(recovered.IsPositionUncertain);
        Assert.Equal(A0, world.GetPosition("a"));
        Assert.Equal(B0, world.GetPosition("b"));
        Assert.Equal(2, recovered.RollbackAttempts.Count);
        Assert.Equal(4, world.MoveCalls.Count);

        var persistedAfterRecovery = await recoveryStore.GetAsync(forward.Session.SessionId);
        Assert.NotNull(persistedAfterRecovery);
        Assert.Equal(RecoverySessionLifecycle.FailedAndRolledBack, persistedAfterRecovery!.Lifecycle);
        Assert.Empty(await recoveryStore.ListUnresolvedAsync());
    }

    private static TargetDisplayLayoutExecutionCoordinator CreateExecutionCoordinator(
        MutableWorld world,
        IRecoverySessionStore recoveryStore)
        => new(
            new FakeDisplayTopologyReader(Monitor, WorkArea),
            world,
            world,
            new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(),
            recoveryStore);

    private static MutableWorld CreateOriginalWorld()
        => new(
            Live("a", A0),
            Live("b", B0));

    private static TargetDisplayLayoutRestorePlan CreateSwapRestorePlan(MutableWorld world)
    {
        var desiredSnapshot = new DesktopLayoutSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Monitor,
            ConnectionMethod.FindWindowSW,
            1,
            new[]
            {
                Snapshot(0, "a", B0),
                Snapshot(1, "b", A0),
            });

        return TargetDisplayLayoutRestorePlanner.CreatePlan(
            desiredSnapshot,
            world.ReadDesktopIcons(),
            new[] { Display() },
            world.GetCapabilities(),
            Monitor);
    }

    private static DisplayDescriptor Display()
        => new(
            Monitor,
            new PixelRect(0, 0, 1920, 1080),
            WorkArea,
            96,
            96,
            true);

    private static DesktopIconSnapshotItem Snapshot(
        int ordinal,
        string stableId,
        PixelPoint position)
        => new(
            ordinal,
            stableId,
            stableId.ToUpperInvariant(),
            null,
            ItemKind.File,
            position,
            Monitor,
            false,
            true);

    private static DesktopIconDescriptor Live(
        string stableId,
        PixelPoint position)
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
        private int _moveCallIndex;

        public MutableWorld(params DesktopIconDescriptor[] icons)
        {
            _icons = icons.ToDictionary(icon => icon.StableId, StringComparer.Ordinal);
        }

        public int? CorruptPreviousOnCallIndex { get; set; }

        public List<(string StableId, PixelPoint Target)> MoveCalls { get; } = [];

        public PixelPoint GetPosition(string stableId)
            => _icons[stableId].PixelPosition;

        public DesktopIconReadResult ReadDesktopIcons()
            => new(
                _icons.Values
                    .OrderBy(icon => icon.StableId, StringComparer.Ordinal)
                    .ToArray(),
                0,
                ConnectionMethod.FindWindowSW,
                TimeSpan.Zero,
                null);

        public DesktopIconPositioningCapabilities GetCapabilities()
            => new(
                false,
                Spacing,
                ConnectionMethod.FindWindowSW,
                null);

        public DesktopIconMoveResult MoveDesktopIcon(
            string stableId,
            PixelPoint targetPosition)
        {
            var current = _icons[stableId];
            var previous = current.PixelPosition;
            var callIndex = _moveCallIndex++;

            MoveCalls.Add((stableId, targetPosition));
            _icons[stableId] = current with
            {
                PixelPosition = targetPosition,
                MonitorDeviceName = Monitor,
            };

            var reportedPrevious = callIndex == CorruptPreviousOnCallIndex
                ? new PixelPoint(previous.X + 1, previous.Y)
                : previous;

            return new DesktopIconMoveResult(
                stableId,
                reportedPrevious,
                targetPosition,
                targetPosition,
                ConnectionMethod.FindWindowSW,
                null);
        }
    }
}
