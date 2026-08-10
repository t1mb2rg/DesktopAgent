using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.Database;

/// <summary>
/// 共享数据库 Schema 管理器。所有 Store 的 InitializeAsync 都必须调用此初始化器。
/// 统一管理 user_version，不得由单个 Store 各自设置 PRAGMA user_version。
/// </summary>
internal static class SqliteDesktopAgentSchema
{
    internal const int CurrentVersion = 6;

    public static void Initialize(SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var readVersion = connection.CreateCommand();
        readVersion.CommandText = "PRAGMA user_version;";
        var version = (int)(long)readVersion.ExecuteScalar()!;

        if (version > CurrentVersion)
            throw new InvalidOperationException(
                $"数据库 user_version={version}，超过当前支持的最高版本 {CurrentVersion}。");

        using var tx = connection.BeginTransaction();
        try
        {
            EnsureV1Schema(connection, tx);
            EnsureV2Schema(connection, tx);
            EnsureV3Schema(connection, tx);
            EnsureV4Schema(connection, tx);
            EnsureV5Schema(connection, tx);
            EnsureV6Schema(connection, tx);

            if (version < CurrentVersion)
            {
                using var setVersion = connection.CreateCommand();
                setVersion.Transaction = tx;
                setVersion.CommandText = $"PRAGMA user_version = {CurrentVersion};";
                setVersion.ExecuteNonQuery();
            }

            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
        }
        catch
        {
            throw;
        }
    }

    private static void EnsureV1Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_layout_snapshots (
                snapshot_id                TEXT PRIMARY KEY NOT NULL,
                created_at_utc             TEXT NOT NULL,
                target_monitor_device_name TEXT NULL,
                connection_method          INTEGER NOT NULL,
                format_version             INTEGER NOT NULL CHECK(format_version > 0),
                icon_count                 INTEGER NOT NULL CHECK(icon_count >= 0)
            );

            CREATE TABLE IF NOT EXISTS desktop_layout_snapshot_items (
                snapshot_id         TEXT NOT NULL,
                ordinal             INTEGER NOT NULL,
                stable_id           TEXT NOT NULL,
                display_name        TEXT NOT NULL,
                parsing_name        TEXT NULL,
                item_kind           INTEGER NOT NULL,
                x                   INTEGER NOT NULL,
                y                   INTEGER NOT NULL,
                monitor_device_name TEXT NULL,
                is_system_item      INTEGER NOT NULL CHECK(is_system_item IN (0, 1)),
                is_file_system_item INTEGER NOT NULL CHECK(is_file_system_item IN (0, 1)),
                PRIMARY KEY (snapshot_id, ordinal),
                FOREIGN KEY (snapshot_id)
                    REFERENCES desktop_layout_snapshots(snapshot_id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_snapshots_created_at_utc_desc
                ON desktop_layout_snapshots (created_at_utc DESC);
        ";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureV2Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_layout_templates (
                template_id                 TEXT PRIMARY KEY NOT NULL,
                name                        TEXT NOT NULL,
                version                     INTEGER NOT NULL CHECK(version > 0),
                target_display_binding_kind INTEGER NOT NULL
                    CHECK(target_display_binding_kind IN (0, 1, 2)),
                target_display_device_name  TEXT NULL,
                zone_count                  INTEGER NOT NULL CHECK(zone_count > 0),
                CHECK(
                    (target_display_binding_kind IN (0, 1) AND target_display_device_name IS NULL)
                    OR
                    (target_display_binding_kind = 2
                     AND target_display_device_name IS NOT NULL
                     AND TRIM(target_display_device_name) != '')
                ),
                CHECK(TRIM(name) != '')
            );

            CREATE TABLE IF NOT EXISTS desktop_layout_template_zones (
                template_id    TEXT NOT NULL,
                ordinal        INTEGER NOT NULL CHECK(ordinal >= 0),
                zone_id        TEXT NOT NULL,
                name           TEXT NOT NULL CHECK(TRIM(name) != ''),
                left_value     INTEGER NOT NULL CHECK(left_value >= 0),
                top_value      INTEGER NOT NULL CHECK(top_value >= 0),
                right_value    INTEGER NOT NULL CHECK(right_value <= 10000),
                bottom_value   INTEGER NOT NULL CHECK(bottom_value <= 10000),
                sort_order     INTEGER NOT NULL CHECK(sort_order >= 0),
                placement_flow INTEGER NOT NULL CHECK(placement_flow IN (0, 1)),
                max_item_count INTEGER NULL CHECK(max_item_count IS NULL OR max_item_count > 0),
                CHECK(right_value > left_value),
                CHECK(bottom_value > top_value),
                PRIMARY KEY (template_id, ordinal),
                FOREIGN KEY (template_id)
                    REFERENCES desktop_layout_templates(template_id)
                    ON DELETE CASCADE,
                UNIQUE(template_id, zone_id),
                UNIQUE(template_id, sort_order)
            );

            CREATE INDEX IF NOT EXISTS idx_templates_name_id_asc
                ON desktop_layout_templates (name COLLATE BINARY ASC, template_id ASC);
        ";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureV3Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_layout_template_icon_rules (
                template_id    TEXT NOT NULL,
                stable_id      TEXT NOT NULL,
                rule_kind      INTEGER NOT NULL CHECK(rule_kind IN (0, 1, 2)),
                target_zone_id TEXT NULL,
                PRIMARY KEY (template_id, stable_id),
                FOREIGN KEY (template_id)
                    REFERENCES desktop_layout_templates(template_id)
                    ON DELETE CASCADE,
                CHECK(
                    (rule_kind IN (0, 1) AND target_zone_id IS NULL)
                    OR
                    (rule_kind = 2 AND target_zone_id IS NOT NULL)
                ),
                CHECK(stable_id != '')
            );
        ";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureV4Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_layout_recovery_sessions (
                session_id                  TEXT PRIMARY KEY NOT NULL,
                safety_snapshot_id          TEXT NOT NULL,
                target_device_name          TEXT NOT NULL CHECK(TRIM(target_device_name) != ''),
                started_at_utc              TEXT NOT NULL,
                lifecycle                   INTEGER NOT NULL,
                requires_manual_recovery    INTEGER NOT NULL,
                is_position_uncertain       INTEGER NOT NULL,
                last_failed_step_index      INTEGER NULL,
                source_restore_plan_json    TEXT NOT NULL,
                execution_plan_json         TEXT NOT NULL,
                plan_payload_format_version INTEGER NOT NULL CHECK(plan_payload_format_version > 0),
                completed_forward_step_indexes_json         TEXT NOT NULL,
                completed_rollback_source_step_indexes_json TEXT NOT NULL,
                forward_failure_type        TEXT NULL,
                forward_failure_message     TEXT NULL,
                recovery_failure_type       TEXT NULL,
                recovery_failure_message    TEXT NULL,
                verification_result_json    TEXT NULL,
                revision                    INTEGER NOT NULL CHECK(revision > 0),
                updated_at_utc              TEXT NOT NULL,
                forward_attempt_count       INTEGER NOT NULL CHECK(forward_attempt_count >= 0),
                rollback_attempt_count      INTEGER NOT NULL CHECK(rollback_attempt_count >= 0)
            );

            CREATE TABLE IF NOT EXISTS desktop_layout_recovery_attempts (
                session_id                  TEXT NOT NULL,
                attempt_index               INTEGER NOT NULL,
                source_step_index           INTEGER NOT NULL,
                phase                       INTEGER NOT NULL,
                expected_stable_id          TEXT NOT NULL,
                expected_previous_x         INTEGER NULL,
                expected_previous_y         INTEGER NULL,
                requested_x                 INTEGER NOT NULL,
                requested_y                 INTEGER NOT NULL,
                reported_stable_id          TEXT NULL,
                reported_previous_x         INTEGER NULL,
                reported_previous_y         INTEGER NULL,
                actual_x                    INTEGER NULL,
                actual_y                    INTEGER NULL,
                has_trusted_actual_position INTEGER NOT NULL,
                is_exact                    INTEGER NOT NULL,
                outcome                     INTEGER NOT NULL,
                connection_method           INTEGER NULL,
                exception_type              TEXT NULL,
                error_message               TEXT NULL,
                primary_path_exception_type TEXT NULL,
                primary_path_exception_msg  TEXT NULL,
                PRIMARY KEY (session_id, attempt_index),
                FOREIGN KEY (session_id)
                    REFERENCES desktop_layout_recovery_sessions(session_id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_recovery_sessions_lifecycle
                ON desktop_layout_recovery_sessions (lifecycle, started_at_utc DESC);
        ";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureV5Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        DropColumnIfExists(connection, transaction,
            "desktop_layout_recovery_sessions", "requires_manual_recovery");
        DropColumnIfExists(connection, transaction,
            "desktop_layout_recovery_sessions", "is_position_uncertain");
    }

    private static void EnsureV6Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_classification_rules (
                rule_id            TEXT PRIMARY KEY NOT NULL,
                name               TEXT NOT NULL CHECK(TRIM(name) != ''),
                stable_id          TEXT NULL,
                file_extension     TEXT NULL,
                file_name_pattern  TEXT NULL,
                item_kind          INTEGER NULL CHECK(item_kind IS NULL OR item_kind IN (0,1,2,3,4)),
                category           TEXT NOT NULL CHECK(TRIM(category) != ''),
                target_template_id TEXT NULL,
                target_zone_id     TEXT NULL,
                authority          INTEGER NOT NULL CHECK(authority IN (0,1)),
                provenance         INTEGER NOT NULL CHECK(provenance IN (0,1,2,3)),
                priority           INTEGER NOT NULL CHECK(priority BETWEEN -10000 AND 10000),
                is_enabled         INTEGER NOT NULL CHECK(is_enabled IN (0,1)),
                CHECK(stable_id IS NOT NULL OR file_extension IS NOT NULL OR
                      file_name_pattern IS NOT NULL OR item_kind IS NOT NULL),
                CHECK(file_extension IS NULL OR
                      (LENGTH(file_extension) > 1 AND SUBSTR(file_extension, 1, 1) = '.')),
                CHECK(file_name_pattern IS NULL OR
                      (TRIM(file_name_pattern) != '' AND LENGTH(file_name_pattern) <= 256)),
                CHECK((target_template_id IS NULL AND target_zone_id IS NULL) OR
                      (target_template_id IS NOT NULL AND target_zone_id IS NOT NULL))
            );

            CREATE INDEX IF NOT EXISTS idx_classification_rules_enabled_authority_priority
                ON desktop_classification_rules
                    (is_enabled DESC, authority ASC, priority DESC, rule_id ASC);

            CREATE TABLE IF NOT EXISTS desktop_user_category_zone_preferences (
                preference_id    TEXT PRIMARY KEY NOT NULL,
                template_id      TEXT NOT NULL,
                category         TEXT NOT NULL COLLATE NOCASE CHECK(TRIM(category) != ''),
                preferred_zone_id TEXT NOT NULL,
                confidence       REAL NOT NULL CHECK(confidence >= 0.0 AND confidence <= 1.0),
                evidence_count   INTEGER NOT NULL CHECK(evidence_count > 0),
                confirmed_at_utc TEXT NOT NULL,
                FOREIGN KEY (template_id)
                    REFERENCES desktop_layout_templates(template_id)
                    ON DELETE CASCADE,
                UNIQUE(template_id, category)
            );

            CREATE INDEX IF NOT EXISTS idx_user_preferences_template_category
                ON desktop_user_category_zone_preferences (template_id, category COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS desktop_classification_evidence (
                evidence_id        TEXT PRIMARY KEY NOT NULL,
                observed_at_utc    TEXT NOT NULL,
                template_id        TEXT NULL,
                stable_id          TEXT NOT NULL CHECK(stable_id != ''),
                display_name       TEXT NOT NULL CHECK(TRIM(display_name) != ''),
                file_extension     TEXT NULL,
                item_kind          INTEGER NOT NULL CHECK(item_kind IN (0,1,2,3,4)),
                proposed_category  TEXT NOT NULL CHECK(TRIM(proposed_category) != ''),
                proposed_zone_id   TEXT NULL,
                proposed_source    INTEGER NOT NULL CHECK(proposed_source IN (0,1,2,3)),
                feedback_kind      INTEGER NOT NULL CHECK(feedback_kind IN (0,1,2)),
                final_category     TEXT NULL,
                final_zone_id      TEXT NULL,
                CHECK(file_extension IS NULL OR
                      (LENGTH(file_extension) > 1 AND SUBSTR(file_extension, 1, 1) = '.')),
                CHECK((proposed_zone_id IS NULL AND final_zone_id IS NULL) OR template_id IS NOT NULL),
                CHECK(
                    (feedback_kind IN (0,1) AND final_category IS NOT NULL AND TRIM(final_category) != '')
                    OR
                    (feedback_kind = 2 AND final_category IS NULL AND final_zone_id IS NULL)
                )
            );

            CREATE INDEX IF NOT EXISTS idx_classification_evidence_observed_desc
                ON desktop_classification_evidence (observed_at_utc DESC, evidence_id ASC);
        ";
        cmd.ExecuteNonQuery();
    }

    private static void DropColumnIfExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        if (!ColumnExists(connection, transaction, tableName, columnName))
            return;

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"ALTER TABLE {tableName} DROP COLUMN {columnName};";
        cmd.ExecuteNonQuery();
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
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
