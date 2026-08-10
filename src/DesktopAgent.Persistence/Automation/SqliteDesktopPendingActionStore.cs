using System.Globalization;
using DesktopAgent.Core.Automation;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.Automation;

/// <summary>M5-02 Pending Queue 的 SQLite 持久化实现。</summary>
public sealed class SqliteDesktopPendingActionStore : IDesktopPendingActionStore
{
    public const int MaxPendingActions = 512;

    private readonly string _databasePath;

    public SqliteDesktopPendingActionStore(string databasePath)
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

    public Task SaveAsync(
        DesktopPendingAction action,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(action);

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO desktop_pending_actions
                (pending_action_id, created_at_utc, template_id,
                 stable_id, display_name, file_extension, item_kind,
                 is_system_item, is_file_system_item,
                 proposed_category, proposed_zone_id, proposed_confidence,
                 proposed_source, explanation,
                 authorization_basis, queue_reason)
            VALUES
                (@pending_action_id, @created_at_utc, @template_id,
                 @stable_id, @display_name, @file_extension, @item_kind,
                 @is_system_item, @is_file_system_item,
                 @proposed_category, @proposed_zone_id, @proposed_confidence,
                 @proposed_source, @explanation,
                 @authorization_basis, @queue_reason)
            ON CONFLICT(pending_action_id) DO UPDATE SET
                created_at_utc       = excluded.created_at_utc,
                template_id          = excluded.template_id,
                stable_id            = excluded.stable_id,
                display_name         = excluded.display_name,
                file_extension       = excluded.file_extension,
                item_kind            = excluded.item_kind,
                is_system_item       = excluded.is_system_item,
                is_file_system_item  = excluded.is_file_system_item,
                proposed_category    = excluded.proposed_category,
                proposed_zone_id     = excluded.proposed_zone_id,
                proposed_confidence  = excluded.proposed_confidence,
                proposed_source      = excluded.proposed_source,
                explanation          = excluded.explanation,
                authorization_basis  = excluded.authorization_basis,
                queue_reason         = excluded.queue_reason;
        ";

        command.Parameters.AddWithValue("@pending_action_id", action.PendingActionId.ToString("D"));
        command.Parameters.AddWithValue("@created_at_utc",
            action.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@template_id",
            action.TemplateId.HasValue ? (object)action.TemplateId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("@stable_id", action.Item.StableId);
        command.Parameters.AddWithValue("@display_name", action.Item.DisplayName);
        command.Parameters.AddWithValue("@file_extension", (object?)action.Item.FileExtension ?? DBNull.Value);
        command.Parameters.AddWithValue("@item_kind", (int)action.Item.ItemKind);
        command.Parameters.AddWithValue("@is_system_item", action.Item.IsSystemItem ? 1 : 0);
        command.Parameters.AddWithValue("@is_file_system_item", action.Item.IsFileSystemItem ? 1 : 0);

        var decision = action.Decision;
        command.Parameters.AddWithValue("@proposed_category", (object?)decision?.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("@proposed_zone_id",
            decision?.TargetZoneId is Guid zoneId ? (object)zoneId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("@proposed_confidence",
            decision is null ? DBNull.Value : decision.Confidence);
        command.Parameters.AddWithValue("@proposed_source",
            decision is null ? DBNull.Value : (int)decision.Source);
        command.Parameters.AddWithValue("@explanation", (object?)decision?.Explanation ?? DBNull.Value);
        command.Parameters.AddWithValue("@authorization_basis", (int)action.AuthorizationBasis);
        command.Parameters.AddWithValue("@queue_reason", (int)action.QueueReason);

        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<DesktopPendingAction?> GetAsync(
        Guid pendingActionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pendingActionId == Guid.Empty)
            throw new ArgumentException("PendingActionId 不能为 Guid.Empty。", nameof(pendingActionId));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            {SelectColumns}
            WHERE pending_action_id = @pending_action_id
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@pending_action_id", pendingActionId.ToString("D"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<DesktopPendingAction?>(null);

        return Task.FromResult<DesktopPendingAction?>(ReadAction(reader));
    }

    public Task<IReadOnlyList<DesktopPendingAction>> GetPendingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > MaxPendingActions)
            throw new ArgumentOutOfRangeException(nameof(limit), $"limit 必须位于 [1, {MaxPendingActions}]。");

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            {SelectColumns}
            ORDER BY created_at_utc ASC, pending_action_id COLLATE BINARY ASC
            LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@limit", limit);

        var result = new List<DesktopPendingAction>(Math.Min(limit, 64));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ReadAction(reader));
        }

        return Task.FromResult<IReadOnlyList<DesktopPendingAction>>(result);
    }

    public Task<bool> DeleteAsync(
        Guid pendingActionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pendingActionId == Guid.Empty)
            throw new ArgumentException("PendingActionId 不能为 Guid.Empty。", nameof(pendingActionId));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM desktop_pending_actions WHERE pending_action_id = @pending_action_id;";
        command.Parameters.AddWithValue("@pending_action_id", pendingActionId.ToString("D"));
        return Task.FromResult(command.ExecuteNonQuery() > 0);
    }

    private const string SelectColumns = @"
        SELECT pending_action_id, created_at_utc, template_id,
               stable_id, display_name, file_extension, item_kind,
               is_system_item, is_file_system_item,
               proposed_category, proposed_zone_id, proposed_confidence,
               proposed_source, explanation,
               authorization_basis, queue_reason
        FROM desktop_pending_actions";

    private static DesktopPendingAction ReadAction(SqliteDataReader reader)
    {
        var id = ReadRequiredGuid(reader, 0, "pending_action_id");

        var createdText = reader.GetString(1);
        if (!DateTimeOffset.TryParse(
                createdText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdAtUtc) ||
            createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"PendingActionId \"{id:D}\" 包含无效 created_at_utc：\"{createdText}\"。");
        }

        var templateId = ReadNullableGuid(reader, 2, "template_id", id);

        var itemKindValue = reader.GetInt32(6);
        if (!Enum.IsDefined((ItemKind)itemKindValue))
            throw new InvalidDataException($"PendingActionId \"{id:D}\" 包含无效 item_kind：{itemKindValue}。");

        var isSystem = ReadBoolean(reader, 7, "is_system_item", id);
        var isFileSystem = ReadBoolean(reader, 8, "is_file_system_item", id);

        DesktopItemClassificationDecision? decision = null;
        var hasDecision = !reader.IsDBNull(9);
        var decisionColumnsHaveNull = reader.IsDBNull(11) || reader.IsDBNull(12) || reader.IsDBNull(13);
        if (hasDecision)
        {
            if (decisionColumnsHaveNull)
                throw new InvalidDataException($"PendingActionId \"{id:D}\" 的 Decision 列不完整。");

            var sourceValue = reader.GetInt32(12);
            if (!Enum.IsDefined((DesktopItemClassificationSource)sourceValue))
                throw new InvalidDataException($"PendingActionId \"{id:D}\" 包含无效 proposed_source：{sourceValue}。");

            var proposedZoneId = ReadNullableGuid(reader, 10, "proposed_zone_id", id);
            try
            {
                decision = new DesktopItemClassificationDecision(
                    reader.GetString(9),
                    proposedZoneId,
                    reader.GetDouble(11),
                    (DesktopItemClassificationSource)sourceValue,
                    reader.GetString(13));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"PendingActionId \"{id:D}\" 的 Decision 无效：{ex.Message}", ex);
            }
        }
        else if (!reader.IsDBNull(10) || !reader.IsDBNull(11) || !reader.IsDBNull(12) || !reader.IsDBNull(13))
        {
            throw new InvalidDataException($"PendingActionId \"{id:D}\" 在无 proposed_category 时携带了残留 Decision 列。");
        }

        var authorizationValue = reader.GetInt32(14);
        if (!Enum.IsDefined((DesktopAutomationAuthorizationBasis)authorizationValue))
            throw new InvalidDataException($"PendingActionId \"{id:D}\" 包含无效 authorization_basis：{authorizationValue}。");

        var reasonValue = reader.GetInt32(15);
        if (!Enum.IsDefined((DesktopAutomationPolicyReason)reasonValue))
            throw new InvalidDataException($"PendingActionId \"{id:D}\" 包含无效 queue_reason：{reasonValue}。");

        try
        {
            var item = new DesktopItemFacts(
                reader.GetString(3),
                reader.GetString(4),
                (ItemKind)itemKindValue,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                isSystem,
                isFileSystem);

            return new DesktopPendingAction(
                id,
                createdAtUtc,
                templateId,
                item,
                decision,
                (DesktopAutomationAuthorizationBasis)authorizationValue,
                (DesktopAutomationPolicyReason)reasonValue);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"PendingActionId \"{id:D}\" 无法恢复为有效 Pending Action：{ex.Message}", ex);
        }
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal, string column, Guid actionId)
    {
        var value = reader.GetInt32(ordinal);
        if (value is not 0 and not 1)
            throw new InvalidDataException($"PendingActionId \"{actionId:D}\" 包含无效 {column}：{value}。");
        return value == 1;
    }

    private static Guid ReadRequiredGuid(SqliteDataReader reader, int ordinal, string column)
    {
        var text = reader.GetString(ordinal);
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty)
            throw new InvalidDataException($"无效 {column}：\"{text}\"。");
        return value;
    }

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal, string column, Guid actionId)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var text = reader.GetString(ordinal);
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty)
            throw new InvalidDataException($"PendingActionId \"{actionId:D}\" 包含无效 {column}：\"{text}\"。");
        return value;
    }
}
