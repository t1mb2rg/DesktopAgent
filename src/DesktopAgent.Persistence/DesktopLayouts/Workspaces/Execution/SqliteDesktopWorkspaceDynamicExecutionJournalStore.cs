using System.Globalization;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;

/// <summary>
/// Dynamic Workspace execution journal 的候选 V10 持久化扩展。
/// 当前先在共享 schema v9 初始化之后幂等创建自己的表，不修改 PRAGMA user_version；
/// 等 Dynamic Coordinator 通过产品/物理 Gate 后再正式折入共享 schema migration。
/// </summary>
public sealed class SqliteDesktopWorkspaceDynamicExecutionJournalStore : IDesktopWorkspaceDynamicExecutionJournalStore
{
    private const int SqliteConstraintPrimaryKey = 1555;
    private readonly string _databasePath;

    public SqliteDesktopWorkspaceDynamicExecutionJournalStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
    }

    private SqliteConnection CreateConnection() => SqliteDesktopAgentConnectionFactory.Open(_databasePath);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_databasePath);
        if (directory is not null && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        using var connection = CreateConnection();
        SqliteDesktopAgentSchema.Initialize(connection, cancellationToken);
        EnsureDynamicSchemaExtension(connection);
        return Task.CompletedTask;
    }

    public Task SaveAsync(
        DesktopWorkspaceDynamicExecutionJournal journal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Revision != 1)
            throw new ArgumentException("首次保存 Dynamic Workspace journal 必须 Revision=1。", nameof(journal));
        journal.Validate();

        using var connection = CreateConnection();
        EnsureDynamicSchemaExtension(connection);
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
                    $"Dynamic Workspace journal {journal.SessionId:D} 已存在。", ex);
            }

            InsertAttempts(connection, transaction, journal.SessionId, journal.ForwardAttempts);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch
        {
            throw;
        }

        return Task.CompletedTask;
    }

    public Task<DesktopWorkspaceDynamicExecutionJournal> UpdateAsync(
        DesktopWorkspaceDynamicExecutionJournal journal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();

        using var connection = CreateConnection();
        EnsureDynamicSchemaExtension(connection);
        using var transaction = connection.BeginTransaction();
        var existing = GetInternal(connection, transaction, journal.SessionId)
            ?? throw new InvalidOperationException($"Dynamic Workspace journal {journal.SessionId:D} 不存在。");
        if (existing.Revision != journal.Revision)
            throw new InvalidOperationException(
                $"Dynamic Workspace journal {journal.SessionId:D} Revision 冲突：" +
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
                UPDATE workspace_dynamic_execution_journals
                SET lifecycle = @lifecycle,
                    last_observed_satisfied_goal_count = @satisfied,
                    last_failed_decision_index = @last_failed,
                    forward_failure_type = @forward_failure_type,
                    forward_failure_message = @forward_failure_message,
                    recovery_failure_type = @recovery_failure_type,
                    recovery_failure_message = @recovery_failure_message,
                    final_verified_at_utc = @final_verified,
                    revision = @next_revision,
                    updated_at_utc = @updated_at,
                    forward_attempt_count = @attempt_count
                WHERE session_id = @session_id AND revision = @expected_revision;
            ";
            BindMutableSessionParameters(update, updated);
            update.Parameters.AddWithValue("@session_id", updated.SessionId.ToString("D"));
            update.Parameters.AddWithValue("@expected_revision", journal.Revision);
            update.Parameters.AddWithValue("@next_revision", updated.Revision);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Dynamic Workspace journal CAS 更新失败。");
        }

        DeleteAttempts(connection, transaction, updated.SessionId);
        InsertAttempts(connection, transaction, updated.SessionId, updated.ForwardAttempts);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.FromResult(updated);
    }

    public Task<DesktopWorkspaceDynamicExecutionJournal?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty)
            throw new ArgumentException("SessionId 不能为 Guid.Empty。", nameof(sessionId));

        using var connection = CreateConnection();
        EnsureDynamicSchemaExtension(connection);
        return Task.FromResult(GetInternal(connection, transaction: null, sessionId));
    }

    public Task<IReadOnlyList<DesktopWorkspaceDynamicExecutionJournal>> ListUnresolvedAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = CreateConnection();
        EnsureDynamicSchemaExtension(connection);
        var ids = new List<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                SELECT session_id
                FROM workspace_dynamic_execution_journals
                WHERE lifecycle NOT IN (@committed, @restored)
                  AND (
                      lifecycle <> @needs_attention
                      OR recovery_failure_type IS NULL
                      OR recovery_failure_type <> @accepted_marker)
                ORDER BY started_at_utc ASC, session_id ASC;
            ";
            command.Parameters.AddWithValue("@committed", (int)DesktopWorkspaceExecutionJournalLifecycle.Committed);
            command.Parameters.AddWithValue("@restored", (int)DesktopWorkspaceExecutionJournalLifecycle.Restored);
            command.Parameters.AddWithValue("@needs_attention", (int)DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention);
            command.Parameters.AddWithValue(
                "@accepted_marker",
                DesktopWorkspaceExecutionResolutionMarkers.UserAcceptedCurrentState);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(Guid.Parse(reader.GetString(0)));
        }

        var journals = new List<DesktopWorkspaceDynamicExecutionJournal>(ids.Count);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            journals.Add(GetInternal(connection, transaction: null, id)
                ?? throw new InvalidDataException($"Dynamic Workspace journal {id:D} 在枚举后消失。"));
        }
        return Task.FromResult<IReadOnlyList<DesktopWorkspaceDynamicExecutionJournal>>(
            Array.AsReadOnly(journals.ToArray()));
    }

    private static void EnsureDynamicSchemaExtension(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS workspace_dynamic_execution_journals (
                session_id                    TEXT PRIMARY KEY NOT NULL,
                safety_snapshot_id            TEXT NOT NULL,
                blueprint_id                  TEXT NOT NULL CHECK(TRIM(blueprint_id) != ''),
                blueprint_revision            INTEGER NOT NULL CHECK(blueprint_revision > 0),
                certificate_fingerprint       TEXT NOT NULL CHECK(TRIM(certificate_fingerprint) != ''),
                started_at_utc                TEXT NOT NULL,
                lifecycle                     INTEGER NOT NULL CHECK(lifecycle IN (0,1,2,3,4,5)),
                confirmation_id               TEXT NOT NULL CHECK(TRIM(confirmation_id) != ''),
                preview_id                    TEXT NOT NULL CHECK(TRIM(preview_id) != ''),
                preview_fingerprint           TEXT NOT NULL CHECK(TRIM(preview_fingerprint) != ''),
                exact_plan_fingerprint        TEXT NOT NULL CHECK(TRIM(exact_plan_fingerprint) != ''),
                geometry_fingerprint          TEXT NOT NULL CHECK(TRIM(geometry_fingerprint) != ''),
                authority_json                TEXT NOT NULL CHECK(TRIM(authority_json) != ''),
                authority_payload_version     INTEGER NOT NULL CHECK(authority_payload_version > 0),
                move_goal_count               INTEGER NOT NULL CHECK(move_goal_count >= 0),
                last_observed_satisfied_goal_count INTEGER NOT NULL CHECK(last_observed_satisfied_goal_count >= 0),
                last_failed_decision_index    INTEGER NULL CHECK(last_failed_decision_index IS NULL OR last_failed_decision_index >= 0),
                forward_failure_type          TEXT NULL,
                forward_failure_message       TEXT NULL,
                recovery_failure_type         TEXT NULL,
                recovery_failure_message      TEXT NULL,
                final_verified_at_utc         TEXT NULL,
                revision                      INTEGER NOT NULL CHECK(revision > 0),
                updated_at_utc                TEXT NOT NULL,
                forward_attempt_count         INTEGER NOT NULL CHECK(forward_attempt_count >= 0),
                FOREIGN KEY (safety_snapshot_id)
                    REFERENCES desktop_layout_snapshots(snapshot_id)
                    ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS workspace_dynamic_execution_attempts (
                session_id                     TEXT NOT NULL,
                attempt_index                  INTEGER NOT NULL CHECK(attempt_index >= 0),
                decision_index                 INTEGER NOT NULL CHECK(decision_index >= 0),
                phase                          INTEGER NOT NULL CHECK(phase IN (0,1)),
                fresh_observation_id           TEXT NOT NULL CHECK(TRIM(fresh_observation_id) != ''),
                stable_id                      TEXT NOT NULL CHECK(stable_id != ''),
                move_kind                      INTEGER NOT NULL CHECK(move_kind IN (0,1)),
                fresh_from_display_ref         TEXT NOT NULL CHECK(TRIM(fresh_from_display_ref) != ''),
                fresh_from_x                   INTEGER NOT NULL,
                fresh_from_y                   INTEGER NOT NULL,
                requested_display_ref          TEXT NOT NULL CHECK(TRIM(requested_display_ref) != ''),
                requested_x                    INTEGER NOT NULL,
                requested_y                    INTEGER NOT NULL,
                requested_slot_id              TEXT NOT NULL CHECK(TRIM(requested_slot_id) != ''),
                frozen_target_display_ref      TEXT NOT NULL CHECK(TRIM(frozen_target_display_ref) != ''),
                frozen_target_x                INTEGER NOT NULL,
                frozen_target_y                INTEGER NOT NULL,
                frozen_target_slot_id          TEXT NOT NULL CHECK(TRIM(frozen_target_slot_id) != ''),
                satisfied_goal_count_before_decision INTEGER NOT NULL CHECK(satisfied_goal_count_before_decision >= 0),
                reported_stable_id             TEXT NULL,
                reported_previous_x            INTEGER NULL,
                reported_previous_y            INTEGER NULL,
                actual_x                       INTEGER NULL,
                actual_y                       INTEGER NULL,
                has_trusted_actual_position    INTEGER NOT NULL CHECK(has_trusted_actual_position IN (0,1)),
                is_exact                       INTEGER NOT NULL CHECK(is_exact IN (0,1)),
                outcome                        INTEGER NOT NULL CHECK(outcome IN (0,1,2,3,4)),
                connection_method              INTEGER NULL,
                exception_type                 TEXT NULL,
                error_message                  TEXT NULL,
                primary_path_exception_type    TEXT NULL,
                primary_path_exception_msg     TEXT NULL,
                PRIMARY KEY (session_id, attempt_index),
                UNIQUE (session_id, decision_index),
                FOREIGN KEY (session_id)
                    REFERENCES workspace_dynamic_execution_journals(session_id)
                    ON DELETE CASCADE,
                CHECK((reported_previous_x IS NULL) = (reported_previous_y IS NULL)),
                CHECK((actual_x IS NULL) = (actual_y IS NULL)),
                CHECK(has_trusted_actual_position = 0 OR actual_x IS NOT NULL),
                CHECK(is_exact = 0 OR (has_trusted_actual_position = 1 AND actual_x = requested_x AND actual_y = requested_y))
            );

            CREATE INDEX IF NOT EXISTS idx_workspace_dynamic_execution_journals_lifecycle
                ON workspace_dynamic_execution_journals (lifecycle ASC, started_at_utc ASC, session_id ASC);
        ";
        command.ExecuteNonQuery();
    }

    private static void InsertSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DesktopWorkspaceDynamicExecutionJournal journal)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO workspace_dynamic_execution_journals (
                session_id, safety_snapshot_id, blueprint_id, blueprint_revision,
                certificate_fingerprint, started_at_utc, lifecycle,
                confirmation_id, preview_id, preview_fingerprint,
                exact_plan_fingerprint, geometry_fingerprint,
                authority_json, authority_payload_version, move_goal_count,
                last_observed_satisfied_goal_count, last_failed_decision_index,
                forward_failure_type, forward_failure_message,
                recovery_failure_type, recovery_failure_message,
                final_verified_at_utc, revision, updated_at_utc, forward_attempt_count)
            VALUES (
                @session_id, @snapshot_id, @blueprint_id, @blueprint_revision,
                @certificate, @started_at, @lifecycle,
                @confirmation_id, @preview_id, @preview_fingerprint,
                @exact_plan_fingerprint, @geometry_fingerprint,
                @authority_json, @authority_payload_version, @move_goal_count,
                @satisfied, @last_failed,
                @forward_failure_type, @forward_failure_message,
                @recovery_failure_type, @recovery_failure_message,
                @final_verified, @revision, @updated_at, @attempt_count);
        ";
        command.Parameters.AddWithValue("@session_id", journal.SessionId.ToString("D"));
        command.Parameters.AddWithValue("@snapshot_id", journal.SafetySnapshotId.ToString("D"));
        command.Parameters.AddWithValue("@blueprint_id", journal.BlueprintId.ToString("D"));
        command.Parameters.AddWithValue("@blueprint_revision", journal.BlueprintRevision);
        command.Parameters.AddWithValue("@certificate", journal.CertificateFingerprint);
        command.Parameters.AddWithValue("@started_at", FormatUtc(journal.StartedAtUtc));
        command.Parameters.AddWithValue("@confirmation_id", journal.Authority.ConfirmationId.ToString("D"));
        command.Parameters.AddWithValue("@preview_id", journal.Authority.PreviewId.ToString("D"));
        command.Parameters.AddWithValue("@preview_fingerprint", journal.Authority.PreviewFingerprint);
        command.Parameters.AddWithValue("@exact_plan_fingerprint", journal.Authority.ExactPlanFingerprint);
        command.Parameters.AddWithValue("@geometry_fingerprint", journal.Authority.GeometryFingerprint);
        command.Parameters.AddWithValue(
            "@authority_json",
            DesktopWorkspaceDynamicExecutionAuthorityJsonSerializer.Serialize(journal.Authority));
        command.Parameters.AddWithValue(
            "@authority_payload_version",
            DesktopWorkspaceDynamicExecutionAuthorityJsonSerializer.CurrentPayloadFormatVersion);
        command.Parameters.AddWithValue("@move_goal_count", journal.Authority.MoveGoalCount);
        command.Parameters.AddWithValue("@revision", journal.Revision);
        BindMutableSessionParameters(command, journal);
        command.ExecuteNonQuery();
    }

    private static void BindMutableSessionParameters(
        SqliteCommand command,
        DesktopWorkspaceDynamicExecutionJournal journal)
    {
        command.Parameters.AddWithValue("@lifecycle", (int)journal.Lifecycle);
        command.Parameters.AddWithValue("@satisfied", journal.LastObservedSatisfiedGoalCount);
        command.Parameters.AddWithValue("@last_failed", DbValue(journal.LastFailedDecisionIndex));
        command.Parameters.AddWithValue("@forward_failure_type", DbValue(journal.ForwardFailureType));
        command.Parameters.AddWithValue("@forward_failure_message", DbValue(journal.ForwardFailureMessage));
        command.Parameters.AddWithValue("@recovery_failure_type", DbValue(journal.RecoveryFailureType));
        command.Parameters.AddWithValue("@recovery_failure_message", DbValue(journal.RecoveryFailureMessage));
        command.Parameters.AddWithValue(
            "@final_verified",
            DbValue(journal.FinalVerifiedAtUtc.HasValue ? FormatUtc(journal.FinalVerifiedAtUtc.Value) : null));
        command.Parameters.AddWithValue("@updated_at", FormatUtc(journal.UpdatedAtUtc));
        command.Parameters.AddWithValue("@attempt_count", journal.ForwardAttempts.Count);
    }

    private static void InsertAttempts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        IReadOnlyList<DesktopWorkspaceDynamicExecutionAttemptEvidence> attempts)
    {
        foreach (var attempt in attempts)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO workspace_dynamic_execution_attempts (
                    session_id, attempt_index, decision_index, phase,
                    fresh_observation_id, stable_id, move_kind,
                    fresh_from_display_ref, fresh_from_x, fresh_from_y,
                    requested_display_ref, requested_x, requested_y, requested_slot_id,
                    frozen_target_display_ref, frozen_target_x, frozen_target_y, frozen_target_slot_id,
                    satisfied_goal_count_before_decision,
                    reported_stable_id, reported_previous_x, reported_previous_y,
                    actual_x, actual_y,
                    has_trusted_actual_position, is_exact, outcome, connection_method,
                    exception_type, error_message,
                    primary_path_exception_type, primary_path_exception_msg)
                VALUES (
                    @session_id, @attempt_index, @decision_index, @phase,
                    @fresh_observation_id, @stable_id, @move_kind,
                    @fresh_from_display_ref, @fresh_from_x, @fresh_from_y,
                    @requested_display_ref, @requested_x, @requested_y, @requested_slot_id,
                    @frozen_target_display_ref, @frozen_target_x, @frozen_target_y, @frozen_target_slot_id,
                    @satisfied,
                    @reported_stable_id, @reported_previous_x, @reported_previous_y,
                    @actual_x, @actual_y,
                    @trusted, @is_exact, @outcome, @connection_method,
                    @exception_type, @error_message,
                    @primary_path_exception_type, @primary_path_exception_msg);
            ";
            command.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
            command.Parameters.AddWithValue("@attempt_index", attempt.AttemptIndex);
            command.Parameters.AddWithValue("@decision_index", attempt.DecisionIndex);
            command.Parameters.AddWithValue("@phase", (int)attempt.Phase);
            command.Parameters.AddWithValue("@fresh_observation_id", attempt.FreshObservationId.ToString("D"));
            command.Parameters.AddWithValue("@stable_id", attempt.StableId);
            command.Parameters.AddWithValue("@move_kind", (int)attempt.MoveKind);
            command.Parameters.AddWithValue("@fresh_from_display_ref", attempt.FreshFromDisplayRef);
            command.Parameters.AddWithValue("@fresh_from_x", attempt.FreshFromPixelPosition.X);
            command.Parameters.AddWithValue("@fresh_from_y", attempt.FreshFromPixelPosition.Y);
            command.Parameters.AddWithValue("@requested_display_ref", attempt.RequestedDisplayRef);
            command.Parameters.AddWithValue("@requested_x", attempt.RequestedPixelPosition.X);
            command.Parameters.AddWithValue("@requested_y", attempt.RequestedPixelPosition.Y);
            command.Parameters.AddWithValue("@requested_slot_id", attempt.RequestedSlotId);
            command.Parameters.AddWithValue("@frozen_target_display_ref", attempt.FrozenTargetDisplayRef);
            command.Parameters.AddWithValue("@frozen_target_x", attempt.FrozenTargetPixelPosition.X);
            command.Parameters.AddWithValue("@frozen_target_y", attempt.FrozenTargetPixelPosition.Y);
            command.Parameters.AddWithValue("@frozen_target_slot_id", attempt.FrozenTargetSlotId);
            command.Parameters.AddWithValue("@satisfied", attempt.SatisfiedGoalCountBeforeDecision);
            command.Parameters.AddWithValue("@reported_stable_id", DbValue(attempt.ReportedStableId));
            command.Parameters.AddWithValue("@reported_previous_x", DbValue(attempt.ReportedPreviousPixelPosition?.X));
            command.Parameters.AddWithValue("@reported_previous_y", DbValue(attempt.ReportedPreviousPixelPosition?.Y));
            command.Parameters.AddWithValue("@actual_x", DbValue(attempt.ActualPixelPosition?.X));
            command.Parameters.AddWithValue("@actual_y", DbValue(attempt.ActualPixelPosition?.Y));
            command.Parameters.AddWithValue("@trusted", attempt.HasTrustedActualPosition ? 1 : 0);
            command.Parameters.AddWithValue("@is_exact", attempt.IsExact ? 1 : 0);
            command.Parameters.AddWithValue("@outcome", (int)attempt.Outcome);
            command.Parameters.AddWithValue(
                "@connection_method",
                DbValue(attempt.ConnectionMethod.HasValue ? (int)attempt.ConnectionMethod.Value : null));
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
        command.CommandText = "DELETE FROM workspace_dynamic_execution_attempts WHERE session_id = @session_id;";
        command.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static DesktopWorkspaceDynamicExecutionJournal? GetInternal(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT safety_snapshot_id, blueprint_id, blueprint_revision,
                   certificate_fingerprint, started_at_utc, lifecycle,
                   confirmation_id, preview_id, preview_fingerprint,
                   exact_plan_fingerprint, geometry_fingerprint,
                   authority_json, authority_payload_version, move_goal_count,
                   last_observed_satisfied_goal_count, last_failed_decision_index,
                   forward_failure_type, forward_failure_message,
                   recovery_failure_type, recovery_failure_message,
                   final_verified_at_utc, revision, updated_at_utc, forward_attempt_count
            FROM workspace_dynamic_execution_journals
            WHERE session_id = @session_id;
        ";
        command.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));

        Guid snapshotId;
        Guid blueprintId;
        int blueprintRevision;
        string certificate;
        DateTimeOffset startedAt;
        DesktopWorkspaceExecutionJournalLifecycle lifecycle;
        Guid confirmationId;
        Guid previewId;
        string previewFingerprint;
        string exactPlanFingerprint;
        string geometryFingerprint;
        DesktopWorkspaceDynamicExecutionAuthoritySnapshot authority;
        int declaredMoveGoalCount;
        int satisfied;
        int? lastFailed;
        string? forwardFailureType;
        string? forwardFailureMessage;
        string? recoveryFailureType;
        string? recoveryFailureMessage;
        DateTimeOffset? finalVerified;
        int revision;
        DateTimeOffset updatedAt;
        int declaredAttemptCount;

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
                throw new InvalidDataException($"Dynamic Workspace lifecycle={lifecycleValue} 无效。 ");
            lifecycle = (DesktopWorkspaceExecutionJournalLifecycle)lifecycleValue;
            confirmationId = Guid.Parse(reader.GetString(6));
            previewId = Guid.Parse(reader.GetString(7));
            previewFingerprint = reader.GetString(8);
            exactPlanFingerprint = reader.GetString(9);
            geometryFingerprint = reader.GetString(10);
            var authorityJson = reader.GetString(11);
            var payloadVersion = reader.GetInt32(12);
            if (payloadVersion != DesktopWorkspaceDynamicExecutionAuthorityJsonSerializer.CurrentPayloadFormatVersion)
                throw new InvalidDataException($"Dynamic Workspace authority payload version={payloadVersion} 不支持。 ");
            authority = DesktopWorkspaceDynamicExecutionAuthorityJsonSerializer.Deserialize(authorityJson);
            declaredMoveGoalCount = reader.GetInt32(13);
            satisfied = reader.GetInt32(14);
            lastFailed = reader.IsDBNull(15) ? null : reader.GetInt32(15);
            forwardFailureType = reader.IsDBNull(16) ? null : reader.GetString(16);
            forwardFailureMessage = reader.IsDBNull(17) ? null : reader.GetString(17);
            recoveryFailureType = reader.IsDBNull(18) ? null : reader.GetString(18);
            recoveryFailureMessage = reader.IsDBNull(19) ? null : reader.GetString(19);
            finalVerified = reader.IsDBNull(20) ? null : ParseUtc(reader.GetString(20), "final_verified_at_utc");
            revision = reader.GetInt32(21);
            updatedAt = ParseUtc(reader.GetString(22), "updated_at_utc");
            declaredAttemptCount = reader.GetInt32(23);
        }

        if (authority.ConfirmationId != confirmationId ||
            authority.PreviewId != previewId ||
            !string.Equals(authority.PreviewFingerprint, previewFingerprint, StringComparison.Ordinal) ||
            !string.Equals(authority.ExactPlanFingerprint, exactPlanFingerprint, StringComparison.Ordinal) ||
            !string.Equals(authority.GeometryFingerprint, geometryFingerprint, StringComparison.Ordinal) ||
            authority.MoveGoalCount != declaredMoveGoalCount)
            throw new InvalidDataException("Dynamic Workspace journal header 与 authority payload 不一致。 ");

        var attempts = ReadAttempts(connection, transaction, sessionId);
        if (attempts.Count != declaredAttemptCount)
            throw new InvalidDataException("Dynamic Workspace attempt count 与持久化摘要不一致。 ");

        var journal = new DesktopWorkspaceDynamicExecutionJournal
        {
            SessionId = sessionId,
            SafetySnapshotId = snapshotId,
            BlueprintId = blueprintId,
            BlueprintRevision = blueprintRevision,
            CertificateFingerprint = certificate,
            StartedAtUtc = startedAt,
            Lifecycle = lifecycle,
            Authority = authority,
            ForwardAttempts = attempts,
            LastObservedSatisfiedGoalCount = satisfied,
            LastFailedDecisionIndex = lastFailed,
            ForwardFailureType = forwardFailureType,
            ForwardFailureMessage = forwardFailureMessage,
            RecoveryFailureType = recoveryFailureType,
            RecoveryFailureMessage = recoveryFailureMessage,
            FinalVerifiedAtUtc = finalVerified,
            Revision = revision,
            UpdatedAtUtc = updatedAt,
        };
        journal.Validate();
        return journal;
    }

    private static IReadOnlyList<DesktopWorkspaceDynamicExecutionAttemptEvidence> ReadAttempts(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT attempt_index, decision_index, phase,
                   fresh_observation_id, stable_id, move_kind,
                   fresh_from_display_ref, fresh_from_x, fresh_from_y,
                   requested_display_ref, requested_x, requested_y, requested_slot_id,
                   frozen_target_display_ref, frozen_target_x, frozen_target_y, frozen_target_slot_id,
                   satisfied_goal_count_before_decision,
                   reported_stable_id, reported_previous_x, reported_previous_y,
                   actual_x, actual_y,
                   has_trusted_actual_position, is_exact, outcome, connection_method,
                   exception_type, error_message,
                   primary_path_exception_type, primary_path_exception_msg
            FROM workspace_dynamic_execution_attempts
            WHERE session_id = @session_id
            ORDER BY attempt_index ASC;
        ";
        command.Parameters.AddWithValue("@session_id", sessionId.ToString("D"));

        var result = new List<DesktopWorkspaceDynamicExecutionAttemptEvidence>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var phaseValue = reader.GetInt32(2);
            var moveKindValue = reader.GetInt32(5);
            var outcomeValue = reader.GetInt32(25);
            if (!Enum.IsDefined((DesktopWorkspaceDynamicExecutionAttemptPhase)phaseValue))
                throw new InvalidDataException($"Dynamic attempt phase={phaseValue} 无效。 ");
            if (!Enum.IsDefined((DesktopWorkspaceDynamicMoveKind)moveKindValue))
                throw new InvalidDataException($"Dynamic attempt move_kind={moveKindValue} 无效。 ");
            if (!Enum.IsDefined((DesktopWorkspaceExecutionAttemptOutcome)outcomeValue))
                throw new InvalidDataException($"Dynamic attempt outcome={outcomeValue} 无效。 ");

            ConnectionMethod? connectionMethod = null;
            if (!reader.IsDBNull(26))
            {
                var value = reader.GetInt32(26);
                if (!Enum.IsDefined((ConnectionMethod)value))
                    throw new InvalidDataException($"Dynamic attempt connection_method={value} 无效。 ");
                connectionMethod = (ConnectionMethod)value;
            }

            result.Add(new DesktopWorkspaceDynamicExecutionAttemptEvidence(
                reader.GetInt32(0),
                reader.GetInt32(1),
                (DesktopWorkspaceDynamicExecutionAttemptPhase)phaseValue,
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                (DesktopWorkspaceDynamicMoveKind)moveKindValue,
                reader.GetString(6),
                new PixelPoint(reader.GetInt32(7), reader.GetInt32(8)),
                reader.GetString(9),
                new PixelPoint(reader.GetInt32(10), reader.GetInt32(11)),
                reader.GetString(12),
                reader.GetString(13),
                new PixelPoint(reader.GetInt32(14), reader.GetInt32(15)),
                reader.GetString(16),
                reader.GetInt32(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                ReadOptionalPoint(reader, 19, 20, "reported_previous"),
                ReadOptionalPoint(reader, 21, 22, "actual"),
                ReadBoolean(reader, 23, "has_trusted_actual_position"),
                ReadBoolean(reader, 24, "is_exact"),
                (DesktopWorkspaceExecutionAttemptOutcome)outcomeValue,
                connectionMethod,
                reader.IsDBNull(27) ? null : reader.GetString(27),
                reader.IsDBNull(28) ? null : reader.GetString(28),
                reader.IsDBNull(29) ? null : reader.GetString(29),
                reader.IsDBNull(30) ? null : reader.GetString(30)));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static void EnsureImmutableIdentity(
        DesktopWorkspaceDynamicExecutionJournal existing,
        DesktopWorkspaceDynamicExecutionJournal candidate)
    {
        if (existing.SessionId != candidate.SessionId ||
            existing.SafetySnapshotId != candidate.SafetySnapshotId ||
            existing.BlueprintId != candidate.BlueprintId ||
            existing.BlueprintRevision != candidate.BlueprintRevision ||
            existing.StartedAtUtc != candidate.StartedAtUtc ||
            !string.Equals(existing.CertificateFingerprint, candidate.CertificateFingerprint, StringComparison.Ordinal) ||
            !string.Equals(
                DesktopWorkspaceDynamicExecutionAuthorityJsonSerializer.Serialize(existing.Authority),
                DesktopWorkspaceDynamicExecutionAuthorityJsonSerializer.Serialize(candidate.Authority),
                StringComparison.Ordinal))
            throw new InvalidOperationException("Dynamic Workspace journal 的冻结 identity / authority 不可变更。 ");
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
            throw new InvalidDataException($"{fieldName} X/Y 必须同时为 null 或同时存在。 ");
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
            throw new InvalidDataException($"{fieldName} 必须为 UTC。 ");
        return result;
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
}
