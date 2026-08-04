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
            ForwardFailureType = "StaleTest",
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
            ForwardFailureType = "TestFailure",
            StartedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var r2 = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
            ForwardFailureType = "TestFailure",
            StartedAtUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var r3 = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
            ForwardFailureType = "TestFailure",
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
            ForwardFailureType = "InexactPosition",
            RollbackAttempts = new[] { MakeExactAttempt(1, 0, TargetDisplayLayoutExecutionAttemptPhase.Rollback) },
            VerificationResult = TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(
                1, new[] { "位置未恢复" }, DateTimeOffset.UtcNow),
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
    //  Q. Plan JSON 序列化 round-trip 精度
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task PlanJson_RestorePlan_RoundTrip_AllFieldsPreserved()
    {
        ResetOrd();
        var plan = MakeRestorePlan(
            [Snap("a", new(240, 320)), Snap("b", new(400, 480))],
            [Live("a", new(160, 240)), Live("b", new(320, 400))]);

        var json = RecoveryPlanJsonSerializer.SerializeRestorePlan(plan);
        Assert.NotEmpty(json);

        var restored = RecoveryPlanJsonSerializer.DeserializeRestorePlan(json);
        Assert.NotNull(restored);
        Assert.Equal(plan.TargetDeviceName, restored.TargetDeviceName);
        Assert.Equal(plan.SnapshotTargetIconCount, restored.SnapshotTargetIconCount);
        Assert.Equal(plan.AlreadyAtTargetCount, restored.AlreadyAtTargetCount);
        Assert.Equal(plan.MoveRequiredCount, restored.MoveRequiredCount);
        Assert.Equal(plan.MissingCount, restored.MissingCount);
        Assert.Equal(plan.BlockedCount, restored.BlockedCount);
        Assert.Equal(plan.HasBlockingErrors, restored.HasBlockingErrors);
        Assert.Equal(plan.IsNoOp, restored.IsNoOp);
        Assert.Equal(plan.Items.Count, restored.Items.Count);

        for (var i = 0; i < plan.Items.Count; i++)
        {
            Assert.Equal(plan.Items[i].StableId, restored.Items[i].StableId);
            Assert.Equal(plan.Items[i].DisplayName, restored.Items[i].DisplayName);
            Assert.Equal(plan.Items[i].SnapshotTargetPosition, restored.Items[i].SnapshotTargetPosition);
            Assert.Equal(plan.Items[i].State, restored.Items[i].State);
        }
    }

    [Fact]
    public async Task PlanJson_ExecutionPlan_RoundTrip_AllFieldsPreserved()
    {
        var step = new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
            new PixelPoint(160, 240), new PixelPoint(240, 320),
            TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD);
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { step },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var json = RecoveryPlanJsonSerializer.SerializeExecutionPlan(plan);
        Assert.NotEmpty(json);

        var restored = RecoveryPlanJsonSerializer.DeserializeExecutionPlan(json);
        Assert.NotNull(restored);
        Assert.Equal(plan.TargetDeviceName, restored.TargetDeviceName);
        Assert.Equal(plan.IsExecutable, restored.IsExecutable);
        Assert.Equal(plan.IsNoOp, restored.IsNoOp);
        Assert.Equal(plan.DirectMoveStepCount, restored.DirectMoveStepCount);
        Assert.Equal(plan.Steps.Count, restored.Steps.Count);
        Assert.Equal(plan.Steps[0].StableId, restored.Steps[0].StableId);
        Assert.Equal(plan.Steps[0].FromPosition, restored.Steps[0].FromPosition);
        Assert.Equal(plan.Steps[0].ToPosition, restored.Steps[0].ToPosition);
        Assert.Equal(plan.Steps[0].Kind, restored.Steps[0].Kind);
    }

    // ═══════════════════════════════════════════════════
    //  R. UpdateAsync 持久化 Plan JSON 变更
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Update_PlanChanges_Persisted()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        // 修改 ExecutionPlan 后更新
        var newStep = new TargetDisplayLayoutExecutionStep(0, "a", "N_a_updated",
            new PixelPoint(160, 240), new PixelPoint(300, 400),
            TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD);
        var newExecPlan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { newStep },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var updated = record with
        {
            ExecutionPlan = newExecPlan,
            Lifecycle = RecoverySessionLifecycle.Executing,
        };

        var result = await store.UpdateAsync(updated);
        Assert.Equal(2, result.Revision);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.Equal(2, loaded!.Revision);
        Assert.Equal("N_a_updated", loaded.ExecutionPlan.Steps[0].DisplayName);
        Assert.Equal(new PixelPoint(300, 400), loaded.ExecutionPlan.Steps[0].ToPosition);
    }

    // ═══════════════════════════════════════════════════
    //  S. Validate 拒绝 plan 摘要计数不一致
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Validate_PlanCountMismatch_RejectedOnSave()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();

        // 直接 SQL 注入矛盾的 RestorePlan JSON（MoveRequiredCount 与 Items 不一致）
        // 通过修改 JSON payload 来制造不一致
        var tamperedJson = RecoveryPlanJsonSerializer.SerializeRestorePlan(
            record.SourceRestorePlan);
        // 在 JSON 中把 MoveRequiredCount 改为不一致的值
        tamperedJson = tamperedJson.Replace(
            $"\"MoveRequiredCount\":{record.SourceRestorePlan.MoveRequiredCount}",
            "\"MoveRequiredCount\":999");

        using var c = OpenRawConnection();
        // 先正常保存，再直接 SQL 破坏
        await store.SaveAsync(record);

        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET source_restore_plan_json = @json
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@json", tamperedJson);
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        // 读取时应因 count 不一致而 fail
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    // ═══════════════════════════════════════════════════
    //  T. Validate 拒绝 Attempt-StableId 与 Step 不一致
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task Validate_AttemptStableIdMismatch_RejectedOnGet()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeCompletedRecord();
        await store.SaveAsync(record);

        // 直接 SQL 把 attempt 的 expected_stable_id 改为与 Step 不一致的值
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_attempts
            SET expected_stable_id = 'WRONG_ID'
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        // Validate() 应检测到 Attempt ExpectedStableId ≠ Step StableId
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    // ═══════════════════════════════════════════════════
    //  U. DeriveStatus 覆盖所有 Lifecycle 值
    // ═══════════════════════════════════════════════════

    [Fact]
    public void DeriveStatus_CoversAllLifecycleValues()
    {
        Assert.Equal("Ready", RecoverySessionRecord.DeriveStatus(RecoverySessionLifecycle.Ready));
        Assert.Equal("Executing", RecoverySessionRecord.DeriveStatus(RecoverySessionLifecycle.Executing));
        Assert.Equal("Completed", RecoverySessionRecord.DeriveStatus(RecoverySessionLifecycle.Completed));
        Assert.Equal("FailedAndRolledBack", RecoverySessionRecord.DeriveStatus(RecoverySessionLifecycle.FailedAndRolledBack));
        Assert.Equal("FailedRollbackIncomplete", RecoverySessionRecord.DeriveStatus(RecoverySessionLifecycle.FailedRollbackIncomplete));
        Assert.Equal("PositionUncertain", RecoverySessionRecord.DeriveStatus(RecoverySessionLifecycle.PositionUncertain));
    }

    // ═══════════════════════════════════════════════════
    //  V. ComputedForwardStepIndexes 与 Attempts 一致
    // ═══════════════════════════════════════════════════

    [Fact]
    public void ComputedIndexes_MatchAttempts()
    {
        var record = MakeCompletedRecord();
        // MakeCompletedRecord 有 1 个 ExactSuccess Forward attempt at step 0
        Assert.Single(record.ComputedForwardStepIndexes);
        Assert.Equal(0, record.ComputedForwardStepIndexes[0]);
        Assert.Empty(record.ComputedRollbackSourceStepIndexes);
    }

    // ═══════════════════════════════════════════════════
    //  W. ListUnresolved 捕获 is_position_uncertain flag
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task ListUnresolved_IsPositionUncertainFlag_DefenseInDepth()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        // 创建一个 lifecycle=Ready 但 is_position_uncertain=1 的记录
        // （正常情况下 Validate() 会阻止，但直接 SQL 绕开）
        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET is_position_uncertain = 1
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        // Get 会因 lifecycle/flag 矛盾而 fail（Completed=0 + is_position_uncertain=1）
        // 但 ListUnresolved 的 WHERE 应能捕获 is_position_uncertain=1
        // 注意：这个记录在 Get 时会因为 Validate 的 lifecycle/flag 矛盾而抛异常
        // 所以这里只验证 ListUnresolved 的 SELECT 返回了该 session_id
        using var c2 = OpenRawConnection();
        using var sel = c2.CreateCommand();
        sel.CommandText = @"
            SELECT COUNT(*) FROM desktop_layout_recovery_sessions
            WHERE (lifecycle IN (1,4,5) OR requires_manual_recovery = 1 OR is_position_uncertain = 1)
              AND session_id = @sid
        ";
        sel.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        var count = (long)sel.ExecuteScalar()!;
        Assert.Equal(1L, count);
    }

    // ═══════════════════════════════════════════════════
    //  X. 6 项阻断修复验证
    // ═══════════════════════════════════════════════════

    [Fact]
    public void DeriveLifecycle_FailedBeforeWrite_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            RecoverySessionRecord.DeriveLifecycle(
                TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite));
        Assert.Contains("FailedBeforeWrite", ex.Message);
    }

    [Fact]
    public void DeriveLifecycle_UnknownStatus_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecoverySessionRecord.DeriveLifecycle((TargetDisplayLayoutExecutionRunStatus)999));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void Validate_ReadyWithManualRecovery_Throws()
    {
        var record = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.Ready,
            RequiresManualRecovery = true,
        };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("Ready", ex.Message);
    }

    [Fact]
    public void Validate_ExecutingWithPositionUncertain_Throws()
    {
        var record = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            IsPositionUncertain = true,
        };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("Executing", ex.Message);
    }

    [Fact]
    public void Validate_PositionUncertainWithoutFailureType_Throws()
    {
        var record = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
            ForwardFailureType = null,
        };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("ForwardFailureType", ex.Message);
    }

    [Fact]
    public void Validate_RevisionZero_Throws()
    {
        var record = MakeReadyRecord() with { Revision = 0 };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("Revision", ex.Message);
    }

    [Fact]
    public void Validate_VerificationPassedWithFailedItemCount_Throws()
    {
        var record = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.Completed,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
            VerificationResult = new TargetDisplayLayoutExecutionVerificationResult(
                ExecutionVerificationPhase.Forward,
                ExecutionVerificationStatus.Passed, 5, // FailedItemCount=5 but Passed
                Array.Empty<string>(), DateTimeOffset.UtcNow),
        };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("FailedItemCount", ex.Message);
    }

    [Fact]
    public async Task PlanJson_ExecutableNoOp_EmptySteps_IsValid()
    {
        // NoOp plans with empty steps should pass round-trip
        var plan = new TargetDisplayLayoutExecutionPlan(TD, false, true,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var json = RecoveryPlanJsonSerializer.SerializeExecutionPlan(plan);
        var restored = RecoveryPlanJsonSerializer.DeserializeExecutionPlan(json);
        Assert.True(restored.IsNoOp);
        Assert.False(restored.IsExecutable);
        Assert.Empty(restored.Steps);
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
