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

public sealed class PersistedRecoveryCoordinatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelRect WA = new(0, 0, 1920, 1080);

    public PersistedRecoveryCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Tests.B0.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    private SqliteRecoverySessionStore CreateStore() => new(_dbPath);

    // ═══════════ Helpers ═══════════

    private static TargetDisplayLayoutExecutionStep MakeStep(int idx, string sid, int fx, int fy, int tx, int ty)
        => new(idx, sid, $"N_{sid}", new PixelPoint(fx, fy), new PixelPoint(tx, ty),
            TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD);

    private static TargetDisplayLayoutExecutionPlan MakeExecPlan(params TargetDisplayLayoutExecutionStep[] steps)
        => new(TD, true, false, steps,
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

    private static TargetDisplayLayoutRestorePlan MakeRestorePlan(
        string sid, int fx, int fy, int tx, int ty, string targetDevice)
    {
        var snapId = Guid.NewGuid();
        var snaps = new DesktopIconSnapshotItem[]
        {
            new(0, sid, $"N_{sid}", null, ItemKind.File, new PixelPoint(tx, ty), targetDevice, false, true)
        };
        var lives = new DesktopIconDescriptor[]
        {
            new(sid, $"N_{sid}", null, ItemKind.File, new PixelPoint(fx, fy), targetDevice, false, true)
        };
        var ss = new DesktopLayoutSnapshot(snapId, DateTimeOffset.UtcNow, targetDevice,
            ConnectionMethod.FindWindowSW, 1, snaps);
        var r = new DesktopIconReadResult(lives, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        return TargetDisplayLayoutRestorePlanner.CreatePlan(ss, r,
            new[] { new DisplayDescriptor(targetDevice, new PixelRect(0, 0, 1920, 1080), WA, 96, 96, true) },
            new DesktopIconPositioningCapabilities(false, new PixelPoint(80, 80), ConnectionMethod.FindWindowSW, null),
            targetDevice);
    }

    private static TargetDisplayLayoutExecutionAttemptResult MakeFwdAttempt(
        int attemptIdx, int stepIdx, string sid, int fx, int fy, int tx, int ty)
        => TargetDisplayLayoutExecutionAttemptResult.FromExactForward(
            attemptIdx, stepIdx,
            MakeStep(stepIdx, sid, fx, fy, tx, ty),
            new DesktopIconMoveResult(sid, new PixelPoint(fx, fy), new PixelPoint(tx, ty), new PixelPoint(tx, ty),
                ConnectionMethod.FindWindowSW, null));

    private static DisplayDescriptor MakeDisplay()
        => new(TD, new PixelRect(0, 0, 1920, 1080), WA, 96, 96, true);

    /// <summary>Seeds a session record into the store and returns it.</summary>
    private async Task<RecoverySessionRecord> SeedAsync(
        SqliteRecoverySessionStore store,
        RecoverySessionLifecycle lifecycle,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> fwdAttempts,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rbAttempts,
        int[] completedFwd,
        int[] completedRb,
        string? fwdFailType = null,
        string? fwdFailMsg = null)
    {
        await store.InitializeAsync();
        var plan = MakeRestorePlan("a", 160, 80, 240, 80, TD);
        var execPlan = MakeExecPlan(MakeStep(0, "a", 160, 80, 240, 80));
        var record = new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = Guid.NewGuid(),
            TargetDeviceName = TD,
            StartedAtUtc = DateTimeOffset.UtcNow,
            SourceRestorePlan = plan,
            ExecutionPlan = execPlan,
            Lifecycle = lifecycle,
            RequiresManualRecovery = lifecycle is RecoverySessionLifecycle.FailedRollbackIncomplete or RecoverySessionLifecycle.PositionUncertain,
            IsPositionUncertain = lifecycle == RecoverySessionLifecycle.PositionUncertain,
            ForwardAttempts = fwdAttempts,
            RollbackAttempts = rbAttempts,
            CompletedForwardStepIndexes = completedFwd,
            CompletedRollbackSourceStepIndexes = completedRb,
            ForwardFailureType = fwdFailType,
            ForwardFailureMessage = fwdFailMsg,
            Revision = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        record.Validate();
        await store.SaveAsync(record);
        var fresh = await store.GetAsync(record.SessionId);
        return fresh!;
    }

    // ═══════════ Spy infrastructure ═══════════

    private sealed class EventStore : IRecoverySessionStore
    {
        private readonly SqliteRecoverySessionStore _inner;
        public List<string> Events { get; }
        public bool FailUpdate;
        public int FailUpdateAfterCount = -1;
        private int _updateCallCount;

        public EventStore(string dbPath, List<string>? events = null)
        { _inner = new SqliteRecoverySessionStore(dbPath); Events = events ?? new List<string>(); }
        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);

        public async Task SaveAsync(RecoverySessionRecord r, CancellationToken ct = default)
        {
            Events.Add("Save");
            await _inner.SaveAsync(r, ct);
        }
        public async Task<RecoverySessionRecord> UpdateAsync(RecoverySessionRecord r, CancellationToken ct = default)
        {
            Events.Add($"Update:{r.Lifecycle}:Revision{r.Revision}");
            _updateCallCount++;
            if (FailUpdateAfterCount >= 0 && _updateCallCount > FailUpdateAfterCount)
                throw new InvalidOperationException("injected Update failure");
            if (FailUpdate) throw new InvalidOperationException("injected Update failure");
            return await _inner.UpdateAsync(r, ct);
        }
        public async Task<RecoverySessionRecord?> GetAsync(Guid sid, CancellationToken ct = default)
        {
            Events.Add("Get");
            return await _inner.GetAsync(sid, ct);
        }
        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(int limit = 50, CancellationToken ct = default)
            => _inner.ListUnresolvedAsync(limit, ct);
    }

    private sealed class SpyWriter : IDesktopIconPositionWriter
    {
        private readonly string _sid;
        private readonly PixelPoint _from, _to;
        private int _callCount;
        public List<string> Events { get; }
        public bool FailMove;

        public SpyWriter(List<string> events, string sid, PixelPoint from, PixelPoint to)
        { Events = events; _sid = sid; _from = from; _to = to; }

        public DesktopIconPositioningCapabilities GetCapabilities()
            => new(false, new PixelPoint(80, 80), ConnectionMethod.FindWindowSW, null);

        public DesktopIconMoveResult MoveDesktopIcon(string sid, PixelPoint target)
        {
            Events.Add($"Writer:{_callCount++}");
            if (FailMove) throw new InvalidOperationException("Writer BOMB");
            return new DesktopIconMoveResult(sid, _from, target, target, ConnectionMethod.FindWindowSW, null);
        }
    }

    /// <summary>Verification reader that emits an event on each read.</summary>
    private sealed class EventReader : IDesktopIconReader
    {
        private readonly List<string> _events;
        private readonly DesktopIconDescriptor _icon;
        public EventReader(List<string> events, DesktopIconDescriptor icon) { _events = events; _icon = icon; }
        public DesktopIconReadResult ReadDesktopIcons()
        {
            _events.Add("VerificationRead");
            return new DesktopIconReadResult(
                new List<DesktopIconDescriptor> { _icon }, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        }
    }

    /// <summary>Delegates to first reader, then second reader.</summary>
    private sealed class CompositeReader : IDesktopIconReader
    {
        private readonly IDesktopIconReader _first;
        private readonly IDesktopIconReader _second;
        private int _callCount;
        public CompositeReader(IDesktopIconReader first, IDesktopIconReader second) { _first = first; _second = second; }
        public DesktopIconReadResult ReadDesktopIcons()
            => (_callCount++ == 0 ? _first : _second).ReadDesktopIcons();
    }

    // ═══════════════════════════════════════════════
    // 1. Recovery_UsesLatestRevisionForTerminalAfterCheckpoint
    // ═══════════════════════════════════════════════
    [Fact]
    public async Task Recovery_UsesLatestRevisionForTerminalAfterCheckpoint()
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            new[] { 0 }, Array.Empty<int>(),
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        var estore = new EventStore(_dbPath, sharedEvents);
        var writer = new SpyWriter(sharedEvents, "a", new(240, 80), new(160, 80));
        // Verification reader that emits to shared event stream
        var verifyReader = new EventReader(sharedEvents,
            new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new PixelPoint(160, 80), TD, false, true));
        // Environment check: icon at ToPosition (240,80) — precondition passes
        var envReader = new ScriptedDesktopIconReader();
        envReader.Enqueue([new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new PixelPoint(240, 80), TD, false, true)]);

        // Compose: first read from envReader, second from verifyReader
        var compositeReader = new CompositeReader(envReader, verifyReader);
        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(estore, topoReader, compositeReader, writer, new StaThreadExecutor());

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        // Precise event sequence: Get(initial), Writer:0, Update:Executing(Rev1), Get,
        // VerificationRead, Update:FailedAndRolledBack(Rev2), Get
        var expected = new[]
        {
            "Get", "Writer:0",
            "Update:Executing:Revision1", "Get",
            "VerificationRead",
            "Update:FailedAndRolledBack:Revision2", "Get"
        };
        Assert.Equal(expected, sharedEvents);
        // Fresh store verification
        var freshStore = CreateStore();
        var record = await freshStore.GetAsync(seed.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionLifecycle.FailedAndRolledBack, record.Lifecycle);
        Assert.Equal(3, record.Revision);
    }

    // ═══════════════════════════════════════════════
    // 2. Recovery_ZeroPendingSteps_PersistsSafeTerminal
    // ═══════════════════════════════════════════════
    [Fact]
    public async Task Recovery_ZeroPendingSteps_PersistsSafeTerminal()
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var rbAttempt = TargetDisplayLayoutExecutionAttemptResult.FromExactRollback(
            1, 0,
            MakeStep(0, "a", 160, 80, 240, 80),
            new PixelPoint(160, 80), new PixelPoint(240, 80),
            new DesktopIconMoveResult("a", new PixelPoint(240, 80), new PixelPoint(160, 80), new PixelPoint(160, 80),
                ConnectionMethod.FindWindowSW, null));
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, new[] { rbAttempt },
            new[] { 0 }, new[] { 0 },
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        var estore = new EventStore(_dbPath, sharedEvents);
        var writer = new SpyWriter(sharedEvents, "a", new(240, 80), new(160, 80));
        var iconReader = new ScriptedDesktopIconReader();
        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(estore, topoReader, iconReader, writer, new StaThreadExecutor());

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        // 0 pending → 0 Writer calls, 1 terminal Update (Revision=1→2)
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:"));
        Assert.Contains("Update:FailedAndRolledBack:Revision1", sharedEvents);
        Assert.Equal(0, result.RollbackAttempts.Count - seed.RollbackAttempts.Count); // no new attempts
        var freshStore = CreateStore();
        var record = await freshStore.GetAsync(seed.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionLifecycle.FailedAndRolledBack, record.Lifecycle);
        Assert.Equal(2, record.Revision);
    }

    // ═══════════════════════════════════════════════
    // 3. Recovery_FailedVerification_PersistsLegalTerminal
    // ═══════════════════════════════════════════════
    [Fact]
    public async Task Recovery_FailedVerification_PersistsLegalTerminal()
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            new[] { 0 }, Array.Empty<int>(),
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        var estore = new EventStore(_dbPath, sharedEvents);
        var writer = new SpyWriter(sharedEvents, "a", new(240, 80), new(160, 80));
        var iconReader = new ScriptedDesktopIconReader();
        // Environment check: icon at ToPosition (240,80)
        iconReader.Enqueue([new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new PixelPoint(240, 80), TD, false, true)]);
        // Independent verification: icon STILL at ToPosition — NOT restored
        iconReader.Enqueue([new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new PixelPoint(240, 80), TD, false, true)]);

        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(estore, topoReader, iconReader, writer, new StaThreadExecutor());

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        var freshStore = CreateStore();
        var record = await freshStore.GetAsync(seed.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionLifecycle.FailedRollbackIncomplete, record.Lifecycle);
        Assert.True(record.RequiresManualRecovery);
        Assert.False(record.IsPositionUncertain);
        Assert.False(string.IsNullOrWhiteSpace(record.ForwardFailureType));
        Assert.False(string.IsNullOrWhiteSpace(record.ForwardFailureMessage));
        Assert.Equal("RecoveryVerificationFailed", record.RecoveryFailureType);
        Assert.False(string.IsNullOrWhiteSpace(record.RecoveryFailureMessage));
        Assert.NotNull(record.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Failed, record.VerificationResult.Status);
    }

    // ═══════════════════════════════════════════════
    // 4. Recovery_StableIdMissing_NoWriter_NoAttempt
    // ═══════════════════════════════════════════════
    [Fact]
    public async Task Recovery_StableIdMissing_NoWriter_NoAttempt()
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            new[] { 0 }, Array.Empty<int>(),
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        var estore = new EventStore(_dbPath, sharedEvents);
        var writer = new SpyWriter(sharedEvents, "a", new(240, 80), new(160, 80));
        // Environment check: "a" NOT present → StableId missing
        var iconReader = new ScriptedDesktopIconReader();
        iconReader.Enqueue([new DesktopIconDescriptor("b", "N_b", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);

        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(estore, topoReader, iconReader, writer, new StaThreadExecutor());

        var beforeRbCount = seed.RollbackAttempts.Count;
        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        // 0 Writer calls
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:"));
        // RollbackAttempts count unchanged
        Assert.Equal(beforeRbCount, result.RollbackAttempts.Count);
        var freshStore = CreateStore();
        var record = await freshStore.GetAsync(seed.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, record.Lifecycle);
        Assert.Equal("RecoveryStableIdMissing", record.RecoveryFailureType);
    }

    // ═══════════════════════════════════════════════
    // 5. Recovery_PreconditionPositionMismatch_NoWriter_NoAttempt
    // ═══════════════════════════════════════════════
    [Fact]
    public async Task Recovery_PreconditionPositionMismatch_NoWriter_NoAttempt()
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            new[] { 0 }, Array.Empty<int>(),
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        var estore = new EventStore(_dbPath, sharedEvents);
        var writer = new SpyWriter(sharedEvents, "a", new(240, 80), new(160, 80));
        // Environment check: "a" at wrong position (999,999) — precondition mismatch
        var iconReader = new ScriptedDesktopIconReader();
        iconReader.Enqueue([new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new PixelPoint(999, 999), TD, false, true)]);

        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(estore, topoReader, iconReader, writer, new StaThreadExecutor());

        var beforeRbCount = seed.RollbackAttempts.Count;
        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        // 0 Writer calls
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:"));
        // RollbackAttempts unchanged — no fake PreviousPositionMismatch Attempt
        Assert.Equal(beforeRbCount, result.RollbackAttempts.Count);
        Assert.DoesNotContain(result.RollbackAttempts,
            a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.PreviousPositionMismatch);
        var freshStore = CreateStore();
        var record = await freshStore.GetAsync(seed.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, record.Lifecycle);
        Assert.Equal("RecoveryPreconditionPositionMismatch", record.RecoveryFailureType);
    }

    // ═══════════════════════════════════════════════
    // 6. Recovery_InvalidStepIndex_NoWriter_NoAttempt
    // ═══════════════════════════════════════════════
    [Fact]
    public async Task Recovery_InvalidStepIndex_NoWriter_NoAttempt()
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            new[] { 0 }, Array.Empty<int>(),
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        // TamperGet returns record with out-of-range pending step index
        // The corrupted record triggers InvalidStepIndex → PersistPreWriteFailureAsync →
        // Validate fails (data inconsistent) → TerminalPersistFailed.
        // DB stays at Executing (defensive path against corruption).
        var tampered = seed with { CompletedForwardStepIndexes = new[] { 99 } };
        var estore = new EventStore(_dbPath, sharedEvents);
        var wrapperStore = new TamperGetStore(estore, tampered);
        var writer = new SpyWriter(sharedEvents, "a", new(240, 80), new(160, 80));
        var iconReader = new ScriptedDesktopIconReader();
        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(wrapperStore, topoReader, iconReader, writer, new StaThreadExecutor());

        var beforeRbCount = seed.RollbackAttempts.Count;
        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        // 0 Writer calls — the InvalidStepIndex is detected before any Writer
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:"));
        // Attempt count unchanged (no fake Attempts)
        Assert.Equal(beforeRbCount, result.RollbackAttempts.Count);
        // DB stays at Executing — corrupted record prevents valid persist
        var freshStore = CreateStore();
        var record = await freshStore.GetAsync(seed.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionLifecycle.Executing, record.Lifecycle);
        // RunResult carries TerminalPersistFailed
        Assert.Equal("TerminalPersistFailed", result.ForwardFailureType);
    }

    private sealed class TamperGetStore : IRecoverySessionStore
    {
        private readonly IRecoverySessionStore _inner;
        private readonly RecoverySessionRecord _tampered;
        public TamperGetStore(IRecoverySessionStore inner, RecoverySessionRecord tampered)
        { _inner = inner; _tampered = tampered; }
        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);
        public Task SaveAsync(RecoverySessionRecord r, CancellationToken ct = default) => _inner.SaveAsync(r, ct);
        public Task<RecoverySessionRecord> UpdateAsync(RecoverySessionRecord r, CancellationToken ct = default) => _inner.UpdateAsync(r, ct);
        public async Task<RecoverySessionRecord?> GetAsync(Guid sid, CancellationToken ct = default)
        {
            var r = await _inner.GetAsync(sid, ct);
            if (r is not null && r.SessionId == _tampered.SessionId) return _tampered;
            return r;
        }
        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(int limit = 50, CancellationToken ct = default)
            => _inner.ListUnresolvedAsync(limit, ct);
    }

    // ═══════════════════════════════════════════════
    // 7. Recovery_IncompleteDesktopRead_NoWriter_NoAttempt
    // ═══════════════════════════════════════════════
    [Fact]
    public async Task Recovery_IncompleteDesktopRead_NoWriter_NoAttempt()
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            new[] { 0 }, Array.Empty<int>(),
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        var estore = new EventStore(_dbPath, sharedEvents);
        var writer = new SpyWriter(sharedEvents, "a", new(240, 80), new(160, 80));
        // Environment check: FailedItemCount=1 → incomplete read
        var rawRead = new DesktopIconReadResult(
            new List<DesktopIconDescriptor>(), 1, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        var iconReader = new FuncDesktopIconReader(() => rawRead);

        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(estore, topoReader, iconReader, writer, new StaThreadExecutor());

        var beforeRbCount = seed.RollbackAttempts.Count;
        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:"));
        Assert.Equal(beforeRbCount, result.RollbackAttempts.Count);
        var freshStore = CreateStore();
        var record = await freshStore.GetAsync(seed.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, record.Lifecycle);
        Assert.Equal("RecoveryDesktopReadIncomplete", record.RecoveryFailureType);
    }

    /// <summary>Simple IDesktopIconReader that returns a fixed result.</summary>
    private sealed class FuncDesktopIconReader : IDesktopIconReader
    {
        private readonly Func<DesktopIconReadResult> _fn;
        public FuncDesktopIconReader(Func<DesktopIconReadResult> fn) => _fn = fn;
        public DesktopIconReadResult ReadDesktopIcons() => _fn();
    }

    // ═══════════════════════════════════════════════
    // 8. Recovery_PreWriteFailurePersistFails_ReturnsTerminalPersistFailed
    // ═══════════════════════════════════════════════
    [Theory]
    [InlineData("StableIdMissing")]
    [InlineData("PositionMismatch")]
    public async Task Recovery_PreWriteFailurePersistFails_ReturnsTerminalPersistFailed(string failureMode)
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            new[] { 0 }, Array.Empty<int>(),
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        var estore = new EventStore(_dbPath, sharedEvents) { FailUpdate = true };
        var writer = new SpyWriter(sharedEvents, "a", new(240, 80), new(160, 80));

        IDesktopIconReader iconReader = failureMode switch
        {
            "StableIdMissing" => new ScriptedDesktopIconReader(),
            "PositionMismatch" => new ScriptedDesktopIconReader(),
            _ => throw new ArgumentException(failureMode)
        };

        if (failureMode == "StableIdMissing")
            ((ScriptedDesktopIconReader)iconReader).Enqueue([new DesktopIconDescriptor("b", "N_b", null, ItemKind.File, new PixelPoint(0, 0), TD, false, true)]);
        else
            ((ScriptedDesktopIconReader)iconReader).Enqueue([new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new PixelPoint(999, 999), TD, false, true)]);

        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(estore, topoReader, iconReader, writer, new StaThreadExecutor());

        var beforeRbCount = seed.RollbackAttempts.Count;
        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.Contains("TerminalPersistFailed", result.ForwardFailureType);
        // 0 Writer calls
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:"));
        // Attempt count unchanged
        Assert.Equal(beforeRbCount, result.RollbackAttempts.Count);
    }

    // ═══════════════════════════════════════════════
    // 9. Recovery_WriterException_UsesFreshCheckpointAndStops
    // ═══════════════════════════════════════════════
    [Fact]
    public async Task Recovery_WriterException_UsesFreshCheckpointAndStops()
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            new[] { 0 }, Array.Empty<int>(),
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        var estore = new EventStore(_dbPath, sharedEvents);
        var writer = new SpyWriter(sharedEvents, "a", new(240, 80), new(160, 80)) { FailMove = true };
        var iconReader = new ScriptedDesktopIconReader();
        // Environment check: icon at ToPosition
        iconReader.Enqueue([new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new PixelPoint(240, 80), TD, false, true)]);

        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(estore, topoReader, iconReader, writer, new StaThreadExecutor());

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        // Writer called exactly once, then exception
        Assert.Contains("Writer:0", sharedEvents);
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:1"));
        // Single Update:PositionUncertain, then Get — no second terminal Update
        Assert.Contains("Update:PositionUncertain:Revision1", sharedEvents);
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Update:FailedAndRolledBack"));
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Update:FailedRollbackIncomplete"));
        // Fresh store: PositionUncertain + Revision increased once
        var freshStore = CreateStore();
        var record = await freshStore.GetAsync(seed.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, record.Lifecycle);
        Assert.True(record.RequiresManualRecovery);
        Assert.True(record.IsPositionUncertain);
        Assert.Equal(2, record.Revision); // 1→save, 2→WriterException persist
        // Exactly one new RollbackAttempt: WriterException
        Assert.Equal(seed.RollbackAttempts.Count + 1, record.RollbackAttempts.Count);
        var rb = record.RollbackAttempts.Last();
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.WriterException, rb.Outcome);
    }

    // ═══════════════════════════════════════════════
    // 10. Recovery_WriterStableIdMismatch_UsesFreshCheckpointAndStops
    // ═══════════════════════════════════════════════
    [Fact]
    public async Task Recovery_WriterStableIdMismatch_UsesFreshCheckpointAndStops()
    {
        var store = CreateStore();
        var fwdAttempt = MakeFwdAttempt(0, 0, "a", 160, 80, 240, 80);
        var seed = await SeedAsync(store, RecoverySessionLifecycle.Executing,
            new[] { fwdAttempt }, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            new[] { 0 }, Array.Empty<int>(),
            "TestInterrupted", "测试中断。");

        var sharedEvents = new List<string>();
        var estore = new EventStore(_dbPath, sharedEvents);
        // Writer returns wrong StableId
        var writer = new WrongStableIdWriter(sharedEvents, "a", new(240, 80), new(160, 80), "WRONG");
        var iconReader = new ScriptedDesktopIconReader();
        // Environment check: icon at ToPosition
        iconReader.Enqueue([new DesktopIconDescriptor("a", "N_a", null, ItemKind.File, new PixelPoint(240, 80), TD, false, true)]);

        var topoReader = new FakeDisplayTopologyReader(TD, WA);
        var coordinator = new PersistedRecoveryCoordinator(estore, topoReader, iconReader, writer, new StaThreadExecutor());

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.Equal("RollbackStableIdMismatch", result.ForwardFailureType);
        // Writer called exactly once
        Assert.Contains(sharedEvents, e => e.StartsWith("Writer:"));
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Writer:1"));
        // Single Update:PositionUncertain — no terminal Update
        Assert.Contains("Update:PositionUncertain:Revision1", sharedEvents);
        Assert.DoesNotContain(sharedEvents, e => e.StartsWith("Update:FailedAndRolledBack"));
        // Fresh store
        var freshStore = CreateStore();
        var record = await freshStore.GetAsync(seed.SessionId);
        Assert.NotNull(record);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, record.Lifecycle);
        Assert.Equal("RollbackStableIdMismatch", record.RecoveryFailureType);
        Assert.Equal(2, record.Revision);
        Assert.Equal(seed.RollbackAttempts.Count + 1, record.RollbackAttempts.Count);
        var rb = record.RollbackAttempts.Last();
        Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.StableIdMismatch, rb.Outcome);
    }

    private sealed class WrongStableIdWriter : IDesktopIconPositionWriter
    {
        private readonly List<string> _events;
        private readonly string _sid;
        private readonly PixelPoint _from, _to;
        private readonly string _wrongSid;
        private int _callCount;
        public WrongStableIdWriter(List<string> events, string sid, PixelPoint from, PixelPoint to, string wrongSid)
        { _events = events; _sid = sid; _from = from; _to = to; _wrongSid = wrongSid; }
        public DesktopIconPositioningCapabilities GetCapabilities()
            => new(false, new PixelPoint(80, 80), ConnectionMethod.FindWindowSW, null);
        public DesktopIconMoveResult MoveDesktopIcon(string sid, PixelPoint target)
        {
            _events.Add($"Writer:{_callCount++}");
            return new DesktopIconMoveResult(_wrongSid, _from, target, target, ConnectionMethod.FindWindowSW, null);
        }
    }
}
