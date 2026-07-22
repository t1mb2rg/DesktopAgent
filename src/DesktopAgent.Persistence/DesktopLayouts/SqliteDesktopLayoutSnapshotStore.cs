using System.Globalization;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.Geometry;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.DesktopLayouts;

public sealed class SqliteDesktopLayoutSnapshotStore : IDesktopLayoutSnapshotStore
{
    private readonly string _databasePath;

    public SqliteDesktopLayoutSnapshotStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径不能为 null、空或纯空白。", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(_databasePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA foreign_keys = ON;
            PRAGMA user_version = 1;

            CREATE TABLE IF NOT EXISTS desktop_layout_snapshots (
                snapshot_id              TEXT PRIMARY KEY NOT NULL,
                created_at_utc           TEXT NOT NULL,
                target_monitor_device_name TEXT NULL,
                connection_method        INTEGER NOT NULL,
                format_version           INTEGER NOT NULL,
                icon_count               INTEGER NOT NULL
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
        return Task.CompletedTask;
    }

    private const int SqliteConstraintPrimaryKey = 1555;
    private const int SqliteConstraintUnique = 2067;

    private static bool IsSnapshotIdConflict(SqliteException exception)
    {
        return exception.SqliteExtendedErrorCode is
            SqliteConstraintPrimaryKey or
            SqliteConstraintUnique;
    }

    public async Task SaveAsync(DesktopLayoutSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.FormatVersion < 1)
            throw new ArgumentException($"Unsupported FormatVersion: {snapshot.FormatVersion}");

        using var connection = CreateConnection();
        using var tx = connection.BeginTransaction();

        // 父表 INSERT — 仅主键/唯一约束冲突转换为重复 SnapshotId
        try
        {
            using var ins = connection.CreateCommand();
            ins.CommandText = @"
                INSERT INTO desktop_layout_snapshots
                    (snapshot_id, created_at_utc, target_monitor_device_name,
                     connection_method, format_version, icon_count)
                VALUES
                    (@id, @created, @target, @method, @version, @count);
            ";
            ins.Parameters.AddWithValue("@id", snapshot.SnapshotId.ToString());
            ins.Parameters.AddWithValue("@created",
                snapshot.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            ins.Parameters.AddWithValue("@target", (object?)snapshot.TargetMonitorDeviceName ?? DBNull.Value);
            ins.Parameters.AddWithValue("@method", (int)snapshot.ConnectionMethod);
            ins.Parameters.AddWithValue("@version", snapshot.FormatVersion);
            ins.Parameters.AddWithValue("@count", snapshot.Icons.Count);
            ins.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsSnapshotIdConflict(ex))
        {
            throw new InvalidOperationException(
                $"快照 {snapshot.SnapshotId:D} 已存在，不能重复保存。", ex);
        }

        // 插入图项
        foreach (var item in snapshot.Icons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ins = connection.CreateCommand();
            ins.CommandText = @"
                INSERT INTO desktop_layout_snapshot_items
                    (snapshot_id, ordinal, stable_id, display_name,
                     parsing_name, item_kind, x, y,
                     monitor_device_name, is_system_item, is_file_system_item)
                VALUES
                    (@sid, @ord, @stable, @display,
                     @parsing, @kind, @x, @y,
                     @mon, @sys, @fs);
            ";
            ins.Parameters.AddWithValue("@sid", snapshot.SnapshotId.ToString());
            ins.Parameters.AddWithValue("@ord", item.Ordinal);
            ins.Parameters.AddWithValue("@stable", item.StableId);
            ins.Parameters.AddWithValue("@display", item.DisplayName);
            ins.Parameters.AddWithValue("@parsing", (object?)item.ParsingName ?? DBNull.Value);
            ins.Parameters.AddWithValue("@kind", (int)item.ItemKind);
            ins.Parameters.AddWithValue("@x", item.PixelPosition.X);
            ins.Parameters.AddWithValue("@y", item.PixelPosition.Y);
            ins.Parameters.AddWithValue("@mon", (object?)item.MonitorDeviceName ?? DBNull.Value);
            ins.Parameters.AddWithValue("@sys", item.IsSystemItem ? 1 : 0);
            ins.Parameters.AddWithValue("@fs", item.IsFileSystemItem ? 1 : 0);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
        await Task.CompletedTask;
    }

    public async Task<DesktopLayoutSnapshot?> GetAsync(
        Guid snapshotId, CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty)
            throw new ArgumentException("SnapshotId 不能为 Guid.Empty。");

        using var connection = CreateConnection();

        // 读取父记录
        using (var sel = connection.CreateCommand())
        {
            sel.CommandText = @"
                SELECT snapshot_id, created_at_utc, target_monitor_device_name,
                       connection_method, format_version, icon_count
                FROM desktop_layout_snapshots
                WHERE snapshot_id = @id
            ";
            sel.Parameters.AddWithValue("@id", snapshotId.ToString());

            using var reader = sel.ExecuteReader();
            if (!reader.Read()) return null;

            var created = DateTimeOffset.Parse(reader.GetString(1),
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var target = reader.IsDBNull(2) ? null : reader.GetString(2);
            var methodVal = reader.GetInt32(3);
            if (!Enum.IsDefined((ConnectionMethod)methodVal))
                throw new InvalidOperationException($"无效的 ConnectionMethod 值: {methodVal}");
            var method = (ConnectionMethod)methodVal;
            var formatVersion = reader.GetInt32(4);
            var declaredIconCount = reader.GetInt32(5);

            if (formatVersion < 1)
                throw new InvalidOperationException($"不支持的快照格式版本: {formatVersion}");

            // 读取图项
            var items = ReadItems(connection, snapshotId);
            if (items.Count != declaredIconCount)
                throw new InvalidDataException(
                    $"快照 {snapshotId:D} 数据不一致：父表声明 {declaredIconCount} 个图标，实际读取 {items.Count} 个图标。");
            return new DesktopLayoutSnapshot(
                snapshotId, created, target, method, formatVersion, items);
        }
    }

    private static List<DesktopIconSnapshotItem> ReadItems(
        SqliteConnection connection, Guid snapshotId)
    {
        var items = new List<DesktopIconSnapshotItem>();
        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT ordinal, stable_id, display_name, parsing_name,
                   item_kind, x, y, monitor_device_name,
                   is_system_item, is_file_system_item
            FROM desktop_layout_snapshot_items
            WHERE snapshot_id = @id
            ORDER BY ordinal ASC
        ";
        sel.Parameters.AddWithValue("@id", snapshotId.ToString());
        using var reader = sel.ExecuteReader();

        while (reader.Read())
        {
            var kindVal = reader.GetInt32(4);
            if (!Enum.IsDefined((ItemKind)kindVal))
                throw new InvalidOperationException($"无效的 ItemKind 值: {kindVal}");

            var sysVal = reader.GetInt32(8);
            if (sysVal != 0 && sysVal != 1)
                throw new InvalidOperationException($"is_system_item 值无效: {sysVal}");
            var fsVal = reader.GetInt32(9);
            if (fsVal != 0 && fsVal != 1)
                throw new InvalidOperationException($"is_file_system_item 值无效: {fsVal}");

            items.Add(new DesktopIconSnapshotItem(
                ordinal: reader.GetInt32(0),
                stableId: reader.GetString(1),
                displayName: reader.GetString(2),
                parsingName: reader.IsDBNull(3) ? null : reader.GetString(3),
                itemKind: (ItemKind)kindVal,
                pixelPosition: new PixelPoint(reader.GetInt32(5), reader.GetInt32(6)),
                monitorDeviceName: reader.IsDBNull(7) ? null : reader.GetString(7),
                isSystemItem: sysVal == 1,
                isFileSystemItem: fsVal == 1));
        }

        return items;
    }

    public async Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(
        int limit = 50, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "limit 必须大于 0。");
        if (limit > 500) throw new ArgumentOutOfRangeException(nameof(limit), "limit 不能超过 500。");

        using var connection = CreateConnection();
        using var sel = connection.CreateCommand();
        sel.CommandText = @"
            SELECT snapshot_id, created_at_utc, target_monitor_device_name,
                   connection_method, icon_count, format_version
            FROM desktop_layout_snapshots
            ORDER BY created_at_utc DESC, snapshot_id ASC
            LIMIT @limit
        ";
        sel.Parameters.AddWithValue("@limit", limit);

        var results = new List<DesktopLayoutSnapshotSummary>();
        using var reader = sel.ExecuteReader();
        while (reader.Read())
        {
            var sid = Guid.Parse(reader.GetString(0));
            var created = DateTimeOffset.Parse(reader.GetString(1),
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var target = reader.IsDBNull(2) ? null : reader.GetString(2);
            var methodVal = reader.GetInt32(3);
            if (!Enum.IsDefined((ConnectionMethod)methodVal))
                throw new InvalidOperationException($"无效的 ConnectionMethod 值: {methodVal}");
            var method = (ConnectionMethod)methodVal;
            var iconCount = reader.GetInt32(4);
            var formatVersion = reader.GetInt32(5);

            results.Add(new DesktopLayoutSnapshotSummary(
                sid, created, target, method, iconCount, formatVersion));
        }

        return results;
    }

    public async Task<bool> DeleteAsync(
        Guid snapshotId, CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty)
            throw new ArgumentException("SnapshotId 不能为 Guid.Empty。");

        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM desktop_layout_snapshots WHERE snapshot_id = @id";
        cmd.Parameters.AddWithValue("@id", snapshotId.ToString());
        var rows = cmd.ExecuteNonQuery();
        return rows > 0;
    }
}
