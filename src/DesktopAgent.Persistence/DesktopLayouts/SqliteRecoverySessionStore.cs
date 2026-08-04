using System.Globalization;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.DesktopLayouts;

public sealed class SqliteRecoverySessionStore : IRecoverySessionStore
{
    private readonly string _databasePath;

    public SqliteRecoverySessionStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径不能为 null、空或纯空白。", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
    }

    private SqliteConnection CreateConnection() =>
        SqliteDesktopAgentConnectionFactory.Open(_databasePath);

    // ═══════════════════════════════════════════════════
    //  InitializeAsync
    // ═══════════════════════════════════════════════════

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dir = Path.GetDirectoryName(_databasePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var connection = CreateConnection();
        SqliteDesktopAgentSchema.Initialize(connection, cancellationToken);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    //  SaveAsync
    // ═══════════════════════════════════════════════════

    private const int SqliteConstraintPrimaryKey = 1555;

    public async Task SaveAsync(RecoverySessionRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);

        // 防御性验证
        record.Validate();

        using var connection = CreateConnection();
        using var tx = connection.BeginTransaction();

        try
        {
            // 插入父记录（主键冲突 → 重复 SessionId）
            try
            {
                InsertSessionRow(connection, record);
            }
            catch (SqliteException ex)
                when (ex.SqliteExtendedErrorCode == SqliteConstraintPrimaryKey)
            {
                throw new InvalidOperationException(
                    $"恢复会话 {record.SessionId:D} 已存在，不能重复保存。", ex);
            }

            // 插入子记录
            InsertAttempts(connection, record.SessionId, record.ForwardAttempts);
            InsertAttempts(connection, record.SessionId, record.RollbackAttempts);

            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
        }
        catch
        {
            // 事务自动回滚，不留下部分数据
            throw;
        }

        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    //  UpdateAsync（乐观并发：Revision 匹配）
    // ═══════════════════════════════════════════════════

    public async Task<RecoverySessionRecord> UpdateAsync(
        RecoverySessionRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);

        record.Validate();

        using var connection = CreateConnection();
        using var tx = connection.BeginTransaction();

        // 读取当前 Revision
        var currentRevision = ReadRevision(connection, record.SessionId);
        if (currentRevision is null)
            throw new InvalidOperationException(
                $"恢复会话 {record.SessionId:D} 不存在，无法更新。");

        if (currentRevision.Value != record.Revision)
            throw new InvalidOperationException(
                $"恢复会话 {record.SessionId:D} Revision 冲突：期望 {record.Revision}，实际 {currentRevision.Value}。");

        // 更新父记录（递增 Revision）
        var updated = record.WithNextRevision();
        UpdateSessionRow(connection, updated);

        // 原子替换子记录：删除旧 Attempts → 插入新 Attempts
        DeleteAttempts(connection, record.SessionId);
        InsertAttempts(connection, updated.SessionId, updated.ForwardAttempts);
        InsertAttempts(connection, updated.SessionId, updated.RollbackAttempts);

        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();

        await Task.CompletedTask;
        return updated;
    }

    // ═══════════════════════════════════════════════════
    //  GetAsync
    // ═══════════════════════════════════════════════════

    public Task<RecoverySessionRecord?> GetAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty)
            throw new ArgumentException("SessionId 不能为 Guid.Empty。");

        using var connection = CreateConnection();
        return Task.FromResult(GetInternal(connection, sessionId));
    }

    private static RecoverySessionRecord? GetInternal(
        SqliteConnection connection, Guid sessionId)
    {
        // 读取父记录
        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT session_id, safety_snapshot_id, target_device_name,
                   started_at_utc, lifecycle,
                   requires_manual_recovery, is_position_uncertain,
                   last_failed_step_index,
                   source_restore_plan_json, execution_plan_json,
                   plan_payload_format_version,
                   forward_failure_type, forward_failure_message,
                   recovery_failure_type, recovery_failure_message,
                   verification_result_json,
                   revision, updated_at_utc,
                   forward_attempt_count, rollback_attempt_count
            FROM desktop_layout_recovery_sessions
            WHERE session_id = @id
        ";
        sel.Parameters.AddWithValue("@id", sessionId.ToString("D"));

        using var reader = sel.ExecuteReader();
        if (!reader.Read()) return null;

        // 读取并验证所有字段
        var readSessionId = Guid.Parse(reader.GetString(0));
        var safetySnapshotId = Guid.Parse(reader.GetString(1));
        var targetDeviceName = reader.GetString(2);
        var startedAtUtc = DateTimeOffset.Parse(reader.GetString(3),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var lifecycleVal = reader.GetInt32(4);
        if (!Enum.IsDefined((RecoverySessionLifecycle)lifecycleVal))
            throw new InvalidDataException(
                $"会话 {sessionId:D} 的 Lifecycle 值无效：{lifecycleVal}。");
        var lifecycle = (RecoverySessionLifecycle)lifecycleVal;

        var requiresManualRaw = reader.GetInt32(5);
        if (requiresManualRaw != 0 && requiresManualRaw != 1)
            throw new InvalidDataException(
                $"会话 {sessionId:D} 的 requires_manual_recovery 值无效：{requiresManualRaw}。");
        var requiresManualRecovery = requiresManualRaw == 1;

        var isPosUncertainRaw = reader.GetInt32(6);
        if (isPosUncertainRaw != 0 && isPosUncertainRaw != 1)
            throw new InvalidDataException(
                $"会话 {sessionId:D} 的 is_position_uncertain 值无效：{isPosUncertainRaw}。");
        var isPositionUncertain = isPosUncertainRaw == 1;

        var lastFailedStepIndex = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);

        var sourceRestorePlanJson = reader.GetString(8);
        var executionPlanJson = reader.GetString(9);
        var planPayloadFormatVersion = reader.GetInt32(10);

        if (planPayloadFormatVersion != RecoveryPlanJsonSerializer.CurrentPlanPayloadFormatVersion)
            throw new InvalidDataException(
                $"会话 {sessionId:D} 的 plan_payload_format_version={planPayloadFormatVersion}，" +
                $"当前支持版本={RecoveryPlanJsonSerializer.CurrentPlanPayloadFormatVersion}。");

        var forwardFailureType = reader.IsDBNull(11) ? null : reader.GetString(11);
        var forwardFailureMessage = reader.IsDBNull(12) ? null : reader.GetString(12);
        var recoveryFailureType = reader.IsDBNull(13) ? null : reader.GetString(13);
        var recoveryFailureMessage = reader.IsDBNull(14) ? null : reader.GetString(14);
        var verificationResultJson = reader.IsDBNull(15) ? null : reader.GetString(15);
        var revision = reader.GetInt32(16);
        var updatedAtUtc = DateTimeOffset.Parse(reader.GetString(17),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var declaredFwdCount = reader.GetInt32(18);
        var declaredRbCount = reader.GetInt32(19);

        // 反序列化 plans
        TargetDisplayLayoutRestorePlan sourceRestorePlan;
        TargetDisplayLayoutExecutionPlan executionPlan;
        try
        {
            sourceRestorePlan = RecoveryPlanJsonSerializer.DeserializeRestorePlan(sourceRestorePlanJson);
            executionPlan = RecoveryPlanJsonSerializer.DeserializeExecutionPlan(executionPlanJson);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"会话 {sessionId:D} Plan payload 反序列化失败：{ex.Message}", ex);
        }

        // 反序列化 VerificationResult
        TargetDisplayLayoutExecutionVerificationResult? verificationResult = null;
        try
        {
            verificationResult = RecoveryPlanJsonSerializer.DeserializeVerificationResult(
                verificationResultJson);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"会话 {sessionId:D} VerificationResult JSON 反序列化失败：{ex.Message}", ex);
        }

        // 读取 Attempts
        var attempts = ReadAttempts(connection, sessionId);
        var forwardAttempts = attempts
            .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward)
            .ToList().AsReadOnly();
        var rollbackAttempts = attempts
            .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback)
            .ToList().AsReadOnly();

        // 一致性验证：数量匹配
        if (forwardAttempts.Count != declaredFwdCount)
            throw new InvalidDataException(
                $"会话 {sessionId:D} ForwardAttempt 数量不一致：父表 {declaredFwdCount}，实际读取 {forwardAttempts.Count}。");
        if (rollbackAttempts.Count != declaredRbCount)
            throw new InvalidDataException(
                $"会话 {sessionId:D} RollbackAttempt 数量不一致：父表 {declaredRbCount}，实际读取 {rollbackAttempts.Count}。");

        // 构造并验证聚合
        var record = new RecoverySessionRecord
        {
            SessionId = readSessionId,
            SafetySnapshotId = safetySnapshotId,
            TargetDeviceName = targetDeviceName,
            StartedAtUtc = startedAtUtc,
            Lifecycle = lifecycle,
            RequiresManualRecovery = requiresManualRecovery,
            IsPositionUncertain = isPositionUncertain,
            LastFailedStepIndex = lastFailedStepIndex,
            SourceRestorePlan = sourceRestorePlan,
            ExecutionPlan = executionPlan,
            ForwardAttempts = forwardAttempts,
            RollbackAttempts = rollbackAttempts,
            ForwardFailureType = forwardFailureType,
            ForwardFailureMessage = forwardFailureMessage,
            RecoveryFailureType = recoveryFailureType,
            RecoveryFailureMessage = recoveryFailureMessage,
            VerificationResult = verificationResult,
            Revision = revision,
            UpdatedAtUtc = updatedAtUtc,
        };

        // 跨模型一致性验证
        record.Validate();

        return record;
    }

    // ═══════════════════════════════════════════════════
    //  ListUnresolvedAsync
    // ═══════════════════════════════════════════════════

    public async Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
        int limit = 50, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "limit 必须大于 0。");

        using var connection = CreateConnection();

        // 查询未解决会话的 SessionId
        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT session_id
            FROM desktop_layout_recovery_sessions
            WHERE lifecycle IN (1, 4, 5)  -- Executing, FailedRollbackIncomplete, PositionUncertain
               OR requires_manual_recovery = 1
            ORDER BY started_at_utc DESC, session_id DESC
            LIMIT @limit
        ";
        sel.Parameters.AddWithValue("@limit", limit);

        var sessionIds = new List<Guid>();
        using (var reader = sel.ExecuteReader())
        {
            while (reader.Read())
                sessionIds.Add(Guid.Parse(reader.GetString(0)));
        }

        // 逐条完整读取
        var results = new List<RecoverySessionRecord>();
        foreach (var sid in sessionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = GetInternal(connection, sid);
            // 损坏记录 fail-closed：任一读取失败则整体失败
            if (record is null)
                throw new InvalidDataException(
                    $"未解决会话 {sid:D} 在查询后读取返回 null（数据竞争或损坏）。");
            results.Add(record);
        }

        return results;
    }

    // ═══════════════════════════════════════════════════
    //  内部 SQL 操作
    // ═══════════════════════════════════════════════════

    private static void InsertSessionRow(
        SqliteConnection connection, RecoverySessionRecord record)
    {
        using var ins = connection.CreateCommand();
        ins.CommandText = @"
            INSERT INTO desktop_layout_recovery_sessions
                (session_id, safety_snapshot_id, target_device_name,
                 started_at_utc, lifecycle,
                 requires_manual_recovery, is_position_uncertain,
                 last_failed_step_index,
                 source_restore_plan_json, execution_plan_json,
                 plan_payload_format_version,
                 forward_failure_type, forward_failure_message,
                 recovery_failure_type, recovery_failure_message,
                 verification_result_json,
                 revision, updated_at_utc,
                 forward_attempt_count, rollback_attempt_count)
            VALUES
                (@sid, @ssid, @tdn,
                 @sat, @lc,
                 @rmr, @ipu,
                 @lfsi,
                 @srpj, @epj,
                 @ppfv,
                 @fft, @ffm,
                 @rft, @rfm,
                 @vrj,
                 @rev, @uat,
                 @fac, @rac);
        ";
        ins.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        ins.Parameters.AddWithValue("@ssid", record.SafetySnapshotId.ToString("D"));
        ins.Parameters.AddWithValue("@tdn", record.TargetDeviceName);
        ins.Parameters.AddWithValue("@sat",
            record.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        ins.Parameters.AddWithValue("@lc", (int)record.Lifecycle);
        ins.Parameters.AddWithValue("@rmr", record.RequiresManualRecovery ? 1 : 0);
        ins.Parameters.AddWithValue("@ipu", record.IsPositionUncertain ? 1 : 0);
        ins.Parameters.AddWithValue("@lfsi", (object?)record.LastFailedStepIndex ?? DBNull.Value);

        var srpJson = RecoveryPlanJsonSerializer.SerializeRestorePlan(record.SourceRestorePlan);
        var epJson = RecoveryPlanJsonSerializer.SerializeExecutionPlan(record.ExecutionPlan);
        ins.Parameters.AddWithValue("@srpj", srpJson);
        ins.Parameters.AddWithValue("@epj", epJson);
        ins.Parameters.AddWithValue("@ppfv", RecoveryPlanJsonSerializer.CurrentPlanPayloadFormatVersion);

        ins.Parameters.AddWithValue("@fft", (object?)record.ForwardFailureType ?? DBNull.Value);
        ins.Parameters.AddWithValue("@ffm", (object?)record.ForwardFailureMessage ?? DBNull.Value);
        ins.Parameters.AddWithValue("@rft", (object?)record.RecoveryFailureType ?? DBNull.Value);
        ins.Parameters.AddWithValue("@rfm", (object?)record.RecoveryFailureMessage ?? DBNull.Value);

        var vrJson = RecoveryPlanJsonSerializer.SerializeVerificationResult(record.VerificationResult);
        ins.Parameters.AddWithValue("@vrj", (object?)vrJson ?? DBNull.Value);

        ins.Parameters.AddWithValue("@rev", record.Revision);
        ins.Parameters.AddWithValue("@uat",
            record.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        ins.Parameters.AddWithValue("@fac", record.ForwardAttempts.Count);
        ins.Parameters.AddWithValue("@rac", record.RollbackAttempts.Count);

        ins.ExecuteNonQuery();
    }

    private static void UpdateSessionRow(
        SqliteConnection connection, RecoverySessionRecord record)
    {
        using var upd = connection.CreateCommand();
        upd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET lifecycle = @lc,
                requires_manual_recovery = @rmr,
                is_position_uncertain = @ipu,
                last_failed_step_index = @lfsi,
                forward_failure_type = @fft,
                forward_failure_message = @ffm,
                recovery_failure_type = @rft,
                recovery_failure_message = @rfm,
                verification_result_json = @vrj,
                revision = @rev,
                updated_at_utc = @uat,
                forward_attempt_count = @fac,
                rollback_attempt_count = @rac
            WHERE session_id = @sid AND revision = @expectedRev;
        ";
        upd.Parameters.AddWithValue("@lc", (int)record.Lifecycle);
        upd.Parameters.AddWithValue("@rmr", record.RequiresManualRecovery ? 1 : 0);
        upd.Parameters.AddWithValue("@ipu", record.IsPositionUncertain ? 1 : 0);
        upd.Parameters.AddWithValue("@lfsi", (object?)record.LastFailedStepIndex ?? DBNull.Value);
        upd.Parameters.AddWithValue("@fft", (object?)record.ForwardFailureType ?? DBNull.Value);
        upd.Parameters.AddWithValue("@ffm", (object?)record.ForwardFailureMessage ?? DBNull.Value);
        upd.Parameters.AddWithValue("@rft", (object?)record.RecoveryFailureType ?? DBNull.Value);
        upd.Parameters.AddWithValue("@rfm", (object?)record.RecoveryFailureMessage ?? DBNull.Value);

        var vrJson = RecoveryPlanJsonSerializer.SerializeVerificationResult(record.VerificationResult);
        upd.Parameters.AddWithValue("@vrj", (object?)vrJson ?? DBNull.Value);

        upd.Parameters.AddWithValue("@rev", record.Revision);
        upd.Parameters.AddWithValue("@uat",
            record.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        upd.Parameters.AddWithValue("@fac", record.ForwardAttempts.Count);
        upd.Parameters.AddWithValue("@rac", record.RollbackAttempts.Count);

        upd.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        upd.Parameters.AddWithValue("@expectedRev", record.Revision - 1);

        var rows = upd.ExecuteNonQuery();
        if (rows == 0)
            throw new InvalidOperationException(
                $"恢复会话 {record.SessionId:D} 更新失败：WHERE 条件不满足（可能被并发更新）。");
    }

    private static int? ReadRevision(SqliteConnection connection, Guid sessionId)
    {
        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT revision
            FROM desktop_layout_recovery_sessions
            WHERE session_id = @id
        ";
        sel.Parameters.AddWithValue("@id", sessionId.ToString("D"));

        using var reader = sel.ExecuteReader();
        if (!reader.Read()) return null;
        return reader.GetInt32(0);
    }

    private static void InsertAttempts(
        SqliteConnection connection,
        Guid sessionId,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> attempts)
    {
        foreach (var a in attempts)
        {
            using var ins = connection.CreateCommand();
            ins.CommandText = @"
                INSERT INTO desktop_layout_recovery_attempts
                    (session_id, attempt_index, source_step_index, phase,
                     expected_stable_id,
                     expected_previous_x, expected_previous_y,
                     requested_x, requested_y,
                     reported_stable_id,
                     reported_previous_x, reported_previous_y,
                     actual_x, actual_y,
                     has_trusted_actual_position, is_exact,
                     outcome, connection_method,
                     exception_type, error_message,
                     primary_path_exception_type, primary_path_exception_msg)
                VALUES
                    (@sid, @ai, @ssi, @ph,
                     @esid,
                     @epx, @epy,
                     @rx, @ry,
                     @rsid,
                     @rpx, @rpy,
                     @ax, @ay,
                     @htap, @ie,
                     @out, @cm,
                     @et, @em,
                     @ppet, @ppem);
            ";
            ins.Parameters.AddWithValue("@sid", sessionId.ToString("D"));
            ins.Parameters.AddWithValue("@ai", a.AttemptIndex);
            ins.Parameters.AddWithValue("@ssi", a.SourceStepIndex);
            ins.Parameters.AddWithValue("@ph", (int)a.Phase);
            ins.Parameters.AddWithValue("@esid", a.ExpectedStableId);
            ins.Parameters.AddWithValue("@epx", (object?)a.ExpectedPreviousPosition?.X ?? DBNull.Value);
            ins.Parameters.AddWithValue("@epy", (object?)a.ExpectedPreviousPosition?.Y ?? DBNull.Value);
            ins.Parameters.AddWithValue("@rx", a.RequestedPosition.X);
            ins.Parameters.AddWithValue("@ry", a.RequestedPosition.Y);
            ins.Parameters.AddWithValue("@rsid", (object?)a.ReportedStableId ?? DBNull.Value);
            ins.Parameters.AddWithValue("@rpx", (object?)a.ReportedPreviousPosition?.X ?? DBNull.Value);
            ins.Parameters.AddWithValue("@rpy", (object?)a.ReportedPreviousPosition?.Y ?? DBNull.Value);
            ins.Parameters.AddWithValue("@ax", (object?)a.ActualPosition?.X ?? DBNull.Value);
            ins.Parameters.AddWithValue("@ay", (object?)a.ActualPosition?.Y ?? DBNull.Value);
            ins.Parameters.AddWithValue("@htap", a.HasTrustedActualPosition ? 1 : 0);
            ins.Parameters.AddWithValue("@ie", a.IsExact ? 1 : 0);
            ins.Parameters.AddWithValue("@out", (int)a.Outcome);
            ins.Parameters.AddWithValue("@cm", (object?)(int?)a.ConnectionMethod ?? DBNull.Value);
            ins.Parameters.AddWithValue("@et", (object?)a.ExceptionType ?? DBNull.Value);
            ins.Parameters.AddWithValue("@em", (object?)a.ErrorMessage ?? DBNull.Value);
            ins.Parameters.AddWithValue("@ppet", (object?)a.PrimaryPathExceptionType ?? DBNull.Value);
            ins.Parameters.AddWithValue("@ppem", (object?)a.PrimaryPathExceptionMessage ?? DBNull.Value);

            ins.ExecuteNonQuery();
        }
    }

    private static List<TargetDisplayLayoutExecutionAttemptResult> ReadAttempts(
        SqliteConnection connection, Guid sessionId)
    {
        var attempts = new List<TargetDisplayLayoutExecutionAttemptResult>();

        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT attempt_index, source_step_index, phase,
                   expected_stable_id,
                   expected_previous_x, expected_previous_y,
                   requested_x, requested_y,
                   reported_stable_id,
                   reported_previous_x, reported_previous_y,
                   actual_x, actual_y,
                   has_trusted_actual_position, is_exact,
                   outcome, connection_method,
                   exception_type, error_message,
                   primary_path_exception_type, primary_path_exception_msg
            FROM desktop_layout_recovery_attempts
            WHERE session_id = @id
            ORDER BY attempt_index ASC
        ";
        sel.Parameters.AddWithValue("@id", sessionId.ToString("D"));

        using var reader = sel.ExecuteReader();
        while (reader.Read())
        {
            var attemptIndex = reader.GetInt32(0);
            var sourceStepIndex = reader.GetInt32(1);

            var phaseVal = reader.GetInt32(2);
            if (!Enum.IsDefined((TargetDisplayLayoutExecutionAttemptPhase)phaseVal))
                throw new InvalidDataException(
                    $"会话 {sessionId:D} Attempt #{attemptIndex} Phase 值无效：{phaseVal}。");
            var phase = (TargetDisplayLayoutExecutionAttemptPhase)phaseVal;

            var expectedStableId = reader.GetString(3);

            PixelPoint? expectedPrevious = null;
            if (!reader.IsDBNull(4))
                expectedPrevious = new PixelPoint(reader.GetInt32(4), reader.GetInt32(5));

            var requestedX = reader.GetInt32(6);
            var requestedY = reader.GetInt32(7);

            string? reportedStableId = reader.IsDBNull(8) ? null : reader.GetString(8);

            PixelPoint? reportedPrevious = null;
            if (!reader.IsDBNull(9))
                reportedPrevious = new PixelPoint(reader.GetInt32(9), reader.GetInt32(10));

            PixelPoint? actualPosition = null;
            if (!reader.IsDBNull(11))
                actualPosition = new PixelPoint(reader.GetInt32(11), reader.GetInt32(12));

            var htapRaw = reader.GetInt32(13);
            if (htapRaw != 0 && htapRaw != 1)
                throw new InvalidDataException(
                    $"会话 {sessionId:D} Attempt #{attemptIndex} has_trusted_actual_position 值无效：{htapRaw}。");
            var hasTrusted = htapRaw == 1;

            var ieRaw = reader.GetInt32(14);
            if (ieRaw != 0 && ieRaw != 1)
                throw new InvalidDataException(
                    $"会话 {sessionId:D} Attempt #{attemptIndex} is_exact 值无效：{ieRaw}。");
            var isExact = ieRaw == 1;

            var outcomeVal = reader.GetInt32(15);
            if (!Enum.IsDefined((TargetDisplayLayoutExecutionAttemptOutcome)outcomeVal))
                throw new InvalidDataException(
                    $"会话 {sessionId:D} Attempt #{attemptIndex} Outcome 值无效：{outcomeVal}。");
            var outcome = (TargetDisplayLayoutExecutionAttemptOutcome)outcomeVal;

            ConnectionMethod? connectionMethod = null;
            if (!reader.IsDBNull(16))
            {
                var cmVal = reader.GetInt32(16);
                if (!Enum.IsDefined((ConnectionMethod)cmVal))
                    throw new InvalidDataException(
                        $"会话 {sessionId:D} Attempt #{attemptIndex} ConnectionMethod 值无效：{cmVal}。");
                connectionMethod = (ConnectionMethod)cmVal;
            }

            var exceptionType = reader.IsDBNull(17) ? null : reader.GetString(17);
            var errorMessage = reader.IsDBNull(18) ? null : reader.GetString(18);
            var ppExceptionType = reader.IsDBNull(19) ? null : reader.GetString(19);
            var ppExceptionMessage = reader.IsDBNull(20) ? null : reader.GetString(20);

            attempts.Add(new TargetDisplayLayoutExecutionAttemptResult(
                attemptIndex, sourceStepIndex, phase,
                expectedStableId, expectedPrevious,
                new PixelPoint(requestedX, requestedY),
                reportedStableId, reportedPrevious, actualPosition,
                hasTrusted, isExact, outcome,
                connectionMethod,
                exceptionType, errorMessage,
                ppExceptionType, ppExceptionMessage));
        }

        return attempts;
    }

    private static void DeleteAttempts(SqliteConnection connection, Guid sessionId)
    {
        using var del = connection.CreateCommand();
        del.CommandText = @"
            DELETE FROM desktop_layout_recovery_attempts
            WHERE session_id = @id
        ";
        del.Parameters.AddWithValue("@id", sessionId.ToString("D"));
        del.ExecuteNonQuery();
    }
}
