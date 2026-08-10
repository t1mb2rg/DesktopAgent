using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.Intelligence;

/// <summary>
/// M4 语义分类规则的 SQLite 持久化实现。
/// Rule 的 Category 是全局语义；可选 TargetZone 仅在其 TargetTemplateId 内有效。
/// </summary>
public sealed class SqliteDesktopClassificationRuleStore : IDesktopClassificationRuleStore
{
    public const int MaxPersistedRules = DesktopClassificationPolicyResolver.MaxRulesPerResolution;

    private readonly string _databasePath;

    public SqliteDesktopClassificationRuleStore(string databasePath)
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
        DesktopClassificationRule rule,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(rule);

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        if (rule.TargetTemplateId is Guid templateId && rule.TargetZoneId is Guid zoneId &&
            !ZoneExists(connection, transaction, templateId, zoneId))
        {
            throw new ArgumentException(
                $"TargetZoneId \"{zoneId:D}\" 不存在或不属于 TargetTemplateId \"{templateId:D}\"。",
                nameof(rule));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO desktop_classification_rules
                (rule_id, name, stable_id, file_extension, file_name_pattern, item_kind,
                 category, target_template_id, target_zone_id,
                 authority, provenance, priority, is_enabled)
            VALUES
                (@rule_id, @name, @stable_id, @file_extension, @file_name_pattern, @item_kind,
                 @category, @target_template_id, @target_zone_id,
                 @authority, @provenance, @priority, @is_enabled)
            ON CONFLICT(rule_id) DO UPDATE SET
                name               = excluded.name,
                stable_id          = excluded.stable_id,
                file_extension     = excluded.file_extension,
                file_name_pattern  = excluded.file_name_pattern,
                item_kind          = excluded.item_kind,
                category           = excluded.category,
                target_template_id = excluded.target_template_id,
                target_zone_id     = excluded.target_zone_id,
                authority          = excluded.authority,
                provenance         = excluded.provenance,
                priority           = excluded.priority,
                is_enabled         = excluded.is_enabled;
        ";

        command.Parameters.AddWithValue("@rule_id", rule.RuleId.ToString("D"));
        command.Parameters.AddWithValue("@name", rule.Name);
        command.Parameters.AddWithValue("@stable_id", (object?)rule.Condition.StableId ?? DBNull.Value);
        command.Parameters.AddWithValue("@file_extension", (object?)rule.Condition.FileExtension ?? DBNull.Value);
        command.Parameters.AddWithValue("@file_name_pattern", (object?)rule.Condition.FileNamePattern ?? DBNull.Value);
        command.Parameters.AddWithValue("@item_kind",
            rule.Condition.ItemKind.HasValue ? (object)(int)rule.Condition.ItemKind.Value : DBNull.Value);
        command.Parameters.AddWithValue("@category", rule.Category);
        command.Parameters.AddWithValue("@target_template_id",
            rule.TargetTemplateId.HasValue ? (object)rule.TargetTemplateId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("@target_zone_id",
            rule.TargetZoneId.HasValue ? (object)rule.TargetZoneId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("@authority", (int)rule.Authority);
        command.Parameters.AddWithValue("@provenance", (int)rule.Provenance);
        command.Parameters.AddWithValue("@priority", rule.Priority);
        command.Parameters.AddWithValue("@is_enabled", rule.IsEnabled ? 1 : 0);
        command.ExecuteNonQuery();

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DesktopClassificationRule>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT rule_id, name, stable_id, file_extension, file_name_pattern, item_kind,
                   category, target_template_id, target_zone_id,
                   authority, provenance, priority, is_enabled
            FROM desktop_classification_rules
            ORDER BY rule_id COLLATE BINARY ASC
            LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@limit", MaxPersistedRules + 1);

        var rules = new List<DesktopClassificationRule>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (rules.Count == MaxPersistedRules)
            {
                throw new InvalidDataException(
                    $"分类规则数量超过持久化读取上限 {MaxPersistedRules}，拒绝无界装入内存。");
            }

            rules.Add(ReadRule(reader));
        }

        return Task.FromResult<IReadOnlyList<DesktopClassificationRule>>(rules);
    }

    public Task<bool> DeleteAsync(
        Guid ruleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ruleId == Guid.Empty)
            throw new ArgumentException("RuleId 不能为 Guid.Empty。", nameof(ruleId));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM desktop_classification_rules WHERE rule_id = @rule_id;";
        command.Parameters.AddWithValue("@rule_id", ruleId.ToString("D"));
        return Task.FromResult(command.ExecuteNonQuery() > 0);
    }

    private static DesktopClassificationRule ReadRule(SqliteDataReader reader)
    {
        var ruleIdText = reader.GetString(0);
        if (!Guid.TryParseExact(ruleIdText, "D", out var ruleId) || ruleId == Guid.Empty)
            throw new InvalidDataException($"无效 rule_id：\"{ruleIdText}\"。");

        var itemKindValue = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        ItemKind? itemKind = null;
        if (itemKindValue.HasValue)
        {
            if (!Enum.IsDefined((ItemKind)itemKindValue.Value))
                throw new InvalidDataException($"RuleId \"{ruleId:D}\" 包含无效 item_kind：{itemKindValue.Value}。");
            itemKind = (ItemKind)itemKindValue.Value;
        }

        var templateId = ReadNullableGuid(reader, 7, "target_template_id", ruleId);
        var zoneId = ReadNullableGuid(reader, 8, "target_zone_id", ruleId);

        var authorityValue = reader.GetInt32(9);
        if (!Enum.IsDefined((DesktopClassificationRuleAuthority)authorityValue))
            throw new InvalidDataException($"RuleId \"{ruleId:D}\" 包含无效 authority：{authorityValue}。");

        var provenanceValue = reader.GetInt32(10);
        if (!Enum.IsDefined((DesktopClassificationRuleProvenance)provenanceValue))
            throw new InvalidDataException($"RuleId \"{ruleId:D}\" 包含无效 provenance：{provenanceValue}。");

        var isEnabledValue = reader.GetInt32(12);
        if (isEnabledValue is not 0 and not 1)
            throw new InvalidDataException($"RuleId \"{ruleId:D}\" 包含无效 is_enabled：{isEnabledValue}。");

        try
        {
            var condition = new DesktopClassificationRuleCondition(
                stableId: reader.IsDBNull(2) ? null : reader.GetString(2),
                fileExtension: reader.IsDBNull(3) ? null : reader.GetString(3),
                fileNamePattern: reader.IsDBNull(4) ? null : reader.GetString(4),
                itemKind: itemKind);

            return new DesktopClassificationRule(
                ruleId,
                reader.GetString(1),
                condition,
                reader.GetString(6),
                templateId,
                zoneId,
                (DesktopClassificationRuleAuthority)authorityValue,
                (DesktopClassificationRuleProvenance)provenanceValue,
                reader.GetInt32(11),
                isEnabledValue == 1);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"RuleId \"{ruleId:D}\" 无法恢复为有效分类规则：{ex.Message}", ex);
        }
    }

    private static Guid? ReadNullableGuid(
        SqliteDataReader reader,
        int ordinal,
        string columnName,
        Guid ruleId)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetString(ordinal);
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
            throw new InvalidDataException(
                $"RuleId \"{ruleId:D}\" 的 {columnName} \"{value}\" 不是有效 Guid。");

        return parsed;
    }

    private static bool ZoneExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid templateId,
        Guid zoneId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            SELECT COUNT(*)
            FROM desktop_layout_template_zones
            WHERE template_id = @template_id AND zone_id = @zone_id;
        ";
        command.Parameters.AddWithValue("@template_id", templateId.ToString("D"));
        command.Parameters.AddWithValue("@zone_id", zoneId.ToString("D"));
        return (long)command.ExecuteScalar()! > 0;
    }
}
