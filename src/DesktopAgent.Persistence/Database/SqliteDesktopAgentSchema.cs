using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.Database;

/// <summary>
/// 共享数据库 Schema 管理器。所有 Store 的 InitializeAsync 都必须调用此初始化器。
/// 统一管理 user_version，不得由单个 Store 各自设置 PRAGMA user_version。
/// </summary>
internal static class SqliteDesktopAgentSchema
{
    internal const int CurrentVersion = 13;

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
            EnsureV7Schema(connection, tx);
            EnsureV8Schema(connection, tx);
            EnsureV9Schema(connection, tx);
            EnsureV10Schema(connection, tx);
            EnsureV11Schema(connection, tx);
            EnsureV12Schema(connection, tx);
            EnsureV13Schema(connection, tx);

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

    private static void EnsureV7Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_pending_actions (
                pending_action_id    TEXT PRIMARY KEY NOT NULL,
                created_at_utc       TEXT NOT NULL,
                template_id          TEXT NULL,
                stable_id            TEXT NOT NULL CHECK(stable_id != ''),
                display_name         TEXT NOT NULL CHECK(TRIM(display_name) != ''),
                file_extension       TEXT NULL,
                item_kind            INTEGER NOT NULL CHECK(item_kind IN (0,1,2,3,4)),
                is_system_item       INTEGER NOT NULL CHECK(is_system_item IN (0,1)),
                is_file_system_item  INTEGER NOT NULL CHECK(is_file_system_item IN (0,1)),
                proposed_category    TEXT NULL,
                proposed_zone_id     TEXT NULL,
                proposed_confidence  REAL NULL,
                proposed_source      INTEGER NULL,
                explanation          TEXT NULL,
                authorization_basis  INTEGER NOT NULL CHECK(authorization_basis IN (0,1,2,3,4)),
                queue_reason         INTEGER NOT NULL CHECK(queue_reason IN (10,11,20,21,22,23)),
                CHECK(file_extension IS NULL OR
                      (LENGTH(file_extension) > 1 AND SUBSTR(file_extension, 1, 1) = '.')),
                CHECK(proposed_zone_id IS NULL OR template_id IS NOT NULL),
                CHECK(
                    (proposed_category IS NULL AND proposed_zone_id IS NULL AND
                     proposed_confidence IS NULL AND proposed_source IS NULL AND explanation IS NULL)
                    OR
                    (proposed_category IS NOT NULL AND TRIM(proposed_category) != '' AND
                     proposed_confidence IS NOT NULL AND
                     proposed_confidence >= 0.0 AND proposed_confidence <= 1.0 AND
                     proposed_source IS NOT NULL AND proposed_source IN (0,1,2,3) AND
                     explanation IS NOT NULL AND TRIM(explanation) != '')
                )
            );

            CREATE INDEX IF NOT EXISTS idx_pending_actions_created_at_utc
                ON desktop_pending_actions (created_at_utc ASC, pending_action_id ASC);
        ";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureV8Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_semantic_understandings (
                understanding_id          TEXT PRIMARY KEY NOT NULL,
                created_at_utc            TEXT NOT NULL,
                source_observation_id     TEXT NOT NULL CHECK(TRIM(source_observation_id) != ''),
                source_state_fingerprint  TEXT NOT NULL CHECK(TRIM(source_state_fingerprint) != ''),
                source_item_count         INTEGER NOT NULL CHECK(source_item_count > 0),
                payload_format_version    INTEGER NOT NULL CHECK(payload_format_version > 0),
                payload_json              TEXT NOT NULL CHECK(TRIM(payload_json) != '')
            );

            CREATE INDEX IF NOT EXISTS idx_semantic_understandings_created_desc
                ON desktop_semantic_understandings
                    (created_at_utc DESC, understanding_id DESC);

            CREATE TABLE IF NOT EXISTS workspace_blueprints (
                blueprint_id       TEXT PRIMARY KEY NOT NULL,
                name               TEXT NOT NULL CHECK(TRIM(name) != ''),
                created_at_utc     TEXT NOT NULL,
                updated_at_utc     TEXT NOT NULL,
                current_revision   INTEGER NOT NULL CHECK(current_revision > 0),
                is_archived        INTEGER NOT NULL CHECK(is_archived IN (0,1)),
                CHECK(updated_at_utc >= created_at_utc)
            );

            CREATE TABLE IF NOT EXISTS workspace_blueprint_revisions (
                blueprint_id            TEXT NOT NULL,
                revision                INTEGER NOT NULL CHECK(revision > 0),
                parent_revision         INTEGER NULL CHECK(parent_revision IS NULL OR parent_revision > 0),
                created_at_utc          TEXT NOT NULL,
                source_understanding_id TEXT NULL,
                source_user_intent      TEXT NULL,
                design_summary          TEXT NOT NULL CHECK(TRIM(design_summary) != ''),
                blueprint_schema_version INTEGER NOT NULL CHECK(blueprint_schema_version > 0),
                payload_format_version  INTEGER NOT NULL CHECK(payload_format_version > 0),
                payload_json            TEXT NOT NULL CHECK(TRIM(payload_json) != ''),
                PRIMARY KEY (blueprint_id, revision),
                FOREIGN KEY (blueprint_id)
                    REFERENCES workspace_blueprints(blueprint_id)
                    ON DELETE CASCADE,
                FOREIGN KEY (source_understanding_id)
                    REFERENCES desktop_semantic_understandings(understanding_id)
                    ON DELETE SET NULL,
                CHECK(parent_revision IS NULL OR parent_revision < revision),
                CHECK(source_user_intent IS NULL OR TRIM(source_user_intent) != '')
            );

            CREATE INDEX IF NOT EXISTS idx_workspace_blueprints_library
                ON workspace_blueprints
                    (is_archived ASC, updated_at_utc DESC, name COLLATE BINARY ASC, blueprint_id ASC);

            CREATE INDEX IF NOT EXISTS idx_workspace_blueprint_revisions_created
                ON workspace_blueprint_revisions
                    (blueprint_id ASC, revision DESC);
        ";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureV9Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS workspace_execution_journals (
                session_id                  TEXT PRIMARY KEY NOT NULL,
                safety_snapshot_id          TEXT NOT NULL,
                blueprint_id                TEXT NOT NULL CHECK(TRIM(blueprint_id) != ''),
                blueprint_revision          INTEGER NOT NULL CHECK(blueprint_revision > 0),
                certificate_fingerprint     TEXT NOT NULL CHECK(TRIM(certificate_fingerprint) != ''),
                started_at_utc              TEXT NOT NULL,
                lifecycle                   INTEGER NOT NULL CHECK(lifecycle IN (0,1,2,3,4,5)),
                preview_fingerprint         TEXT NOT NULL CHECK(TRIM(preview_fingerprint) != ''),
                exact_plan_fingerprint      TEXT NOT NULL CHECK(TRIM(exact_plan_fingerprint) != ''),
                execution_plan_fingerprint  TEXT NOT NULL CHECK(TRIM(execution_plan_fingerprint) != ''),
                execution_plan_json         TEXT NOT NULL CHECK(TRIM(execution_plan_json) != ''),
                plan_payload_format_version INTEGER NOT NULL CHECK(plan_payload_format_version > 0),
                completed_forward_step_indexes_json         TEXT NOT NULL,
                completed_rollback_source_step_indexes_json TEXT NOT NULL,
                last_failed_step_index       INTEGER NULL CHECK(last_failed_step_index IS NULL OR last_failed_step_index >= 0),
                forward_failure_type         TEXT NULL,
                forward_failure_message      TEXT NULL,
                recovery_failure_type        TEXT NULL,
                recovery_failure_message     TEXT NULL,
                revision                     INTEGER NOT NULL CHECK(revision > 0),
                updated_at_utc               TEXT NOT NULL,
                forward_attempt_count        INTEGER NOT NULL CHECK(forward_attempt_count >= 0),
                rollback_attempt_count       INTEGER NOT NULL CHECK(rollback_attempt_count >= 0),
                FOREIGN KEY (safety_snapshot_id)
                    REFERENCES desktop_layout_snapshots(snapshot_id)
                    ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS workspace_execution_attempts (
                session_id                     TEXT NOT NULL,
                attempt_index                  INTEGER NOT NULL CHECK(attempt_index >= 0),
                source_step_index              INTEGER NOT NULL CHECK(source_step_index >= 0),
                phase                          INTEGER NOT NULL CHECK(phase IN (0,1)),
                expected_stable_id             TEXT NOT NULL CHECK(expected_stable_id != ''),
                expected_previous_display_ref  TEXT NOT NULL CHECK(TRIM(expected_previous_display_ref) != ''),
                expected_previous_x            INTEGER NOT NULL,
                expected_previous_y            INTEGER NOT NULL,
                requested_display_ref          TEXT NOT NULL CHECK(TRIM(requested_display_ref) != ''),
                requested_x                    INTEGER NOT NULL,
                requested_y                    INTEGER NOT NULL,
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
                FOREIGN KEY (session_id)
                    REFERENCES workspace_execution_journals(session_id)
                    ON DELETE CASCADE,
                CHECK((reported_previous_x IS NULL) = (reported_previous_y IS NULL)),
                CHECK((actual_x IS NULL) = (actual_y IS NULL)),
                CHECK(has_trusted_actual_position = 0 OR actual_x IS NOT NULL),
                CHECK(is_exact = 0 OR (has_trusted_actual_position = 1 AND actual_x = requested_x AND actual_y = requested_y))
            );

            CREATE INDEX IF NOT EXISTS idx_workspace_execution_journals_lifecycle
                ON workspace_execution_journals (lifecycle ASC, started_at_utc ASC, session_id ASC);
        ";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureV10Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS explorer_grid_measurements (
                observation_id        TEXT PRIMARY KEY NOT NULL,
                device_name           TEXT NOT NULL CHECK(TRIM(device_name) != ''),
                topology_fingerprint  TEXT NOT NULL CHECK(TRIM(topology_fingerprint) != ''),
                evidence_source       INTEGER NOT NULL CHECK(evidence_source IN (0,1)),
                spacing_x             INTEGER NOT NULL CHECK(spacing_x > 0),
                spacing_y             INTEGER NOT NULL CHECK(spacing_y > 0),
                phase_x               INTEGER NOT NULL CHECK(phase_x >= 0 AND phase_x < spacing_x),
                phase_y               INTEGER NOT NULL CHECK(phase_y >= 0 AND phase_y < spacing_y),
                sample_count          INTEGER NOT NULL CHECK(sample_count > 0),
                consensus_count       INTEGER NOT NULL CHECK(consensus_count >= 0 AND consensus_count <= sample_count),
                spacing_constant      INTEGER NOT NULL CHECK(spacing_constant IN (0,1)),
                phase_constant        INTEGER NOT NULL CHECK(phase_constant IN (0,1)),
                observed_at_utc       TEXT NOT NULL CHECK(TRIM(observed_at_utc) != '' AND observed_at_utc LIKE '%+00:00'),
                recorded_at_utc       TEXT NOT NULL CHECK(TRIM(recorded_at_utc) != '' AND recorded_at_utc LIKE '%+00:00'),
                CHECK(evidence_source = 0 OR sample_count >= 3),
                CHECK(evidence_source = 0 OR consensus_count = sample_count)
            );

            CREATE INDEX IF NOT EXISTS idx_grid_measurements_device_observed_desc
                ON explorer_grid_measurements (device_name ASC, observed_at_utc DESC, recorded_at_utc DESC);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// V11 只做一件事：把 desktop_pending_actions.queue_reason 的允许列表
    /// 从 (10,11,20,21,22,23) 宽化为 (10,11,20,21,22,23,24)，
    /// 以接纳 DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured = 24。
    /// SQLite 无法 ALTER 已有的 CHECK 约束，因此对已存在的旧表只能在当前事务内整表重建。
    /// 允许列表仍然保持枚举级别的精确约束，不使用宽范围。
    /// 该方法在每次 Initialize 时都会执行，因此必须自带“是否需要重建”的判断以保持幂等。
    /// </summary>
    private static void EnsureV11Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!PendingActionsTableNeedsV11Rebuild(connection, transaction))
            return;

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // 说明：
        // 1. 列定义与 V7 完全一致，仅 queue_reason 的允许列表追加 24。
        // 2. 不写 IF NOT EXISTS：若上一次重建的临时表残留（正常不应发生），
        //    宁可显式失败，也不能把残留表当成新表重命名而丢掉真实数据。
        // 3. desktop_pending_actions 没有任何外键引用它，因此不需要处理外键关系，
        //    也不需要在事务内改写 PRAGMA foreign_keys。
        cmd.CommandText = @"
            CREATE TABLE desktop_pending_actions_v11 (
                pending_action_id    TEXT PRIMARY KEY NOT NULL,
                created_at_utc       TEXT NOT NULL,
                template_id          TEXT NULL,
                stable_id            TEXT NOT NULL CHECK(stable_id != ''),
                display_name         TEXT NOT NULL CHECK(TRIM(display_name) != ''),
                file_extension       TEXT NULL,
                item_kind            INTEGER NOT NULL CHECK(item_kind IN (0,1,2,3,4)),
                is_system_item       INTEGER NOT NULL CHECK(is_system_item IN (0,1)),
                is_file_system_item  INTEGER NOT NULL CHECK(is_file_system_item IN (0,1)),
                proposed_category    TEXT NULL,
                proposed_zone_id     TEXT NULL,
                proposed_confidence  REAL NULL,
                proposed_source      INTEGER NULL,
                explanation          TEXT NULL,
                authorization_basis  INTEGER NOT NULL CHECK(authorization_basis IN (0,1,2,3,4)),
                queue_reason         INTEGER NOT NULL CHECK(queue_reason IN (10,11,20,21,22,23,24)),
                CHECK(file_extension IS NULL OR
                      (LENGTH(file_extension) > 1 AND SUBSTR(file_extension, 1, 1) = '.')),
                CHECK(proposed_zone_id IS NULL OR template_id IS NOT NULL),
                CHECK(
                    (proposed_category IS NULL AND proposed_zone_id IS NULL AND
                     proposed_confidence IS NULL AND proposed_source IS NULL AND explanation IS NULL)
                    OR
                    (proposed_category IS NOT NULL AND TRIM(proposed_category) != '' AND
                     proposed_confidence IS NOT NULL AND
                     proposed_confidence >= 0.0 AND proposed_confidence <= 1.0 AND
                     proposed_source IS NOT NULL AND proposed_source IN (0,1,2,3) AND
                     explanation IS NOT NULL AND TRIM(explanation) != '')
                )
            );

            INSERT INTO desktop_pending_actions_v11
                (pending_action_id, created_at_utc, template_id,
                 stable_id, display_name, file_extension, item_kind,
                 is_system_item, is_file_system_item,
                 proposed_category, proposed_zone_id, proposed_confidence,
                 proposed_source, explanation,
                 authorization_basis, queue_reason)
            SELECT
                 pending_action_id, created_at_utc, template_id,
                 stable_id, display_name, file_extension, item_kind,
                 is_system_item, is_file_system_item,
                 proposed_category, proposed_zone_id, proposed_confidence,
                 proposed_source, explanation,
                 authorization_basis, queue_reason
            FROM desktop_pending_actions;

            DROP TABLE desktop_pending_actions;

            ALTER TABLE desktop_pending_actions_v11 RENAME TO desktop_pending_actions;

            CREATE INDEX IF NOT EXISTS idx_pending_actions_created_at_utc
                ON desktop_pending_actions (created_at_utc ASC, pending_action_id ASC);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// V12 只新增 explorer_grid_display_authorizations 这一张表，
    /// 用于记录"用户对某块显示器在当前显示拓扑下的站定授权"。
    ///
    /// 授权特意与 V10 的网格事实（explorer_grid_measurements）分表存放：
    /// 网格事实回答"图标会落在哪"，那是证据；授权回答"人是否已经同意它自己动手"，
    /// 那是决定。合在一张表里会让"实测过"看起来像"已经同意过"。
    ///
    /// 读取按 device_name + topology_fingerprint 进行，因此换屏、改分辨率、
    /// 移动任务栏之后，绑定旧指纹的授权自然不再被选出，无需改写任何历史记录。
    /// 与其它版本方法一样，本方法每次 Initialize 都会执行，必须保持幂等。
    /// </summary>
    private static void EnsureV12Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS explorer_grid_display_authorizations (
                authorization_id     TEXT PRIMARY KEY NOT NULL,
                device_name          TEXT NOT NULL CHECK(TRIM(device_name) != ''),
                topology_fingerprint TEXT NOT NULL CHECK(TRIM(topology_fingerprint) != ''),
                state                INTEGER NOT NULL CHECK(state IN (0,1)),
                authorized_at_utc    TEXT NOT NULL CHECK(TRIM(authorized_at_utc) != '' AND authorized_at_utc LIKE '%+00:00'),
                recorded_at_utc      TEXT NOT NULL CHECK(TRIM(recorded_at_utc) != '' AND recorded_at_utc LIKE '%+00:00')
            );

            CREATE INDEX IF NOT EXISTS idx_grid_authorizations_device_recorded_desc
                ON explorer_grid_display_authorizations (device_name ASC, recorded_at_utc DESC);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// V13 给 desktop_layout_recovery_sessions 增加 origin：这一笔写盘是「用户的一次整理」，
    /// 还是「撤销上次整理时按检查点还原」。
    ///
    /// 为什么必须落库：撤销入口的候选是「最近一次成功收尾的写盘」。若不区分来路，
    /// 撤销自己刚写下的那一笔立刻变成下一次撤销的对象——用户点第二次「撤销」就会把桌面
    /// 重新整理一遍（2026-10-14 的真机演练确实发生了）。来路只有写盘时知道，
    /// 事后靠数据反推不可靠，所以在写入行上留一个字段。
    ///
    /// 默认 'organize'：既有历史行都是整理写入，旧调用不传来源时也按整理处理。
    /// 与其它版本方法一样，本方法每次 Initialize 都会执行，必须保持幂等。
    /// </summary>
    private static void EnsureV13Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (ColumnExists(connection, transaction, "desktop_layout_recovery_sessions", "origin"))
            return;

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            ALTER TABLE desktop_layout_recovery_sessions
                ADD COLUMN origin TEXT NOT NULL DEFAULT 'organize'
                CHECK(origin IN ('organize','checkpoint-restore'));
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 判断 desktop_pending_actions 是否仍停留在 V10 的 queue_reason 约束上。
    /// 表尚不存在（例如空库，由 EnsureV7Schema 在本次调用中先行创建）时返回 false。
    /// 已经包含 24 的宽化约束时返回 false，从而保证重复 Initialize 不会反复重建。
    /// 比较前去除空白，兼容 CREATE TABLE 文本的不同排版。
    /// </summary>
    private static bool PendingActionsTableNeedsV11Rebuild(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'desktop_pending_actions';
        ";

        if (cmd.ExecuteScalar() is not string sql || string.IsNullOrEmpty(sql))
            return false;

        var normalized = new string(
            sql.Where(character => !char.IsWhiteSpace(character)).ToArray());

        return !normalized.Contains(
            "queue_reasonIN(10,11,20,21,22,23,24)",
            StringComparison.Ordinal);
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
