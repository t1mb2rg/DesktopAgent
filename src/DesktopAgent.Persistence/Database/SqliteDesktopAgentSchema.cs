using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.Database;

/// <summary>
/// 共享数据库 Schema 管理器。所有 Store 的 InitializeAsync 都必须调用此初始化器。
/// 统一管理 user_version：v1=快照表，v2=模板表。不得由单个 Store 各自设置 PRAGMA user_version。
/// </summary>
internal static class SqliteDesktopAgentSchema
{
    internal const int CurrentVersion = 3;

    /// <summary>
    /// 初始化或升级数据库。幂等。始终在事务内确保所有表和索引存在。
    /// v2 版本标记仍会重新验证完整 Schema。
    /// </summary>
    public static void Initialize(SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 读取当前版本
        using var readVersion = connection.CreateCommand();
        readVersion.CommandText = "PRAGMA user_version;";
        var version = (int)(long)readVersion.ExecuteScalar()!;

        // 未来版本拒绝（在任何 DDL 前）
        if (version > CurrentVersion)
            throw new InvalidOperationException(
                $"数据库 user_version={version}，超过当前支持的最高版本 {CurrentVersion}。");

        // 始终开启事务，即使 version == CurrentVersion 也要确保完整 Schema
        using var tx = connection.BeginTransaction();

        try
        {
            // 确保 v1 Schema
            EnsureV1Schema(connection, tx);

            // 确保 v2 Schema
            EnsureV2Schema(connection, tx);

            // 确保 v3 Schema
            EnsureV3Schema(connection, tx);

            // 更新版本标记
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
            // 事务自动回滚，user_version 不变
            throw;
        }
    }

    private static void EnsureV1Schema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_layout_snapshots (
                snapshot_id              TEXT PRIMARY KEY NOT NULL,
                created_at_utc           TEXT NOT NULL,
                target_monitor_device_name TEXT NULL,
                connection_method        INTEGER NOT NULL,
                format_version           INTEGER NOT NULL CHECK(format_version > 0),
                icon_count               INTEGER NOT NULL CHECK(icon_count >= 0)
            );

            CREATE TABLE IF NOT EXISTS desktop_layout_snapshot_items (
                snapshot_id        TEXT NOT NULL,
                ordinal            INTEGER NOT NULL,
                stable_id          TEXT NOT NULL,
                display_name       TEXT NOT NULL,
                parsing_name       TEXT NULL,
                item_kind          INTEGER NOT NULL,
                x                  INTEGER NOT NULL,
                y                  INTEGER NOT NULL,
                monitor_device_name TEXT NULL,
                is_system_item     INTEGER NOT NULL CHECK(is_system_item IN (0, 1)),
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
                template_id     TEXT NOT NULL,
                ordinal         INTEGER NOT NULL CHECK(ordinal >= 0),
                zone_id         TEXT NOT NULL,
                name            TEXT NOT NULL CHECK(TRIM(name) != ''),
                left_value      INTEGER NOT NULL CHECK(left_value >= 0),
                top_value       INTEGER NOT NULL CHECK(top_value >= 0),
                right_value     INTEGER NOT NULL CHECK(right_value <= 10000),
                bottom_value    INTEGER NOT NULL CHECK(bottom_value <= 10000),
                sort_order      INTEGER NOT NULL CHECK(sort_order >= 0),
                placement_flow  INTEGER NOT NULL CHECK(placement_flow IN (0, 1)),
                max_item_count  INTEGER NULL CHECK(max_item_count IS NULL OR max_item_count > 0),
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
        // DesktopLayoutIconRuleKind enum values:
        //   Exclude = 0, Pin = 1, ZoneOverride = 2
        // CHECK 约束使用枚举整数值，保证数据库只接受合法 Kind。
        // Exclude(0) / Pin(1) 时 target_zone_id 必须为 NULL。
        // ZoneOverride(2) 时 target_zone_id 必须非 NULL。
        // stable_id 不允许空字符串。
        // 不做 TRIM 检查 — StableId 语义明确不 Trim。
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_layout_template_icon_rules (
                template_id     TEXT NOT NULL,
                stable_id       TEXT NOT NULL,
                rule_kind       INTEGER NOT NULL
                    CHECK(rule_kind IN (0, 1, 2)),
                target_zone_id  TEXT NULL,
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
}
