using System.Globalization;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Persistence.Intelligence;

/// <summary>
/// DesktopAgent 自有 User Profile 的 SQLite 实现。
/// Preference 是用户确认后的软偏好；Evidence 只追加历史，不拥有 Policy authority。
/// </summary>
public sealed class SqliteDesktopUserProfileStore : IDesktopUserProfileStore
{
    public const int MaxRecentEvidenceRead = 512;

    private const int SqliteConstraintPrimaryKey = 1555;
    private const int SqliteConstraintUnique = 2067;

    private readonly string _databasePath;

    public SqliteDesktopUserProfileStore(string databasePath)
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

    public Task SavePreferenceAsync(
        DesktopCategoryZonePreference preference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(preference);

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        if (!ZoneExists(connection, transaction, preference.TemplateId, preference.PreferredZoneId))
        {
            throw new ArgumentException(
                $"PreferredZoneId \"{preference.PreferredZoneId:D}\" 不存在或不属于 TemplateId \"{preference.TemplateId:D}\"。",
                nameof(preference));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO desktop_user_category_zone_preferences
                (preference_id, template_id, category, preferred_zone_id,
                 confidence, evidence_count, confirmed_at_utc)
            VALUES
                (@preference_id, @template_id, @category, @preferred_zone_id,
                 @confidence, @evidence_count, @confirmed_at_utc)
            ON CONFLICT(template_id, category) DO UPDATE SET
                preference_id     = excluded.preference_id,
                category          = excluded.category,
                preferred_zone_id = excluded.preferred_zone_id,
                confidence        = excluded.confidence,
                evidence_count    = excluded.evidence_count,
                confirmed_at_utc  = excluded.confirmed_at_utc;
        ";
        command.Parameters.AddWithValue("@preference_id", preference.PreferenceId.ToString("D"));
        command.Parameters.AddWithValue("@template_id", preference.TemplateId.ToString("D"));
        command.Parameters.AddWithValue("@category", preference.Category);
        command.Parameters.AddWithValue("@preferred_zone_id", preference.PreferredZoneId.ToString("D"));
        command.Parameters.AddWithValue("@confidence", preference.Confidence);
        command.Parameters.AddWithValue("@evidence_count", preference.EvidenceCount);
        command.Parameters.AddWithValue("@confirmed_at_utc",
            preference.ConfirmedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<DesktopCategoryZonePreference?> GetPreferenceAsync(
        Guid templateId,
        string category,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为 Guid.Empty。", nameof(templateId));
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Category 不能为空。", nameof(category));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT preference_id, template_id, category, preferred_zone_id,
                   confidence, evidence_count, confirmed_at_utc
            FROM desktop_user_category_zone_preferences
            WHERE template_id = @template_id AND category = @category COLLATE NOCASE
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@template_id", templateId.ToString("D"));
        command.Parameters.AddWithValue("@category", category);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<DesktopCategoryZonePreference?>(null);

        return Task.FromResult<DesktopCategoryZonePreference?>(ReadPreference(reader));
    }

    public Task<bool> DeletePreferenceAsync(
        Guid preferenceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preferenceId == Guid.Empty)
            throw new ArgumentException("PreferenceId 不能为 Guid.Empty。", nameof(preferenceId));

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM desktop_user_category_zone_preferences
            WHERE preference_id = @preference_id;
        ";
        command.Parameters.AddWithValue("@preference_id", preferenceId.ToString("D"));
        return Task.FromResult(command.ExecuteNonQuery() > 0);
    }

    public Task AppendEvidenceAsync(
        DesktopClassificationBehaviorEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(evidence);

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO desktop_classification_evidence
                (evidence_id, observed_at_utc, template_id,
                 stable_id, display_name, file_extension, item_kind,
                 proposed_category, proposed_zone_id, proposed_source,
                 feedback_kind, final_category, final_zone_id)
            VALUES
                (@evidence_id, @observed_at_utc, @template_id,
                 @stable_id, @display_name, @file_extension, @item_kind,
                 @proposed_category, @proposed_zone_id, @proposed_source,
                 @feedback_kind, @final_category, @final_zone_id);
        ";
        command.Parameters.AddWithValue("@evidence_id", evidence.EvidenceId.ToString("D"));
        command.Parameters.AddWithValue("@observed_at_utc",
            evidence.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@template_id",
            evidence.TemplateId.HasValue ? (object)evidence.TemplateId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("@stable_id", evidence.StableId);
        command.Parameters.AddWithValue("@display_name", evidence.DisplayName);
        command.Parameters.AddWithValue("@file_extension", (object?)evidence.FileExtension ?? DBNull.Value);
        command.Parameters.AddWithValue("@item_kind", (int)evidence.ItemKind);
        command.Parameters.AddWithValue("@proposed_category", evidence.ProposedCategory);
        command.Parameters.AddWithValue("@proposed_zone_id",
            evidence.ProposedZoneId.HasValue ? (object)evidence.ProposedZoneId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("@proposed_source", (int)evidence.ProposedSource);
        command.Parameters.AddWithValue("@feedback_kind", (int)evidence.FeedbackKind);
        command.Parameters.AddWithValue("@final_category", (object?)evidence.FinalCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("@final_zone_id",
            evidence.FinalZoneId.HasValue ? (object)evidence.FinalZoneId.Value.ToString("D") : DBNull.Value);

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueConstraint(ex))
        {
            throw new InvalidOperationException(
                $"EvidenceId \"{evidence.EvidenceId:D}\" 已存在，行为证据不能静默覆盖。", ex);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DesktopClassificationBehaviorEvidence>> GetRecentEvidenceAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0 || limit > MaxRecentEvidenceRead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"limit 必须位于 [1, {MaxRecentEvidenceRead}]。");
        }

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT evidence_id, observed_at_utc, template_id,
                   stable_id, display_name, file_extension, item_kind,
                   proposed_category, proposed_zone_id, proposed_source,
                   feedback_kind, final_category, final_zone_id
            FROM desktop_classification_evidence
            ORDER BY observed_at_utc DESC, evidence_id ASC
            LIMIT @limit;
        ";
        command.Parameters.AddWithValue("@limit", limit);

        var evidence = new List<DesktopClassificationBehaviorEvidence>(limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            evidence.Add(ReadEvidence(reader));
        }

        return Task.FromResult<IReadOnlyList<DesktopClassificationBehaviorEvidence>>(evidence);
    }

    public Task<int> ClearEvidenceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM desktop_classification_evidence;";
        return Task.FromResult(command.ExecuteNonQuery());
    }

    private static DesktopCategoryZonePreference ReadPreference(SqliteDataReader reader)
    {
        var preferenceId = ReadRequiredGuid(reader.GetString(0), "preference_id");
        var templateId = ReadRequiredGuid(reader.GetString(1), "template_id", preferenceId);
        var preferredZoneId = ReadRequiredGuid(reader.GetString(3), "preferred_zone_id", preferenceId);
        var confirmedAtUtc = ReadUtcTimestamp(reader.GetString(6), "confirmed_at_utc", preferenceId);

        try
        {
            return new DesktopCategoryZonePreference(
                preferenceId,
                templateId,
                reader.GetString(2),
                preferredZoneId,
                reader.GetDouble(4),
                reader.GetInt32(5),
                confirmedAtUtc);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"PreferenceId \"{preferenceId:D}\" 无法恢复为有效用户偏好：{ex.Message}", ex);
        }
    }

    private static DesktopClassificationBehaviorEvidence ReadEvidence(SqliteDataReader reader)
    {
        var evidenceId = ReadRequiredGuid(reader.GetString(0), "evidence_id");
        var observedAtUtc = ReadUtcTimestamp(reader.GetString(1), "observed_at_utc", evidenceId);
        var templateId = reader.IsDBNull(2)
            ? (Guid?)null
            : ReadRequiredGuid(reader.GetString(2), "template_id", evidenceId);

        var itemKindValue = reader.GetInt32(6);
        if (!Enum.IsDefined((ItemKind)itemKindValue))
            throw new InvalidDataException($"EvidenceId \"{evidenceId:D}\" 包含无效 item_kind：{itemKindValue}。");

        var proposedZoneId = reader.IsDBNull(8)
            ? (Guid?)null
            : ReadRequiredGuid(reader.GetString(8), "proposed_zone_id", evidenceId);

        var sourceValue = reader.GetInt32(9);
        if (!Enum.IsDefined((DesktopItemClassificationSource)sourceValue))
            throw new InvalidDataException($"EvidenceId \"{evidenceId:D}\" 包含无效 proposed_source：{sourceValue}。");

        var feedbackValue = reader.GetInt32(10);
        if (!Enum.IsDefined((DesktopClassificationFeedbackKind)feedbackValue))
            throw new InvalidDataException($"EvidenceId \"{evidenceId:D}\" 包含无效 feedback_kind：{feedbackValue}。");

        var finalZoneId = reader.IsDBNull(12)
            ? (Guid?)null
            : ReadRequiredGuid(reader.GetString(12), "final_zone_id", evidenceId);

        try
        {
            return new DesktopClassificationBehaviorEvidence(
                evidenceId,
                observedAtUtc,
                templateId,
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                (ItemKind)itemKindValue,
                reader.GetString(7),
                proposedZoneId,
                (DesktopItemClassificationSource)sourceValue,
                (DesktopClassificationFeedbackKind)feedbackValue,
                reader.IsDBNull(11) ? null : reader.GetString(11),
                finalZoneId);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"EvidenceId \"{evidenceId:D}\" 无法恢复为有效行为证据：{ex.Message}", ex);
        }
    }

    private static Guid ReadRequiredGuid(string value, string columnName, Guid? ownerId = null)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
        {
            var owner = ownerId.HasValue ? $"记录 \"{ownerId.Value:D}\" 的 " : string.Empty;
            throw new InvalidDataException($"{owner}{columnName} \"{value}\" 不是有效 Guid。");
        }

        return parsed;
    }

    private static DateTimeOffset ReadUtcTimestamp(string value, string columnName, Guid ownerId)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"记录 \"{ownerId:D}\" 的 {columnName} \"{value}\" 不是有效 UTC 时间戳。");
        }

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

    private static bool IsUniqueConstraint(SqliteException exception)
        => exception.SqliteExtendedErrorCode is SqliteConstraintPrimaryKey or SqliteConstraintUnique;
}
