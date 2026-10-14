using System.Globalization;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// 网格事实的 SQLite store。
///
/// 观测是追加式的：一条事实一旦写入就不修改。
/// "当前事实"由 device_name + topology_fingerprint 下最新一条观测选出；
/// 拓扑变化后旧观测不再被选出，但历史证据仍在。
/// </summary>
public sealed class SqliteExplorerGridMeasurementStore : IExplorerGridMeasurementStore
{
    private const string SelectColumns = @"
        observation_id, device_name, topology_fingerprint, evidence_source,
        spacing_x, spacing_y, phase_x, phase_y,
        sample_count, consensus_count, spacing_constant, phase_constant,
        observed_at_utc, recorded_at_utc";

    private readonly string _databasePath;

    public SqliteExplorerGridMeasurementStore(string databasePath)
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

    public Task AppendObservationAsync(
        ExplorerGridMeasurement observation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(observation);

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO explorer_grid_measurements
                    (observation_id, device_name, topology_fingerprint, evidence_source,
                     spacing_x, spacing_y, phase_x, phase_y,
                     sample_count, consensus_count, spacing_constant, phase_constant,
                     observed_at_utc, recorded_at_utc)
                VALUES
                    (@observation_id, @device_name, @topology_fingerprint, @evidence_source,
                     @spacing_x, @spacing_y, @phase_x, @phase_y,
                     @sample_count, @consensus_count, @spacing_constant, @phase_constant,
                     @observed_at_utc, @recorded_at_utc);
            ";
            insert.Parameters.AddWithValue("@observation_id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("@device_name", observation.DeviceName);
            insert.Parameters.AddWithValue("@topology_fingerprint", observation.TopologyFingerprint);
            insert.Parameters.AddWithValue("@evidence_source", (int)observation.Source);
            insert.Parameters.AddWithValue("@spacing_x", observation.Spacing.X);
            insert.Parameters.AddWithValue("@spacing_y", observation.Spacing.Y);
            insert.Parameters.AddWithValue("@phase_x", observation.Phase.X);
            insert.Parameters.AddWithValue("@phase_y", observation.Phase.Y);
            insert.Parameters.AddWithValue("@sample_count", observation.SampleCount);
            insert.Parameters.AddWithValue("@consensus_count", observation.ConsensusCount);
            insert.Parameters.AddWithValue("@spacing_constant", observation.SpacingConstant ? 1 : 0);
            insert.Parameters.AddWithValue("@phase_constant", observation.PhaseConstant ? 1 : 0);
            insert.Parameters.AddWithValue("@observed_at_utc", FormatTimestamp(observation.ObservedAtUtc));
            insert.Parameters.AddWithValue("@recorded_at_utc", FormatTimestamp(DateTimeOffset.UtcNow));
            insert.ExecuteNonQuery();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<ExplorerGridMeasurement?> GetActiveAsync(
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
            FROM explorer_grid_measurements
            WHERE device_name = @device_name
              AND topology_fingerprint = @topology_fingerprint
            ORDER BY observed_at_utc DESC, recorded_at_utc DESC
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@device_name", deviceName.Trim());
        command.Parameters.AddWithValue("@topology_fingerprint", currentTopologyFingerprint.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<ExplorerGridMeasurement?>(null);

        return Task.FromResult<ExplorerGridMeasurement?>(ReadMeasurement(reader));
    }

    public Task<ExplorerGridMeasurement?> GetLatestByDeviceAsync(
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("DeviceName 不能为空。", nameof(deviceName));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            SELECT {SelectColumns}
            FROM explorer_grid_measurements
            WHERE device_name = @device_name
            ORDER BY observed_at_utc DESC, recorded_at_utc DESC
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@device_name", deviceName.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<ExplorerGridMeasurement?>(null);

        return Task.FromResult<ExplorerGridMeasurement?>(ReadMeasurement(reader));
    }

    public Task<IReadOnlyList<ExplorerGridMeasurement>> ListActiveAsync(
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
            FROM explorer_grid_measurements
            WHERE topology_fingerprint = @topology_fingerprint
            ORDER BY device_name ASC, observed_at_utc DESC, recorded_at_utc DESC;
        ";
        command.Parameters.AddWithValue("@topology_fingerprint", currentTopologyFingerprint.Trim());

        var results = new List<ExplorerGridMeasurement>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var measurement = ReadMeasurement(reader);
            // 每块显示器只取最新一条；结果已按 device_name 排序，首条即当前事实。
            if (results.Count == 0 || results[^1].DeviceName != measurement.DeviceName)
                results.Add(measurement);
        }

        return Task.FromResult<IReadOnlyList<ExplorerGridMeasurement>>(results);
    }

    private SqliteConnection CreateConnection() =>
        SqliteDesktopAgentConnectionFactory.Open(_databasePath);

    private static ExplorerGridMeasurement ReadMeasurement(SqliteDataReader reader)
    {
        var source = reader.GetInt32(3);
        if (!Enum.IsDefined((ExplorerGridEvidenceSource)source))
            throw new InvalidDataException($"evidence_source 值 {source} 无效。");

        return new ExplorerGridMeasurement(
            (ExplorerGridEvidenceSource)source,
            reader.GetString(1),
            new PixelPoint(reader.GetInt32(4), reader.GetInt32(5)),
            new PixelPoint(reader.GetInt32(6), reader.GetInt32(7)),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10) == 1,
            reader.GetInt32(11) == 1,
            reader.GetString(2),
            ParseTimestamp(reader.GetString(12), "observed_at_utc"));
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
