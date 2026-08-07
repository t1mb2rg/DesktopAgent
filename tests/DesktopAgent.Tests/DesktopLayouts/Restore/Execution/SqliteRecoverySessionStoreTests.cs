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
            CompletedForwardStepIndexes = new[] { 0 },
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
            LastFailedStepIndex = 0,
            ForwardFailureType = "InexactPosition",
            ForwardFailureMessage = "位置不精确。",
        };
    }

    // ═══════════════════════════════════════════════════
    //  A. 全新数据库初始化为 v5
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
    public async Task FreshInit_UserVersion5()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        Assert.Equal(5, ReadUserVersion());
    }

    [Fact]
    public async Task FreshInit_RecoveryIndexExists()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        Assert.True(IndexExists("idx_recovery_sessions_lifecycle"));
    }

    // ═══════════════════════════════════════════════════
    //  C. v5 重复 Init 幂等
    // ═══════════════════════════════════════════════════

    [Fact]
    public async Task RepeatedInit_Idempotent()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.InitializeAsync();
        await store.InitializeAsync();
        Assert.Equal(5, ReadUserVersion());
    }

    [Fact]
    public async Task SaveReady_RoundTrip_ExactMatch()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.NotNull(loaded);

        Assert.Equal(record.SessionId, loaded!.SessionId);
        Assert.Equal(record.SafetySnapshotId, loaded.SafetySnapshotId);
        Assert.Equal(record.TargetDeviceName, loaded.TargetDeviceName);
        Assert.Equal(record.Lifecycle, loaded.Lifecycle);
        Assert.Equal(record.GetDisposition(), loaded.GetDisposition());
        Assert.Equal(record.LastFailedStepIndex, loaded.LastFailedStepIndex);
        Assert.Equal(record.ForwardFailureType, loaded.ForwardFailureType);
        Assert.Equal(record.ForwardFailureMessage, loaded.ForwardFailureMessage);
        Assert.Equal(record.RecoveryFailureType, loaded.RecoveryFailureType);
        Assert.Equal(record.RecoveryFailureMessage, loaded.RecoveryFailureMessage);
        Assert.Equal(record.Revision, loaded.Revision);
        Assert.Null(loaded.VerificationResult);
        Assert.Empty(loaded.ForwardAttempts);
        Assert.Empty(loaded.RollbackAttempts);

        Assert.Equal(record.SourceRestorePlan.TargetDeviceName, loaded.SourceRestorePlan.TargetDeviceName);
        Assert.Equal(record.SourceRestorePlan.Items.Count, loaded.SourceRestorePlan.Items.Count);
        Assert.Equal(record.ExecutionPlan.Steps.Count, loaded.ExecutionPlan.Steps.Count);
    }

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
        var disposition = loaded.GetDisposition();
        Assert.True(disposition.RequiresManualRecovery);
        Assert.True(disposition.IsPositionUncertain);
        Assert.Equal("InexactPosition", loaded.ForwardFailureType);
    }

    [Fact]
    public async Task Update_RevisionIncrements()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);
        Assert.Equal(1, record.Revision);

        var updated = record with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
            CompletedForwardStepIndexes = new[] { 0 },
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

        var r2 = r1 with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
            CompletedForwardStepIndexes = new[] { 0 },
        };
        var result2 = await store.UpdateAsync(r2);
        Assert.Equal(2, result2.Revision);

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

    [Fact]
    public async Task Update_StaleRevision_ThrowsInvalidOperation()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        var updated1 = record with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
            CompletedForwardStepIndexes = new[] { 0 },
        };
        var result = await store.UpdateAsync(updated1);
        Assert.Equal(2, result.Revision);

        var stale = record with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            ForwardFailureType = "StaleTest",
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(stale));
        Assert.Contains("Revision", ex.Message);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.Equal(2, loaded!.Revision);
        Assert.Equal(RecoverySessionLifecycle.Executing, loaded.Lifecycle);
        Assert.Single(loaded.ForwardAttempts);
    }

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

        var loaded = await store.GetAsync(record.SessionId);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Get_Nonexistent_ReturnsNull()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var result = await store.GetAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task ListUnresolved_ReturnsOnlyUnresolved()
    {
        var store = CreateStore();
        await store.InitializeAsync();

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
            ForwardFailureType = "TestFailure",
            StartedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var r2 = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            ForwardFailureType = "TestFailure",
            StartedAtUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var r3 = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            ForwardFailureType = "TestFailure",
            StartedAtUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        };

        await store.SaveAsync(r1);
        await store.SaveAsync(r2);
        await store.SaveAsync(r3);

        var unresolved = await store.ListUnresolvedAsync();
        Assert.Equal(3, unresolved.Count);
        Assert.Equal(r2.SessionId, unresolved[0].SessionId);
        Assert.Equal(r3.SessionId, unresolved[1].SessionId);
        Assert.Equal(r1.SessionId, unresolved[2].SessionId);
    }

    [Fact]
    public async Task Save_ModifyOriginalAfterSave_StoredDataUnchanged()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.Equal(RecoverySessionLifecycle.Ready, loaded!.Lifecycle);

        var tampered = record with { Lifecycle = RecoverySessionLifecycle.PositionUncertain };
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, tampered.Lifecycle);

        var loaded2 = await store.GetAsync(record.SessionId);
        Assert.Equal(RecoverySessionLifecycle.Ready, loaded2!.Lifecycle);
    }

    [Fact]
    public async Task Corrupted_InvalidLifecycle_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET lifecycle = 99
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task LegacyRecoveryFlagColumns_ArePhysicallyAbsent()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        Assert.False(ColumnExists("desktop_layout_recovery_sessions", "requires_manual_recovery"));
        Assert.False(ColumnExists("desktop_layout_recovery_sessions", "is_position_uncertain"));
        Assert.True(ColumnExists("desktop_layout_recovery_sessions", "lifecycle"));
    }

    [Fact]
    public async Task Corrupted_AttemptCountMismatch_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

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
    public async Task ListUnresolved_MultipleCorrupted_ReportsAll()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var r1 = MakePositionUncertainRecord();
        await store.SaveAsync(r1);
        var r2 = MakePositionUncertainRecord();
        await store.SaveAsync(r2);
        var r3 = MakePositionUncertainRecord();
        await store.SaveAsync(r3);

        using var c = OpenRawConnection();
        using var cmd1 = c.CreateCommand();
        cmd1.CommandText = "UPDATE desktop_layout_recovery_sessions SET lifecycle = 99 WHERE session_id = @sid";
        cmd1.Parameters.AddWithValue("@sid", r1.SessionId.ToString("D"));
        cmd1.ExecuteNonQuery();

        using var cmd3 = c.CreateCommand();
        cmd3.CommandText = "UPDATE desktop_layout_recovery_sessions SET lifecycle = 99 WHERE session_id = @sid";
        cmd3.Parameters.AddWithValue("@sid", r3.SessionId.ToString("D"));
        cmd3.ExecuteNonQuery();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ListUnresolvedAsync());
        Assert.Contains("2", ex.Message);
        Assert.Contains(r1.SessionId.ToString("D"), ex.Message);
        Assert.Contains(r3.SessionId.ToString("D"), ex.Message);
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

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET lifecycle = 99
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", r2.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ListUnresolvedAsync());
    }

    [Fact]
    public async Task ProductionPath_FullRoundTrip_WithRevisionUpdate()
    {
        var dbPath = Path.Combine(_tempDir, "prod.db");
        var store1 = new SqliteRecoverySessionStore(dbPath);
        await store1.InitializeAsync();

        var record = MakeCompletedRecord();
        await store1.SaveAsync(record);

        store1 = null!;
        GC.Collect();

        var store2 = new SqliteRecoverySessionStore(dbPath);
        var loaded = await store2.GetAsync(record.SessionId);

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

        var rbAttempt = new TargetDisplayLayoutExecutionAttemptResult(
            1, 0, TargetDisplayLayoutExecutionAttemptPhase.Rollback,
            "a", new PixelPoint(240, 320), new PixelPoint(160, 240),
            "a", new PixelPoint(240, 320), new PixelPoint(160, 240),
            true, true,
            TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess,
            ConnectionMethod.FindWindowSW,
            null, null, null, null);
        var updated = loaded with
        {
            Lifecycle = RecoverySessionLifecycle.FailedRollbackIncomplete,
            ForwardFailureType = "InexactPosition",
            RollbackAttempts = new[] { rbAttempt },
            CompletedRollbackSourceStepIndexes = new[] { 0 },
            VerificationResult = TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(
                1, new[] { "位置未恢复" }, DateTimeOffset.UtcNow),
        };
        var result = await store2.UpdateAsync(updated);
        Assert.Equal(2, result.Revision);

        var stale = loaded;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store2.UpdateAsync(stale));
        Assert.Contains("Revision", ex.Message);

        var finalLoaded = await store2.GetAsync(record.SessionId);
        Assert.Equal(2, finalLoaded!.Revision);
        Assert.Equal(RecoverySessionLifecycle.FailedRollbackIncomplete, finalLoaded.Lifecycle);
    }

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

    [Fact]
    public async Task Update_PlanChanged_Throws()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);
        var newStep = new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
            new PixelPoint(160, 240), new PixelPoint(300, 400),
            TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD);
        var newExecPlan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            new[] { newStep },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var updated = loaded! with
        {
            ExecutionPlan = newExecPlan,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(updated));
        Assert.Contains("ExecutionPlan", ex.Message);
    }

    [Fact]
    public async Task Validate_PlanCountMismatch_RejectedOnSave()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();

        var tamperedJson = RecoveryPlanJsonSerializer.SerializeRestorePlan(
            record.SourceRestorePlan);
        tamperedJson = tamperedJson.Replace(
            $"\"MoveRequiredCount\":{record.SourceRestorePlan.MoveRequiredCount}",
            "\"MoveRequiredCount\":999");

        using var c = OpenRawConnection();
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

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

    [Fact]
    public async Task Validate_AttemptStableIdMismatch_RejectedOnGet()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeCompletedRecord();
        await store.SaveAsync(record);

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            UPDATE desktop_layout_recovery_attempts
            SET expected_stable_id = 'WRONG_ID'
            WHERE session_id = @sid
        ";
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync(record.SessionId));
    }

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

    [Fact]
    public void CompletedIndexes_MatchAttempts()
    {
        var record = MakeCompletedRecord();
        Assert.Single(record.CompletedForwardStepIndexes);
        Assert.Equal(0, record.CompletedForwardStepIndexes[0]);
        Assert.Empty(record.CompletedRollbackSourceStepIndexes);
    }

    [Fact]
    public async Task Save_RevisionNotOne_Throws()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var record = MakeReadyRecord() with { Revision = 2 };
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(record));
        Assert.Contains("Revision", ex.Message);
    }

    [Fact]
    public async Task SaveExecuting_CompletedIndexes_RoundTrip()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
            CompletedForwardStepIndexes = new[] { 0 },
        };
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.CompletedForwardStepIndexes);
        Assert.Equal(0, loaded.CompletedForwardStepIndexes[0]);
        Assert.Empty(loaded.CompletedRollbackSourceStepIndexes);
    }

    [Fact]
    public async Task Record_DefensiveFreeze_BeforeAndAfterSave_InputMutationIgnored()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var mutableAttempts = new List<TargetDisplayLayoutExecutionAttemptResult>
        {
            MakeExactAttempt(0, 0),
        };
        var mutableCompleted = new List<int> { 0 };

        var record = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = mutableAttempts,
            CompletedForwardStepIndexes = mutableCompleted,
        };

        mutableAttempts.Add(MakeExactAttempt(1, 1));
        mutableCompleted.Add(99);

        Assert.Single(record.ForwardAttempts);
        Assert.Single(record.CompletedForwardStepIndexes);
        Assert.Equal(0, record.CompletedForwardStepIndexes[0]);

        await store.SaveAsync(record);

        mutableAttempts.Add(MakeExactAttempt(2, 2));
        mutableCompleted.Add(100);

        var loaded = await store.GetAsync(record.SessionId);
        Assert.Single(loaded!.ForwardAttempts);
        Assert.Single(loaded.CompletedForwardStepIndexes);
        Assert.Equal(0, loaded.CompletedForwardStepIndexes[0]);
    }

    [Fact]
    public async Task Update_ImmutableFieldChanged_Throws()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);
        var tampered = loaded! with { SafetySnapshotId = Guid.NewGuid() };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(tampered));
        Assert.Contains("SafetySnapshotId", ex.Message);
    }

    [Fact]
    public async Task Update_ReturnedRecord_MatchesFreshGet()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);
        var updated = loaded! with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
            CompletedForwardStepIndexes = new[] { 0 },
        };

        var result = await store.UpdateAsync(updated);
        var freshGet = await store.GetAsync(record.SessionId);

        Assert.Equal(freshGet!.SessionId, result.SessionId);
        Assert.Equal(freshGet.SafetySnapshotId, result.SafetySnapshotId);
        Assert.Equal(freshGet.TargetDeviceName, result.TargetDeviceName);
        Assert.Equal(freshGet.StartedAtUtc, result.StartedAtUtc);
        Assert.Equal(freshGet.Lifecycle, result.Lifecycle);
        Assert.Equal(freshGet.Revision, result.Revision);
        Assert.Equal(freshGet.UpdatedAtUtc, result.UpdatedAtUtc);
        Assert.Equal(freshGet.ForwardAttempts.Count, result.ForwardAttempts.Count);
        Assert.Equal(freshGet.CompletedForwardStepIndexes.Count, result.CompletedForwardStepIndexes.Count);
    }

    [Fact]
    public async Task Save_ChildInsertFails_NoPartialParent()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        using var c = OpenRawConnection();
        using var setup = c.CreateCommand();
        setup.CommandText = @"
            CREATE TRIGGER IF NOT EXISTS trg_fail_attempts
            BEFORE INSERT ON desktop_layout_recovery_attempts
            BEGIN
                SELECT RAISE(ABORT, 'trigger-forced rollback');
            END;
        ";
        setup.ExecuteNonQuery();

        var record = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
            CompletedForwardStepIndexes = new[] { 0 },
        };

        await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync(record));

        var loaded = await store.GetAsync(record.SessionId);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Update_DeleteOldAttempts_InsertFails_OldStateRestored()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = new[] { MakeExactAttempt(0, 0) },
            CompletedForwardStepIndexes = new[] { 0 },
        };
        await store.SaveAsync(record);

        var loaded = await store.GetAsync(record.SessionId);

        using var c = OpenRawConnection();
        using var setup = c.CreateCommand();
        setup.CommandText = @"
            CREATE TRIGGER IF NOT EXISTS trg_fail_attempts
            BEFORE INSERT ON desktop_layout_recovery_attempts
            BEGIN
                SELECT RAISE(ABORT, 'trigger-forced rollback');
            END;
        ";
        setup.ExecuteNonQuery();

        var updated = loaded! with
        {
            Lifecycle = RecoverySessionLifecycle.Completed,
            VerificationResult = TargetDisplayLayoutExecutionVerificationResult.ForwardPassed(
                DateTimeOffset.UtcNow),
        };

        await Assert.ThrowsAsync<SqliteException>(() => store.UpdateAsync(updated));

        using var cleanup = c.CreateCommand();
        cleanup.CommandText = "DROP TRIGGER IF EXISTS trg_fail_attempts;";
        cleanup.ExecuteNonQuery();

        var restored = await store.GetAsync(record.SessionId);
        Assert.Equal(1, restored!.Revision);
        Assert.Equal(RecoverySessionLifecycle.Executing, restored.Lifecycle);
        Assert.Single(restored.ForwardAttempts);
        Assert.Single(restored.CompletedForwardStepIndexes);
    }

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
    public void Validate_PositionUncertainWithoutFailureType_Throws()
    {
        var record = MakeReadyRecord() with
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
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
            CompletedForwardStepIndexes = new[] { 0 },
            VerificationResult = new TargetDisplayLayoutExecutionVerificationResult(
                ExecutionVerificationPhase.Forward,
                ExecutionVerificationStatus.Passed, 5,
                Array.Empty<string>(), DateTimeOffset.UtcNow),
        };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("FailedItemCount", ex.Message);
    }

    [Fact]
    public async Task PlanJson_ExecutableNoOp_EmptySteps_IsValid()
    {
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

    [Fact]
    public void Validate_RollbackExpectedPreviousMismatch_Throws()
    {
        var plan = MakeExecutionPlan();
        var badAttempt = new TargetDisplayLayoutExecutionAttemptResult(
            0, 0, TargetDisplayLayoutExecutionAttemptPhase.Rollback,
            "a", new PixelPoint(999, 999), new PixelPoint(160, 240),
            "a", new PixelPoint(999, 999), new PixelPoint(160, 240),
            true, true, TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess,
            ConnectionMethod.FindWindowSW, null, null, null, null);

        var record = MakeReadyRecord() with
        {
            ExecutionPlan = plan,
            RollbackAttempts = new[] { badAttempt },
            CompletedRollbackSourceStepIndexes = new[] { 0 },
        };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("ExpectedPrevious", ex.Message);
    }

    [Fact]
    public void Validate_ExactSuccessReportedStableIdMismatch_Throws()
    {
        var plan = MakeExecutionPlan();
        var badAttempt = new TargetDisplayLayoutExecutionAttemptResult(
            0, 0, TargetDisplayLayoutExecutionAttemptPhase.Forward,
            "a", new PixelPoint(160, 240), new PixelPoint(240, 320),
            "WRONG_ID", new PixelPoint(160, 240), new PixelPoint(240, 320),
            true, true, TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess,
            ConnectionMethod.FindWindowSW, null, null, null, null);

        var record = MakeReadyRecord() with
        {
            ExecutionPlan = plan,
            ForwardAttempts = new[] { badAttempt },
            CompletedForwardStepIndexes = new[] { 0 },
            Lifecycle = RecoverySessionLifecycle.Completed,
            VerificationResult = TargetDisplayLayoutExecutionVerificationResult.ForwardPassed(
                DateTimeOffset.UtcNow),
        };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("ReportedStableId", ex.Message);
    }

    [Fact]
    public void Validate_WriterExceptionWithWriterResultFields_Throws()
    {
        var plan = MakeExecutionPlan();
        var badAttempt = new TargetDisplayLayoutExecutionAttemptResult(
            0, 0, TargetDisplayLayoutExecutionAttemptPhase.Forward,
            "a", new PixelPoint(160, 240), new PixelPoint(240, 320),
            null, null, null,
            false, false, TargetDisplayLayoutExecutionAttemptOutcome.WriterException,
            ConnectionMethod.FindWindowSW,
            "SomeException", "BOOM", null, null);

        var record = MakeReadyRecord() with
        {
            ExecutionPlan = plan,
            ForwardAttempts = new[] { badAttempt },
            CompletedForwardStepIndexes = Array.Empty<int>(),
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            ForwardFailureType = "SomeException",
        };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("ConnectionMethod", ex.Message);
    }

    [Fact]
    public void Validate_UnreferencedStagingStepIndex_Throws()
    {
        var steps = new[]
        {
            new TargetDisplayLayoutExecutionStep(0, "b", "N_b",
                new PixelPoint(100, 100), new PixelPoint(200, 200),
                TargetDisplayLayoutExecutionStepKind.MoveToTarget, TD),
            new TargetDisplayLayoutExecutionStep(1, "a", "N_a",
                new PixelPoint(300, 300), new PixelPoint(400, 400),
                TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, TD, 1),
            new TargetDisplayLayoutExecutionStep(2, "a", "N_a",
                new PixelPoint(400, 400), new PixelPoint(600, 600),
                TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, TD, 1),
            new TargetDisplayLayoutExecutionStep(3, "a", "N_a",
                new PixelPoint(700, 700), new PixelPoint(800, 800),
                TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, TD, 2),
        };
        var stagingAssignments = new[]
        {
            new TargetDisplayLayoutStagingAssignment("a",
                new PixelPoint(400, 400), 1, 2),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            steps, stagingAssignments,
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var record = MakeReadyRecord() with { ExecutionPlan = plan };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("StepIndex", ex.Message);
        Assert.Contains("EnterStepIndex", ex.Message);
    }

    [Fact]
    public void Validate_DuplicateStagingEnterStepIndex_Throws()
    {
        var steps = new[]
        {
            new TargetDisplayLayoutExecutionStep(0, "a", "N_a",
                new PixelPoint(100, 100), new PixelPoint(200, 200),
                TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, TD, 1),
            new TargetDisplayLayoutExecutionStep(1, "a", "N_a",
                new PixelPoint(200, 200), new PixelPoint(300, 300),
                TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, TD, 1),
            new TargetDisplayLayoutExecutionStep(2, "a", "N_a",
                new PixelPoint(100, 100), new PixelPoint(200, 200),
                TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, TD, 2),
            new TargetDisplayLayoutExecutionStep(3, "a", "N_a",
                new PixelPoint(200, 200), new PixelPoint(300, 300),
                TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, TD, 2),
        };
        var stagingAssignments = new[]
        {
            new TargetDisplayLayoutStagingAssignment("a",
                new PixelPoint(200, 200), 0, 1),
            new TargetDisplayLayoutStagingAssignment("a",
                new PixelPoint(200, 200), 0, 3),
        };
        var plan = new TargetDisplayLayoutExecutionPlan(TD, true, false,
            steps, stagingAssignments,
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var record = MakeReadyRecord() with { ExecutionPlan = plan };
        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());
        Assert.Contains("EnterStepIndex", ex.Message);
    }

    [Fact]
    public async Task ListUnresolved_InvalidRawSessionId_ReportsCorruption()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var record = MakeReadyRecord();
        await store.SaveAsync(record);

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE desktop_layout_recovery_sessions SET session_id = @bad WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@bad", "not-a-guid");
        cmd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        cmd.ExecuteNonQuery();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ListUnresolvedAsync());
        Assert.Contains("not-a-guid", ex.Message);
    }

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

    private bool ColumnExists(string tableName, string columnName)
    {
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
