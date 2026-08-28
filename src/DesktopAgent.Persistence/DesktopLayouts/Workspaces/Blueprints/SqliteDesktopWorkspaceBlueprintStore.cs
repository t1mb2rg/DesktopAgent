using System.Globalization;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Blueprints;

/// <summary>
/// Durable Workspace Blueprint Library 的 SQLite Store。
/// 只保存 Blueprint 资产和 immutable revisions；不保存 Exact Plan、Frozen Preview、Confirmation 或 Writer authority。
/// </summary>
public sealed class SqliteDesktopWorkspaceBlueprintStore : IDesktopWorkspaceBlueprintStore
{
    private readonly string _databasePath;

    public SqliteDesktopWorkspaceBlueprintStore(string databasePath)
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

    public Task CreateAsync(
        string name,
        DesktopWorkspaceBlueprintRevisionRecord firstRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));
        ArgumentNullException.ThrowIfNull(firstRevision);

        var blueprint = firstRevision.Blueprint;
        if (blueprint.Revision != 1)
            throw new ArgumentException("FirstRevision 必须是 Revision=1。", nameof(firstRevision));
        if (blueprint.ParentRevision is not null)
            throw new ArgumentException("FirstRevision 的 ParentRevision 必须为 null。", nameof(firstRevision));

        var payloadJson = DesktopWorkspaceBlueprintJsonSerializer.Serialize(blueprint);
        var createdAt = FormatTimestamp(firstRevision.CreatedAtUtc);

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            using (var asset = connection.CreateCommand())
            {
                asset.Transaction = transaction;
                asset.CommandText = @"
                    INSERT INTO workspace_blueprints
                        (blueprint_id, name, created_at_utc, updated_at_utc,
                         current_revision, is_archived)
                    VALUES
                        (@blueprint_id, @name, @created_at_utc, @updated_at_utc,
                         1, 0);
                ";
                asset.Parameters.AddWithValue("@blueprint_id", blueprint.BlueprintId.ToString("D"));
                asset.Parameters.AddWithValue("@name", name.Trim());
                asset.Parameters.AddWithValue("@created_at_utc", createdAt);
                asset.Parameters.AddWithValue("@updated_at_utc", createdAt);
                asset.ExecuteNonQuery();
            }

            InsertRevision(connection, transaction, firstRevision, payloadJson);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"BlueprintId \"{blueprint.BlueprintId:D}\" 无法创建；资产已存在、来源 Understanding 不存在或持久化约束被拒绝。",
                ex);
        }

        return Task.CompletedTask;
    }

    public Task AppendRevisionAsync(
        DesktopWorkspaceBlueprintRevisionRecord revision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(revision);

        var blueprint = revision.Blueprint;
        var payloadJson = DesktopWorkspaceBlueprintJsonSerializer.Serialize(blueprint);

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        int currentRevision;
        DateTimeOffset updatedAtUtc;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = @"
                SELECT current_revision, updated_at_utc
                FROM workspace_blueprints
                WHERE blueprint_id = @blueprint_id;
            ";
            current.Parameters.AddWithValue("@blueprint_id", blueprint.BlueprintId.ToString("D"));

            using var reader = current.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    $"BlueprintId \"{blueprint.BlueprintId:D}\" 不存在，不能追加 revision。");
            }

            currentRevision = reader.GetInt32(0);
            updatedAtUtc = ParseTimestamp(reader.GetString(1), "updated_at_utc");
        }

        if (blueprint.Revision != currentRevision + 1)
        {
            throw new InvalidOperationException(
                $"BlueprintId \"{blueprint.BlueprintId:D}\" 当前为 v{currentRevision}；" +
                $"只能追加 v{currentRevision + 1}，实际为 v{blueprint.Revision}。");
        }
        if (blueprint.ParentRevision != currentRevision)
        {
            throw new InvalidOperationException(
                $"Blueprint v{blueprint.Revision} 的 ParentRevision 必须为当前 v{currentRevision}。");
        }
        if (revision.CreatedAtUtc < updatedAtUtc)
        {
            throw new InvalidOperationException(
                "新 Blueprint revision 的 CreatedAtUtc 不能早于当前资产 UpdatedAtUtc。");
        }

        try
        {
            InsertRevision(connection, transaction, revision, payloadJson);
            cancellationToken.ThrowIfCancellationRequested();

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"
                UPDATE workspace_blueprints
                SET current_revision = @new_revision,
                    updated_at_utc = @updated_at_utc
                WHERE blueprint_id = @blueprint_id
                  AND current_revision = @expected_revision;
            ";
            update.Parameters.AddWithValue("@new_revision", blueprint.Revision);
            update.Parameters.AddWithValue("@updated_at_utc", FormatTimestamp(revision.CreatedAtUtc));
            update.Parameters.AddWithValue("@blueprint_id", blueprint.BlueprintId.ToString("D"));
            update.Parameters.AddWithValue("@expected_revision", currentRevision);

            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"BlueprintId \"{blueprint.BlueprintId:D}\" 的 CurrentRevision 在追加期间发生变化；拒绝覆盖并发更新。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"BlueprintId \"{blueprint.BlueprintId:D}\" v{blueprint.Revision} 无法追加；" +
                "revision 已存在、来源 Understanding 不存在或持久化约束被拒绝。",
                ex);
        }

        return Task.CompletedTask;
    }

    public Task<DesktopWorkspaceBlueprintAssetSummary?> GetAssetAsync(
        Guid blueprintId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBlueprintId(blueprintId);

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT a.blueprint_id, a.name, a.current_revision,
                   a.created_at_utc, a.updated_at_utc, a.is_archived,
                   r.revision
            FROM workspace_blueprints a
            LEFT JOIN workspace_blueprint_revisions r
              ON r.blueprint_id = a.blueprint_id
             AND r.revision = a.current_revision
            WHERE a.blueprint_id = @blueprint_id;
        ";
        command.Parameters.AddWithValue("@blueprint_id", blueprintId.ToString("D"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<DesktopWorkspaceBlueprintAssetSummary?>(null);

        var summary = ReadAsset(reader, requireCurrentRevision: true);
        return Task.FromResult<DesktopWorkspaceBlueprintAssetSummary?>(summary);
    }

    public Task<DesktopWorkspaceBlueprintRevisionRecord?> GetRevisionAsync(
        Guid blueprintId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBlueprintId(blueprintId);
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));

        using var connection = CreateConnection();
        using var command = CreateRevisionSelect(connection);
        command.CommandText += " WHERE blueprint_id = @blueprint_id AND revision = @revision;";
        command.Parameters.AddWithValue("@blueprint_id", blueprintId.ToString("D"));
        command.Parameters.AddWithValue("@revision", revision);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<DesktopWorkspaceBlueprintRevisionRecord?>(null);

        var record = ReadRevision(reader);
        return Task.FromResult<DesktopWorkspaceBlueprintRevisionRecord?>(record);
    }

    public Task<DesktopWorkspaceBlueprintRevisionRecord?> GetCurrentRevisionAsync(
        Guid blueprintId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBlueprintId(blueprintId);

        using var connection = CreateConnection();
        int currentRevision;
        using (var current = connection.CreateCommand())
        {
            current.CommandText = @"
                SELECT current_revision
                FROM workspace_blueprints
                WHERE blueprint_id = @blueprint_id;
            ";
            current.Parameters.AddWithValue("@blueprint_id", blueprintId.ToString("D"));
            var scalar = current.ExecuteScalar();
            if (scalar is null)
                return Task.FromResult<DesktopWorkspaceBlueprintRevisionRecord?>(null);
            currentRevision = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        }

        using var command = CreateRevisionSelect(connection);
        command.CommandText += " WHERE blueprint_id = @blueprint_id AND revision = @revision;";
        command.Parameters.AddWithValue("@blueprint_id", blueprintId.ToString("D"));
        command.Parameters.AddWithValue("@revision", currentRevision);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException(
                $"BlueprintId \"{blueprintId:D}\" 的 current_revision={currentRevision} 没有对应 immutable revision。");
        }

        var record = ReadRevision(reader);
        return Task.FromResult<DesktopWorkspaceBlueprintRevisionRecord?>(record);
    }

    public Task<IReadOnlyList<DesktopWorkspaceBlueprintAssetSummary>> ListAsync(
        bool includeArchived = false,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "limit 必须大于 0。");

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT a.blueprint_id, a.name, a.current_revision,
                   a.created_at_utc, a.updated_at_utc, a.is_archived,
                   r.revision
            FROM workspace_blueprints a
            LEFT JOIN workspace_blueprint_revisions r
              ON r.blueprint_id = a.blueprint_id
             AND r.revision = a.current_revision
            WHERE (@include_archived = 1 OR a.is_archived = 0)
            ORDER BY a.is_archived ASC,
                     a.updated_at_utc DESC,
                     a.name COLLATE BINARY ASC,
                     a.blueprint_id ASC
            LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@include_archived", includeArchived ? 1 : 0);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<DesktopWorkspaceBlueprintAssetSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ReadAsset(reader, requireCurrentRevision: true));
        }

        return Task.FromResult<IReadOnlyList<DesktopWorkspaceBlueprintAssetSummary>>(results);
    }

    private static void InsertRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DesktopWorkspaceBlueprintRevisionRecord record,
        string payloadJson)
    {
        var blueprint = record.Blueprint;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO workspace_blueprint_revisions
                (blueprint_id, revision, parent_revision, created_at_utc,
                 source_understanding_id, source_user_intent, design_summary,
                 blueprint_schema_version, payload_format_version, payload_json)
            VALUES
                (@blueprint_id, @revision, @parent_revision, @created_at_utc,
                 @source_understanding_id, @source_user_intent, @design_summary,
                 @blueprint_schema_version, @payload_format_version, @payload_json);
        ";
        command.Parameters.AddWithValue("@blueprint_id", blueprint.BlueprintId.ToString("D"));
        command.Parameters.AddWithValue("@revision", blueprint.Revision);
        command.Parameters.AddWithValue(
            "@parent_revision",
            blueprint.ParentRevision.HasValue ? (object)blueprint.ParentRevision.Value : DBNull.Value);
        command.Parameters.AddWithValue("@created_at_utc", FormatTimestamp(record.CreatedAtUtc));
        command.Parameters.AddWithValue(
            "@source_understanding_id",
            record.SourceUnderstandingId.HasValue
                ? (object)record.SourceUnderstandingId.Value.ToString("D")
                : DBNull.Value);
        command.Parameters.AddWithValue("@source_user_intent", (object?)record.SourceUserIntent ?? DBNull.Value);
        command.Parameters.AddWithValue("@design_summary", blueprint.DesignSummary);
        command.Parameters.AddWithValue("@blueprint_schema_version", blueprint.SchemaVersion);
        command.Parameters.AddWithValue(
            "@payload_format_version",
            DesktopWorkspaceBlueprintJsonSerializer.CurrentPayloadFormatVersion);
        command.Parameters.AddWithValue("@payload_json", payloadJson);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand CreateRevisionSelect(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT blueprint_id, revision, parent_revision, created_at_utc,
                   source_understanding_id, source_user_intent, design_summary,
                   blueprint_schema_version, payload_format_version, payload_json
            FROM workspace_blueprint_revisions";
        return command;
    }

    private static DesktopWorkspaceBlueprintRevisionRecord ReadRevision(SqliteDataReader reader)
    {
        var blueprintId = ParseGuid(reader.GetString(0), "blueprint_id");
        var revision = reader.GetInt32(1);
        var parentRevision = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        var createdAtUtc = ParseTimestamp(reader.GetString(3), "created_at_utc");
        var sourceUnderstandingId = reader.IsDBNull(4)
            ? (Guid?)null
            : ParseGuid(reader.GetString(4), "source_understanding_id");
        var sourceUserIntent = reader.IsDBNull(5) ? null : reader.GetString(5);
        var designSummary = reader.GetString(6);
        var blueprintSchemaVersion = reader.GetInt32(7);
        var payloadFormatVersion = reader.GetInt32(8);
        var payloadJson = reader.GetString(9);

        var blueprint = DesktopWorkspaceBlueprintJsonSerializer.Deserialize(
            payloadJson,
            payloadFormatVersion);

        if (blueprint.BlueprintId != blueprintId ||
            blueprint.Revision != revision ||
            blueprint.ParentRevision != parentRevision ||
            blueprint.SchemaVersion != blueprintSchemaVersion ||
            !string.Equals(blueprint.DesignSummary, designSummary, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"BlueprintId \"{blueprintId:D}\" v{revision} 的 revision 元数据与 payload 不一致。");
        }

        try
        {
            return new DesktopWorkspaceBlueprintRevisionRecord(
                blueprint,
                createdAtUtc,
                sourceUnderstandingId,
                sourceUserIntent);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"BlueprintId \"{blueprintId:D}\" v{revision} 的持久化元数据未通过领域校验：{ex.Message}",
                ex);
        }
    }

    private static DesktopWorkspaceBlueprintAssetSummary ReadAsset(
        SqliteDataReader reader,
        bool requireCurrentRevision)
    {
        var blueprintId = ParseGuid(reader.GetString(0), "blueprint_id");
        var name = reader.GetString(1);
        var currentRevision = reader.GetInt32(2);
        var createdAtUtc = ParseTimestamp(reader.GetString(3), "created_at_utc");
        var updatedAtUtc = ParseTimestamp(reader.GetString(4), "updated_at_utc");
        var isArchivedRaw = reader.GetInt32(5);

        if (isArchivedRaw is not 0 and not 1)
            throw new InvalidDataException($"BlueprintId \"{blueprintId:D}\" 的 is_archived={isArchivedRaw} 非法。");
        if (requireCurrentRevision && reader.IsDBNull(6))
        {
            throw new InvalidDataException(
                $"BlueprintId \"{blueprintId:D}\" 的 current_revision={currentRevision} 没有对应 immutable revision。");
        }

        try
        {
            return new DesktopWorkspaceBlueprintAssetSummary(
                blueprintId,
                name,
                currentRevision,
                createdAtUtc,
                updatedAtUtc,
                isArchivedRaw == 1);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"BlueprintId \"{blueprintId:D}\" 的资产元数据未通过领域校验：{ex.Message}", ex);
        }
    }

    private static void ValidateBlueprintId(Guid blueprintId)
    {
        if (blueprintId == Guid.Empty)
            throw new ArgumentException("BlueprintId 不能为 Guid.Empty。", nameof(blueprintId));
    }

    private static Guid ParseGuid(string value, string column)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
            throw new InvalidDataException($"{column} \"{value}\" 不是有效的非空 Guid。");
        return parsed;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

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
