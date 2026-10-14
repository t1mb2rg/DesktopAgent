using System.Globalization;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// 站定授权的 SQLite store。
///
/// 与网格事实 store 同构：写是追加式的，一条授权（或撤销）写入后就不再修改，
/// "当前授权"由 device_name + topology_fingerprint 下最新一条记录选出。
/// 撤销是追加一条 Revoked，而不是删除历史——"它被收回过"这件事本身必须可读。
///
/// 读取一律带上当前 topology fingerprint：授权是环境绑定的，
/// 拓扑变化后旧授权不再被选出；但记录仍在表里，供调用方显式降级时解释原因。
/// </summary>
public sealed class SqliteExplorerGridDisplayAuthorizationStore : IExplorerGridDisplayAuthorizationStore
{
    private const string SelectColumns = @"
        authorization_id, device_name, topology_fingerprint,
        state, authorized_at_utc, recorded_at_utc";

    // recorded_at_utc 是写入时刻，其时钟粒度可能让连续两次 Append 得到同一个值，
    // 因此补上 authorized_at_utc DESC 与 rowid DESC 作为确定性的后续排序键：
    // 并列时后写的记录（也就是用户更近一次的决定）胜出。
    private const string ActiveOrderBy = "recorded_at_utc DESC, authorized_at_utc DESC, rowid DESC";

    private readonly string _databasePath;

    public SqliteExplorerGridDisplayAuthorizationStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException(
                "数据库路径不能为 null、空或纯空白。", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(_databasePath);
        if (directory is not null && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        using var connection = CreateConnection();
        SqliteDesktopAgentSchema.Initialize(connection, cancellationToken);
        return Task.CompletedTask;
    }

    public Task AppendAsync(
        ExplorerGridDisplayAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(authorization);

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO explorer_grid_display_authorizations
                    (authorization_id, device_name, topology_fingerprint,
                     state, authorized_at_utc, recorded_at_utc)
                VALUES
                    (@authorization_id, @device_name, @topology_fingerprint,
                     @state, @authorized_at_utc, @recorded_at_utc);
            ";
            insert.Parameters.AddWithValue("@authorization_id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("@device_name", authorization.DeviceName);
            insert.Parameters.AddWithValue("@topology_fingerprint", authorization.TopologyFingerprint);
            insert.Parameters.AddWithValue("@state", (int)authorization.State);
            insert.Parameters.AddWithValue("@authorized_at_utc", FormatTimestamp(authorization.AuthorizedAtUtc));
            insert.Parameters.AddWithValue("@recorded_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));
            insert.ExecuteNonQuery();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<ExplorerGridDisplayAuthorization?> GetActiveAsync(
        string deviceName,
        string currentTopologyFingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("DeviceName 不能为空。", nameof(deviceName));
        if (string.IsNullOrWhiteSpace(currentTopologyFingerprint))
            throw new ArgumentException("TopologyFingerprint 不能为空。", nameof(currentTopologyFingerprint));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            SELECT {SelectColumns}
            FROM explorer_grid_display_authorizations
            WHERE device_name = @device_name
              AND topology_fingerprint = @topology_fingerprint
            ORDER BY {ActiveOrderBy}
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@device_name", deviceName.Trim());
        command.Parameters.AddWithValue("@topology_fingerprint", currentTopologyFingerprint.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<ExplorerGridDisplayAuthorization?>(null);

        return Task.FromResult<ExplorerGridDisplayAuthorization?>(ReadAuthorization(reader));
    }

    public Task<ExplorerGridDisplayAuthorization?> GetLatestByDeviceAsync(
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("DeviceName 不能为空。", nameof(deviceName));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        // 不带 topology_fingerprint 条件：调用方需要能读到"曾经授权过、但已随拓扑变化失效"，
        // 才能向用户显式降级，而不是把这段历史当成不存在。
        command.CommandText = $@"
            SELECT {SelectColumns}
            FROM explorer_grid_display_authorizations
            WHERE device_name = @device_name
            ORDER BY {ActiveOrderBy}
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@device_name", deviceName.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<ExplorerGridDisplayAuthorization?>(null);

        return Task.FromResult<ExplorerGridDisplayAuthorization?>(ReadAuthorization(reader));
    }

    public Task<IReadOnlyList<ExplorerGridDisplayAuthorization>> ListActiveAsync(
        string currentTopologyFingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(currentTopologyFingerprint))
            throw new ArgumentException("TopologyFingerprint 不能为空。", nameof(currentTopologyFingerprint));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            SELECT {SelectColumns}
            FROM explorer_grid_display_authorizations
            WHERE topology_fingerprint = @topology_fingerprint
            ORDER BY device_name ASC, {ActiveOrderBy};
        ";
        command.Parameters.AddWithValue("@topology_fingerprint", currentTopologyFingerprint.Trim());

        var results = new List<ExplorerGridDisplayAuthorization>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var authorization = ReadAuthorization(reader);
            // 每块显示器只取最新一条；结果已按 device_name 排序，首条即当前授权。
            if (results.Count == 0 || results[^1].DeviceName != authorization.DeviceName)
                results.Add(authorization);
        }

        return Task.FromResult<IReadOnlyList<ExplorerGridDisplayAuthorization>>(results);
    }

    private SqliteConnection CreateConnection() =>
        SqliteDesktopAgentConnectionFactory.Open(_databasePath);

    private static ExplorerGridDisplayAuthorization ReadAuthorization(SqliteDataReader reader)
    {
        var state = reader.GetInt32(3);
        if (!Enum.IsDefined((ExplorerGridAuthorizationState)state))
            throw new InvalidDataException($"state 值 {state} 无效。");

        return new ExplorerGridDisplayAuthorization(
            (ExplorerGridAuthorizationState)state,
            reader.GetString(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(4), "authorized_at_utc"));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value, string column)
    {
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            throw new InvalidDataException($"{column} 无法解析为 UTC 时间戳。");

        return parsed.ToUniversalTime();
    }
}
