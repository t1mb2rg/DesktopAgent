using DesktopAgent.Persistence.Automation;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.DesktopLayouts.Templates.Rules;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Persistence.Intelligence;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.Persistence;

public sealed class SqliteDesktopAgentSchemaTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDesktopAgentSchemaTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DesktopAgent.Tests.Schema.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // 尽力清理测试目录。
        }
    }

    [Fact]
    public async Task FreshDatabase_CreatesCompleteV13Schema()
    {
        await new SqliteDesktopLayoutSnapshotStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));

        // V13：恢复会话行必须记住「这次写盘是谁发起的」，否则撤销入口分不清整理和撤销自己写下的还原。
        Assert.Contains("origin", ReadColumnNames(_dbPath, "desktop_layout_recovery_sessions"));

        var expectedTables = new[]
        {
            "desktop_layout_snapshots",
            "desktop_layout_snapshot_items",
            "desktop_layout_templates",
            "desktop_layout_template_zones",
            "desktop_layout_template_icon_rules",
            "desktop_layout_recovery_sessions",
            "desktop_layout_recovery_attempts",
            "desktop_classification_rules",
            "desktop_user_category_zone_preferences",
            "desktop_classification_evidence",
            "desktop_pending_actions",
            "desktop_semantic_understandings",
            "workspace_blueprints",
            "workspace_blueprint_revisions",
            "workspace_execution_journals",
            "workspace_execution_attempts",
            "explorer_grid_measurements",
            "explorer_grid_display_authorizations",
        };
        foreach (var table in expectedTables)
            Assert.True(TableExists(_dbPath, table), $"缺少表 {table}");

        var expectedIndexes = new[]
        {
            "idx_snapshots_created_at_utc_desc",
            "idx_templates_name_id_asc",
            "idx_recovery_sessions_lifecycle",
            "idx_classification_rules_enabled_authority_priority",
            "idx_user_preferences_template_category",
            "idx_classification_evidence_observed_desc",
            "idx_pending_actions_created_at_utc",
            "idx_semantic_understandings_created_desc",
            "idx_workspace_blueprints_library",
            "idx_workspace_blueprint_revisions_created",
            "idx_workspace_execution_journals_lifecycle",
            "idx_grid_measurements_device_observed_desc",
            "idx_grid_authorizations_device_recorded_desc",
        };
        foreach (var index in expectedIndexes)
            Assert.True(IndexExists(_dbPath, index), $"缺少索引 {index}");
    }

    [Fact]
    public async Task EveryStore_UsesSharedV13Initializer_AndRepeatedInitIsIdempotent()
    {
        var paths = Enumerable.Range(0, 9)
            .Select(index => Path.Combine(_tempDir, $"store-{index}.db"))
            .ToArray();

        var snapshotStore = new SqliteDesktopLayoutSnapshotStore(paths[0]);
        await snapshotStore.InitializeAsync();
        await snapshotStore.InitializeAsync();

        var templateStore = new SqliteDesktopLayoutTemplateStore(paths[1]);
        await templateStore.InitializeAsync();
        await templateStore.InitializeAsync();

        var iconRuleStore = new SqliteDesktopLayoutIconRuleStore(paths[2]);
        await iconRuleStore.InitializeAsync();
        await iconRuleStore.InitializeAsync();

        var classificationRuleStore = new SqliteDesktopClassificationRuleStore(paths[3]);
        await classificationRuleStore.InitializeAsync();
        await classificationRuleStore.InitializeAsync();

        var profileStore = new SqliteDesktopUserProfileStore(paths[4]);
        await profileStore.InitializeAsync();
        await profileStore.InitializeAsync();

        var pendingStore = new SqliteDesktopPendingActionStore(paths[5]);
        await pendingStore.InitializeAsync();
        await pendingStore.InitializeAsync();

        var workspaceJournalStore = new SqliteDesktopWorkspaceExecutionJournalStore(paths[6]);
        await workspaceJournalStore.InitializeAsync();
        await workspaceJournalStore.InitializeAsync();

        var gridMeasurementStore = new SqliteExplorerGridMeasurementStore(paths[7]);
        await gridMeasurementStore.InitializeAsync();
        await gridMeasurementStore.InitializeAsync();

        var displayAuthorizationStore = new SqliteExplorerGridDisplayAuthorizationStore(paths[8]);
        await displayAuthorizationStore.InitializeAsync();
        await displayAuthorizationStore.InitializeAsync();

        foreach (var path in paths)
        {
            Assert.Equal(13, ReadUserVersion(path));
            Assert.True(TableExists(path, "desktop_classification_rules"));
            Assert.True(TableExists(path, "desktop_classification_evidence"));
            Assert.True(TableExists(path, "desktop_pending_actions"));
            Assert.True(TableExists(path, "desktop_semantic_understandings"));
            Assert.True(TableExists(path, "workspace_blueprints"));
            Assert.True(TableExists(path, "workspace_blueprint_revisions"));
            Assert.True(TableExists(path, "workspace_execution_journals"));
            Assert.True(TableExists(path, "workspace_execution_attempts"));
            Assert.True(TableExists(path, "explorer_grid_measurements"));
            Assert.True(TableExists(path, "explorer_grid_display_authorizations"));
        }
    }

    [Fact]
    public async Task V1Upgrade_PreservesSnapshotParentAndItems()
    {
        using (var connection = OpenRawConnection(_dbPath))
        {
            CreateV1Tables(connection);
            using var command = connection.CreateCommand();
            command.CommandText = @"
                PRAGMA user_version = 1;
                INSERT INTO desktop_layout_snapshots VALUES
                    ('00000000-0000-0000-0000-000000000001',
                     '2026-07-01T12:30:00.0000000+00:00', 'DISPLAY1', 1, 1, 1);
                INSERT INTO desktop_layout_snapshot_items VALUES
                    ('00000000-0000-0000-0000-000000000001', 0,
                     'desktop:file', 'file.txt', 'C:\Users\Test\Desktop\file.txt',
                     0, 150, 250, 'DISPLAY1', 0, 1);
            ";
            command.ExecuteNonQuery();
        }

        await new SqliteDesktopLayoutSnapshotStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        using var upgraded = OpenRawConnection(_dbPath);
        using var select = upgraded.CreateCommand();
        select.CommandText = @"
            SELECT s.icon_count, i.stable_id, i.x, i.y
            FROM desktop_layout_snapshots s
            JOIN desktop_layout_snapshot_items i ON i.snapshot_id = s.snapshot_id
            WHERE s.snapshot_id = '00000000-0000-0000-0000-000000000001';
        ";
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("desktop:file", reader.GetString(1));
        Assert.Equal(150, reader.GetInt32(2));
        Assert.Equal(250, reader.GetInt32(3));
    }

    [Fact]
    public async Task V2Upgrade_PreservesTemplateAndZoneData()
    {
        using (var connection = OpenRawConnection(_dbPath))
        {
            CreateV1Tables(connection);
            CreateV2Tables(connection);
            using var command = connection.CreateCommand();
            command.CommandText = @"
                PRAGMA user_version = 2;
                INSERT INTO desktop_layout_templates
                    (template_id, name, version, target_display_binding_kind,
                     target_display_device_name, zone_count)
                VALUES
                    ('00000000-0000-0000-0000-000000000021',
                     'V2 Template', 2, 1, NULL, 1);
                INSERT INTO desktop_layout_template_zones
                    (template_id, ordinal, zone_id, name,
                     left_value, top_value, right_value, bottom_value,
                     sort_order, placement_flow, max_item_count)
                VALUES
                    ('00000000-0000-0000-0000-000000000021', 0,
                     '00000000-0000-0000-0000-000000000022', 'Main',
                     0, 0, 10000, 10000, 0, 0, NULL);
            ";
            command.ExecuteNonQuery();
        }

        await new SqliteDesktopLayoutTemplateStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        using var upgraded = OpenRawConnection(_dbPath);
        using var select = upgraded.CreateCommand();
        select.CommandText = @"
            SELECT t.name, t.version, z.zone_id, z.name
            FROM desktop_layout_templates t
            JOIN desktop_layout_template_zones z ON z.template_id = t.template_id
            WHERE t.template_id = '00000000-0000-0000-0000-000000000021';
        ";
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("V2 Template", reader.GetString(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal("00000000-0000-0000-0000-000000000022", reader.GetString(2));
        Assert.Equal("Main", reader.GetString(3));
    }

    [Fact]
    public async Task V3Upgrade_PreservesIconRuleData()
    {
        using (var connection = OpenRawConnection(_dbPath))
        {
            CreateV1Tables(connection);
            CreateV2Tables(connection);
            CreateV3Table(connection);
            using var command = connection.CreateCommand();
            command.CommandText = @"
                PRAGMA user_version = 3;
                INSERT INTO desktop_layout_templates
                    (template_id, name, version, target_display_binding_kind,
                     target_display_device_name, zone_count)
                VALUES
                    ('00000000-0000-0000-0000-000000000031',
                     'V3 Template', 1, 1, NULL, 1);
                INSERT INTO desktop_layout_template_zones
                    (template_id, ordinal, zone_id, name,
                     left_value, top_value, right_value, bottom_value,
                     sort_order, placement_flow, max_item_count)
                VALUES
                    ('00000000-0000-0000-0000-000000000031', 0,
                     '00000000-0000-0000-0000-000000000032', 'Main',
                     0, 0, 10000, 10000, 0, 0, NULL);
                INSERT INTO desktop_layout_template_icon_rules
                    (template_id, stable_id, rule_kind, target_zone_id)
                VALUES
                    ('00000000-0000-0000-0000-000000000031',
                     'desktop:item', 2,
                     '00000000-0000-0000-0000-000000000032');
            ";
            command.ExecuteNonQuery();
        }

        await new SqliteDesktopLayoutIconRuleStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        using var upgraded = OpenRawConnection(_dbPath);
        using var select = upgraded.CreateCommand();
        select.CommandText = @"
            SELECT stable_id, rule_kind, target_zone_id
            FROM desktop_layout_template_icon_rules
            WHERE template_id = '00000000-0000-0000-0000-000000000031';
        ";
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("desktop:item", reader.GetString(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal("00000000-0000-0000-0000-000000000032", reader.GetString(2));
    }

    [Fact]
    public async Task V5Upgrade_CreatesM4AndM5TablesAndPreservesExistingData()
    {
        var bootstrap = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        await bootstrap.InitializeAsync();

        using (var connection = OpenRawConnection(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                INSERT INTO desktop_layout_snapshots
                    (snapshot_id, created_at_utc, target_monitor_device_name,
                     connection_method, format_version, icon_count)
                VALUES
                    ('00000000-0000-0000-0000-000000000051',
                     '2026-08-10T00:00:00.0000000+00:00', NULL, 1, 1, 0);
                DROP TABLE workspace_execution_attempts;
                DROP TABLE workspace_execution_journals;
                DROP TABLE workspace_blueprint_revisions;
                DROP TABLE workspace_blueprints;
                DROP TABLE desktop_semantic_understandings;
                DROP TABLE desktop_pending_actions;
                DROP TABLE desktop_classification_evidence;
                DROP TABLE desktop_user_category_zone_preferences;
                DROP TABLE desktop_classification_rules;
                PRAGMA user_version = 5;
            ";
            command.ExecuteNonQuery();
        }

        Assert.Equal(5, ReadUserVersion(_dbPath));
        Assert.False(TableExists(_dbPath, "desktop_classification_rules"));
        Assert.False(TableExists(_dbPath, "desktop_pending_actions"));
        Assert.False(TableExists(_dbPath, "desktop_semantic_understandings"));

        await new SqliteDesktopClassificationRuleStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.True(TableExists(_dbPath, "desktop_classification_rules"));
        Assert.True(TableExists(_dbPath, "desktop_user_category_zone_preferences"));
        Assert.True(TableExists(_dbPath, "desktop_classification_evidence"));
        Assert.True(TableExists(_dbPath, "desktop_pending_actions"));
        Assert.True(TableExists(_dbPath, "desktop_semantic_understandings"));
        Assert.True(TableExists(_dbPath, "workspace_blueprints"));
        Assert.True(TableExists(_dbPath, "workspace_blueprint_revisions"));
        Assert.True(TableExists(_dbPath, "workspace_execution_journals"));

        using var upgraded = OpenRawConnection(_dbPath);
        using var select = upgraded.CreateCommand();
        select.CommandText = @"
            SELECT COUNT(*)
            FROM desktop_layout_snapshots
            WHERE snapshot_id = '00000000-0000-0000-0000-000000000051';
        ";
        Assert.Equal(1L, (long)select.ExecuteScalar()!);
    }

    [Fact]
    public async Task V6Upgrade_CreatesPendingQueueWithoutRewritingM4Data()
    {
        var bootstrap = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        await bootstrap.InitializeAsync();

        using (var connection = OpenRawConnection(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                INSERT INTO desktop_classification_rules
                    (rule_id, name, stable_id, category,
                     target_template_id, target_zone_id,
                     authority, provenance, priority, is_enabled)
                VALUES
                    ('00000000-0000-0000-0000-000000000071',
                     'keep-me', 'desktop:keep', 'Course',
                     NULL, NULL, 0, 0, 0, 1);
                DROP TABLE workspace_execution_attempts;
                DROP TABLE workspace_execution_journals;
                DROP TABLE workspace_blueprint_revisions;
                DROP TABLE workspace_blueprints;
                DROP TABLE desktop_semantic_understandings;
                DROP TABLE desktop_pending_actions;
                PRAGMA user_version = 6;
            ";
            command.ExecuteNonQuery();
        }

        await new SqliteDesktopPendingActionStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.True(TableExists(_dbPath, "desktop_pending_actions"));
        Assert.True(TableExists(_dbPath, "desktop_semantic_understandings"));
        Assert.True(TableExists(_dbPath, "workspace_blueprints"));
        Assert.True(TableExists(_dbPath, "workspace_execution_journals"));

        using var upgraded = OpenRawConnection(_dbPath);
        using var select = upgraded.CreateCommand();
        select.CommandText = @"
            SELECT name
            FROM desktop_classification_rules
            WHERE rule_id = '00000000-0000-0000-0000-000000000071';
        ";
        Assert.Equal("keep-me", (string)select.ExecuteScalar()!);
    }

    [Fact]
    public async Task V7Upgrade_CreatesWorkspaceKnowledgeTablesWithoutRewritingExistingData()
    {
        var bootstrap = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        await bootstrap.InitializeAsync();

        using (var connection = OpenRawConnection(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                INSERT INTO desktop_layout_snapshots
                    (snapshot_id, created_at_utc, target_monitor_device_name,
                     connection_method, format_version, icon_count)
                VALUES
                    ('00000000-0000-0000-0000-000000000081',
                     '2026-08-28T04:00:00.0000000+00:00', NULL, 1, 1, 0);
                DROP TABLE workspace_execution_attempts;
                DROP TABLE workspace_execution_journals;
                DROP TABLE workspace_blueprint_revisions;
                DROP TABLE workspace_blueprints;
                DROP TABLE desktop_semantic_understandings;
                PRAGMA user_version = 7;
            ";
            command.ExecuteNonQuery();
        }

        Assert.Equal(7, ReadUserVersion(_dbPath));
        Assert.False(TableExists(_dbPath, "desktop_semantic_understandings"));
        Assert.False(TableExists(_dbPath, "workspace_blueprints"));

        await new SqliteDesktopLayoutSnapshotStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.True(TableExists(_dbPath, "desktop_semantic_understandings"));
        Assert.True(TableExists(_dbPath, "workspace_blueprints"));
        Assert.True(TableExists(_dbPath, "workspace_blueprint_revisions"));
        Assert.True(TableExists(_dbPath, "workspace_execution_journals"));

        using var upgraded = OpenRawConnection(_dbPath);
        using var select = upgraded.CreateCommand();
        select.CommandText = @"
            SELECT COUNT(*)
            FROM desktop_layout_snapshots
            WHERE snapshot_id = '00000000-0000-0000-0000-000000000081';
        ";
        Assert.Equal(1L, (long)select.ExecuteScalar()!);
    }

    [Fact]
    public async Task V8Upgrade_CreatesWorkspaceExecutionJournalWithoutRewritingKnowledgeData()
    {
        var bootstrap = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        await bootstrap.InitializeAsync();

        using (var connection = OpenRawConnection(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                INSERT INTO desktop_layout_snapshots
                    (snapshot_id, created_at_utc, target_monitor_device_name,
                     connection_method, format_version, icon_count)
                VALUES
                    ('00000000-0000-0000-0000-000000000091',
                     '2026-08-28T08:00:00.0000000+00:00', NULL, 1, 1, 0);
                INSERT INTO workspace_blueprints
                    (blueprint_id, name, created_at_utc, updated_at_utc, current_revision, is_archived)
                VALUES
                    ('00000000-0000-0000-0000-000000000092', 'keep-v8',
                     '2026-08-28T08:00:00.0000000+00:00',
                     '2026-08-28T08:00:00.0000000+00:00', 1, 0);
                DROP TABLE workspace_execution_attempts;
                DROP TABLE workspace_execution_journals;
                PRAGMA user_version = 8;
            ";
            command.ExecuteNonQuery();
        }

        Assert.Equal(8, ReadUserVersion(_dbPath));
        Assert.False(TableExists(_dbPath, "workspace_execution_journals"));

        await new SqliteDesktopLayoutSnapshotStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.True(TableExists(_dbPath, "workspace_execution_journals"));
        Assert.True(TableExists(_dbPath, "workspace_execution_attempts"));
        using var upgraded = OpenRawConnection(_dbPath);
        using var select = upgraded.CreateCommand();
        select.CommandText = @"
            SELECT name
            FROM workspace_blueprints
            WHERE blueprint_id = '00000000-0000-0000-0000-000000000092';
        ";
        Assert.Equal("keep-v8", (string)select.ExecuteScalar()!);
    }

    [Fact]
    public async Task V11Upgrade_RebuildsPendingQueueConstraintWithoutLosingColumnsOrIndex()
    {
        var bootstrap = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        await bootstrap.InitializeAsync();

        // pending 表置为“缺失”并把版本回退到 10：下一次 Initialize 会先走 EnsureV7Schema。
        // 用生成的 DDL 建出真正的 V10 表，再由 EnsureV11Schema 整表重建。
        // 因此这里走的是真实升级路径，而不是手写的 V10 表。
        using (var connection = OpenRawConnection(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                DROP TABLE desktop_pending_actions;
                PRAGMA user_version = 10;
            ";
            command.ExecuteNonQuery();
        }

        Assert.Equal(10, ReadUserVersion(_dbPath));
        Assert.False(TableExists(_dbPath, "desktop_pending_actions"));

        await new SqliteDesktopPendingActionStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.True(TableExists(_dbPath, "desktop_pending_actions"));
        Assert.False(TableExists(_dbPath, "desktop_pending_actions_v11"));
        Assert.True(IndexExists(_dbPath, "idx_pending_actions_created_at_utc"));

        var tableSql = ReadTableSql(_dbPath, "desktop_pending_actions");
        Assert.Contains("queue_reason IN (10,11,20,21,22,23,24)", tableSql);

        // 重建不得丢失或重排列。
        Assert.Equal(
            new[]
            {
                "pending_action_id", "created_at_utc", "template_id",
                "stable_id", "display_name", "file_extension", "item_kind",
                "is_system_item", "is_file_system_item",
                "proposed_category", "proposed_zone_id", "proposed_confidence",
                "proposed_source", "explanation",
                "authorization_basis", "queue_reason",
            },
            ReadColumnNames(_dbPath, "desktop_pending_actions"));

        // 已经宽化的表在重跑 Initialize 时不得再次重建。
        await new SqliteDesktopPendingActionStore(_dbPath).InitializeAsync();
        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.Contains(
            "queue_reason IN (10,11,20,21,22,23,24)",
            ReadTableSql(_dbPath, "desktop_pending_actions"));
    }

    [Fact]
    public async Task V12Upgrade_AddsDisplayAuthorizationTableWithoutTouchingExistingTables()
    {
        var bootstrap = new SqliteExplorerGridMeasurementStore(_dbPath);
        await bootstrap.InitializeAsync();

        // V11 的库里没有这张表：把表丢掉并把版本回退到 11，下一次 Initialize 必须走真实升级路径。
        using (var connection = OpenRawConnection(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                DROP TABLE explorer_grid_display_authorizations;
                PRAGMA user_version = 11;
            ";
            command.ExecuteNonQuery();
        }

        Assert.Equal(11, ReadUserVersion(_dbPath));
        Assert.False(TableExists(_dbPath, "explorer_grid_display_authorizations"));

        await new SqliteExplorerGridDisplayAuthorizationStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.True(TableExists(_dbPath, "explorer_grid_display_authorizations"));
        Assert.True(IndexExists(_dbPath, "idx_grid_authorizations_device_recorded_desc"));
        Assert.Equal(
            new[]
            {
                "authorization_id", "device_name", "topology_fingerprint",
                "state", "authorized_at_utc", "recorded_at_utc",
            },
            ReadColumnNames(_dbPath, "explorer_grid_display_authorizations"));

        // V12 只加一张表：既有的证据表与待办表都必须原地还在。
        Assert.True(TableExists(_dbPath, "explorer_grid_measurements"));
        Assert.True(TableExists(_dbPath, "desktop_pending_actions"));

        await new SqliteExplorerGridDisplayAuthorizationStore(_dbPath).InitializeAsync();
        Assert.Equal(13, ReadUserVersion(_dbPath));
    }

    [Fact]
    public async Task V13Upgrade_AddsOriginColumnAndBackfillsExistingSessions()
    {
        await new SqliteDesktopLayoutSnapshotStore(_dbPath).InitializeAsync();

        // V12 的会话表里还没有 origin：换成一模一样但缺这一列的表、回退版本到 12，并留下一行既有会话。
        // 下一次 Initialize 必须走真实的 ALTER TABLE 升级路径，而不是重建整张表。
        // （列约束在这里从简：本测试只关心 ADD COLUMN 与回填，列名与 V4 建表语句保持一致。）
        using (var connection = OpenRawConnection(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                DROP TABLE desktop_layout_recovery_sessions;
                CREATE TABLE desktop_layout_recovery_sessions (
                    session_id                  TEXT PRIMARY KEY NOT NULL,
                    safety_snapshot_id          TEXT,
                    target_device_name          TEXT,
                    started_at_utc              TEXT,
                    lifecycle                   INTEGER,
                    requires_manual_recovery    INTEGER,
                    is_position_uncertain       INTEGER,
                    last_failed_step_index      INTEGER,
                    source_restore_plan_json    TEXT,
                    execution_plan_json         TEXT,
                    plan_payload_format_version INTEGER,
                    completed_forward_step_indexes_json         TEXT,
                    completed_rollback_source_step_indexes_json TEXT,
                    forward_failure_type        TEXT,
                    forward_failure_message     TEXT,
                    recovery_failure_type       TEXT,
                    recovery_failure_message    TEXT,
                    verification_result_json    TEXT,
                    revision                    INTEGER,
                    updated_at_utc              TEXT,
                    forward_attempt_count       INTEGER,
                    rollback_attempt_count      INTEGER
                );
                INSERT INTO desktop_layout_recovery_sessions (session_id)
                    VALUES ('5f2a9c1e-0d3b-4a6f-9c2d-7b1e0a4d8f31');
                PRAGMA user_version = 12;
            ";
            command.ExecuteNonQuery();
        }

        Assert.Equal(12, ReadUserVersion(_dbPath));
        Assert.DoesNotContain("origin", ReadColumnNames(_dbPath, "desktop_layout_recovery_sessions"));

        await new SqliteDesktopLayoutSnapshotStore(_dbPath).InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.Contains("origin", ReadColumnNames(_dbPath, "desktop_layout_recovery_sessions"));

        using (var upgraded = OpenRawConnection(_dbPath))
        {
            using var backfilled = upgraded.CreateCommand();
            backfilled.CommandText =
                "SELECT origin FROM desktop_layout_recovery_sessions WHERE session_id = '5f2a9c1e-0d3b-4a6f-9c2d-7b1e0a4d8f31';";
            Assert.Equal("organize", backfilled.ExecuteScalar() as string);

            // origin 是受约束的枚举：升级完之后非法值必须被拒绝，否则撤销候选会被脏数据污染。
            using var invalid = upgraded.CreateCommand();
            invalid.CommandText = "UPDATE desktop_layout_recovery_sessions SET origin = 'bogus';";
            Assert.Throws<SqliteException>(() => invalid.ExecuteNonQuery());
        }

        await new SqliteDesktopLayoutSnapshotStore(_dbPath).InitializeAsync();
        Assert.Equal(13, ReadUserVersion(_dbPath));
    }

    [Fact]
    public void FutureVersion_RejectsWithoutCreatingApplicationTables()
    {
        using (var connection = OpenRawConnection(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new SqliteDesktopLayoutSnapshotStore(_dbPath)
                .InitializeAsync().GetAwaiter().GetResult());

        Assert.Contains("99", ex.Message);
        Assert.Contains("9", ex.Message);
        Assert.Equal(99, ReadUserVersion(_dbPath));
        Assert.False(TableExists(_dbPath, "desktop_layout_snapshots"));
        Assert.False(TableExists(_dbPath, "desktop_classification_rules"));
        Assert.False(TableExists(_dbPath, "desktop_pending_actions"));
        Assert.False(TableExists(_dbPath, "desktop_semantic_understandings"));
        Assert.False(TableExists(_dbPath, "workspace_blueprints"));
        Assert.False(TableExists(_dbPath, "workspace_execution_journals"));
    }

    [Fact]
    public async Task ForeignKeys_AndM4CheckConstraints_AreEnforced()
    {
        await new SqliteDesktopLayoutSnapshotStore(_dbPath).InitializeAsync();

        using var connection = OpenRawConnection(_dbPath);

        using (var orphanZone = connection.CreateCommand())
        {
            orphanZone.CommandText = @"
                INSERT INTO desktop_layout_template_zones
                    (template_id, ordinal, zone_id, name,
                     left_value, top_value, right_value, bottom_value,
                     sort_order, placement_flow, max_item_count)
                VALUES
                    ('00000000-0000-0000-0000-000000000061', 0,
                     '00000000-0000-0000-0000-000000000062', 'orphan',
                     0, 0, 10000, 10000, 0, 0, NULL);
            ";
            Assert.Throws<SqliteException>(() => orphanZone.ExecuteNonQuery());
        }

        using (var incompleteTarget = connection.CreateCommand())
        {
            incompleteTarget.CommandText = @"
                INSERT INTO desktop_classification_rules
                    (rule_id, name, stable_id, category,
                     target_template_id, target_zone_id,
                     authority, provenance, priority, is_enabled)
                VALUES
                    ('00000000-0000-0000-0000-000000000063',
                     'bad', 'desktop:item', 'Development',
                     '00000000-0000-0000-0000-000000000061', NULL,
                     1, 1, 0, 1);
            ";
            Assert.Throws<SqliteException>(() => incompleteTarget.ExecuteNonQuery());
        }

        using (var invalidEvidence = connection.CreateCommand())
        {
            invalidEvidence.CommandText = @"
                INSERT INTO desktop_classification_evidence
                    (evidence_id, observed_at_utc, template_id,
                     stable_id, display_name, file_extension, item_kind,
                     proposed_category, proposed_zone_id, proposed_source,
                     feedback_kind, final_category, final_zone_id)
                VALUES
                    ('00000000-0000-0000-0000-000000000064',
                     '2026-08-10T00:00:00.0000000+00:00', NULL,
                     'desktop:item', 'demo', NULL, 99,
                     'Development', NULL, 3,
                     2, NULL, NULL);
            ";
            Assert.Throws<SqliteException>(() => invalidEvidence.ExecuteNonQuery());
        }

        using (var orphanRevision = connection.CreateCommand())
        {
            orphanRevision.CommandText = @"
                INSERT INTO workspace_blueprint_revisions
                    (blueprint_id, revision, parent_revision, created_at_utc,
                     source_understanding_id, source_user_intent, design_summary,
                     blueprint_schema_version, payload_format_version, payload_json)
                VALUES
                    ('00000000-0000-0000-0000-000000000065', 1, NULL,
                     '2026-08-28T04:00:00.0000000+00:00', NULL, NULL,
                     'orphan', 1, 1, '{}');
            ";
            Assert.Throws<SqliteException>(() => orphanRevision.ExecuteNonQuery());
        }
    }

    [Fact]
    public void PreCanceledInitialization_DoesNotCreateDatabase()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new SqliteDesktopLayoutSnapshotStore(_dbPath)
                .InitializeAsync(cancellation.Token).GetAwaiter().GetResult());
        Assert.False(File.Exists(_dbPath));
    }

    private static void CreateV1Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
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
        ";
        command.ExecuteNonQuery();
    }

    private static void CreateV2Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS desktop_layout_templates (
                template_id                 TEXT PRIMARY KEY NOT NULL,
                name                        TEXT NOT NULL,
                version                     INTEGER NOT NULL CHECK(version > 0),
                target_display_binding_kind INTEGER NOT NULL CHECK(target_display_binding_kind IN (0, 1, 2)),
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
        ";
        command.ExecuteNonQuery();
    }

    private static void CreateV3Table(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
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
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenRawConnection(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static int ReadUserVersion(string path)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (int)(long)command.ExecuteScalar()!;
    }

    private static string ReadTableSql(string path, string name)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", name);
        return (string)command.ExecuteScalar()!;
    }

    private static string[] ReadColumnNames(string path, string name)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({name});";

        var columns = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns.ToArray();
    }

    private static bool TableExists(string path, string name)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = @name;
        ";
        command.Parameters.AddWithValue("@name", name);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static bool IndexExists(string path, string name)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index' AND name = @name;
        ";
        command.Parameters.AddWithValue("@name", name);
        return (long)command.ExecuteScalar()! > 0;
    }
}
