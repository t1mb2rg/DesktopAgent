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

    // ═══ Fresh v0 → v2 ═══

    [Fact]
    public void FreshV0_InitViaSnapshotStore_SetsUserVersion2()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.Equal(2, ReadUserVersion());
    }

    [Fact]
    public void FreshV0_InitViaTemplateStore_SetsUserVersion2()
    {
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));
        Assert.Equal(2, ReadUserVersion());
    }

    [Fact]
    public void FreshV0_AllFourTablesExist()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.True(TableExists("desktop_layout_snapshots"));
        Assert.True(TableExists("desktop_layout_snapshot_items"));
        Assert.True(TableExists("desktop_layout_templates"));
        Assert.True(TableExists("desktop_layout_template_zones"));
    }

    [Fact]
    public void FreshV0_RequiredIndexesExist()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.True(IndexExists("idx_snapshots_created_at_utc_desc"));
        Assert.True(IndexExists("idx_templates_name_id_asc"));
    }

    // ═══ Idempotent ═══

    [Fact]
    public void RepeatedInit_IsIdempotent()
    {
        var s = new SqliteDesktopLayoutSnapshotStore(_dbPath);
        InitS(s); InitS(s); InitS(s);
        Assert.Equal(2, ReadUserVersion());
    }

    // ═══ Init order ═══

    [Fact]
    public void SnapshotThenTemplate_Version2()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));
        Assert.Equal(2, ReadUserVersion());
    }

    [Fact]
    public void TemplateThenSnapshot_Version2()
    {
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.Equal(2, ReadUserVersion());
    }

    // ═══ Single-store init reaches full v2 ═══

    [Fact]
    public void SnapshotOnly_ReachesV2()
    {
        InitS(new SqliteDesktopLayoutSnapshotStore(_dbPath));
        Assert.Equal(2, ReadUserVersion());
        Assert.True(TableExists("desktop_layout_templates"));
        Assert.True(TableExists("desktop_layout_template_zones"));
    }

    [Fact]
    public void TemplateOnly_ReachesV2()
    {
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));
        Assert.Equal(2, ReadUserVersion());
        Assert.True(TableExists("desktop_layout_snapshots"));
        Assert.True(TableExists("desktop_layout_snapshot_items"));
    }

    // ═══ v1 → v2 upgrade preserves snapshots ═══

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

    // ═══ v2 repairs missing tables ═══

    [Fact]
    public void V2WithMissingTables_RepairsSchema()
    {
        // Set user_version=2 on empty DB
        using (var c = OpenRawConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 2;";
            cmd.ExecuteNonQuery();
        }

        Assert.False(TableExists("desktop_layout_templates"));

        // Init should repair
        InitT(new SqliteDesktopLayoutTemplateStore(_dbPath));

        Assert.True(TableExists("desktop_layout_templates"));
        Assert.True(TableExists("desktop_layout_template_zones"));
        Assert.Equal(2, ReadUserVersion());
    }

    [Fact]
    public void V2MissingTemplateTable_Repairs()
    {
        // Create v2 with only snapshot tables
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
        Assert.Contains("2", ex.Message);
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

        // Try inserting orphan zone — should fail
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
}
