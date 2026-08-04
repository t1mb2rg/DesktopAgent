using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class SqliteRecoverySessionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelPoint Sp = new(80, 80);
    private static readonly PixelRect WA = new(0, 0, 800, 600);

    public SqliteRecoverySessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DesktopAgent.Tests.RecoveryStore.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* 尽力清理 */ }
    }

    private SqliteRecoverySessionStore CreateStore() => new(_dbPath);

    private static DisplayDescriptor MakeDisplay() =>
        new(TD, new PixelRect(0, 0, 1920, 1080), WA, 96, 96, true);

    private static DesktopIconPositioningCapabilities MakeCaps() =>
        new(false, Sp, ConnectionMethod.FindWindowSW, null);

    // ═══════════════════════════════════════════════════
    //  测试数据工厂
    // ═══════════════════════════════════════════════════

    private static int _ord;
    private void ResetOrd() => _ord = 0;

    private static DesktopIconSnapshotItem Snap(string sid, PixelPoint pos) =>
        new(_ord++, sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    private static DesktopIconDescriptor Live(string sid, PixelPoint pos) =>
        new(sid, $"N_{sid}", null, ItemKind.File, pos, TD, false, true);

    private TargetDisplayLayoutRestorePlan MakeRestorePlan(
        DesktopIconSnapshotItem[] snaps, DesktopIconDescriptor[] lives)
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow,
            TD, ConnectionMethod.FindWindowSW, 1, snaps);
        var read = new DesktopIconReadResult(lives, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);
        return TargetDisplayLayoutRestorePlanner.CreatePlan(snapshot, read,
            new[] { MakeDisplay() }, MakeCaps(), TD);
    }

    private static TargetDisplayLayoutExecutionPlan MakeExecutionPlan()
    {
        var step = new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
            new PixelPoint(160, 240), new PixelPoint(240, 320),
            TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD);
        return new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { step },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
    }

    private static TargetDisplayLayoutExecutionAttemptResult MakeExactAttempt(
        int attemptIndex, int sourceStepIndex,
        TargetDisplayLayoutExecutionAttemptPhase phase = TargetDisplayLayoutExecutionAttemptPhase.Forward)
    {
        return new TargetDisplayLayoutExecutionAttemptResult(
            attemptIndex, sourceStepIndex, phase,
            "a", new PixelPoint(160, 240), new PixelPoint(240, 320),
            "a", new PixelPoint(160, 240), new PixelPoint(240, 320),
            true, true,
            TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess,
            ConnectionMethod.FindWindowSW,
            null, null, null, null);
    }

    private RecoverySessionRecord MakeReadyRecord(
        Guid? sessionId = null, Guid? safetySnapshotId = null)
    {
        ResetOrd();
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 320))], [Live("a", new(160, 240))]);
        var execPlan = MakeExecutionPlan();

        return new RecoverySessionRecord
        {
            SessionId = sessionId ?? Guid.NewGuid(),
            SafetySnapshotId = safetySnapshotId ?? Guid.NewGuid(),
            TargetDeviceName = TD,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Lifecycle = RecoverySessionLifecycle.Ready,
            RequiresManualRecovery = false,
            IsPositionUncertain = false,
            LastFailedStepIndex = null,
            SourceRestorePlan = plan,
            ExecutionPlan = execPlan,
            ForwardAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            RollbackAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            ForwardFailureType = null,
            ForwardFailureMessage = null,
            RecoveryFailureType = null,
            RecoveryFailureMessage = null,
            VerificationResult = null,
            Revision = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private RecoverySessionRecord MakeCompletedRecord()
    {
        var record = MakeReadyRecord();
        var fwdAttempt = MakeExactAttempt(0, 0);
        return record with
        {
            Lifecycle = RecoverySessionLifecycle.Completed,
            ForwardAttempts = new[] { fwdAttempt },
            VerificationResult = TargetDisplayLayoutExecutionVerificationResult.ForwardPassed(
                DateTimeOffset.UtcNow),
        };
    }

    private RecoverySessionRecord MakePositionUncertainRecord()
    {
        var record = MakeReadyRecord();
        return record with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
            LastFailedStepIndex = 0,
            ForwardFailureType = "InexactPosition",
            ForwardFailureMessage = "位置不精确。",
        };
    }

    // ═══════════════════════════════════════════════════
    //  A. 全新数据库初始化为 v4
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task FreshInit_CreatesRecoveryTables()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        using var c = OpenRawConnection();
        Assert.True(TableExists("desktop_layout_recovery_sessions"));
        Assert.True(TableExists("desktop_layout_recovery_attempts"));
    }

    [Fact]
    public async Task FreshInit_UserVersion4()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        Assert.Equal(4, ReadUserVersion());
    }

    [Fact]
    public async Task FreshInit_RecoveryIndexExists()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        Assert.True(IndexExists("idx_recovery_sessions_lifecycle"));
    }

    // ═══════════════════════════════════════════════════
    //  C. v4 重复 Init 幂等
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task RepeatedInit_Idempotent()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.InitializeAsync();
        await store.InitializeAsync();
        Assert.Equal(4, ReadUserVersion());
    }

    // ═══════════════════════════════════════════════════
    //  E. Save Ready 检查点 — 精确 Round-trip
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task SaveReady_RoundTrip_ExactMatch()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.NotNull(loaded);

        // 逐字段精确验证
        Assert.Equal(record.SessionId, loaded!.SessionId);
        Assert.Equal(record.SafetySnapshotId, loaded.SafetySnapshotId);
        Assert.Equal(record.TargetDeviceName, loaded.TargetDeviceName);
        Assert.Equal(record.Lifecycle, loaded.Lifecycle);
        Assert.Equal(record.RequiresManualRecovery, loaded.RequiresManualRecovery);
        Assert.Equal(record.IsPositionUncertain, loaded.IsPositionUncertain);
        Assert.Equal(record.LastFailedStepIndex, loaded.LastFailedStepIndex);
        Assert.Equal(record.ForwardFailureType, loaded.ForwardFailureType);
        Assert.Equal(record.ForwardFailureMessage, loaded.ForwardFailureMessage);
        Assert.Equal(record.RecoveryFailureType, loaded.RecoveryFailureType);
        Assert.Equal(record.RecoveryFailureMessage, loaded.RecoveryFailureMessage);
        Assert.Equal(record.Revision, loaded.Revision);
        Assert.Null(loaded.VerificationResult);
        Assert.Empty(loaded.ForwardAttempts);
        Assert.Empty(loaded.RollbackAttempts);

        // Plans round-trip
        Assert.Equal(record.SourceRestorePlan.TargetDeviceName, loaded.SourceRestorePlan.TargetDeviceName);
        Assert.Equal(record.SourceRestorePlan.Items.Count, loaded.SourceRestorePlan.Items.Count);
        Assert.Equal(record.ExecutionPlan.Steps.Count, loaded.ExecutionPlan.Steps.Count);
    }

    // ═══════════════════════════════════════════════════
    //  F. Save 含 Forward Attempts + Verification
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task SaveCompleted_WithAttemptsAndVerification_RoundTrip()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeCompletedRecord();
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal(RecoverySessionLifecycle.Completed, loaded!.Lifecycle);
        Assert.Single(loaded.ForwardAttempts);
        Assert.Empty(loaded.RollbackAttempts);
        Assert.NotNull(loaded.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Passed, loaded.VerificationResult!.Status);
        Assert.Equal(ExecutionVerificationPhase.Forward, loaded.VerificationResult.Phase);
    }

    // ═══════════════════════════════════════════════════
    //  G. Save 含 Forward + Rollback + 人工处理状态
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task SavePositionUncertain_WithFailureEvidence_RoundTrip()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakePositionUncertainRecord();
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, loaded!.Lifecycle);
        Assert.True(loaded.RequiresManualRecovery);
        Assert.True(loaded.IsPositionUncertain);
        Assert.Equal("InexactPosition", loaded.ForwardFailureType);
    }

    // ═══════════════════════════════════════════════════
    //  H. Revision 原子更新
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Update_RevisionIncrements()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);
        Assert.Equal(1, record.Revision);

        // 更新生命周期（不带 WithNextRevision — UpdateAsync 内部递增）
        var updated = record with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
        };
        var result = await store.UpdateAsync(updated);
        Assert.Equal(2, result.Revision);
        Assert.Equal(RecoverySessionLifecycle.Executing, result.Lifecycle);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.Equal(2, loaded!.Revision);
        Assert.Single(loaded.ForwardAttempts);
    }

    [Fact]
    public async Task Update_MultipleRevisions_Accumulates()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var r1 = MakeReadyRecord();
        await store.SaveAsync(r1);

        // rev 1 → 2: Executing（UpdateAsync 内部调用 WithNextRevision）
        var r2 = r1 with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
        };
        var result2 = await store.UpdateAsync(r2);
        Assert.Equal(2, result2.Revision);

        // rev 2 → 3: Completed
        var r3 = result2 with
        {
            Lifecycle = RecoverySessionLifecycle.Completed,
            VerificationResult = TargetDisplayLayoutExecutionVerificationResult.ForwardPassed(
                DateTimeOffset.UtcNow),
        };
        var result3 = await store.UpdateAsync(r3);
        Assert.Equal(3, result3.Revision);

        var loaded = await store.GetAsync(r1.SessionId);
        Assert.Equal(3, loaded!.Revision);
        Assert.Equal(RecoverySessionLifecycle.Completed, loaded.Lifecycle);
    }

    // ═══════════════════════════════════════════════════
    //  I. 陈旧 Revision 拒绝
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Update_StaleRevision_ThrowsInvalidOperation()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        // 第一次更新成功（UpdateAsync 内部递增到 2）
        var updated1 = record with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
        };
        var result = await store.UpdateAsync(updated1);
        Assert.Equal(2, result.Revision);

        // 使用旧 Revision 1 尝试更新 → 应被拒绝
        var stale = record with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(stale));
        Assert.Contains("Revision", ex.Message);

        // 数据库仍保留 Revision 2 的完整数据
        var loaded = await store.GetAsync(record.SessionId);
        Assert.Equal(2, loaded!.Revision);
        Assert.Equal(RecoverySessionLifecycle.Executing, loaded.Lifecycle);
        Assert.Single(loaded.ForwardAttempts);
    }

    // ═══════════════════════════════════════════════════
    //  J. 保存过程中取消，数据库完整
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Save_Canceled_NoPartialData()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var record = MakeReadyRecord();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.SaveAsync(record, cts.Token));

        // 数据库不应有该记录
        var loaded = await store.GetAsync(record.SessionId);
        Assert.Null(loaded);
    }

    // ═══════════════════════════════════════════════════
    //  K. Get 不存在 → null
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Get_Nonexistent_ReturnsNull()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var result = await store.GetAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    // ═══════════════════════════════════════════════════
    //  L. ListUnresolved
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task ListUnresolved_ReturnsOnlyUnresolved()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        // Save 3 sessions with different states
        var completed = MakeCompletedRecord();
        await store.SaveAsync(completed);

        var farb = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.FailedAndRolledBack,
        };
        await store.SaveAsync(farb);

        var pu = MakePositionUncertainRecord();
        await store.SaveAsync(pu);

        var executing = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
        };
        await store.SaveAsync(executing);

        var unresolved = await store.ListUnresolvedAsync();
        Assert.Contains(unresolved, r => r.SessionId == pu.SessionId);
        Assert.Contains(unresolved, r => r.SessionId == executing.SessionId);
        Assert.DoesNotContain(unresolved, r => r.SessionId == completed.SessionId);
        Assert.DoesNotContain(unresolved, r => r.SessionId == farb.SessionId);
    }

    [Fact]
    public async Task ListUnresolved_OrderedByStartedAtUtcDesc()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var r1 = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
            StartedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var r2 = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
            StartedAtUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var r3 = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
            StartedAtUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        };

        await store.SaveAsync(r1);
        await store.SaveAsync(r2);
        await store.SaveAsync(r3);

        var unresolved = await store.ListUnresolvedAsync();
        Assert.Equal(3, unresolved.Count);
        // 按 StartedAtUtc DESC 排序
        Assert.Equal(r2.SessionId, unresolved[0].SessionId); // June
        Assert.Equal(r3.SessionId, unresolved[1].SessionId); // March
        Assert.Equal(r1.SessionId, unresolved[2].SessionId); // January
    }

    // ═══════════════════════════════════════════════════
    //  M. 防御性复制
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Save_ModifyOriginalAfterSave_StoredDataUnchanged()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        // 修改原始 record（模拟调用方修改）
        // 由于 RecoverySessionRecord 是不可变的 record，这个测试验证的是
        // Store 保存的是序列化副本，不受后续对象修改影响
        var loaded = await store.GetAsync(record.SessionId);
        Assert.Equal(RecoverySessionLifecycle.Ready, loaded!.Lifecycle);

        // 修改原始 reference 类型字段的副本
        var tampered = record with { Lifecycle = RecoverySessionLifecycle.PositionUncertain };
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, tampered.Lifecycle);

        // 原始存储记录不受影响
        var loaded2 = await store.GetAsync(record.SessionId);
        Assert.Equal(RecoverySessionLifecycle.Ready, loaded2!.Lifecycle);
    }

    // ═══════════════════════════════════════════════════
    //  N. 损坏数据 fail-closed
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Corrupted_InvalidLifecycle_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        // 直接 SQL 注入无效 Lifecycle 值
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET lifecycle = 99
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        // 读取应抛出 InvalidDataException
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task Corrupted_InvalidBoolean_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET requires_manual_recovery = 99
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task Corrupted_AttemptCountMismatch_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        // 注入额外的 attempt（但不更新父表计数）
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO desktop_layout_recovery_attempts
                (session_id, attempt_index, source_step_index, phase,
                 expected_stable_id, requested_x, requested_y,
                 has_trusted_actual_position, is_exact, outcome)
            VALUES
                (@sid, 0, 0, 0, 'x', 0, 0, 0, 0, 0);
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        // 父表 forward_attempt_count=0，子表有 1 条 → 不匹配
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task Corrupted_InvalidAttemptOutcome_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeCompletedRecord();
        await store.SaveAsync(record);

        // 注入无效 Outcome 值
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_attempts
            SET outcome = 99
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task Corrupted_InvalidPlanPayloadVersion_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET plan_payload_format_version = 99
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task Corrupted_InvalidAttemptPhase_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeCompletedRecord();
        await store.SaveAsync(record);

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_attempts
            SET phase = 99
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task Corrupted_AttemptBooleanInvalid_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeCompletedRecord();
        await store.SaveAsync(record);

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_attempts
            SET has_trusted_actual_position = 99
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task Corrupted_ConflictingLifecycleAndFlags_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        // Completed 状态下 flags 矛盾
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET lifecycle = 2,  -- Completed
                requires_manual_recovery = 1
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        // Validate() 应检测到 Completed + RequiresManualRecovery=true 矛盾
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task ListUnresolved_CorruptedRecord_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var r1 = MakePositionUncertainRecord();
        await store.SaveAsync(r1);

        var r2 = MakePositionUncertainRecord();
        await store.SaveAsync(r2);

        // 损坏 r2
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET lifecycle = 99
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", r2.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        // ListUnresolved 遇到损坏记录 → fail-closed，不静默跳过
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ListUnresolvedAsync());
    }

    // ═══════════════════════════════════════════════════
    //  P. 生产接线证明：真实 SQLite 文件完整 Round-trip
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task ProductionPath_FullRoundTrip_WithRevisionUpdate()
    {
        // 1. 创建真实临时 SQLite 文件
        var dbPath = Path.Combine(_tempDir, "prod.db");
        var store1 = new SqliteRecoverySessionStore(dbPath);

        // 2. 通过公开 Store 接口 Initialize
        await store1.InitializeAsync();

        // 3. 保存完整恢复聚合
        var record = MakeCompletedRecord();
        await store1.SaveAsync(record);

        // 4. 销毁 Store
        store1 = null!;
        GC.Collect();

        // 5. 新建 Store
        var store2 = new SqliteRecoverySessionStore(dbPath);

        // 6. 重新读取
        var loaded = await store2.GetAsync(record.SessionId);

        // 7. 逐字段比较完整聚合
        Assert.NotNull(loaded);
        Assert.Equal(record.SessionId, loaded!.SessionId);
        Assert.Equal(record.SafetySnapshotId, loaded.SafetySnapshotId);
        Assert.Equal(record.TargetDeviceName, loaded.TargetDeviceName);
        Assert.Equal(record.Lifecycle, loaded.Lifecycle);
        Assert.Equal(record.Revision, loaded.Revision);
        Assert.Single(loaded.ForwardAttempts);
        Assert.NotNull(loaded.VerificationResult);
        Assert.Equal(ExecutionVerificationStatus.Passed, loaded.VerificationResult!.Status);
        Assert.Equal(record.SourceRestorePlan.Items.Count, loaded.SourceRestorePlan.Items.Count);
        Assert.Equal(record.ExecutionPlan.Steps.Count, loaded.ExecutionPlan.Steps.Count);

        // 8. 再执行一次 Revision 更新并验证陈旧写入拒绝
        var updated = loaded with
        {
            Lifecycle = RecoverySessionLifecycle.FailedRollbackIncomplete,
            RequiresManualRecovery = true,
            RollbackAttempts = new[] { MakeExactAttempt(1, 0, TargetDisplayLayoutExecutionAttemptPhase.Rollback) },
            VerificationResult = TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(
                0, Array.Empty<string>(), DateTimeOffset.UtcNow),
        };
        var result = await store2.UpdateAsync(updated);
        Assert.Equal(2, result.Revision);

        // 陈旧 Revision 拒绝（loaded 有 Revision=1，但 DB 现在是 2）
        var stale = loaded;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store2.UpdateAsync(stale));
        Assert.Contains("Revision", ex.Message);

        // 最终读取确认
        var finalLoaded = await store2.GetAsync(record.SessionId);
        Assert.Equal(2, finalLoaded!.Revision);
        Assert.Equal(RecoverySessionLifecycle.FailedRollbackIncomplete, finalLoaded.Lifecycle);
    }

    // ═══════════════════════════════════════════════════
    //  内部辅助
    // ═══════════════════════════════════════════════════

    private SqliteConnection OpenRawConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        };
        var c = new SqliteConnection(builder.ConnectionString);
        c.Open();
        return c;
    }

    private int ReadUserVersion()
    {
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private bool TableExists(string name)
    {
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private bool IndexExists(string name)
    {
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        return (long)cmd.ExecuteScalar()! > 0;
    }
}
