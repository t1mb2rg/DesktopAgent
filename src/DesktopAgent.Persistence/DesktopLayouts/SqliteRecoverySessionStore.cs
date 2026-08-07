using System.Globalization;
using System.Text.Json;
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

        // 冻结 + Revision 强制为 1
        // Record 已在 init 赋值时自动冻结
        if (record.Revision != 1)
            throw new ArgumentException(
                $"SaveAsync 要求 Revision=1，实际为 {record.Revision}。", nameof(record));

        record.Validate();

        using var connection = CreateConnection();
        using var tx = connection.BeginTransaction();

        try
        {
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

            InsertAttempts(connection, record.SessionId, record.ForwardAttempts);
            InsertAttempts(connection, record.SessionId, record.RollbackAttempts);

            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
        }
        catch
        {
            throw;
        }

        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    //  UpdateAsync（CAS：Revision N → N+1，不可变字段冻结）
    // ═══════════════════════════════════════════════════

    public async Task<RecoverySessionRecord> UpdateAsync(
        RecoverySessionRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);

        // Record 已在 init 赋值时自动冻结
        record.Validate();

        using var connection = CreateConnection();
        using var tx = connection.BeginTransaction();

        // 读取 DB 当前记录以对比不可变字段
        var dbRecord = GetInternal(connection, record.SessionId);
        if (dbRecord is null)
            throw new InvalidOperationException(
                $"恢复会话 {record.SessionId:D} 不存在，无法更新。");

        // Revision 必须匹配（CAS）
        if (dbRecord.Revision != record.Revision)
            throw new InvalidOperationException(
                $"恢复会话 {record.SessionId:D} Revision 冲突：期望 {record.Revision}，实际 {dbRecord.Revision}。");

        // ── 不可变字段比较 ──
        if (record.SessionId != dbRecord.SessionId)
            throw new InvalidOperationException("SessionId 不可变更。");
        if (record.SafetySnapshotId != dbRecord.SafetySnapshotId)
            throw new InvalidOperationException("SafetySnapshotId 不可变更。");
        if (!string.Equals(record.TargetDeviceName, dbRecord.TargetDeviceName, StringComparison.Ordinal))
            throw new InvalidOperationException("TargetDeviceName 不可变更。");
        if (record.StartedAtUtc != dbRecord.StartedAtUtc)
            throw new InvalidOperationException("StartedAtUtc 不可变更。");
        if (!PlansAreEquivalent(record.SourceRestorePlan, dbRecord.SourceRestorePlan))
            throw new InvalidOperationException("SourceRestorePlan 不可变更。");
        if (!PlansAreEquivalent(record.ExecutionPlan, dbRecord.ExecutionPlan))
            throw new InvalidOperationException("ExecutionPlan 不可变更。");

        // CAS 更新到 Revision + 1
        var updated = record with
        {
            Revision = record.Revision + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        UpdateSessionRow(connection, updated);

        // 原子替换 Attempts
        DeleteAttempts(connection, record.SessionId);
        InsertAttempts(connection, updated.SessionId, updated.ForwardAttempts);
        InsertAttempts(connection, updated.SessionId, updated.RollbackAttempts);

        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();

        await Task.CompletedTask;

        // 从 DB 重读完整记录，确保返回对象与 fresh Get 一致
        return (await Task.FromResult(GetInternal(connection, record.SessionId)))!;
    }

    // ═══════════════════════════════════════════════════
    //  Plan 深度等价比较（覆盖全部嵌套集合和顺序）
    // ═══════════════════════════════════════════════════

    private static bool PlansAreEquivalent(
        TargetDisplayLayoutRestorePlan a, TargetDisplayLayoutRestorePlan b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        return string.Equals(a.TargetDeviceName, b.TargetDeviceName, StringComparison.Ordinal)
            && a.SnapshotTargetIconCount == b.SnapshotTargetIconCount
            && a.AlreadyAtTargetCount == b.AlreadyAtTargetCount
            && a.MoveRequiredCount == b.MoveRequiredCount
            && a.MissingCount == b.MissingCount
            && a.BlockedCount == b.BlockedCount
            && a.HasBlockingErrors == b.HasBlockingErrors
            && a.RequiresTemporaryStaging == b.RequiresTemporaryStaging
            && a.IsNoOp == b.IsNoOp
            && ItemsAreEquivalent(a.Items, b.Items)
            && DependenciesAreEquivalent(a.Dependencies, b.Dependencies)
            && CyclesAreEquivalent(a.Cycles, b.Cycles)
            && IssuesAreEquivalent(a.Issues, b.Issues)
            && MoveExecutionOrderAreEquivalent(a.MoveExecutionOrder, b.MoveExecutionOrder);
    }

    private static bool ItemsAreEquivalent(
        IReadOnlyList<TargetDisplayLayoutRestorePlanItem> a,
        IReadOnlyList<TargetDisplayLayoutRestorePlanItem> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].StableId, b[i].StableId, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].DisplayName, b[i].DisplayName)) return false;
            if (a[i].SnapshotTargetPosition != b[i].SnapshotTargetPosition) return false;
            if (a[i].CurrentPosition != b[i].CurrentPosition) return false;
            if (!string.Equals(a[i].CurrentMonitorDeviceName, b[i].CurrentMonitorDeviceName, StringComparison.Ordinal)) return false;
            if (a[i].State != b[i].State) return false;
            if (a[i].IsFileSystemItem != b[i].IsFileSystemItem) return false;
            if (a[i].IsSystemItem != b[i].IsSystemItem) return false;
            if (!string.Equals(a[i].BlockReason, b[i].BlockReason, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool DependenciesAreEquivalent(
        IReadOnlyList<TargetDisplayLayoutRestoreDependency> a,
        IReadOnlyList<TargetDisplayLayoutRestoreDependency> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].DependsOn, b[i].DependsOn, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].ActionItem, b[i].ActionItem, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool CyclesAreEquivalent(
        IReadOnlyList<TargetDisplayLayoutRestoreCycle> a,
        IReadOnlyList<TargetDisplayLayoutRestoreCycle> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].StableIds.SequenceEqual(b[i].StableIds, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private static bool IssuesAreEquivalent(
        IReadOnlyList<TargetDisplayLayoutRestorePlanIssue> a,
        IReadOnlyList<TargetDisplayLayoutRestorePlanIssue> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Code != b[i].Code) return false;
            if (!string.Equals(a[i].Message, b[i].Message, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].RelatedStableId, b[i].RelatedStableId, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool MoveExecutionOrderAreEquivalent(
        IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static bool PlansAreEquivalent(
        TargetDisplayLayoutExecutionPlan a, TargetDisplayLayoutExecutionPlan b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        return string.Equals(a.TargetDeviceName, b.TargetDeviceName, StringComparison.Ordinal)
            && a.IsExecutable == b.IsExecutable
            && a.IsNoOp == b.IsNoOp
            && StepsAreEquivalent(a.Steps, b.Steps)
            && StagingAssignmentsAreEquivalent(a.StagingAssignments, b.StagingAssignments)
            && ExecutionPlanIssuesAreEquivalent(a.Issues, b.Issues);
    }

    private static bool StepsAreEquivalent(
        IReadOnlyList<TargetDisplayLayoutExecutionStep> a,
        IReadOnlyList<TargetDisplayLayoutExecutionStep> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].StepIndex != b[i].StepIndex) return false;
            if (!string.Equals(a[i].StableId, b[i].StableId, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].DisplayName, b[i].DisplayName)) return false;
            if (a[i].FromPosition != b[i].FromPosition) return false;
            if (a[i].ToPosition != b[i].ToPosition) return false;
            if (a[i].Kind != b[i].Kind) return false;
            if (!string.Equals(a[i].ExpectedMonitorDeviceName, b[i].ExpectedMonitorDeviceName, StringComparison.Ordinal)) return false;
            if (a[i].StagingSequence != b[i].StagingSequence) return false;
        }
        return true;
    }

    private static bool StagingAssignmentsAreEquivalent(
        IReadOnlyList<TargetDisplayLayoutStagingAssignment> a,
        IReadOnlyList<TargetDisplayLayoutStagingAssignment> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].StableId, b[i].StableId, StringComparison.Ordinal)) return false;
            if (a[i].StagingPosition != b[i].StagingPosition) return false;
            if (a[i].EnterStepIndex != b[i].EnterStepIndex) return false;
            if (a[i].ExitStepIndex != b[i].ExitStepIndex) return false;
        }
        return true;
    }

    private static bool ExecutionPlanIssuesAreEquivalent(
        IReadOnlyList<TargetDisplayLayoutExecutionPlanIssue> a,
        IReadOnlyList<TargetDisplayLayoutExecutionPlanIssue> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Code != b[i].Code) return false;
            if (!string.Equals(a[i].Message, b[i].Message, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].RelatedStableId, b[i].RelatedStableId, StringComparison.Ordinal)) return false;
        }
        return true;
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
        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT session_id, safety_snapshot_id, target_device_name,
                   started_at_utc, lifecycle,
                   requires_manual_recovery, is_position_uncertain,
                   last_failed_step_index,
                   source_restore_plan_json, execution_plan_json,
                   plan_payload_format_version,
                   completed_forward_step_indexes_json,
                   completed_rollback_source_step_indexes_json,
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

        var readSessionId = Guid.Parse(reader.GetString(0));
        var safetySnapshotId = Guid.Parse(reader.GetString(1));
        var targetDeviceName = reader.GetString(2);
        var startedAtUtc = DateTimeOffset.Parse(reader.GetString(3),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        // UTC 强制校验
        if (startedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException(
                $"会话 {sessionId:D} started_at_utc 非 UTC (Offset={startedAtUtc.Offset})。");

        var lifecycleVal = reader.GetInt32(4);
        if (!Enum.IsDefined((RecoverySessionLifecycle)lifecycleVal))
            throw new InvalidDataException(
                $"会话 {sessionId:D} Lifecycle 值无效：{lifecycleVal}。");
        var lifecycle = (RecoverySessionLifecycle)lifecycleVal;

        var requiresManualRaw = reader.GetInt32(5);
        if (requiresManualRaw != 0 && requiresManualRaw != 1)
            throw new InvalidDataException(
                $"会话 {sessionId:D} requires_manual_recovery 值无效：{requiresManualRaw}。");
        var requiresManualRecovery = requiresManualRaw == 1;

        var isPosUncertainRaw = reader.GetInt32(6);
        if (isPosUncertainRaw != 0 && isPosUncertainRaw != 1)
            throw new InvalidDataException(
                $"会话 {sessionId:D} is_position_uncertain 值无效：{isPosUncertainRaw}。");
        var isPositionUncertain = isPosUncertainRaw == 1;

        var lastFailedStepIndex = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);

        var sourceRestorePlanJson = reader.GetString(8);
        var executionPlanJson = reader.GetString(9);
        var planPayloadFormatVersion = reader.GetInt32(10);

        if (planPayloadFormatVersion != RecoveryPlanJsonSerializer.CurrentPlanPayloadFormatVersion)
            throw new InvalidDataException(
                $"会话 {sessionId:D} plan_payload_format_version={planPayloadFormatVersion}，不支持。");

        // Completed indexes JSON
        var completedFwdJson = reader.GetString(11);
        var completedRbJson = reader.GetString(12);

        var forwardFailureType = reader.IsDBNull(13) ? null : reader.GetString(13);
        var forwardFailureMessage = reader.IsDBNull(14) ? null : reader.GetString(14);
        var recoveryFailureType = reader.IsDBNull(15) ? null : reader.GetString(15);
        var recoveryFailureMessage = reader.IsDBNull(16) ? null : reader.GetString(16);
        var verificationResultJson = reader.IsDBNull(17) ? null : reader.GetString(17);
        var revision = reader.GetInt32(18);
        var updatedAtUtc = DateTimeOffset.Parse(reader.GetString(19),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        if (updatedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException(
                $"会话 {sessionId:D} updated_at_utc 非 UTC (Offset={updatedAtUtc.Offset})。");

        var declaredFwdCount = reader.GetInt32(20);
        var declaredRbCount = reader.GetInt32(21);

        // ── 反序列化 Plans ──
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

        // Plan JSON round-trip 完整性验证
        if (sourceRestorePlan.Items is null || sourceRestorePlan.Items.Count == 0)
        {
            if (sourceRestorePlan.SnapshotTargetIconCount > 0)
                throw new InvalidDataException(
                    $"会话 {sessionId:D} RestorePlan SnapshotTargetIconCount={sourceRestorePlan.SnapshotTargetIconCount} 但 Items 为空。");
        }
        if (executionPlan.Steps is null)
            throw new InvalidDataException($"会话 {sessionId:D} ExecutionPlan Steps 为 null。");
        if (executionPlan.IsExecutable && !executionPlan.IsNoOp && executionPlan.Steps.Count == 0)
            throw new InvalidDataException(
                $"会话 {sessionId:D} ExecutionPlan IsExecutable=true, IsNoOp=false 但 Steps 为空。");

        // ── 反序列化 Completed Indexes ──
        IReadOnlyList<int> completedFwd;
        IReadOnlyList<int> completedRb;
        try
        {
            completedFwd = DeserializeIntList(completedFwdJson);
            completedRb = DeserializeIntList(completedRbJson);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"会话 {sessionId:D} Completed indexes JSON 反序列化失败：{ex.Message}", ex);
        }

        // ── 反序列化 VerificationResult ──
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

        // UTC 校验 VerificationResult
        if (verificationResult is not null)
        {
            try { RecoverySessionRecord.RequireUtc(verificationResult.VerifiedAtUtc, "VerificationResult.VerifiedAtUtc"); }
            catch (ArgumentException ex)
            { throw new InvalidDataException(ex.Message, ex); }
        }

        // ── 读取 Attempts ──
        var attempts = ReadAttempts(connection, sessionId);
        var forwardAttempts = attempts
            .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward)
            .ToList().AsReadOnly();
        var rollbackAttempts = attempts
            .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback)
            .ToList().AsReadOnly();

        if (forwardAttempts.Count != declaredFwdCount)
            throw new InvalidDataException(
                $"会话 {sessionId:D} ForwardAttempt 数量不一致：父表 {declaredFwdCount}，实际 {forwardAttempts.Count}。");
        if (rollbackAttempts.Count != declaredRbCount)
            throw new InvalidDataException(
                $"会话 {sessionId:D} RollbackAttempt 数量不一致：父表 {declaredRbCount}，实际 {rollbackAttempts.Count}。");

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
            CompletedForwardStepIndexes = completedFwd,
            CompletedRollbackSourceStepIndexes = completedRb,
            ForwardFailureType = forwardFailureType,
            ForwardFailureMessage = forwardFailureMessage,
            RecoveryFailureType = recoveryFailureType,
            RecoveryFailureMessage = recoveryFailureMessage,
            VerificationResult = verificationResult,
            Revision = revision,
            UpdatedAtUtc = updatedAtUtc,
        };

        record.Validate();

        return record;
    }

    private static IReadOnlyList<int> DeserializeIntList(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<int>();
        var list = JsonSerializer.Deserialize<List<int>>(json);
        return list is null ? Array.Empty<int>() : list.AsReadOnly();
    }

    private static string SerializeIntList(IReadOnlyList<int> list)
    {
        return JsonSerializer.Serialize(list.ToArray());
    }

    // ═══════════════════════════════════════════════════
    //  ListUnresolvedAsync（两阶段全扫描）
    // ═══════════════════════════════════════════════════

    public async Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
        int limit = 50, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "limit 必须大于 0。");

        using var connection = CreateConnection();

        // 阶段 1：全表扫描 — 以原始 TEXT 收集并逐条验证
        var rawIds = new List<string>();
        using (var sel = connection.CreateCommand())
        {
            sel.CommandText = "SELECT session_id FROM desktop_layout_recovery_sessions ORDER BY rowid";
            using var reader = sel.ExecuteReader();
            while (reader.Read())
                rawIds.Add(reader.GetString(0));
        }

        // 逐条 TryParse + GetInternal，收集全部损坏
        var corruptedIds = new List<string>();
        var cleanRecords = new List<RecoverySessionRecord>();
        foreach (var raw in rawIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(raw, out var sid))
            {
                corruptedIds.Add($"无效 SessionId 原始值：\"{raw}\"");
                continue;
            }
            try
            {
                var record = GetInternal(connection, sid);
                if (record is null)
                {
                    corruptedIds.Add($"{sid:D} (null)");
                    continue;
                }
                cleanRecords.Add(record);
            }
            catch (Exception ex)
            {
                corruptedIds.Add($"{sid:D} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        // 任一损坏 → fail-closed 全部报告
        if (corruptedIds.Count > 0)
            throw new InvalidDataException(
                $"ListUnresolved 检测到 {corruptedIds.Count} 条损坏记录：" +
                string.Join("; ", corruptedIds));

        // 阶段 2：全表可信 → 只按 Core Disposition 分类，再排序和 limit。
        // 旧兼容 bool 已由 GetInternal + Validate 证明只是 Lifecycle 的一致性影子，
        // 这里不得再次读取它们生成另一套 unresolved 规则。
        return cleanRecords
            .Where(r => r.GetDisposition().IsUnresolved)
            .OrderByDescending(r => r.StartedAtUtc)
            .ThenByDescending(r => r.SessionId)
            .Take(limit)
            .ToList().AsReadOnly();
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
                 completed_forward_step_indexes_json,
                 completed_rollback_source_step_indexes_json,
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
                 @cfwdj, @crbj,
                 @fft, @ffm,
                 @rft, @rfm,
                 @vrj,
                 @rev, @uat,
                 @fac, @rac);
        ";
        var disposition = record.GetDisposition();

        ins.Parameters.AddWithValue("@sid", record.SessionId.ToString("D"));
        ins.Parameters.AddWithValue("@ssid", record.SafetySnapshotId.ToString("D"));
        ins.Parameters.AddWithValue("@tdn", record.TargetDeviceName);
        ins.Parameters.AddWithValue("@sat",
            record.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        ins.Parameters.AddWithValue("@lc", (int)record.Lifecycle);
        ins.Parameters.AddWithValue("@rmr", disposition.RequiresManualRecovery ? 1 : 0);
        ins.Parameters.AddWithValue("@ipu", disposition.IsPositionUncertain ? 1 : 0);
        ins.Parameters.AddWithValue("@lfsi", (object?)record.LastFailedStepIndex ?? DBNull.Value);

        ins.Parameters.AddWithValue("@srpj",
            RecoveryPlanJsonSerializer.SerializeRestorePlan(record.SourceRestorePlan));
        ins.Parameters.AddWithValue("@epj",
            RecoveryPlanJsonSerializer.SerializeExecutionPlan(record.ExecutionPlan));
        ins.Parameters.AddWithValue("@ppfv", RecoveryPlanJsonSerializer.CurrentPlanPayloadFormatVersion);

        ins.Parameters.AddWithValue("@cfwdj", SerializeIntList(record.CompletedForwardStepIndexes));
        ins.Parameters.AddWithValue("@crbj", SerializeIntList(record.CompletedRollbackSourceStepIndexes));

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
        // 不再更新 Plan JSON 列 — 计划不可变
        upd.CommandText = @"
            UPDATE desktop_layout_recovery_sessions
            SET lifecycle = @lc,
                requires_manual_recovery = @rmr,
                is_position_uncertain = @ipu,
                last_failed_step_index = @lfsi,
                completed_forward_step_indexes_json = @cfwdj,
                completed_rollback_source_step_indexes_json = @crbj,
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
        var disposition = record.GetDisposition();

        upd.Parameters.AddWithValue("@lc", (int)record.Lifecycle);
        upd.Parameters.AddWithValue("@rmr", disposition.RequiresManualRecovery ? 1 : 0);
        upd.Parameters.AddWithValue("@ipu", disposition.IsPositionUncertain ? 1 : 0);
        upd.Parameters.AddWithValue("@lfsi", (object?)record.LastFailedStepIndex ?? DBNull.Value);

        upd.Parameters.AddWithValue("@cfwdj", SerializeIntList(record.CompletedForwardStepIndexes));
        upd.Parameters.AddWithValue("@crbj", SerializeIntList(record.CompletedRollbackSourceStepIndexes));

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

    private static void InsertAttempts(
        SqliteConnection connection, Guid sessionId,
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
        del.CommandText = "DELETE FROM desktop_layout_recovery_attempts WHERE session_id = @id";
        del.Parameters.AddWithValue("@id", sessionId.ToString("D"));
        del.ExecuteNonQuery();
    }
}
