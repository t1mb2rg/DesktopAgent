using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
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
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* 尽力清理 */ }
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

    private static void InitS(SqliteDesktopLayoutSnapshotStore store)
        => store.InitializeAsync().GetAwaiter().GetResult();

    private static void InitT(SqliteDesktopLayoutTemplateStore store)
        => store.InitializeAsync().GetAwaiter().GetResult();

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

    // ═══ Fresh v0 → v5 ═══

    [Fact]
    public void FreshV0_InitViaSnapshotStore_SetsUserVersion5()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.Equal(5, ReadUserVersion());
    }

    [Fact]
    public void FreshV0_InitViaTemplateStore_SetsUserVersion5()
    {
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));
        Assert.Equal(5, ReadUserVersion());
    }

    [Fact]
    public void FreshV0_AllCurrentTablesExist()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.True(TableExists("desktop_layout_snapshots"));
        Assert.True(TableExists("desktop_layout_snapshot_items"));
        Assert.True(TableExists("desktop_layout_templates"));
        Assert.True(TableExists("desktop_layout_template_zones"));
        Assert.True(TableExists("desktop_layout_template_icon_rules"));
        Assert.True(TableExists("desktop_layout_recovery_sessions"));
        Assert.True(TableExists("desktop_layout_recovery_attempts"));
    }

    [Fact]
    public void FreshV0_RequiredIndexesExist()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.True(IndexExists("idx_snapshots_created_at_utc_desc"));
        Assert.True(IndexExists("idx_templates_name_id_asc"));
        Assert.True(IndexExists("idx_recovery_sessions_lifecycle"));
    }

    // ═══ Idempotent ═══

    [Fact]
    public void RepeatedInit_IsIdempotent()
    {
        var s = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        InitS(s); InitS(s); InitS(s);
        Assert.Equal(5, ReadUserVersion());
    }

    // ═══ Init order ═══

    [Fact]
    public void SnapshotThenTemplate_Version5()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));
        Assert.Equal(5, ReadUserVersion());
    }

    [Fact]
    public void TemplateThenSnapshot_Version5()
    {
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.Equal(5, ReadUserVersion());
    }

    // ═══ Single-store init reaches full v5 ═══

    [Fact]
    public void SnapshotOnly_ReachesV5()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.Equal(5, ReadUserVersion());
        Assert.True(TableExists("desktop_layout_templates"));
        Assert.True(TableExists("desktop_layout_template_zones"));
        Assert.True(TableExists("desktop_layout_recovery_sessions"));
    }

    [Fact]
    public void TemplateOnly_ReachesV5()
    {
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));
        Assert.Equal(5, ReadUserVersion());
        Assert.True(TableExists("desktop_layout_snapshots"));
        Assert.True(TableExists("desktop_layout_snapshot_items"));
        Assert.True(TableExists("desktop_layout_recovery_sessions"));
    }

    // ═══ v1 → v5 upgrade preserves snapshots ═══

    [Fact]
    public void V1Upgrade_PreservesSnapshotParent()
    {
        using (var c = OpenRawConnection())
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                PRAGMA user_version = 1;
                CREATE TABLE IF NOT EXISTS desktop_layout_snapshots (
                    snapshot_id TEXT PRIMARY KEY NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    target_monitor_device_name TEXT NULL,
                    connection_method INTEGER NOT NULL,
                    format_version INTEGER NOT NULL CHECK(format_version > 0),
                    icon_count INTEGER NOT NULL CHECK(icon_count >= 0)
                );
                INSERT INTO desktop_layout_snapshots VALUES
                    ('00000000-0000-0000-0000-000000000001',
                     '2026-07-01T12:30:00.0000000+00:00', 'DISPLAY1', 1, 1, 3);
            ";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));

        {
            using var c = OpenRawConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT snapshot_id, created_at_utc, target_monitor_device_name, connection_method, format_version, icon_count FROM desktop_layout_snapshots WHERE snapshot_id = '00000000-0000-0000-0000-000000000001'";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("00000000-0000-0000-0000-000000000001", r.GetString(0));
            Assert.Contains("2026-07-01T12:30:00", r.GetString(1));
            Assert.Equal("DISPLAY1", r.GetString(2));
            Assert.Equal(1, r.GetInt32(3));
            Assert.Equal(1, r.GetInt32(4));
            Assert.Equal(3, r.GetInt32(5));
        }
    }

    [Fact]
    public void V1Upgrade_PreservesSnapshotItems()
    {
        using (var c = OpenRawConnection())
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                PRAGMA user_version = 1;
                CREATE TABLE IF NOT EXISTS desktop_layout_snapshots (
                    snapshot_id TEXT PRIMARY KEY NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    target_monitor_device_name TEXT NULL,
                    connection_method INTEGER NOT NULL,
                    format_version INTEGER NOT NULL CHECK(format_version > 0),
                    icon_count INTEGER NOT NULL CHECK(icon_count >= 0)
                );
                CREATE TABLE IF NOT EXISTS desktop_layout_snapshot_items (
                    snapshot_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
                    stable_id TEXT NOT NULL, display_name TEXT NOT NULL,
                    parsing_name TEXT NULL, item_kind INTEGER NOT NULL,
                    x INTEGER NOT NULL, y INTEGER NOT NULL,
                    monitor_device_name TEXT NULL,
                    is_system_item INTEGER NOT NULL, is_file_system_item INTEGER NOT NULL,
                    PRIMARY KEY (snapshot_id, ordinal),
                    FOREIGN KEY (snapshot_id) REFERENCES desktop_layout_snapshots(snapshot_id) ON DELETE CASCADE
                );
                INSERT INTO desktop_layout_snapshots VALUES
                    ('00000000-0000-0000-0000-000000000002',
                     '2026-07-01T12:00:00.0000000+00:00', '\\.\DISPLAY2', 0, 1, 1);
                INSERT INTO desktop_layout_snapshot_items VALUES
                    ('00000000-0000-0000-0000-000000000002', 0,
                     'C:\users\test\file.txt', 'file.txt', 'C:\Users\Test\Desktop\file.txt',
                     0, 150, 250, '\\.\DISPLAY2', 0, 1);
            ";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        var s = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        InitS(s);
        var snap = s.GetAsync(Guid.Parse("00000000-0000-0000-0000-000000000002"))
            .GetAwaiter().GetResult();
        Assert.NotNull(snap);
        Assert.Single(snap!.Icons);
        var item = snap.Icons[0];
        Assert.Equal(0, item.Ordinal);
        Assert.Equal("C:\\users\\test\\file.txt", item.StableId);
        Assert.Equal("file.txt", item.DisplayName);
        Assert.Equal("C:\\Users\\Test\\Desktop\\file.txt", item.ParsingName);
        Assert.Equal(DesktopAgent.Core.DesktopIcons.ItemKind.File, item.ItemKind);
        Assert.Equal(150, item.PixelPosition.X);
        Assert.Equal(250, item.PixelPosition.Y);
        Assert.Equal(@"\\.\DISPLAY2", item.MonitorDeviceName);
        Assert.False(item.IsSystemItem);
        Assert.True(item.IsFileSystemItem);
    }

    // ═══ v5 repairs missing tables ═══

    [Fact]
    public void V2WithMissingTables_RepairsSchema()
    {
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 2;";
            cmd.ExecuteNonQuery();
        }

        Assert.False(TableExists("desktop_layout_templates"));
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));

        Assert.True(TableExists("desktop_layout_templates"));
        Assert.True(TableExists("desktop_layout_template_zones"));
        Assert.Equal(5, ReadUserVersion());
    }

    [Fact]
    public void V2MissingTemplateTable_Repairs()
    {
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                PRAGMA user_version = 2;
                CREATE TABLE IF NOT EXISTS desktop_layout_snapshots (
                    snapshot_id TEXT PRIMARY KEY NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    target_monitor_device_name TEXT NULL,
                    connection_method INTEGER NOT NULL,
                    format_version INTEGER NOT NULL,
                    icon_count INTEGER NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }

        Assert.True(TableExists("desktop_layout_snapshots"));
        Assert.False(TableExists("desktop_layout_templates"));

        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));

        Assert.True(TableExists("desktop_layout_templates"));
        Assert.True(TableExists("desktop_layout_template_zones"));
    }

    // ═══ Future version rejection ═══

    [Fact]
    public void FutureVersion_Throws()
    {
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() =>
            InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath)));
        Assert.Contains("99", ex.Message);
        Assert.Contains("5", ex.Message);
        Assert.Equal(99, ReadUserVersion());
    }

    [Fact]
    public void FutureVersion_DoesNotCreateAppTables()
    {
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() =>
            InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath)));

        Assert.False(TableExists("desktop_layout_snapshots"));
        Assert.False(TableExists("desktop_layout_templates"));
    }

    // ═══ Foreign keys ═══

    [Fact]
    public void ForeignKeys_AreEnforced()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO desktop_layout_template_zones
                (template_id, ordinal, zone_id, name,
                 left_value, top_value, right_value, bottom_value,
                 sort_order, placement_flow)
            VALUES
                ('nonexistent', 0, '00000000-0000-0000-0000-000000000099',
                 'orphan', 0, 0, 1000, 1000, 0, 0);
        ";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    // ═══ Cancel ═══

    [Fact]
    public void PreCanceledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new SqliteDesktopLayoutSnapshotStore(_dbPath)
                .InitializeAsync(cts.Token).GetAwaiter().GetResult());
        Assert.False(File.Exists(_dbPath));
    }

    // ═══════════════════════════ Schema v5 ═══════════════════════════

    [Fact]
    public void FreshV0_InitViaRuleStore_SetsUserVersion5()
    {
        var store = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();
        Assert.Equal(5, ReadUserVersion());
        Assert.True(TableExists("desktop_layout_template_icon_rules"));
    }

    [Fact]
    public void V1ToV5_PreservesSnapshotData()
    {
        using (var c = OpenRawConnection())
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                PRAGMA user_version = 1;
                CREATE TABLE IF NOT EXISTS desktop_layout_snapshots (
                    snapshot_id TEXT PRIMARY KEY NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    target_monitor_device_name TEXT NULL,
                    connection_method INTEGER NOT NULL,
                    format_version INTEGER NOT NULL CHECK(format_version > 0),
                    icon_count INTEGER NOT NULL CHECK(icon_count >= 0)
                );
                INSERT INTO desktop_layout_snapshots VALUES
                    ('00000000-0000-0000-0000-000000000011',
                     '2026-07-15T12:00:00.0000000+00:00', 'DISPLAY1', 1, 1, 5);
            ";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        var store = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();

        Assert.Equal(5, ReadUserVersion());
        Assert.True(TableExists("desktop_layout_template_icon_rules"));

        using var c2 = OpenRawConnection();
        using var sel = c2.CreateCommand();
        sel.CommandText = "SELECT icon_count FROM desktop_layout_snapshots WHERE snapshot_id = '00000000-0000-0000-0000-000000000011'";
        Assert.Equal(5L, (long)sel.ExecuteScalar()!);
    }

    [Fact]
    public void V2ToV5_PreservesTemplateData()
    {
        using (var c = OpenRawConnection())
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                PRAGMA user_version = 2;
                CREATE TABLE IF NOT EXISTS desktop_layout_templates (
                    template_id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL, version INTEGER NOT NULL,
                    target_display_binding_kind INTEGER NOT NULL,
                    target_display_device_name TEXT NULL,
                    zone_count INTEGER NOT NULL
                );
                INSERT INTO desktop_layout_templates VALUES
                    ('00000000-0000-0000-0000-000000000021',
                     'V2-Template', 1, 1, NULL, 2);
                CREATE TABLE IF NOT EXISTS desktop_layout_template_zones (
                    template_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
                    zone_id TEXT NOT NULL, name TEXT NOT NULL,
                    left_value INTEGER NOT NULL, top_value INTEGER NOT NULL,
                    right_value INTEGER NOT NULL, bottom_value INTEGER NOT NULL,
                    sort_order INTEGER NOT NULL, placement_flow INTEGER NOT NULL,
                    max_item_count INTEGER NULL,
                    PRIMARY KEY (template_id, ordinal),
                    FOREIGN KEY (template_id) REFERENCES desktop_layout_templates(template_id) ON DELETE CASCADE
                );
                INSERT INTO desktop_layout_template_zones VALUES
                    ('00000000-0000-0000-0000-000000000021', 0,
                     '00000000-0000-0000-0000-0000000000a1', 'Z0',
                     0, 0, 5000, 10000, 0, 0, NULL);
                INSERT INTO desktop_layout_template_zones VALUES
                    ('00000000-0000-0000-0000-000000000021', 1,
                     '00000000-0000-0000-0000-0000000000a2', 'Z1',
                     5000, 0, 10000, 10000, 1, 0, NULL);
            ";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        var store = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();

        Assert.Equal(5, ReadUserVersion());
        Assert.True(TableExists("desktop_layout_template_icon_rules"));

        using var c2 = OpenRawConnection();
        using var sel = c2.CreateCommand();
        sel.CommandText = "SELECT name, zone_count FROM desktop_layout_templates WHERE template_id = '00000000-0000-0000-0000-000000000021'";
        using var r = sel.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("V2-Template", r.GetString(0));
        Assert.Equal(2, r.GetInt32(1));
    }

    [Fact]
    public void V0ToV5_CreatesAllTables()
    {
        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version = 0;";
        cmd.ExecuteNonQuery();

        var store = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();

        Assert.Equal(5, ReadUserVersion());
        Assert.True(TableExists("desktop_layout_snapshots"));
        Assert.True(TableExists("desktop_layout_templates"));
        Assert.True(TableExists("desktop_layout_template_icon_rules"));
        Assert.True(TableExists("desktop_layout_recovery_sessions"));
        Assert.True(TableExists("desktop_layout_recovery_attempts"));
    }

    [Fact]
    public void V5RepeatedInit_Idempotent()
    {
        var store = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();
        store.InitializeAsync().GetAwaiter().GetResult();
        store.InitializeAsync().GetAwaiter().GetResult();
        Assert.Equal(5, ReadUserVersion());
        Assert.True(TableExists("desktop_layout_template_icon_rules"));
    }

    [Fact]
    public void RuleTable_HasCorrectPK()
    {
        var store = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            SELECT sql FROM sqlite_master
            WHERE type='table' AND name='desktop_layout_template_icon_rules';
        ";
        var sql = (string)cmd.ExecuteScalar()!;
        Assert.Contains("PRIMARY KEY", sql);
        Assert.Contains("template_id", sql);
        Assert.Contains("stable_id", sql);
    }

    [Fact]
    public void RuleFK_CascadeOnTemplateDelete()
    {
        var tStore = new SqliteDesktopLayoutTemplateStore(_dbPath);
        tStore.InitializeAsync().GetAwaiter().GetResult();

        var tid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(Guid.NewGuid(), "Z",
            new NormalizedRect(0, 0, 10000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        var template = new DesktopLayoutTemplate(tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(), new[] { zone });
        tStore.SaveAsync(template).GetAwaiter().GetResult();

        var rStore = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        rStore.InitializeAsync().GetAwaiter().GetResult();

        var ruleSet = new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("a") });
        rStore.SaveAsync(ruleSet).GetAwaiter().GetResult();

        tStore.DeleteAsync(tid).GetAwaiter().GetResult();
        Assert.Null(rStore.GetAsync(tid).GetAwaiter().GetResult());
    }

    [Fact]
    public void RuleKindCheck_RejectsInvalidValue()
    {
        var tStore = new SqliteDesktopLayoutTemplateStore(_dbPath);
        tStore.InitializeAsync().GetAwaiter().GetResult();

        var tid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(Guid.NewGuid(), "Z",
            new NormalizedRect(0, 0, 10000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        tStore.SaveAsync(new DesktopLayoutTemplate(tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(), new[] { zone }))
            .GetAwaiter().GetResult();

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO desktop_layout_template_icon_rules
                (template_id, stable_id, rule_kind, target_zone_id)
            VALUES
                (@tid, 'test', 99, NULL);
        ";
        cmd.Parameters.AddWithValue("@tid", tid.ToString("D"));
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void RuleKindTargetZoneIdCheck_RejectsInvalidCombo()
    {
        var tStore = new SqliteDesktopLayoutTemplateStore(_dbPath);
        tStore.InitializeAsync().GetAwaiter().GetResult();

        var tid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(Guid.NewGuid(), "Z",
            new NormalizedRect(0, 0, 10000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        tStore.SaveAsync(new DesktopLayoutTemplate(tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(), new[] { zone }))
            .GetAwaiter().GetResult();

        using var c = OpenRawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO desktop_layout_template_icon_rules
                (template_id, stable_id, rule_kind, target_zone_id)
            VALUES
                (@tid, 'ex', 0, @zid);
        ";
        cmd.Parameters.AddWithValue("@tid", tid.ToString("D"));
        cmd.Parameters.AddWithValue("@zid", Guid.NewGuid().ToString("D"));
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void CaseSensitiveStableId_Coexist()
    {
        var tStore = new SqliteDesktopLayoutTemplateStore(_dbPath);
        tStore.InitializeAsync().GetAwaiter().GetResult();

        var tid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(Guid.NewGuid(), "Z",
            new NormalizedRect(0, 0, 10000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        tStore.SaveAsync(new DesktopLayoutTemplate(tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(), new[] { zone }))
            .GetAwaiter().GetResult();

        var rStore = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        rStore.InitializeAsync().GetAwaiter().GetResult();

        var ruleSet = new DesktopLayoutIconRuleSet(tid,
            new[] {
                DesktopLayoutIconRule.ForExclude("Abc"),
                DesktopLayoutIconRule.ForExclude("abc")
            });
        rStore.SaveAsync(ruleSet).GetAwaiter().GetResult();

        var loaded = rStore.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Equal(2, loaded!.RuleCount);
        Assert.Equal("Abc", loaded.Rules[0].StableId);
        Assert.Equal("abc", loaded.Rules[1].StableId);
    }

    [Fact]
    public void DuplicateStableId_PKRejected()
    {
        var tStore = new SqliteDesktopLayoutTemplateStore(_dbPath);
        tStore.InitializeAsync().GetAwaiter().GetResult();

        var tid = Guid.NewGuid();
        var zone = new DesktopLayoutZone(Guid.NewGuid(), "Z",
            new NormalizedRect(0, 0, 10000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        tStore.SaveAsync(new DesktopLayoutTemplate(tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(), new[] { zone }))
            .GetAwaiter().GetResult();

        using var c = OpenRawConnection();
        using var cmd1 = c.CreateCommand();
        cmd1.CommandText = @"
            INSERT INTO desktop_layout_template_icon_rules VALUES (@tid, 'dup', 0, NULL);
        ";
        cmd1.Parameters.AddWithValue("@tid", tid.ToString("D"));
        cmd1.ExecuteNonQuery();

        using var cmd2 = c.CreateCommand();
        cmd2.CommandText = @"
            INSERT INTO desktop_layout_template_icon_rules VALUES (@tid, 'dup', 1, NULL);
        ";
        cmd2.Parameters.AddWithValue("@tid", tid.ToString("D"));
        Assert.Throws<SqliteException>(() => cmd2.ExecuteNonQuery());
    }

    [Fact]
    public void DifferentTemplates_SameStableId_Allowed()
    {
        var tStore = new SqliteDesktopLayoutTemplateStore(_dbPath);
        tStore.InitializeAsync().GetAwaiter().GetResult();

        var tid1 = Guid.NewGuid();
        var tid2 = Guid.NewGuid();
        tStore.SaveAsync(new DesktopLayoutTemplate(tid1, "T1", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) }))
            .GetAwaiter().GetResult();
        tStore.SaveAsync(new DesktopLayoutTemplate(tid2, "T2", 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[] { new DesktopLayoutZone(Guid.NewGuid(), "Z",
                new NormalizedRect(0, 0, 10000, 10000), 0,
                ZonePlacementFlow.ColumnMajor, null) }))
            .GetAwaiter().GetResult();

        var rStore = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        rStore.InitializeAsync().GetAwaiter().GetResult();

        rStore.SaveAsync(new DesktopLayoutIconRuleSet(tid1,
            new[] { DesktopLayoutIconRule.ForExclude("same") }))
            .GetAwaiter().GetResult();
        rStore.SaveAsync(new DesktopLayoutIconRuleSet(tid2,
            new[] { DesktopLayoutIconRule.ForExclude("same") }))
            .GetAwaiter().GetResult();

        Assert.Single(rStore.GetAsync(tid1).GetAwaiter().GetResult()!.Rules);
        Assert.Single(rStore.GetAsync(tid2).GetAwaiter().GetResult()!.Rules);
    }

    // ═══════════════════════════ V3ToV5 Icon Rule Migration ═══════════════════════════

    [Fact]
    public void V3ToV5_PreservesIconRuleData()
    {
        using (var c = OpenRawConnection())
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                PRAGMA user_version = 3;
                CREATE TABLE IF NOT EXISTS desktop_layout_snapshots (
                    snapshot_id TEXT PRIMARY KEY NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    target_monitor_device_name TEXT NULL,
                    connection_method INTEGER NOT NULL,
                    format_version INTEGER NOT NULL,
                    icon_count INTEGER NOT NULL
                );
                INSERT INTO desktop_layout_snapshots VALUES
                    ('00000000-0000-0000-0000-000000000099',
                     '2026-08-01T12:00:00.0000000+00:00', 'DISPLAY1', 1, 1, 2);
                CREATE TABLE IF NOT EXISTS desktop_layout_templates (
                    template_id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL, version INTEGER NOT NULL,
                    target_display_binding_kind INTEGER NOT NULL,
                    target_display_device_name TEXT NULL,
                    zone_count INTEGER NOT NULL
                );
                INSERT INTO desktop_layout_templates VALUES
                    ('00000000-0000-0000-0000-000000000088',
                     'Mig-Template', 1, 1, NULL, 1);
                CREATE TABLE IF NOT EXISTS desktop_layout_template_zones (
                    template_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
                    zone_id TEXT NOT NULL, name TEXT NOT NULL,
                    left_value INTEGER NOT NULL, top_value INTEGER NOT NULL,
                    right_value INTEGER NOT NULL, bottom_value INTEGER NOT NULL,
                    sort_order INTEGER NOT NULL, placement_flow INTEGER NOT NULL,
                    max_item_count INTEGER NULL,
                    PRIMARY KEY (template_id, ordinal),
                    FOREIGN KEY (template_id) REFERENCES desktop_layout_templates(template_id) ON DELETE CASCADE
                );
                INSERT INTO desktop_layout_template_zones VALUES
                    ('00000000-0000-0000-0000-000000000088', 0,
                     '00000000-0000-0000-0000-000000000077', 'Z',
                     0, 0, 10000, 10000, 0, 0, NULL);
                CREATE TABLE IF NOT EXISTS desktop_layout_template_icon_rules (
                    template_id TEXT NOT NULL, stable_id TEXT NOT NULL,
                    rule_kind INTEGER NOT NULL, target_zone_id TEXT NULL,
                    PRIMARY KEY (template_id, stable_id),
                    FOREIGN KEY (template_id) REFERENCES desktop_layout_templates(template_id) ON DELETE CASCADE
                );
                INSERT INTO desktop_layout_template_icon_rules VALUES
                    ('00000000-0000-0000-0000-000000000088', 'my-icon', 2,
                     '00000000-0000-0000-0000-000000000077');
            ";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        Assert.Equal(3, ReadUserVersion());

        var store = new DesktopAgent.Persistence.DesktopLayouts.Templates.Rules
            .SqliteDesktopLayoutIconRuleStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();

        Assert.Equal(5, ReadUserVersion());

        using var c2 = OpenRawConnection();
        using var selSnap = c2.CreateCommand();
        selSnap.CommandText = "SELECT icon_count FROM desktop_layout_snapshots WHERE snapshot_id = '00000000-0000-0000-0000-000000000099'";
        Assert.Equal(2L, (long)selSnap.ExecuteScalar()!);

        using var selTpl = c2.CreateCommand();
        selTpl.CommandText = "SELECT name FROM desktop_layout_templates WHERE template_id = '00000000-0000-0000-0000-000000000088'";
        Assert.Equal("Mig-Template", (string)selTpl.ExecuteScalar()!);

        using var selRule = c2.CreateCommand();
        selRule.CommandText = @"
            SELECT stable_id, rule_kind, target_zone_id
            FROM desktop_layout_template_icon_rules
            WHERE template_id = '00000000-0000-0000-0000-000000000088'
        ";
        using var r = selRule.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("my-icon", r.GetString(0));
        Assert.Equal(2, r.GetInt32(1));
        Assert.Equal("00000000-0000-0000-0000-000000000077", r.GetString(2));

        Assert.True(TableExists("desktop_layout_recovery_sessions"));
        Assert.True(TableExists("desktop_layout_recovery_attempts"));
    }
}
