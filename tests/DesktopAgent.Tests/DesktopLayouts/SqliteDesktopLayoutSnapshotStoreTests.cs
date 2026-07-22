using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.DesktopLayouts;

public sealed class SqliteDesktopLayoutSnapshotStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDesktopLayoutSnapshotStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DesktopAgent.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* 尽力清理 */ }
    }

    private SqliteDesktopLayoutSnapshotStore CreateStore() => new(_dbPath);

    private static DesktopLayoutSnapshot CreateSimpleSnapshot(
        Guid? id = null, int iconCount = 3, ItemKind kind = ItemKind.File)
    {
        var icons = new List<DesktopIconSnapshotItem>();
        for (var i = 0; i < iconCount; i++)
        {
            icons.Add(new DesktopIconSnapshotItem(
                ordinal: i,
                stableId: $"C:\\test\\file_{i}.txt".ToLowerInvariant(),
                displayName: $"file_{i}.txt",
                parsingName: $"C:\\Users\\Test\\Desktop\\file_{i}.txt",
                itemKind: kind,
                pixelPosition: new PixelPoint(100 * i, 200 * i),
                monitorDeviceName: i == 0 ? @"\\.\DISPLAY1" : null,
                isSystemItem: kind == ItemKind.SystemItem,
                isFileSystemItem: kind != ItemKind.SystemItem));
        }
        return new DesktopLayoutSnapshot(
            snapshotId: id ?? Guid.NewGuid(),
            createdAtUtc: new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero),
            targetMonitorDeviceName: @"\\.\DISPLAY1",
            connectionMethod: ConnectionMethod.FindWindowSW,
            formatVersion: 1,
            icons: icons);
    }

    // ── Domain model tests ──

    [Fact]
    public void SnapshotItem_NegativeOrdinal_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopIconSnapshotItem(-1, "a", "b", null, ItemKind.File, default, null, false, true));
    }

    [Fact]
    public void SnapshotItem_NullStableId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopIconSnapshotItem(0, null!, "b", null, ItemKind.File, default, null, false, true));
    }

    [Fact]
    public void SnapshotItem_NullDisplayName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopIconSnapshotItem(0, "a", null!, null, ItemKind.File, default, null, false, true));
    }

    [Fact]
    public void Snapshot_DuplicateOrdinal_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, ConnectionMethod.FindWindowSW, 1,
                [new(0, "a", "b", null, ItemKind.File, default, null, false, true),
                 new(0, "c", "d", null, ItemKind.File, default, null, false, true)]));
    }

    [Fact]
    public void Snapshot_DuplicateStableId_Allowed()
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
            ConnectionMethod.FindWindowSW, 1,
            [new(0, "same", "a", null, ItemKind.File, default, null, false, true),
             new(1, "same", "b", null, ItemKind.File, default, null, false, true)]);
        Assert.Equal(2, snapshot.Icons.Count);
    }

    [Fact]
    public void Snapshot_NormalizesToUtc()
    {
        var local = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.FromHours(8));
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(), local, null,
            ConnectionMethod.FindWindowSW, 1,
            [new(0, "a", "b", null, ItemKind.File, default, null, false, true)]);
        Assert.Equal(TimeSpan.Zero, snapshot.CreatedAtUtc.Offset);
    }

    [Fact]
    public void Snapshot_InvalidFormatVersion_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
                ConnectionMethod.FindWindowSW, 0,
                [new(0, "a", "b", null, ItemKind.File, default, null, false, true)]));
    }

    [Fact]
    public void Summary_EmptyGuid_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new DesktopLayoutSnapshotSummary(Guid.Empty, DateTimeOffset.UtcNow, null,
                ConnectionMethod.FindWindowSW, 0, 1));
    }

    [Fact]
    public void Summary_InvalidFormatVersion_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopLayoutSnapshotSummary(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
                ConnectionMethod.FindWindowSW, 0, 0));
    }

    [Fact]
    public void Summary_NegativeIconCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopLayoutSnapshotSummary(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
                ConnectionMethod.FindWindowSW, -1, 1));
    }

    [Fact]
    public void Snapshot_Immutability()
    {
        var icons = new List<DesktopIconSnapshotItem>
        {
            new(0, "a", "b", null, ItemKind.File, default, null, false, true)
        };
        var snapshot = new DesktopLayoutSnapshot(
            Guid.NewGuid(), DateTimeOffset.UtcNow, null, ConnectionMethod.FindWindowSW, 1, icons);
        icons.Clear();
        Assert.Single(snapshot.Icons);

        var collection =
            Assert.IsAssignableFrom<ICollection<DesktopIconSnapshotItem>>(
                snapshot.Icons);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() =>
            collection.Add(new DesktopIconSnapshotItem(
                ordinal: 0,
                stableId: "x",
                displayName: "y",
                parsingName: null,
                itemKind: ItemKind.File,
                pixelPosition: default,
                monitorDeviceName: null,
                isSystemItem: false,
                isFileSystemItem: true)));
    }

    // ── Factory tests ──

    [Fact]
    public void Factory_NullIcons_Throws()
    {
        var factory = new DesktopLayoutSnapshotFactory();
        Assert.Throws<ArgumentNullException>(() =>
            factory.Create(null!, null, ConnectionMethod.FindWindowSW));
    }

    [Fact]
    public void Factory_UsesTimeProvider()
    {
        var fixedTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var tp = new TestTimeProvider(fixedTime);
        var factory = new DesktopLayoutSnapshotFactory(tp);
        var snapshot = factory.Create(
            [new DesktopIconDescriptor("a", "b", null, ItemKind.File, default, null, false, true)],
            null, ConnectionMethod.FindWindowSW);
        Assert.Equal(fixedTime, snapshot.CreatedAtUtc);
    }

    [Fact]
    public void Factory_GeneratesGuid()
    {
        var factory = new DesktopLayoutSnapshotFactory();
        var snapshot = factory.Create(
            [new DesktopIconDescriptor("a", "b", null, ItemKind.File, default, null, false, true)],
            null, ConnectionMethod.FindWindowSW);
        Assert.NotEqual(Guid.Empty, snapshot.SnapshotId);
    }

    [Fact]
    public void Factory_ContinuousOrdinals()
    {
        var factory = new DesktopLayoutSnapshotFactory();
        var snapshot = factory.Create(
            [new DesktopIconDescriptor("a", "a1", null, ItemKind.File, default, null, false, true),
             new DesktopIconDescriptor("b", "b1", null, ItemKind.File, default, null, false, true),
             new DesktopIconDescriptor("c", "c1", null, ItemKind.File, default, null, false, true)],
            null, ConnectionMethod.FindWindowSW);
        for (var i = 0; i < 3; i++) Assert.Equal(i, snapshot.Icons[i].Ordinal);
    }

    [Fact]
    public void Factory_MapsAllFields()
    {
        var factory = new DesktopLayoutSnapshotFactory();
        var icon = new DesktopIconDescriptor(
            "sid", "disp", "C:\\p", ItemKind.Shortcut, new PixelPoint(42, 99), "D1", true, true);
        var snapshot = factory.Create([icon], "T1", ConnectionMethod.EnumerationFallback);
        var item = snapshot.Icons[0];
        Assert.Equal("sid", item.StableId);
        Assert.Equal("disp", item.DisplayName);
        Assert.Equal("C:\\p", item.ParsingName);
        Assert.Equal(ItemKind.Shortcut, item.ItemKind);
        Assert.Equal(42, item.PixelPosition.X);
        Assert.Equal(99, item.PixelPosition.Y);
        Assert.Equal("D1", item.MonitorDeviceName);
        Assert.True(item.IsSystemItem);
        Assert.True(item.IsFileSystemItem);
    }

    [Fact]
    public void Factory_InputListModified_DoesNotAffectSnapshot()
    {
        var factory = new DesktopLayoutSnapshotFactory();
        var input = new List<DesktopIconDescriptor>
        { new("a", "b", null, ItemKind.File, default, null, false, true) };
        var snapshot = factory.Create(input, null, ConnectionMethod.FindWindowSW);
        input.Add(new DesktopIconDescriptor("c", "d", null, ItemKind.File, default, null, false, true));
        Assert.Single(snapshot.Icons);
    }

    // ── SQLite persistence tests ──

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.InitializeAsync();
    }

    [Fact]
    public async Task ListAsync_Empty_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task SaveAndGet_RoundTrip()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var snapshot = CreateSimpleSnapshot();
        await store.SaveAsync(snapshot);
        var restored = await store.GetAsync(snapshot.SnapshotId);
        Assert.NotNull(restored);
        Assert.Equal(snapshot.SnapshotId, restored!.SnapshotId);
        Assert.Equal(snapshot.Icons.Count, restored.Icons.Count);
        for (var i = 0; i < snapshot.Icons.Count; i++)
        {
            var a = snapshot.Icons[i]; var b = restored.Icons[i];
            Assert.Equal(a.Ordinal, b.Ordinal);
            Assert.Equal(a.StableId, b.StableId);
            Assert.Equal(a.DisplayName, b.DisplayName);
            Assert.Equal(a.ParsingName, b.ParsingName);
            Assert.Equal(a.ItemKind, b.ItemKind);
            Assert.Equal(a.PixelPosition, b.PixelPosition);
            Assert.Equal(a.IsSystemItem, b.IsSystemItem);
        }
    }

    [Fact]
    public async Task NegativeCoordinates_RoundTrip()
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero), null,
            ConnectionMethod.EnumerationFallback, 1,
            [new(0, "sid", "test", null, ItemKind.File, new PixelPoint(-1920, -1080), @"\\.\DISPLAY2", false, true)]);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(snapshot);
        var r = await store.GetAsync(snapshot.SnapshotId);
        Assert.Equal(-1920, r!.Icons[0].PixelPosition.X);
        Assert.Equal(-1080, r.Icons[0].PixelPosition.Y);
    }

    [Fact]
    public async Task NullParsingName_RoundTrip()
    {
        var snapshot = new DesktopLayoutSnapshot(Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero), null,
            ConnectionMethod.EnumerationFallback, 1,
            [new(0, "sid", "x", null, ItemKind.SystemItem, new PixelPoint(0, 0), null, true, false)]);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(snapshot);
        Assert.Null((await store.GetAsync(snapshot.SnapshotId))!.Icons[0].ParsingName);
    }

    [Fact]
    public async Task SystemItem_RoundTrip()
    {
        var s = CreateSimpleSnapshot(kind: ItemKind.SystemItem);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(s);
        var r = await store.GetAsync(s.SnapshotId);
        Assert.True(r!.Icons[0].IsSystemItem);
        Assert.False(r.Icons[0].IsFileSystemItem);
    }

    [Fact]
    public async Task ListAsync_DescendingOrder()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var s1 = CreateSimpleSnapshot(id: Guid.NewGuid());
        var s2 = new DesktopLayoutSnapshot(Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
            s1.TargetMonitorDeviceName, s1.ConnectionMethod, s1.FormatVersion, s1.Icons);
        await store.SaveAsync(s1); await store.SaveAsync(s2);
        var list = await store.ListAsync(limit: 100);
        Assert.True(list[0].CreatedAtUtc >= list[1].CreatedAtUtc);
    }

    [Fact]
    public async Task Icons_OrdinalOrder()
    {
        var s = CreateSimpleSnapshot(iconCount: 5);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(s);
        var r = await store.GetAsync(s.SnapshotId);
        for (var i = 0; i < 5; i++) Assert.Equal(i, r!.Icons[i].Ordinal);
    }

    [Fact]
    public async Task Delete_Existing_ReturnsTrue()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var s = CreateSimpleSnapshot(); await store.SaveAsync(s);
        Assert.True(await store.DeleteAsync(s.SnapshotId));
        Assert.Null(await store.GetAsync(s.SnapshotId));
    }

    [Fact]
    public async Task Delete_NonExisting_ReturnsFalse()
    {
        var store = CreateStore(); await store.InitializeAsync();
        Assert.False(await store.DeleteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Save_DuplicateSnapshotId_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();

        // 创建两个 SnapshotId 相同但内容明显不同的快照
        var sharedId = Guid.NewGuid();
        var original = new DesktopLayoutSnapshot(sharedId,
            new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
            @"\\.\DISPLAY1", ConnectionMethod.FindWindowSW, 1,
            [new DesktopIconSnapshotItem(0, "original_id", "original_name",
                @"C:\original.txt", ItemKind.File, new PixelPoint(100, 200),
                @"\\.\DISPLAY1", false, true)]);
        var conflicting = new DesktopLayoutSnapshot(sharedId,
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            @"\\.\DISPLAY2", ConnectionMethod.EnumerationFallback, 1,
            [new DesktopIconSnapshotItem(0, "conflict_id", "conflict_name",
                @"C:\conflict.txt", ItemKind.Folder, new PixelPoint(999, 888),
                @"\\.\DISPLAY2", true, false)]);

        await store.SaveAsync(original);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(conflicting));
        Assert.Contains(sharedId.ToString("D"), ex.Message);
        Assert.IsType<SqliteException>(ex.InnerException);

        // 确认原数据完整未被覆盖
        var restored = await store.GetAsync(sharedId);
        Assert.NotNull(restored);
        Assert.Equal(original.CreatedAtUtc, restored!.CreatedAtUtc);
        Assert.Equal(original.TargetMonitorDeviceName, restored.TargetMonitorDeviceName);
        Assert.Equal(original.ConnectionMethod, restored.ConnectionMethod);
        Assert.Single(restored.Icons);
        Assert.Equal("original_id", restored.Icons[0].StableId);
        Assert.Equal("original_name", restored.Icons[0].DisplayName);
        Assert.Equal(new PixelPoint(100, 200), restored.Icons[0].PixelPosition);
    }

    [Fact]
    public async Task Save_NullSnapshot_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!));
    }

    [Fact]
    public async Task Get_EmptyGuid_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(Guid.Empty));
    }

    [Fact]
    public async Task Delete_EmptyGuid_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(Guid.Empty));
    }

    [Fact]
    public async Task ListAsync_InvalidLimit_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListAsync(0));
    }

    [Fact]
    public async Task ConnectionMethod_RoundTrip_AllValues()
    {
        foreach (var method in Enum.GetValues<ConnectionMethod>())
        {
            var store = CreateStore(); await store.InitializeAsync();
            var s = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null, method, 1,
                [new(0, "a", "b", null, ItemKind.File, default, null, false, true)]);
            await store.SaveAsync(s);
            Assert.Equal(method, (await store.GetAsync(s.SnapshotId))!.ConnectionMethod);
        }
    }

    [Fact]
    public async Task ItemKind_RoundTrip_AllValues()
    {
        foreach (var kind in Enum.GetValues<ItemKind>())
        {
            var store = CreateStore(); await store.InitializeAsync();
            var s = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
                ConnectionMethod.FindWindowSW, 1,
                [new(0, "a", "b", null, kind, default, null, kind == ItemKind.SystemItem, kind != ItemKind.SystemItem)]);
            await store.SaveAsync(s);
            Assert.Equal(kind, (await store.GetAsync(s.SnapshotId))!.Icons[0].ItemKind);
        }
    }

    [Fact]
    public async Task BoolTrue_RoundTrip()
    {
        var s = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
            ConnectionMethod.FindWindowSW, 1,
            [new(0, "a", "b", null, ItemKind.File, default, null, true, true)]);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(s);
        var r = await store.GetAsync(s.SnapshotId);
        Assert.True(r!.Icons[0].IsSystemItem);
        Assert.True(r.Icons[0].IsFileSystemItem);
    }

    [Fact]
    public async Task BoolFalse_RoundTrip()
    {
        var s = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
            ConnectionMethod.FindWindowSW, 1,
            [new(0, "a", "b", null, ItemKind.File, default, null, false, false)]);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(s);
        var r = await store.GetAsync(s.SnapshotId);
        Assert.False(r!.Icons[0].IsSystemItem);
        Assert.False(r.Icons[0].IsFileSystemItem);
    }

    [Fact]
    public async Task NullMonitorDeviceName_RoundTrip()
    {
        var s = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
            ConnectionMethod.FindWindowSW, 1,
            [new(0, "a", "b", null, ItemKind.File, default, null, false, true)]);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(s);
        Assert.Null((await store.GetAsync(s.SnapshotId))!.Icons[0].MonitorDeviceName);
    }

    [Fact]
    public async Task DuplicateStableId_RoundTrip()
    {
        var s = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
            ConnectionMethod.FindWindowSW, 1,
            [new(0, "dup", "a", null, ItemKind.File, default, null, false, true),
             new(1, "dup", "b", null, ItemKind.File, default, null, false, true)]);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(s);
        var r = await store.GetAsync(s.SnapshotId);
        Assert.Equal(2, r!.Icons.Count);
    }

    [Fact]
    public async Task EmptyIcons_RoundTrip()
    {
        var s = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
            ConnectionMethod.FindWindowSW, 1, []);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(s);
        var r = await store.GetAsync(s.SnapshotId);
        Assert.Empty(r!.Icons);
    }

    [Fact]
    public async Task ListAsync_LimitEffective()
    {
        var store = CreateStore(); await store.InitializeAsync();
        for (var i = 0; i < 5; i++)
        {
            var s = new DesktopLayoutSnapshot(Guid.NewGuid(),
                new DateTimeOffset(2026, 7, 22, i, 0, 0, TimeSpan.Zero), null,
                ConnectionMethod.FindWindowSW, 1, []);
            await store.SaveAsync(s);
        }
        Assert.Equal(3, (await store.ListAsync(limit: 3)).Count);
    }

    [Fact]
    public async Task StoreReopened_StillReadable()
    {
        var store1 = CreateStore(); await store1.InitializeAsync();
        var s = CreateSimpleSnapshot(); await store1.SaveAsync(s);
        var store2 = CreateStore();
        var r = await store2.GetAsync(s.SnapshotId);
        Assert.NotNull(r);
    }

    [Fact]
    public async Task PragmaForeignKeys_ReturnsExpected()
    {
        var store = CreateStore(); await store.InitializeAsync();
        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Delete_CascadesToItems()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var s = CreateSimpleSnapshot(iconCount: 3); await store.SaveAsync(s);
        await store.DeleteAsync(s.SnapshotId);

        // 直接查询子表确认已级联删除
        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM desktop_layout_snapshot_items WHERE snapshot_id = @id";
        cmd.Parameters.AddWithValue("@id", s.SnapshotId.ToString());
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Save_RollsBackOnTriggerFailure()
    {
        var store = CreateStore(); await store.InitializeAsync();

        using (var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS test_force_failure
                BEFORE INSERT ON desktop_layout_snapshot_items
                WHEN NEW.ordinal = 1
                BEGIN
                    SELECT RAISE(ABORT, 'forced snapshot item failure');
                END;
            ";
            cmd.ExecuteNonQuery();
        }

        var s = CreateSimpleSnapshot(iconCount: 3, id: Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync(s));
        Assert.Contains("forced snapshot item failure", exception.Message, StringComparison.OrdinalIgnoreCase);

        // 验证 Trigger 故障未被误报为重复 SnapshotId（不应是 InvalidOperationException）
        // 验证父子表均无记录
        using (var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM desktop_layout_snapshots WHERE snapshot_id = @id";
            cmd.Parameters.AddWithValue("@id", s.SnapshotId.ToString());
            Assert.Equal(0L, cmd.ExecuteScalar());

            cmd.CommandText = "SELECT COUNT(1) FROM desktop_layout_snapshot_items WHERE snapshot_id = @id";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public async Task UnicodeAndSpecialCharacters_RoundTrip()
    {
        var s = new DesktopLayoutSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, null,
            ConnectionMethod.FindWindowSW, 1,
            [new(0, "测试标识符", "中文显示名称 🖥️", @"C:\Users\测试\Desktop\文件.txt",
                ItemKind.Folder, new PixelPoint(42, 99), null, false, true)]);
        var store = CreateStore(); await store.InitializeAsync();
        await store.SaveAsync(s);
        var r = await store.GetAsync(s.SnapshotId);
        Assert.Equal("测试标识符", r!.Icons[0].StableId);
        Assert.Equal("中文显示名称 🖥️", r.Icons[0].DisplayName);
    }

    [Fact]
    public async Task Get_InvalidFormatVersion_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var snapshotId = new Guid("11111111-1111-1111-1111-111111111111");

        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
        conn.Open();

        using (var pragmaCmd = conn.CreateCommand())
        { pragmaCmd.CommandText = "PRAGMA ignore_check_constraints = ON"; pragmaCmd.ExecuteNonQuery(); }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO desktop_layout_snapshots
                (snapshot_id, created_at_utc, connection_method, format_version, icon_count)
            VALUES (@id, '2026-07-22T00:00:00.0000000+00:00', 0, 0, 1);
        ";
        cmd.Parameters.AddWithValue("@id", snapshotId.ToString());
        cmd.ExecuteNonQuery();

        using (var pragmaCmd = conn.CreateCommand())
        { pragmaCmd.CommandText = "PRAGMA ignore_check_constraints = OFF"; pragmaCmd.ExecuteNonQuery(); }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetAsync(snapshotId));
        Assert.Contains("0", ex.Message);
    }

    [Fact]
    public async Task ListAsync_SameCreatedAtUtc_UsesSnapshotIdDescending()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var sameTime = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

        // 创建多个同一时间、不同 SnapshotId 的快照
        var id1 = new Guid("11111111-1111-1111-1111-111111111111");
        var id2 = new Guid("22222222-2222-2222-2222-222222222222");
        var id3 = new Guid("33333333-3333-3333-3333-333333333333");

        await store.SaveAsync(new DesktopLayoutSnapshot(id3, sameTime, null,
            ConnectionMethod.FindWindowSW, 1, []));
        await store.SaveAsync(new DesktopLayoutSnapshot(id1, sameTime, null,
            ConnectionMethod.FindWindowSW, 1, []));
        await store.SaveAsync(new DesktopLayoutSnapshot(id2, sameTime, null,
            ConnectionMethod.FindWindowSW, 1, []));

        var list = await store.ListAsync(limit: 10);
        Assert.True(list.Count >= 3);
        Assert.Equal(
            new[] { id3, id2, id1 },
            list.Select(x => x.SnapshotId).ToArray());
    }

    [Fact]
    public async Task Get_InvalidConnectionMethod_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var s = CreateSimpleSnapshot(); await store.SaveAsync(s);

        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE desktop_layout_snapshots SET connection_method = 999 WHERE snapshot_id = @id";
        cmd.Parameters.AddWithValue("@id", s.SnapshotId.ToString());
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(s.SnapshotId));
    }

    [Fact]
    public async Task Get_InvalidItemKind_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var s = CreateSimpleSnapshot(iconCount: 1); await store.SaveAsync(s);

        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE desktop_layout_snapshot_items SET item_kind = 999 WHERE snapshot_id = @id AND ordinal = 0";
        cmd.Parameters.AddWithValue("@id", s.SnapshotId.ToString());
        cmd.ExecuteNonQuery();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(s.SnapshotId));
    }

    [Fact]
    public async Task Get_InvalidBoolValue_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var s = CreateSimpleSnapshot(iconCount: 1); await store.SaveAsync(s);

        // 生产表有 CHECK(is_system_item IN (0,1))，UPDATE 会被拒绝。
        // 使用 PRAGMA ignore_check_constraints 模拟数据库损坏场景。
        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
        conn.Open();
        using (var pragmaCmd = conn.CreateCommand())
        { pragmaCmd.CommandText = "PRAGMA ignore_check_constraints = ON"; pragmaCmd.ExecuteNonQuery(); }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE desktop_layout_snapshot_items SET is_system_item = 2 WHERE snapshot_id = @id AND ordinal = 0";
        cmd.Parameters.AddWithValue("@id", s.SnapshotId.ToString());
        cmd.ExecuteNonQuery();
        using (var pragmaCmd = conn.CreateCommand())
        { pragmaCmd.CommandText = "PRAGMA ignore_check_constraints = OFF"; pragmaCmd.ExecuteNonQuery(); }

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(s.SnapshotId));
    }

    [Fact]
    public async Task Get_IconCountMismatch_Throws()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var s = CreateSimpleSnapshot(iconCount: 2, id: Guid.NewGuid());
        await store.SaveAsync(s);

        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE desktop_layout_snapshots SET icon_count = 5 WHERE snapshot_id = @id";
        cmd.Parameters.AddWithValue("@id", s.SnapshotId.ToString());
        cmd.ExecuteNonQuery();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(s.SnapshotId));
        Assert.Contains(s.SnapshotId.ToString("D"), ex.Message);
        Assert.Contains("5", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public async Task SaveAsync_PreCanceledToken_DoesNotPersistSnapshot()
    {
        var store = CreateStore(); await store.InitializeAsync();
        var snapshot = CreateSimpleSnapshot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.SaveAsync(snapshot, cts.Token));

        // 确认未写入
        Assert.Null(await store.GetAsync(snapshot.SnapshotId));

        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM desktop_layout_snapshots WHERE snapshot_id = @id";
        cmd.Parameters.AddWithValue("@id", snapshot.SnapshotId.ToString());
        Assert.Equal(0L, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(1) FROM desktop_layout_snapshot_items WHERE snapshot_id = @id";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _fixed;
        public TestTimeProvider(DateTimeOffset fixedTime) => _fixed = fixedTime;
        public override DateTimeOffset GetUtcNow() => _fixed;
    }
}
