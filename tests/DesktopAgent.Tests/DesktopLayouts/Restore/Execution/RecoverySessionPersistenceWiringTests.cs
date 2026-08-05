using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoverySessionPersistenceWiringTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelPoint Sp = new(80, 80);
    private static readonly PixelRect WA = new(0, 0, 800, 600);

    public RecoverySessionPersistenceWiringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Tests.Wiring.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    private SqliteRecoverySessionStore CreateStore() => new(_dbPath);
    private static DisplayDescriptor MakeDisplay() => new(TD, new PixelRect(0, 0, 1920, 1080), WA, 96, 96, true);
    private static DesktopIconPositioningCapabilities MakeCaps() => new(false, Sp, ConnectionMethod.FindWindowSW, null);
    private static int _ord; private void ResetOrd() => _ord = 0;

    private static DesktopIconSnapshotItem Snap(string s, PixelPoint p) => new(_ord++, s, $"N_{s}", null, ItemKind.File, p, TD, false, true);
    private static DesktopIconDescriptor Live(string s, PixelPoint p) => new(s, $"N_{s}", null, ItemKind.File, p, TD, false, true);

    private TargetDisplayLayoutRestorePlan MakeRestorePlan(DesktopIconSnapshotItem[] snaps, DesktopIconDescriptor[] lives)
    {
        var ss = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1, snaps);
        var r = new DesktopIconReadResult(lives, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        return TargetDisplayLayoutRestorePlanner.CreatePlan(ss, r, new[] { MakeDisplay() }, MakeCaps(), TD);
    }

    private static TargetDisplayLayoutExecutionPlan MakeExecPlan()
    {
        var step = new TargetDisplayLayoutExecutionStep(0, "a", "N_a", new PixelPoint(160, 240), new PixelPoint(240, 320),
            TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD);
        return new TargetDisplayLayoutExecutionPlan(TD, true, false, new[] { step },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(), Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
    }

    // ═══════════ Event spy infrastructure ═══════════
    private sealed class EventStore : IRecoverySessionStore
    {
        private readonly IRecoverySessionStore _inner;
        public List<string> Events = new();
        public bool FailSave;
        public bool FailUpdate;
        public bool FailGet;
        public Func<RecoverySessionRecord, RecoverySessionRecord?>? TamperGet;

        public EventStore(string dbPath) { _inner = new SqliteRecoverySessionStore(dbPath); }
        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);

        public async Task SaveAsync(RecoverySessionRecord r, CancellationToken ct = default)
        {
            Events.Add("Save");
            if (FailSave) throw new InvalidOperationException("injected Save failure");
            await _inner.SaveAsync(r, ct);
        }
        public async Task<RecoverySessionRecord> UpdateAsync(RecoverySessionRecord r, CancellationToken ct = default)
        {
            Events.Add($"Update:{r.Lifecycle}");
            if (FailUpdate) throw new InvalidOperationException("injected Update failure");
            return await _inner.UpdateAsync(r, ct);
        }
        public async Task<RecoverySessionRecord?> GetAsync(Guid sid, CancellationToken ct = default)
        {
            Events.Add("Get");
            if (FailGet) throw new InvalidOperationException("injected Get failure");
            var r = await _inner.GetAsync(sid, ct);
            if (r is not null && TamperGet is not null) r = TamperGet(r);
            return r;
        }
        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(int limit = 50, CancellationToken ct = default)
            => _inner.ListUnresolvedAsync(limit, ct);
    }

    private sealed class SpyWriter : IDesktopIconPositionWriter
    {
        private readonly Dictionary<string, (PixelPoint from, PixelPoint to)> _icons;
        private int _moveCallCount;
        public List<string> Events { get; }
        public bool FailMove; public string? WrongStableId; public PixelPoint? WrongPrevious;
        public DesktopIconPositioningCapabilities GetCapabilities() => new(false, Sp, ConnectionMethod.FindWindowSW, null);

        public SpyWriter(List<string> events, string sid, PixelPoint from, PixelPoint to)
        {
            Events = events;
            _icons = new Dictionary<string, (PixelPoint, PixelPoint)>(StringComparer.Ordinal)
            {
                [sid] = (from, to)
            };
        }

        public SpyWriter(List<string> events, Dictionary<string, (PixelPoint from, PixelPoint to)> icons)
        {
            Events = events;
            _icons = icons;
        }

        public DesktopIconMoveResult MoveDesktopIcon(string sid, PixelPoint target)
        {
            Events.Add($"Writer:{_moveCallCount++}");
            if (FailMove) throw new InvalidOperationException("Writer BOMB");
            var reportedSid = WrongStableId ?? sid;
            var (from, _) = _icons.TryGetValue(sid, out var icon) ? icon : (target, target);
            var prevPos = WrongPrevious ?? from;
            return new DesktopIconMoveResult(reportedSid, prevPos, target, target, ConnectionMethod.FindWindowSW, null);
        }
    }

    // ═══════════ 1. InitialReadbackMismatch_NoWriter ═══════════
    [Fact] public async Task InitialReadbackMismatch_NoWriter()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var sharedEvents = new List<string>();
        var writer = new SpyWriter(sharedEvents, "a", new(160, 240), new(240, 320));
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);
        reader.Enqueue([Live("a", new(240, 320))]);

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        // Tamper Get: 修改 SourceRestorePlan 的 Items（不一致）
        estore.TamperGet = r => r with { SourceRestorePlan = MakeRestorePlan(
            [Snap("b", new(400, 480))], [Live("b", new(320, 400))]) };

        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal(new[] { "Save", "Get" }, sharedEvents);
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer"));
    }

    // ═══════════ 2. InitialCheckpointPersistedBeforeFirstWriter ═══════════
    [Fact] public async Task InitialCheckpointPersistedBeforeFirstWriter()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var sharedEvents = new List<string>();
        var writer = new SpyWriter(sharedEvents, "a", new(160, 240), new(240, 320));
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);
        reader.Enqueue([Live("a", new(240, 320))]);

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);

        Assert.Equal(
            new[] { "Save", "Get", "Writer:0" },
            sharedEvents.Take(3));
    }

    // ═══════════ 3. ForwardCheckpointBeforeNextWriter ═══════════
    [Fact] public async Task ForwardCheckpointBeforeNextWriter()
    {
        ResetOrd();
        // 两步骤计划
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var sharedEvents = new List<string>();
        var icons = new Dictionary<string, (PixelPoint from, PixelPoint to)>
        {
            ["a"] = (new PixelPoint(160, 80), new PixelPoint(240, 80)),
            ["b"] = (new PixelPoint(320, 80), new PixelPoint(400, 80)),
        };
        var writer = new SpyWriter(sharedEvents, icons);
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80))]);
        reader.Enqueue([Live("a", new(240, 80)), Live("b", new(400, 80))]); // verification

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);

        var expectedPrefix = new[] { "Save", "Get", "Writer:0", "Update:Executing", "Get", "Writer:1", "Update:Executing", "Get" };
        Assert.Equal(expectedPrefix, sharedEvents.Take(expectedPrefix.Length));
    }

    // ═══════════ 4. ForwardCheckpointFailure_StopsNextWriter ═══════════
    [Fact] public async Task ForwardCheckpointFailure_StopsNextWriter()
    {
        ResetOrd();
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 80), new(240, 80));
        // 不 script b 的 result — 不应该被执行
        writer.ScriptExactResult("b", new(320, 80), new(400, 80)); // 仍 script 以防万一
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80))]);

        var estore = new EventStore(_dbPath) { FailUpdate = true };
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("CheckpointPersistFailed", result.ForwardFailureType);
        Assert.True(result.ForwardAttempts.Count >= 1); // Writer:0 executed
        Assert.False(result.ForwardAttempts.Any(a => a.SourceStepIndex == 1)); // Writer:1 NOT executed
    }

    // ═══════════ 5. WriterException_PersistsPositionUncertain ═══════════
    [Fact] public async Task WriterException_PersistsPositionUncertain()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new SpyWriter(new List<string>(), "a", new(160, 240), new(240, 320)) { FailMove = true };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);

        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: new SqliteRecoverySessionStore(_dbPath));

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);

        // 验证数据库终态
        var freshStore = new SqliteRecoverySessionStore(_dbPath);
        var unresolved = await freshStore.ListUnresolvedAsync();
        Assert.Single(unresolved);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, unresolved[0].Lifecycle);
        Assert.True(unresolved[0].RequiresManualRecovery);
        Assert.True(unresolved[0].IsPositionUncertain);
    }

    // ═══════════ 6. StableIdMismatch_PersistsPositionUncertain ═══════════
    [Fact] public async Task StableIdMismatch_PersistsPositionUncertain()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new SpyWriter(new List<string>(), "a", new(160, 240), new(240, 320)) { WrongStableId = "WRONG" };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);

        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: new SqliteRecoverySessionStore(_dbPath));

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("StableIdMismatch", result.ForwardFailureType);

        var freshStore = new SqliteRecoverySessionStore(_dbPath);
        var unresolved = await freshStore.ListUnresolvedAsync();
        Assert.Single(unresolved);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, unresolved[0].Lifecycle);
    }

    // ═══════════ 7. PreviousPositionMismatch_PersistsPositionUncertain ═══════════
    [Fact] public async Task PreviousPositionMismatch_PersistsPositionUncertain()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new SpyWriter(new List<string>(), "a", new(160, 240), new(240, 320)) { WrongPrevious = new(999, 999) };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);

        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: new SqliteRecoverySessionStore(_dbPath));

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("PreviousPositionMismatch", result.ForwardFailureType);

        var freshStore = new SqliteRecoverySessionStore(_dbPath);
        var unresolved = await freshStore.ListUnresolvedAsync();
        Assert.Single(unresolved);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, unresolved[0].Lifecycle);
    }

    // ═══════════ 8. TerminalPersistFails_ReturnsPositionUncertain ═══════════
    [Fact] public async Task TerminalPersistFails_ReturnsPositionUncertain()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new SpyWriter(new List<string>(), "a", new(160, 240), new(240, 320));
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);
        reader.Enqueue([Live("a", new(240, 320))]); // verification passes

        var failStore = new FailingTerminalStore(_dbPath);
        await failStore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: failStore);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Contains("TerminalPersistFailed", result.ForwardFailureType);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.NotEqual(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
    }

    // ═══════════ 9. UncertainForwardTerminalPersistFails_ReturnsTerminalPersistFailed ═══════════
    [Theory]
    [InlineData("WriterException")]
    [InlineData("StableIdMismatch")]
    [InlineData("PreviousPositionMismatch")]
    public async Task UncertainForwardTerminalPersistFails_ReturnsTerminalPersistFailed(string failureMode)
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);

        var sharedEvents = new List<string>();
        SpyWriter writer = failureMode switch
        {
            "WriterException" => new SpyWriter(sharedEvents, "a", new(160, 240), new(240, 320)) { FailMove = true },
            "StableIdMismatch" => new SpyWriter(sharedEvents, "a", new(160, 240), new(240, 320)) { WrongStableId = "WRONG" },
            "PreviousPositionMismatch" => new SpyWriter(sharedEvents, "a", new(160, 240), new(240, 320)) { WrongPrevious = new(999, 999) },
            _ => throw new ArgumentException(failureMode)
        };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);

        var estore = new EventStore(_dbPath) { FailUpdate = true };
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("TerminalPersistFailed", result.ForwardFailureType);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        // 不执行后续 Writer（单步骤计划无 Writer:1）
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:1"));
    }

    private sealed class FailingTerminalStore : IRecoverySessionStore
    {
        private readonly SqliteRecoverySessionStore _inner;
        private int _updateCallCount;
        public FailingTerminalStore(string dbPath) { _inner = new SqliteRecoverySessionStore(dbPath); }
        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);
        public Task SaveAsync(RecoverySessionRecord r, CancellationToken ct = default) => _inner.SaveAsync(r, ct);
        public async Task<RecoverySessionRecord> UpdateAsync(RecoverySessionRecord r, CancellationToken ct = default)
        {
            _updateCallCount++;
            if (_updateCallCount >= 2) throw new InvalidOperationException("terminal persist failure");
            return await _inner.UpdateAsync(r, ct);
        }
        public Task<RecoverySessionRecord?> GetAsync(Guid sid, CancellationToken ct = default) => _inner.GetAsync(sid, ct);
        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(int limit = 50, CancellationToken ct = default)
            => _inner.ListUnresolvedAsync(limit, ct);
    }
}
