using DesktopAgent.Persistence.Automation;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.DesktopLayouts.Templates.Rules;
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
    public async Task FreshDatabase_CreatesCompleteV8Schema()
    {
        await new SqliteDesktopLayoutSnapshotStore(_dbPath).InitializeAsync();

        Assert.Equal(8, ReadUserVersion(_dbPath));

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
        };
        foreach (var index in expectedIndexes)
            Assert.True(IndexExists(_dbPath, index), $"缺少索引 {index}");
    }

    [Fact]
    public async Task EveryStore_UsesSharedV8Initializer_AndRepeatedInitIsIdempotent()
    {
        var paths = Enumerable.Range(0, 6)
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

        foreach (var path in paths)
        {
            Assert.Equal(8, ReadUserVersion(path));
            Assert.True(TableExists(path, "desktop_classification_rules"));
            Assert.True(TableExists(path, "desktop_classification_evidence"));
            Assert.True(TableExists(path, "desktop_pending_actions"));
            Assert.True(TableExists(path, "desktop_semantic_understandings"));
            Assert.True(TableExists(path, "workspace_blueprints"));
            Assert.True(TableExists(path, "workspace_blueprint_revisions"));
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

        Assert.Equal(8, ReadUserVersion(_dbPath));
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

        Assert.Equal(8, ReadUserVersion(_dbPath));
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

        Assert.Equal(8, ReadUserVersion(_dbPath));
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

        Assert.Equal(8, ReadUserVersion(_dbPath));
        Assert.True(TableExists(_dbPath, "desktop_classification_rules"));
        Assert.True(TableExists(_dbPath, "desktop_user_category_zone_preferences"));
        Assert.True(TableExists(_dbPath, "desktop_classification_evidence"));
        Assert.True(TableExists(_dbPath, "desktop_pending_actions"));
        Assert.True(TableExists(_dbPath, "desktop_semantic_understandings"));
        Assert.True(TableExists(_dbPath, "workspace_blueprints"));
        Assert.True(TableExists(_dbPath, "workspace_blueprint_revisions"));

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
                DROP TABLE workspace_blueprint_revisions;
                DROP TABLE workspace_blueprints;
                DROP TABLE desktop_semantic_understandings;
                DROP TABLE desktop_pending_actions;
                PRAGMA user_version = 6;
            ";
            command.ExecuteNonQuery();
        }

        await new SqliteDesktopPendingActionStore(_dbPath).InitializeAsync();

        Assert.Equal(8, ReadUserVersion(_dbPath));
        Assert.True(TableExists(_dbPath, "desktop_pending_actions"));
        Assert.True(TableExists(_dbPath, "desktop_semantic_understandings"));
        Assert.True(TableExists(_dbPath, "workspace_blueprints"));

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

        Assert.Equal(8, ReadUserVersion(_dbPath));
        Assert.True(TableExists(_dbPath, "desktop_semantic_understandings"));
        Assert.True(TableExists(_dbPath, "workspace_blueprints"));
        Assert.True(TableExists(_dbPath, "workspace_blueprint_revisions"));

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
        Assert.Contains("8", ex.Message);
        Assert.Equal(99, ReadUserVersion(_dbPath));
        Assert.False(TableExists(_dbPath, "desktop_layout_snapshots"));
        Assert.False(TableExists(_dbPath, "desktop_classification_rules"));
        Assert.False(TableExists(_dbPath, "desktop_pending_actions"));
        Assert.False(TableExists(_dbPath, "desktop_semantic_understandings"));
        Assert.False(TableExists(_dbPath, "workspace_blueprints"));
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
