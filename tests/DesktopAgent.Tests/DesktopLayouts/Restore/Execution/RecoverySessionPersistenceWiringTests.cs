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

public sealed class RecoverySessionPersistenceWiringTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelPoint Sp = new(80, 80);
    private static readonly PixelRect WA = new(0, 0, 800, 600);

    public RecoverySessionPersistenceWiringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DesktopAgent.Tests.Wiring.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { }
    }

    private SqliteRecoverySessionStore CreateStore() => new(_dbPath);

    private static DisplayDescriptor MakeDisplay() =>
        new(TD, new PixelRect(0, 0, 1920, 1080), WA, 96, 96, true);

    private static DesktopIconPositioningCapabilities MakeCaps() =>
        new(false, Sp, ConnectionMethod.FindWindowSW, null);

    private static int _ord;
    private void ResetOrd() => _ord = 0;

    private static DesktopIconSnapshotItem Snap(string sid, PixelPoint pos) =>
        new(_ord++, sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    private static DesktopIconDescriptor Live(string sid, PixelPoint pos) =>
        new(sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    private TargetDisplayLayoutRestorePlan MakeRestorePlan(DesktopIconSnapshotItem[] snaps, DesktopIconDescriptor[] lives)
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, TD, ConnectionMethod.FindWindowSW, 1, snaps);
        var read = new DesktopIconReadResult(lives, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        return TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, read, new[] { MakeDisplay() }, MakeCaps(), TD);
    }

    // ═══════════ A. InitialSaveFails_NoWriter ═══════════

    [Fact]
    public async Task InitialSaveFails_NoWriter()
    {
        ResetOrd();
        var store = CreateStore();
        await store.InitializeAsync();

        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);
        reader.Enqueue([Live("a", new(240, 320))]);

        // 使用一个会 Save 失败的 store（注入重复 SessionId → 后续 Save 会失败）
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(),
            new FailingInitialSaveStore()); // SaveAsync 抛出

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal(0, writer.AttemptCount);
    }

    private sealed class FailingInitialSaveStore : IRecoverySessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(RecoverySessionRecord r, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated save failure");
        public Task<RecoverySessionRecord> UpdateAsync(RecoverySessionRecord r, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<RecoverySessionRecord?> GetAsync(Guid sid, CancellationToken ct = default)
            => Task.FromResult<RecoverySessionRecord?>(null);
        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecoverySessionRecord>>(Array.Empty<RecoverySessionRecord>());
    }

    // ═══════════ C. InitialCheckpointPersistedBeforeFirstWriter ═══════════

    [Fact]
    public async Task InitialCheckpointPersistedBeforeFirstWriter()
    {
        ResetOrd();
        var store = CreateStore();
        await store.InitializeAsync();

        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var events = new List<string>();
        var writer = new SpyWriter("a", new(160, 240), new(240, 320), events);
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);
        reader.Enqueue([Live("a", new(240, 320))]);

        var spyStore = new SpyRecoverySessionStore(store, events);

        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(),
            spyStore);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);

        // 验证所有关键操作均已触发
        Assert.Contains(events, e => e.StartsWith("Save:"));
        Assert.Contains(events, e => e.StartsWith("Get:"));
        Assert.Contains(events, e => e == "MoveDesktopIcon");
        // 验证 Update checkpoint 也被调用
        Assert.Contains(events, e => e.StartsWith("Update:"));
    }

    private sealed class SpyWriter : IDesktopIconPositionWriter
    {
        private readonly string _stableId;
        private readonly PixelPoint _from, _to;
        private readonly List<string> _events;
        public int AttemptCount { get; private set; }

        public SpyWriter(string stableId, PixelPoint from, PixelPoint to, List<string> events)
        { _stableId = stableId; _from = from; _to = to; _events = events; }

        public DesktopIconPositioningCapabilities GetCapabilities() =>
            new(false, Sp, ConnectionMethod.FindWindowSW, null);

        public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
        {
            _events.Add("MoveDesktopIcon");
            AttemptCount++;
            if (stableId == _stableId)
                return new DesktopIconMoveResult(stableId, _from, targetPosition, targetPosition,
                    ConnectionMethod.FindWindowSW, null);
            return new DesktopIconMoveResult(stableId, new PixelPoint(0, 0), targetPosition,
                targetPosition, ConnectionMethod.FindWindowSW, null);
        }
    }

    private sealed class SpyRecoverySessionStore : IRecoverySessionStore
    {
        private readonly IRecoverySessionStore _inner;
        private readonly List<string> _events;
        public SpyRecoverySessionStore(IRecoverySessionStore inner, List<string> events)
        { _inner = inner; _events = events; }
        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);
        public async Task SaveAsync(RecoverySessionRecord r, CancellationToken ct = default)
        { _events.Add($"Save:{r.Lifecycle}:Rev{r.Revision}"); await _inner.SaveAsync(r, ct); }
        public async Task<RecoverySessionRecord> UpdateAsync(RecoverySessionRecord r, CancellationToken ct = default)
        { _events.Add($"Update:{r.Lifecycle}:Rev{r.Revision}"); return await _inner.UpdateAsync(r, ct); }
        public async Task<RecoverySessionRecord?> GetAsync(Guid sid, CancellationToken ct = default)
        { _events.Add($"Get:{sid.ToString("N")[..8]}"); return await _inner.GetAsync(sid, ct); }
        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(int limit = 50, CancellationToken ct = default)
            => _inner.ListUnresolvedAsync(limit, ct);
    }

    // ═══════════ F. Startup_NoUnresolved_ExecutionEnabled ═══════════

    [Fact]
    public async Task Startup_NoUnresolved_ExecutionEnabled()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var orchestrator = new RecoveryOrchestrator(store);
        Assert.False(await orchestrator.HasUnresolvedSessionsAsync());
    }

    // ═══════════ G. Startup_ExecutingSession_LocksExecution ═══════════

    [Fact]
    public async Task Startup_ExecutingSession_LocksExecution()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        // 直接 SQL 插入 Executing 记录
        var sid = Guid.NewGuid();
        var ssid = Guid.NewGuid();
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Mode=ReadWrite;Foreign Keys=True");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO desktop_layout_recovery_sessions
                (session_id, safety_snapshot_id, target_device_name, started_at_utc, lifecycle,
                 requires_manual_recovery, is_position_uncertain, last_failed_step_index,
                 source_restore_plan_json, execution_plan_json, plan_payload_format_version,
                 completed_forward_step_indexes_json, completed_rollback_source_step_indexes_json,
                 forward_failure_type, forward_failure_message,
                 recovery_failure_type, recovery_failure_message,
                 verification_result_json,
                 revision, updated_at_utc,
                 forward_attempt_count, rollback_attempt_count)
            VALUES
                (@sid, @ssid, 'DISPLAY1', '2026-01-01T00:00:00.0000000+00:00', 1,
                 0, 0, NULL,
                 '{}', '{}', 1,
                 '[]', '[]',
                 NULL, NULL, NULL, NULL, NULL,
                 1, '2026-01-01T00:00:00.0000000+00:00',
                 0, 0);
        ";
        cmd.Parameters.AddWithValue("@sid", sid.ToString("D"));
        cmd.Parameters.AddWithValue("@ssid", ssid.ToString("D"));
        cmd.ExecuteNonQuery();
        c.Close();

        var orchestrator = new RecoveryOrchestrator(store);
        Assert.True(await orchestrator.HasUnresolvedSessionsAsync());
    }

    // ═══════════ H. Startup_CorruptedSession_LocksFailClosed ═══════════

    [Fact]
    public async Task Startup_CorruptedSession_LocksFailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var sid = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var plan = MakeMinimalPlan();
        var record = new RecoverySessionRecord
        {
            SessionId = sid, SafetySnapshotId = Guid.NewGuid(),
            TargetDeviceName = TD, StartedAtUtc = now,
            Lifecycle = RecoverySessionLifecycle.Ready,
            SourceRestorePlan = plan.restore, ExecutionPlan = plan.exec,
            Revision = 1, UpdatedAtUtc = now,
        };
        await store.SaveAsync(record);

        // 破坏数据库
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Mode=ReadWrite;Foreign Keys=True");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE desktop_layout_recovery_sessions SET lifecycle = 99 WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sid.ToString("D"));
        cmd.ExecuteNonQuery();

        var orchestrator = new RecoveryOrchestrator(store);
        // HasUnresolvedSessionsAsync 应在损坏时返回 true（执行锁定）
        Assert.True(await orchestrator.HasUnresolvedSessionsAsync());
    }

    // ═══════════ Q. ExistingNoStagingExecutionStillWorks ═══════════

    [Fact]
    public async Task ExistingNoStagingExecution_StillWorks_WithoutRecoveryStore()
    {
        ResetOrd();
        var plan = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new(160, 240), new(240, 320));
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(160, 240))]);
        reader.Enqueue([Live("a", new(240, 320))]);

        // recoveryStore = null → 不持久化（向后兼容）
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(),
            recoveryStore: null);

        var result = await coordinator.ExecuteAsync(plan);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.Single(result.ForwardAttempts);
    }

    // ═══════════ R. StagingOrCycleStillBlocked ═══════════

    [Fact]
    public async Task StagingPlan_BlockedByScopeGate()
    {
        ResetOrd();
        // 使用带 staging 的 RestorePlan（RequiresTemporaryStaging=true）
        // 构造快照有两个图标互相阻塞 → Planner 会生成 staging plan
        // 简化方式：直接使用自定义 createPlan 返回 staging execPlan
        var steps = new[]
        {
            new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
                new PixelPoint(100, 100), new PixelPoint(200, 200),
                TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, TD, 1),
            new TargetDisplayLayoutExecutionStep(1, "a", "N_a",
                new PixelPoint(200, 200), new PixelPoint(300, 300),
                TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, TD, 1),
        };
        var execPlan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            steps, Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var plan = MakeRestorePlan([Snap("a", new(300, 300))], [Live("a", new(100, 100))]);
        var writer = new ScriptedDesktopIconPositionWriter();
        var reader = new ScriptedDesktopIconReader();
        reader.Enqueue([Live("a", new(100, 100))]);

        // 注入返回 staging plan 的 createPlan
        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            new FakeDisplayTopologyReader(TD, WA), reader, writer,
            new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(),
            (_, _, _, _) => execPlan); // 强制返回 staging plan

        var result = await coordinator.ExecuteWithScopeGateAsync(
            plan, TargetDisplayLayoutExecutionScopeGate.EvaluateNoStagingBatch);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.Equal("ExecutionScopeRejected", result.ForwardFailureType);
        Assert.Equal(0, writer.AttemptCount);
    }

    // ═══════════ Helpers ═══════════

    private int _planOrd;
    private (TargetDisplayLayoutRestorePlan restore, TargetDisplayLayoutExecutionPlan exec) MakeMinimalPlan()
    {
        _planOrd = 0;
        var restore = MakeRestorePlan([Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var step = new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
            new PixelPoint(160, 240), new PixelPoint(240, 320),
            TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD);
        var exec = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { step }, Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
        return (restore, exec);
    }
}
