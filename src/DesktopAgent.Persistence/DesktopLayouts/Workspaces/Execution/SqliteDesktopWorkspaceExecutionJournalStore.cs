using System.Globalization;
using System.Text.Json;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;

public sealed class SqliteDesktopWorkspaceExecutionJournalStore : IDesktopWorkspaceExecutionJournalStore
{
    private const int SqliteConstraintPrimaryKey = 1555;
    private readonly string _databasePath;

    public SqliteDesktopWorkspaceExecutionJournalStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
    }

    private SqliteConnection CreateConnection() =>
        SqliteDesktopAgentConnectionFactory.Open(_databasePath);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_databasePath);
        if (directory is not null && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        using var connection = CreateConnection();
        SqliteDesktopAgentSchema.Initialize(connection, cancellationToken);
        return Task.CompletedTask;
    }

    public Task SaveAsync(
        DesktopWorkspaceExecutionJournal journal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Revision != 1)
            throw new ArgumentException("首次保存 Workspace execution journal 必须 Revision=1。", nameof(journal));
        journal.Validate();

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            try
            {
                InsertSession(connection, transaction, journal);
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintPrimaryKey)
            {
                throw new InvalidOperationException(
                    $"Workspace execution journal {journal.SessionId:D} 已存在。", ex);
            }

            InsertAttempts(connection, transaction, journal.SessionId, journal.ForwardAttempts);
            InsertAttempts(connection, transaction, journal.SessionId, journal.RollbackAttempts);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch
        {
            throw;
        }

        return Task.CompletedTask;
    }

    public Task<DesktopWorkspaceExecutionJournal> UpdateAsync(
        DesktopWorkspaceExecutionJournal journal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        var existing = GetInternal(connection, transaction, journal.SessionId)
            ?? throw new InvalidOperationException(
                $"Workspace execution journal {journal.SessionId:D} 不存在。");

        if (existing.Revision != journal.Revision)
            throw new InvalidOperationException(
                $"Workspace execution journal {journal.SessionId:D} Revision 冲突：" +
                $"期望 {journal.Revision}，实际 {existing.Revision}。");
        EnsureImmutableIdentity(existing, journal);

        var updated = journal with
        {
            Revision = checked(journal.Revision + 1),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        updated.Validate();

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = @"
                UPDATE workspace_execution_journals
                SET lifecycle = @lifecycle,
                    completed_forward_step_indexes_json = @completed_forward,
                    completed_rollback_source_step_indexes_json = @completed_rollback,
                    last_failed_step_index = @last_failed,
                    forward_failure_type = @forward_failure_type,
                    forward_failure_message = @forward_failure_message,
                    recovery_failure_type = @recovery_failure_type,
                    recovery_failure_message = @recovery_failure_message,
                    revision = @next_revision,
                    updated_at_utc = @updated_at,
                    forward_attempt_count = @forward_attempt_count,
                    rollback_attempt_count = @rollback_attempt_count
                WHERE session_id = @session_id AND revision = @expected_revision;
            ";
            BindMutableSessionParameters(update, updated);
            update.Parameters.AddWithValue("@session_id", updated.SessionId.ToString("D"));
            update.Parameters.AddWithValue("@expected_revision", journal.Revision);
            update.Parameters.AddWithValue("@next_revision", updated.Revision);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Workspace execution journal CAS 更新失败。");
        }

        DeleteAttempts(connection, transaction, updated.SessionId);
        InsertAttempts(connection, transaction, updated.SessionId, updated.ForwardAttempts);
        InsertAttempts(connection, transaction, updated.SessionId, updated.RollbackAttempts);

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.FromResult(updated);
    }

    public Task<DesktopWorkspaceExecutionJournal?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty)
            throw new ArgumentException("SessionId 不能为 Guid.Empty。", nameof(sessionId));

        using var connection = CreateConnection();
        return Task.FromResult(GetInternal(connection, transaction: null, sessionId));
    }

    public Task<IReadOnlyList<DesktopWorkspaceExecutionJournal>> ListUnresolvedAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = CreateConnection();
        var ids = new List<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                SELECT session_id
                FROM workspace_execution_journals
                WHERE lifecycle NOT IN (@committed, @restored)
                ORDER BY started_at_utc ASC, session_id ASC;
            ";
            command.Parameters.AddWithValue("@committed", (int)DesktopWorkspaceExecutionJournalLifecycle.Committed);
            command.Parameters.AddWithValue("@restored", (int)DesktopWorkspaceExecutionJournalLifecycle.Restored);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(Guid.Parse(reader.GetString(0)));
        }

        var result = new List<DesktopWorkspaceExecutionJournal>(ids.Count);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(GetInternal(connection, transaction: null, id)
                ?? throw new InvalidDataException($"Workspace execution journal {id:D} 在枚举后消失。"));
        }
        return Task.FromResult<IReadOnlyList<DesktopWorkspaceExecutionJournal>>(
            Array.AsReadOnly(result.ToArray()));
    }

    private static void InsertSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DesktopWorkspaceExecutionJournal journal)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO workspace_execution_journals (
                session_id, safety_snapshot_id, blueprint_id, blueprint_revision,
                certificate_fingerprint, started_at_utc, lifecycle,
                preview_fingerprint, exact_plan_fingerprint, execution_plan_fingerprint,
                execution_plan_json, plan_payload_format_version,
                completed_forward_step_indexes_json,
                completed_rollback_source_step_indexes_json,
                last_failed_step_index,
                forward_failure_type, forward_failure_message,
                recovery_failure_type, recovery_failure_message,
                revision, updated_at_utc, forward_attempt_count, rollback_attempt_count)
            VALUES (
                @session_id, @snapshot_id, @blueprint_id, @blueprint_revision,
                @certificate, @started_at, @lifecycle,
                @preview_fingerprint, @exact_plan_fingerprint, @execution_plan_fingerprint,
                @execution_plan_json, @payload_version,
                @completed_forward, @completed_rollback,
                @last_failed,
                @forward_failure_type, @forward_failure_message,
                @recovery_failure_type, @recovery_failure_message,
                @revision, @updated_at, @forward_attempt_count, @rollback_attempt_count);
        ";
        command.Parameters.AddWithValue("@session_id", journal.SessionId.ToString("D"));
        command.Parameters.AddWithValue("@snapshot_id", journal.SafetySnapshotId.ToString("D"));
        command.Parameters.AddWithValue("@blueprint_id", journal.BlueprintId.ToString("D"));
        command.Parameters.AddWithValue("@blueprint_revision", journal.BlueprintRevision);
        command.Parameters.AddWithValue("@certificate", journal.CertificateFingerprint);
        command.Parameters.AddWithValue("@started_at", FormatUtc(journal.StartedAtUtc));
        command.Parameters.AddWithValue("@preview_fingerprint", journal.ExecutionPlan.PreviewFingerprint);
        command.Parameters.AddWithValue("@exact_plan_fingerprint", journal.ExecutionPlan.ExactPlanFingerprint);
        command.Parameters.AddWithValue("@execution_plan_fingerprint", journal.ExecutionPlan.ExecutionPlanFingerprint);
        command.Parameters.AddWithValue(
            "@execution_plan_json",
            DesktopWorkspaceExecutionPlanSnapshotJsonSerializer.Serialize(journal.ExecutionPlan));
        command.Parameters.AddWithValue(
            "@payload_version",
            DesktopWorkspaceExecutionPlanSnapshotJsonSerializer.CurrentPayloadFormatVersion);
        command.Parameters.AddWithValue("@revision", journal.Revision);
        BindMutableSessionParameters(command, journal);
        command.ExecuteNonQuery();
    }

    private static void BindMutableSessionParameters(
        SqliteCommand command,
        DesktopWorkspaceExecutionJournal journal)
    {
        command.Parameters.AddWithValue("@lifecycle", (int)journal.Lifecycle);
        command.Parameters.AddWithValue(
            "@completed_forward",
            JsonSerializer.Serialize(journal.CompletedForwardStepIndexes));
        command.Parameters.AddWithValue(
            "@completed_rollback",
            JsonSerializer.Serialize(journal.CompletedRollbackSourceStepIndexes));
        command.Parameters.AddWithValue("@last_failed", DbValue(journal.LastFailedStepIndex));
        command.Parameters.AddWithValue("@forward_failure_type", DbValue(journal.ForwardFailureType));
        command.Parameters.AddWithValue("@forward_failure_message", DbValue(journal.ForwardFailureMessage));
        command.Parameters.AddWithValue("@recovery_failure_type", DbValue(journal.RecoveryFailureType));
        command.Parameters.AddWithValue("@recovery_failure_message", DbValue(journal.RecoveryFailureMessage));
        command.Parameters.AddWithValue("@updated_at", FormatUtc(journal.UpdatedAtUtc));
        command.Parameters.AddWithValue("@forward_attempt_count", journal.ForwardAttempts.Count);
        command.Parameters.AddWithValue("@rollback_attempt_count", journal.RollbackAttempts.Count);
    }

    private static void InsertAttempts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> attempts)
    {
        foreach (var attempt in attempts)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO workspace_execution_attempts (
                    session_id, attempt_index, source_step_index, phase,
                    expected_stable_id,
                    expected_previous_display_ref, expected_previous_x, expected_previous_y,
                    requested_display_ref, requested_x, requested_y,
                    reported_stable_id, reported_previous_x, reported_previous_y,
                    actual_x, actual_y,
                    has_trusted_actual_position, is_exact, outcome, connection_method,
                    exception_type, error_message,
                    primary_path_exception_type, primary_path_exception_msg)
                VALUES (
                    @session_id, @attempt_index, @source_step_index, @phase,
                    @expected_stable_id,
                    @expected_previous_display_ref, @expected_previous_x, @expected_previous_y,
                    @requested_display_ref, @requested_x, @requested_y,
                    @reported_stable_id, @reported_previous_x, @reported_previous_y,
                    @actual_x, @actual_y,
                    @trusted, @is_exact, @outcome, @connection_method,
                    @exception_type, @error_message,
                    @primary_path_exception_type, @primary_path_exception_msg);
            ";
            command.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
            command.Parameters.AddWithValue("@attempt_index", attempt.AttemptIndex);
            command.Parameters.AddWithValue("@source_step_index", attempt.SourceStepIndex);
            command.Parameters.AddWithValue("@phase", (int)attempt.Phase);
            command.Parameters.AddWithValue("@expected_stable_id", attempt.ExpectedStableId);
            command.Parameters.AddWithValue("@expected_previous_display_ref", attempt.ExpectedPreviousDisplayRef);
            command.Parameters.AddWithValue("@expected_previous_x", attempt.ExpectedPreviousPixelPosition.X);
            command.Parameters.AddWithValue("@expected_previous_y", attempt.ExpectedPreviousPixelPosition.Y);
            command.Parameters.AddWithValue("@requested_display_ref", attempt.RequestedDisplayRef);
            command.Parameters.AddWithValue("@requested_x", attempt.RequestedPixelPosition.X);
            command.Parameters.AddWithValue("@requested_y", attempt.RequestedPixelPosition.Y);
            command.Parameters.AddWithValue("@reported_stable_id", DbValue(attempt.ReportedStableId));
            command.Parameters.AddWithValue("@reported_previous_x", DbValue(attempt.ReportedPreviousPixelPosition?.X));
            command.Parameters.AddWithValue("@reported_previous_y", DbValue(attempt.ReportedPreviousPixelPosition?.Y));
            command.Parameters.AddWithValue("@actual_x", DbValue(attempt.ActualPixelPosition?.X));
            command.Parameters.AddWithValue("@actual_y", DbValue(attempt.ActualPixelPosition?.Y));
            command.Parameters.AddWithValue("@trusted", attempt.HasTrustedActualPosition ? 1 : 0);
            command.Parameters.AddWithValue("@is_exact", attempt.IsExact ? 1 : 0);
            command.Parameters.AddWithValue("@outcome", (int)attempt.Outcome);
            command.Parameters.AddWithValue("@connection_method", DbValue(attempt.ConnectionMethod is null ? null : (int)attempt.ConnectionMethod.Value));
            command.Parameters.AddWithValue("@exception_type", DbValue(attempt.ExceptionType));
            command.Parameters.AddWithValue("@error_message", DbValue(attempt.ErrorMessage));
            command.Parameters.AddWithValue("@primary_path_exception_type", DbValue(attempt.PrimaryPathExceptionType));
            command.Parameters.AddWithValue("@primary_path_exception_msg", DbValue(attempt.PrimaryPathExceptionMessage));
            command.ExecuteNonQuery();
        }
    }

    private static void DeleteAttempts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM workspace_execution_attempts WHERE session_id = @session_id;";
        command.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static DesktopWorkspaceExecutionJournal? GetInternal(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT safety_snapshot_id, blueprint_id, blueprint_revision,
                   certificate_fingerprint, started_at_utc, lifecycle,
                   preview_fingerprint, exact_plan_fingerprint, execution_plan_fingerprint,
                   execution_plan_json, plan_payload_format_version,
                   completed_forward_step_indexes_json,
                   completed_rollback_source_step_indexes_json,
                   last_failed_step_index,
                   forward_failure_type, forward_failure_message,
                   recovery_failure_type, recovery_failure_message,
                   revision, updated_at_utc,
                   forward_attempt_count, rollback_attempt_count
            FROM workspace_execution_journals
            WHERE session_id = @session_id;
        ";
        command.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));

        Guid snapshotId;
        Guid blueprintId;
        int blueprintRevision;
        string certificate;
        DateTimeOffset startedAt;
        DesktopWorkspaceExecutionJournalLifecycle lifecycle;
        string previewFingerprint;
        string exactPlanFingerprint;
        string executionPlanFingerprint;
        DesktopWorkspaceExecutionPlanSnapshot plan;
        IReadOnlyList<int> completedForward;
        IReadOnlyList<int> completedRollback;
        int? lastFailed;
        string? forwardFailureType;
        string? forwardFailureMessage;
        string? recoveryFailureType;
        string? recoveryFailureMessage;
        int revision;
        DateTimeOffset updatedAt;
        int declaredForwardAttemptCount;
        int declaredRollbackAttemptCount;

        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return null;

            snapshotId = Guid.Parse(reader.GetString(0));
            blueprintId = Guid.Parse(reader.GetString(1));
            blueprintRevision = reader.GetInt32(2);
            certificate = reader.GetString(3);
            startedAt = ParseUtc(reader.GetString(4), "started_at_utc");
            var lifecycleValue = reader.GetInt32(5);
            if (!Enum.IsDefined((DesktopWorkspaceExecutionJournalLifecycle)lifecycleValue))
                throw new InvalidDataException($"Workspace journal lifecycle={lifecycleValue} 无效。");
            lifecycle = (DesktopWorkspaceExecutionJournalLifecycle)lifecycleValue;
            previewFingerprint = reader.GetString(6);
            exactPlanFingerprint = reader.GetString(7);
            executionPlanFingerprint = reader.GetString(8);
            var planJson = reader.GetString(9);
            var payloadVersion = reader.GetInt32(10);
            if (payloadVersion != DesktopWorkspaceExecutionPlanSnapshotJsonSerializer.CurrentPayloadFormatVersion)
                throw new InvalidDataException($"Workspace execution plan payload version={payloadVersion} 不支持。");
            plan = DesktopWorkspaceExecutionPlanSnapshotJsonSerializer.Deserialize(planJson);
            completedForward = DeserializeIndexes(reader.GetString(11), "completed_forward_step_indexes_json");
            completedRollback = DeserializeIndexes(reader.GetString(12), "completed_rollback_source_step_indexes_json");
            lastFailed = reader.IsDBNull(13) ? null : reader.GetInt32(13);
            forwardFailureType = reader.IsDBNull(14) ? null : reader.GetString(14);
            forwardFailureMessage = reader.IsDBNull(15) ? null : reader.GetString(15);
            recoveryFailureType = reader.IsDBNull(16) ? null : reader.GetString(16);
            recoveryFailureMessage = reader.IsDBNull(17) ? null : reader.GetString(17);
            revision = reader.GetInt32(18);
            updatedAt = ParseUtc(reader.GetString(19), "updated_at_utc");
            declaredForwardAttemptCount = reader.GetInt32(20);
            declaredRollbackAttemptCount = reader.GetInt32(21);
        }

        if (!string.Equals(plan.PreviewFingerprint, previewFingerprint, StringComparison.Ordinal) ||
            !string.Equals(plan.ExactPlanFingerprint, exactPlanFingerprint, StringComparison.Ordinal) ||
            !string.Equals(plan.ExecutionPlanFingerprint, executionPlanFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Workspace journal header fingerprints 与 plan payload 不一致。");

        var attempts = ReadAttempts(connection, transaction, sessionId);
        var forwardAttempts = attempts
            .Where(static attempt => attempt.Phase == DesktopWorkspaceExecutionAttemptPhase.Forward)
            .ToArray();
        var rollbackAttempts = attempts
            .Where(static attempt => attempt.Phase == DesktopWorkspaceExecutionAttemptPhase.Rollback)
            .ToArray();
        if (forwardAttempts.Length != declaredForwardAttemptCount ||
            rollbackAttempts.Length != declaredRollbackAttemptCount)
            throw new InvalidDataException("Workspace journal attempt count 与持久化摘要不一致。");

        var journal = new DesktopWorkspaceExecutionJournal
        {
            SessionId = sessionId,
            SafetySnapshotId = snapshotId,
            BlueprintId = blueprintId,
            BlueprintRevision = blueprintRevision,
            CertificateFingerprint = certificate,
            StartedAtUtc = startedAt,
            Lifecycle = lifecycle,
            ExecutionPlan = plan,
            ForwardAttempts = forwardAttempts,
            RollbackAttempts = rollbackAttempts,
            CompletedForwardStepIndexes = completedForward,
            CompletedRollbackSourceStepIndexes = completedRollback,
            LastFailedStepIndex = lastFailed,
            ForwardFailureType = forwardFailureType,
            ForwardFailureMessage = forwardFailureMessage,
            RecoveryFailureType = recoveryFailureType,
            RecoveryFailureMessage = recoveryFailureMessage,
            Revision = revision,
            UpdatedAtUtc = updatedAt,
        };
        journal.Validate();
        return journal;
    }

    private static IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> ReadAttempts(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT attempt_index, source_step_index, phase,
                   expected_stable_id,
                   expected_previous_display_ref, expected_previous_x, expected_previous_y,
                   requested_display_ref, requested_x, requested_y,
                   reported_stable_id, reported_previous_x, reported_previous_y,
                   actual_x, actual_y,
                   has_trusted_actual_position, is_exact, outcome, connection_method,
                   exception_type, error_message,
                   primary_path_exception_type, primary_path_exception_msg
            FROM workspace_execution_attempts
            WHERE session_id = @session_id
            ORDER BY attempt_index ASC;
        ";
        command.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));

        var result = new List<DesktopWorkspaceExecutionAttemptEvidence>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var phaseValue = reader.GetInt32(2);
            var outcomeValue = reader.GetInt32(17);
            if (!Enum.IsDefined((DesktopWorkspaceExecutionAttemptPhase)phaseValue))
                throw new InvalidDataException($"Workspace attempt phase={phaseValue} 无效。");
            if (!Enum.IsDefined((DesktopWorkspaceExecutionAttemptOutcome)outcomeValue))
                throw new InvalidDataException($"Workspace attempt outcome={outcomeValue} 无效。");

            ConnectionMethod? connectionMethod = null;
            if (!reader.IsDBNull(18))
            {
                var value = reader.GetInt32(18);
                if (!Enum.IsDefined((ConnectionMethod)value))
                    throw new InvalidDataException($"Workspace attempt connection_method={value} 无效。");
                connectionMethod = (ConnectionMethod)value;
            }

            var reportedPrevious = ReadOptionalPoint(reader, 11, 12, "reported_previous");
            var actual = ReadOptionalPoint(reader, 13, 14, "actual");
            result.Add(new DesktopWorkspaceExecutionAttemptEvidence(
                reader.GetInt32(0),
                reader.GetInt32(1),
                (DesktopWorkspaceExecutionAttemptPhase)phaseValue,
                reader.GetString(3),
                reader.GetString(4),
                new PixelPoint(reader.GetInt32(5), reader.GetInt32(6)),
                reader.GetString(7),
                new PixelPoint(reader.GetInt32(8), reader.GetInt32(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reportedPrevious,
                actual,
                ReadBoolean(reader, 15, "has_trusted_actual_position"),
                ReadBoolean(reader, 16, "is_exact"),
                (DesktopWorkspaceExecutionAttemptOutcome)outcomeValue,
                connectionMethod,
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetString(22)));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static void EnsureImmutableIdentity(
        DesktopWorkspaceExecutionJournal existing,
        DesktopWorkspaceExecutionJournal candidate)
    {
        if (existing.SessionId != candidate.SessionId ||
            existing.SafetySnapshotId != candidate.SafetySnapshotId ||
            existing.BlueprintId != candidate.BlueprintId ||
            existing.BlueprintRevision != candidate.BlueprintRevision ||
            existing.StartedAtUtc != candidate.StartedAtUtc ||
            !string.Equals(existing.CertificateFingerprint, candidate.CertificateFingerprint, StringComparison.Ordinal) ||
            !PlanSnapshotsEquivalent(existing.ExecutionPlan, candidate.ExecutionPlan))
            throw new InvalidOperationException("Workspace execution journal 的冻结 identity / plan 不可变更。");
    }

    private static bool PlanSnapshotsEquivalent(
        DesktopWorkspaceExecutionPlanSnapshot first,
        DesktopWorkspaceExecutionPlanSnapshot second)
    {
        if (first.ConfirmationId != second.ConfirmationId ||
            first.PreviewId != second.PreviewId ||
            first.FreshObservationId != second.FreshObservationId ||
            first.MoveItemCount != second.MoveItemCount ||
            first.UsesTemporaryStaging != second.UsesTemporaryStaging ||
            !string.Equals(first.PreviewFingerprint, second.PreviewFingerprint, StringComparison.Ordinal) ||
            !string.Equals(first.ExactPlanFingerprint, second.ExactPlanFingerprint, StringComparison.Ordinal) ||
            !string.Equals(first.ExecutionPlanFingerprint, second.ExecutionPlanFingerprint, StringComparison.Ordinal) ||
            first.Steps.Count != second.Steps.Count)
            return false;

        for (var index = 0; index < first.Steps.Count; index++)
        {
            var a = first.Steps[index];
            var b = second.Steps[index];
            if (a.StepIndex != b.StepIndex ||
                !string.Equals(a.StableId, b.StableId, StringComparison.Ordinal) ||
                !string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal) ||
                a.Kind != b.Kind ||
                !string.Equals(a.FromDisplayRef, b.FromDisplayRef, StringComparison.Ordinal) ||
                a.FromPixelPosition != b.FromPixelPosition ||
                !string.Equals(a.ToDisplayRef, b.ToDisplayRef, StringComparison.Ordinal) ||
                a.ToPixelPosition != b.ToPixelPosition ||
                !string.Equals(a.ToSlotId, b.ToSlotId, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static IReadOnlyList<int> DeserializeIndexes(string json, string fieldName)
    {
        try
        {
            var values = JsonSerializer.Deserialize<List<int>>(json)
                ?? throw new InvalidDataException($"{fieldName} 反序列化为 null。");
            return Array.AsReadOnly(values.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{fieldName} JSON 无效。", ex);
        }
    }

    private static PixelPoint? ReadOptionalPoint(
        SqliteDataReader reader,
        int xOrdinal,
        int yOrdinal,
        string fieldName)
    {
        var xNull = reader.IsDBNull(xOrdinal);
        var yNull = reader.IsDBNull(yOrdinal);
        if (xNull != yNull)
            throw new InvalidDataException($"{fieldName} X/Y 必须同时为 null 或同时存在。");
        return xNull ? null : new PixelPoint(reader.GetInt32(xOrdinal), reader.GetInt32(yOrdinal));
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal, string fieldName)
    {
        var value = reader.GetInt32(ordinal);
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"{fieldName}={value} 不是合法布尔值。"),
        };
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("持久化时间必须为 UTC。", nameof(value));
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value, string fieldName)
    {
        var result = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (result.Offset != TimeSpan.Zero)
            throw new InvalidDataException($"{fieldName} 必须为 UTC。");
        return result;
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
}
