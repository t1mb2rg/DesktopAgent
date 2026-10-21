using System.Globalization;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.Intelligence;

/// <summary>
/// 纯语义桌面理解快照的 SQLite Store。
/// 只保存历史事实，不计算 freshness，也不恢复任何 Workspace / Preview / Writer authority。
/// </summary>
public sealed class SqliteDesktopSemanticUnderstandingStore : IDesktopSemanticUnderstandingStore
{
    private readonly string _databasePath;

    public SqliteDesktopSemanticUnderstandingStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径不能为 null、空或纯空白。", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
    }

    private SqliteConnection CreateConnection() =>
        SqliteDesktopAgentConnectionFactory.Open(_databasePath);

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

    public Task AddAsync(
        DesktopSemanticUnderstandingSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);

        var payloadJson = DesktopSemanticUnderstandingJsonSerializer.Serialize(snapshot.Understanding);

        using var connection = CreateConnection();

        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = @"
                SELECT 1
                FROM desktop_semantic_understandings
                WHERE understanding_id = @id
                LIMIT 1;
            ";
            exists.Parameters.AddWithValue("@id", snapshot.UnderstandingId.ToString("D"));
            if (exists.ExecuteScalar() is not null)
            {
                throw new InvalidOperationException(
                    $"UnderstandingId \"{snapshot.UnderstandingId:D}\" 已存在；理解快照不可覆盖。");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO desktop_semantic_understandings
                (understanding_id, created_at_utc, source_observation_id,
                 source_state_fingerprint, source_item_count,
                 payload_format_version, payload_json)
            VALUES
                (@understanding_id, @created_at_utc, @source_observation_id,
                 @source_state_fingerprint, @source_item_count,
                 @payload_format_version, @payload_json);
        ";
        command.Parameters.AddWithValue("@understanding_id", snapshot.UnderstandingId.ToString("D"));
        command.Parameters.AddWithValue(
            "@created_at_utc",
            snapshot.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@source_observation_id", snapshot.SourceObservationId.ToString("D"));
        command.Parameters.AddWithValue("@source_state_fingerprint", snapshot.SourceStateFingerprint);
        command.Parameters.AddWithValue("@source_item_count", snapshot.SourceItemCount);
        command.Parameters.AddWithValue(
            "@payload_format_version",
            DesktopSemanticUnderstandingJsonSerializer.CurrentPayloadFormatVersion);
        command.Parameters.AddWithValue("@payload_json", payloadJson);

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"UnderstandingId \"{snapshot.UnderstandingId:D}\" 无法追加；持久化约束拒绝覆盖或无效数据。",
                ex);
        }

        return Task.CompletedTask;
    }

    public Task<DesktopSemanticUnderstandingSnapshot?> GetAsync(
        Guid understandingId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (understandingId == Guid.Empty)
            throw new ArgumentException("UnderstandingId 不能为 Guid.Empty。", nameof(understandingId));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT understanding_id, created_at_utc, source_observation_id,
                   source_state_fingerprint, source_item_count,
                   payload_format_version, payload_json
            FROM desktop_semantic_understandings
            WHERE understanding_id = @id;
        ";
        command.Parameters.AddWithValue("@id", understandingId.ToString("D"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<DesktopSemanticUnderstandingSnapshot?>(null);

        var snapshot = ReadSnapshot(reader);
        return Task.FromResult<DesktopSemanticUnderstandingSnapshot?>(snapshot);
    }

    public Task<DesktopSemanticUnderstandingSnapshot?> GetLatestAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT understanding_id, created_at_utc, source_observation_id,
                   source_state_fingerprint, source_item_count,
                   payload_format_version, payload_json
            FROM desktop_semantic_understandings
            ORDER BY created_at_utc DESC, understanding_id DESC
            LIMIT 1;
        ";

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<DesktopSemanticUnderstandingSnapshot?>(null);

        var snapshot = ReadSnapshot(reader);
        return Task.FromResult<DesktopSemanticUnderstandingSnapshot?>(snapshot);
    }

    private static DesktopSemanticUnderstandingSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        var understandingId = ParseGuid(reader.GetString(0), "understanding_id");
        var createdAtUtc = ParseTimestamp(reader.GetString(1), "created_at_utc");
        var sourceObservationId = ParseGuid(reader.GetString(2), "source_observation_id");
        var sourceStateFingerprint = reader.GetString(3);
        var sourceItemCount = reader.GetInt32(4);
        var payloadFormatVersion = reader.GetInt32(5);
        var payloadJson = reader.GetString(6);

        var understanding = DesktopSemanticUnderstandingJsonSerializer.Deserialize(
            payloadJson,
            payloadFormatVersion);

        try
        {
            return new DesktopSemanticUnderstandingSnapshot(
                understandingId,
                createdAtUtc,
                sourceObservationId,
                sourceStateFingerprint,
                sourceItemCount,
                understanding);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"UnderstandingId \"{understandingId:D}\" 的持久化元数据未通过领域校验：{ex.Message}",
                ex);
        }
    }

    private static Guid ParseGuid(string value, string column)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
            throw new InvalidDataException($"{column} \"{value}\" 不是有效的非空 Guid。");
        return parsed;
    }

    private static DateTimeOffset ParseTimestamp(string value, string column)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            throw new InvalidDataException($"{column} \"{value}\" 不是有效 ISO-8601 round-trip 时间。");
        }

        return parsed.ToUniversalTime();
    }
}
