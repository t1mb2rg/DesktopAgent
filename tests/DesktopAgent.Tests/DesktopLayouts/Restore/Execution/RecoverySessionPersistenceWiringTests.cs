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
        private int _updateCallCount;
        public List<string> Events = new();
        public bool FailSave;
        public bool FailUpdate;
        public int FailUpdateAfterCount = -1;
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
            _updateCallCount++;
            if (FailUpdateAfterCount >= 0 && _updateCallCount > FailUpdateAfterCount)
                throw new InvalidOperationException("injected Update failure (threshold)");
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
        public bool FailMove; public string? WrongStableId; public PixelPoint? WrongPrevious; public PixelPoint? WrongActual;
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
            var actual = WrongActual ?? target;
            return new DesktopIconMoveResult(reportedSid, prevPos, actual, target, ConnectionMethod.FindWindowSW, null);
        }
    }

    /// <summary>Phase-aware spy that emits ForwardWriter:/RollbackWriter: events.</summary>
    private sealed class PhaseSpyWriter : IDesktopIconPositionWriter
    {
        private readonly Dictionary<string, (PixelPoint from, PixelPoint to)> _icons;
        private readonly Dictionary<string, int>? _sidToStep;
        private int _callCount;
        public List<string> Events { get; }
        public int ForwardStepCount { get; set; } = int.MaxValue;
        public Action<int>? OnMoveCall;
        // unconditional (all calls)
        public bool FailMove; public string? WrongStableId; public PixelPoint? WrongPrevious;
        // rollback-only
        public bool FailMoveRb; public string? WrongStableIdRb; public PixelPoint? WrongPreviousRb; public PixelPoint? WrongActualRb;
        public DesktopIconPositioningCapabilities GetCapabilities() => new(false, Sp, ConnectionMethod.FindWindowSW, null);

        public PhaseSpyWriter(List<string> events, string sid, PixelPoint from, PixelPoint to,
            Dictionary<string, int>? sidToStep = null)
        {
            Events = events;
            _icons = new Dictionary<string, (PixelPoint, PixelPoint)>(StringComparer.Ordinal) { [sid] = (from, to) };
            _sidToStep = sidToStep;
        }

        public PhaseSpyWriter(List<string> events, Dictionary<string, (PixelPoint from, PixelPoint to)> icons,
            Dictionary<string, int>? sidToStep = null)
        {
            Events = events;
            _icons = icons;
            _sidToStep = sidToStep;
        }

        public DesktopIconMoveResult MoveDesktopIcon(string sid, PixelPoint target)
        {
            var isRb = _callCount >= ForwardStepCount;
            var phase = isRb ? "RollbackWriter" : "ForwardWriter";
            var idx = _sidToStep?.TryGetValue(sid, out var i) == true ? i : _callCount;
            Events.Add($"{phase}:{idx}");
            OnMoveCall?.Invoke(_callCount);
            _callCount++;
            if (isRb && FailMoveRb) throw new InvalidOperationException("RollbackWriter BOMB");
            if (FailMove) throw new InvalidOperationException("Writer BOMB");
            var reportedSid = (isRb ? WrongStableIdRb : null) ?? WrongStableId ?? sid;
            var (from, to) = _icons.TryGetValue(sid, out var icon) ? icon : (target, target);
            // Forward: PreviousPosition = from (icon was at FromPosition)
            // Rollback: PreviousPosition = to (icon was at ToPosition after forward move)
            var prevPos = (isRb ? WrongPreviousRb : null) ?? WrongPrevious ?? (isRb ? to : from);
            var actual = (isRb ? WrongActualRb : null) ?? target;
            return new DesktopIconMoveResult(reportedSid, prevPos, actual, target, ConnectionMethod.FindWindowSW, null);
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

    // ═══════════ 2b. CheckpointRestoreOriginReachesTheStoredRow ═══════════
    // 撤销入口把 origin 交给协调器，协调器必须一路带到落库那一刻；
    // 否则撤销自己写下的那一笔会以默认的 organize 落库，成为下一次撤销的候选。
    [Fact] public async Task CheckpointRestoreOriginReachesTheStoredRow()
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

        var result = await coordinator.ExecuteWithScopeGateAsync(
            plan,
            TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove,
            CancellationToken.None,
            RecoverySessionOrigin.CheckpointRestore);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.NotNull(result.Session);

        var record = await store.GetAsync(result.Session!.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionOrigin.CheckpointRestore, record!.Origin);

        // 撤销候选只认整理来源：这一笔不该出现在候选列表里。
        Assert.Empty(await store.ListCommittedAsync(10));
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
        Assert.True(unresolved[0].GetDisposition().RequiresManualRecovery);
        Assert.True(unresolved[0].GetDisposition().IsPositionUncertain);
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

    // ═══════════ 10. InexactForward_DoesNotInvokeRollbackWriter ═══════════
    [Fact] public async Task InexactForward_DoesNotInvokeRollbackWriter()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var sharedEvents = new List<string>();
        // WrongActual 使 IsExact=false（ActualPosition ≠ RequestedPosition）
        var writer = new SpyWriter(sharedEvents, "a", new(160, 240), new(240, 320))
            { WrongActual = new(250, 330) };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("InexactPosition", result.ForwardFailureType);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        // 只有一笔 Forward Writer，没有 Rollback Writer
        Assert.Single(result.ForwardAttempts);
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.InexactKnown, result.ForwardAttempts[0].Outcome);
        Assert.Empty(result.RollbackAttempts);
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:1"));
        // 数据库终态
        var freshStore = new SqliteRecoverySessionStore(_dbPath);
        var unresolved = await freshStore.ListUnresolvedAsync();
        Assert.Single(unresolved);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, unresolved[0].Lifecycle);
        Assert.Empty(unresolved[0].CompletedForwardStepIndexes);
    }

    // ═══════════ 11. RollbackCheckpointBeforeNextRollbackWriter ═══════════
    [Fact] public async Task RollbackCheckpointBeforeNextRollbackWriter()
    {
        ResetOrd();
        // 3 步骤计划：步骤 0,1 成功，步骤 2 未执行，取消触发 Rollback
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80)), Snap("c", new(560, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80)), Live("c", new(480, 80))]);
        var sharedEvents = new List<string>();
        var icons = new Dictionary<string, (PixelPoint from, PixelPoint to)>
        {
            ["a"] = (new PixelPoint(160, 80), new PixelPoint(240, 80)),
            ["b"] = (new PixelPoint(320, 80), new PixelPoint(400, 80)),
            ["c"] = (new PixelPoint(480, 80), new PixelPoint(560, 80)),
        };
        var sidToStep = new Dictionary<string, int> { ["a"] = 0, ["b"] = 1, ["c"] = 2 };
        var cts = new CancellationTokenSource();
        var writer = new PhaseSpyWriter(sharedEvents, icons, sidToStep)
        {
            ForwardStepCount = 2, // 步骤 0,1 为 Forward；之后为 Rollback
            OnMoveCall = callIdx => { if (callIdx == 1) cts.Cancel(); } // 第 2 次 Forward 调用后取消
        };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80)), Live("c", new(480, 80))]);
        // Rollback 验证读取：所有图标在 FromPosition（回滚成功）
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80)), Live("c", new(480, 80))]);

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan, cts.Token);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);

        // 验证 Rollback 检查点在两笔 Rollback Writer 之间
        var expectedRbPrefix = new[] { "RollbackWriter:1", "Update:Executing", "Get", "RollbackWriter:0", "Update:Executing", "Get" };
        var rbStart = sharedEvents.FindIndex(e => e.StartsWith("RollbackWriter:"));
        Assert.True(rbStart >= 0);
        Assert.Equal(expectedRbPrefix, sharedEvents.Skip(rbStart).Take(expectedRbPrefix.Length));
    }

    // ═══════════ 12. RollbackCheckpointFailure_StopsNextRollbackWriter ═══════════
    [Fact] public async Task RollbackCheckpointFailure_StopsNextRollbackWriter()
    {
        ResetOrd();
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80)), Snap("c", new(560, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80)), Live("c", new(480, 80))]);
        var sharedEvents = new List<string>();
        var icons = new Dictionary<string, (PixelPoint from, PixelPoint to)>
        {
            ["a"] = (new PixelPoint(160, 80), new PixelPoint(240, 80)),
            ["b"] = (new PixelPoint(320, 80), new PixelPoint(400, 80)),
            ["c"] = (new PixelPoint(480, 80), new PixelPoint(560, 80)),
        };
        var sidToStep = new Dictionary<string, int> { ["a"] = 0, ["b"] = 1, ["c"] = 2 };
        var cts = new CancellationTokenSource();
        var writer = new PhaseSpyWriter(sharedEvents, icons, sidToStep)
        {
            ForwardStepCount = 2,
            OnMoveCall = callIdx => { if (callIdx == 1) cts.Cancel(); }
        };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80)), Live("c", new(480, 80))]);

        // 前 2 次 Update 成功（Forward checkpoints），第 3 次（Rollback checkpoint）失败
        var estore = new EventStore(_dbPath) { FailUpdateAfterCount = 2 };
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan, cts.Token);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RollbackCheckpointPersistFailed", result.RecoveryFailureType);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        // RollbackWriter:1 执行了但 checkpoint 失败 → RollbackWriter:0 不得执行
        Assert.Contains("RollbackWriter:1", sharedEvents);
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("RollbackWriter:0"));
    }

    // ═══════════ 13. RollbackWriterException_PersistsPositionUncertain ═══════════
    [Fact] public async Task RollbackWriterException_PersistsPositionUncertain()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var sharedEvents = new List<string>();
        var icons = new Dictionary<string, (PixelPoint from, PixelPoint to)>
        {
            ["a"] = (new PixelPoint(160, 80), new PixelPoint(240, 80)),
            ["b"] = (new PixelPoint(320, 80), new PixelPoint(400, 80)),
        };
        var cts = new CancellationTokenSource();
        var writer = new PhaseSpyWriter(sharedEvents, icons) { ForwardStepCount = 1, FailMoveRb = true,
            OnMoveCall = callIdx => { if (callIdx == 0) cts.Cancel(); } };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80))]);

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan, cts.Token);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RollbackWriterException", result.RecoveryFailureType);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        // 数据库终态
        var freshStore = new SqliteRecoverySessionStore(_dbPath);
        var unresolved = await freshStore.ListUnresolvedAsync();
        Assert.Single(unresolved);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, unresolved[0].Lifecycle);
    }

    // ═══════════ 14. RollbackStableIdMismatch_PersistsPositionUncertain ═══════════
    [Fact] public async Task RollbackStableIdMismatch_PersistsPositionUncertain()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var sharedEvents = new List<string>();
        var icons = new Dictionary<string, (PixelPoint from, PixelPoint to)>
        {
            ["a"] = (new PixelPoint(160, 80), new PixelPoint(240, 80)),
            ["b"] = (new PixelPoint(320, 80), new PixelPoint(400, 80)),
        };
        var cts = new CancellationTokenSource();
        var writer = new PhaseSpyWriter(sharedEvents, icons) { ForwardStepCount = 1, WrongStableIdRb = "WRONG",
            OnMoveCall = callIdx => { if (callIdx == 0) cts.Cancel(); } };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80))]);

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan, cts.Token);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RollbackStableIdMismatch", result.RecoveryFailureType);
        var freshStore = new SqliteRecoverySessionStore(_dbPath);
        var unresolved = await freshStore.ListUnresolvedAsync();
        Assert.Single(unresolved);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, unresolved[0].Lifecycle);
    }

    // ═══════════ 15. RollbackPreviousPositionMismatch_PersistsPositionUncertain ═══════════
    [Fact] public async Task RollbackPreviousPositionMismatch_PersistsPositionUncertain()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var sharedEvents = new List<string>();
        var icons = new Dictionary<string, (PixelPoint from, PixelPoint to)>
        {
            ["a"] = (new PixelPoint(160, 80), new PixelPoint(240, 80)),
            ["b"] = (new PixelPoint(320, 80), new PixelPoint(400, 80)),
        };
        var cts = new CancellationTokenSource();
        var writer = new PhaseSpyWriter(sharedEvents, icons) { ForwardStepCount = 1, WrongPreviousRb = new(999, 999),
            OnMoveCall = callIdx => { if (callIdx == 0) cts.Cancel(); } };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80))]);

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan, cts.Token);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RollbackPreviousPositionMismatch", result.RecoveryFailureType);
        var freshStore = new SqliteRecoverySessionStore(_dbPath);
        var unresolved = await freshStore.ListUnresolvedAsync();
        Assert.Single(unresolved);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, unresolved[0].Lifecycle);
    }

    // ═══════════ 16. RollbackInexact_PersistsFailedRollbackIncomplete ═══════════
    [Fact] public async Task RollbackInexact_PersistsFailedRollbackIncomplete()
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80))]);
        var sharedEvents = new List<string>();
        var icons = new Dictionary<string, (PixelPoint from, PixelPoint to)>
        {
            ["a"] = (new PixelPoint(160, 80), new PixelPoint(240, 80)),
            ["b"] = (new PixelPoint(320, 80), new PixelPoint(400, 80)),
        };
        var cts = new CancellationTokenSource();
        var writer = new PhaseSpyWriter(sharedEvents, icons) { ForwardStepCount = 1, WrongActualRb = new(999, 999),
            OnMoveCall = callIdx => { if (callIdx == 0) cts.Cancel(); } };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80))]);

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan, cts.Token);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.Equal("RollbackInexact", result.RecoveryFailureType);
        Assert.True(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        // 数据库终态
        var freshStore = new SqliteRecoverySessionStore(_dbPath);
        var unresolved = await freshStore.ListUnresolvedAsync();
        Assert.Single(unresolved);
        Assert.Equal(RecoverySessionLifecycle.FailedRollbackIncomplete, unresolved[0].Lifecycle);
        Assert.True(unresolved[0].GetDisposition().RequiresManualRecovery);
        Assert.False(unresolved[0].GetDisposition().IsPositionUncertain);
    }

    // ═══════════ 17. RollbackVerificationTerminalMapping ═══════════
    [Theory]
    [InlineData("Passed", ExecutionVerificationStatus.Passed, TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack,
        RecoverySessionLifecycle.FailedAndRolledBack, false, false)]
    [InlineData("Failed", ExecutionVerificationStatus.Failed, TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
        RecoverySessionLifecycle.FailedRollbackIncomplete, true, false)]
    [InlineData("Uncertain", ExecutionVerificationStatus.Uncertain, TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
        RecoverySessionLifecycle.PositionUncertain, true, true)]
    public async Task RollbackVerificationTerminalMapping(
        string _, ExecutionVerificationStatus verifyStatus,
        TargetDisplayLayoutExecutionRunStatus expectedStatus, RecoverySessionLifecycle expectedLifecycle,
        bool expectedManual, bool expectedUncertain)
    {
        ResetOrd(); var store = CreateStore(); await store.InitializeAsync();
        // 3 步骤计划：步骤 0,1 成功，步骤 2 未执行，取消触发 Rollback
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 80)), Snap("b", new(400, 80)), Snap("c", new(560, 80))],
            [Live("a", new(160, 80)), Live("b", new(320, 80)), Live("c", new(480, 80))]);
        var sharedEvents = new List<string>();
        var icons = new Dictionary<string, (PixelPoint from, PixelPoint to)>
        {
            ["a"] = (new PixelPoint(160, 80), new PixelPoint(240, 80)),
            ["b"] = (new PixelPoint(320, 80), new PixelPoint(400, 80)),
            ["c"] = (new PixelPoint(480, 80), new PixelPoint(560, 80)),
        };
        var cts = new CancellationTokenSource();
        var writer = new PhaseSpyWriter(sharedEvents, icons)
        {
            ForwardStepCount = 2,
            OnMoveCall = callIdx => { if (callIdx == 1) cts.Cancel(); }
        };
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 80)), Live("b", new(320, 80)), Live("c", new(480, 80))]);

        var estore = new EventStore(_dbPath);
        estore.Events = sharedEvents;
        await estore.InitializeAsync();

        // 注入自定义 Rollback 验证，返回指定状态
        var verifyRb = (TargetDisplayLayoutExecutionPlan ep, TargetDisplayLayoutRecoverySession s, CancellationToken ct) =>
        {
            return verifyStatus switch
            {
                ExecutionVerificationStatus.Passed => Task.FromResult(
                    TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(DateTimeOffset.UtcNow)),
                ExecutionVerificationStatus.Failed => Task.FromResult(
                    TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(1, new[] { "test failure" }, DateTimeOffset.UtcNow)),
                ExecutionVerificationStatus.Uncertain => Task.FromResult(
                    TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(0, new[] { "test uncertain" }, DateTimeOffset.UtcNow)),
                _ => Task.FromResult(
                    TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(DateTimeOffset.UtcNow))
            };
        };

        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(), new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(), TargetDisplayLayoutExecutionPlanner.CreatePlan,
            verifyRollback: verifyRb,
            recoveryStore: estore);

        var result = await coordinator.ExecuteAsync(plan, cts.Token);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedManual, result.RequiresManualRecovery);
        Assert.Equal(expectedUncertain, result.IsPositionUncertain);

        // 数据库终态
        var freshStore = new SqliteRecoverySessionStore(_dbPath);
        var sessionId = result.Session!.SessionId;
        var record = await freshStore.GetAsync(sessionId);
        Assert.NotNull(record);
        Assert.Equal(expectedLifecycle, record.Lifecycle);
        Assert.Equal(expectedManual, record.GetDisposition().RequiresManualRecovery);
        Assert.Equal(expectedUncertain, record.GetDisposition().IsPositionUncertain);
        Assert.NotNull(record.VerificationResult);
        Assert.Equal(verifyStatus, record.VerificationResult.Status);
        // CompletedRollbackSourceStepIndexes 包含两个回滚步骤
        Assert.Equal(2, record.CompletedRollbackSourceStepIndexes.Count);
        Assert.Contains(0, record.CompletedRollbackSourceStepIndexes);
        Assert.Contains(1, record.CompletedRollbackSourceStepIndexes);
        // Revision 已增加
        Assert.True(record.Revision > 1);
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
