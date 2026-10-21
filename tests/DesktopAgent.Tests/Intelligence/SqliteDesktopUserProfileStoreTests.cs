using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Intelligence;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.Intelligence;

public sealed class SqliteDesktopUserProfileStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDesktopUserProfileStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(), $"DesktopAgent.Tests.M4ProfileStore.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // 尽力清理测试目录。
        }
    }

    [Fact]
    public async Task SaveGetPreference_CategoryKeyIsCaseInsensitiveAndLatestConfirmationWins()
    {
        var store = await CreateStoreAsync();
        var templateId = Guid.NewGuid();
        var firstZoneId = Guid.NewGuid();
        var secondZoneId = Guid.NewGuid();
        InsertTemplateWithZones(templateId, firstZoneId, secondZoneId);

        var first = new DesktopCategoryZonePreference(
            Guid.NewGuid(), templateId, "Development", firstZoneId,
            0.75, 2, Utc("2026-08-10T01:00:00Z"));
        var second = new DesktopCategoryZonePreference(
            Guid.NewGuid(), templateId, "development", secondZoneId,
            0.95, 5, Utc("2026-08-10T02:00:00Z"));

        await store.SavePreferenceAsync(first);
        await store.SavePreferenceAsync(second);

        var loaded = await store.GetPreferenceAsync(templateId, "DEVELOPMENT");
        Assert.NotNull(loaded);
        Assert.Equal(second.PreferenceId, loaded!.PreferenceId);
        Assert.Equal("development", loaded.Category);
        Assert.Equal(secondZoneId, loaded.PreferredZoneId);
        Assert.Equal(0.95, loaded.Confidence);
        Assert.Equal(5, loaded.EvidenceCount);
        Assert.Equal(second.ConfirmedAtUtc, loaded.ConfirmedAtUtc);

        using var connection = OpenRawConnection();
        using var count = connection.CreateCommand();
        count.CommandText = @"
            SELECT COUNT(*)
            FROM desktop_user_category_zone_preferences
            WHERE template_id = @template_id;
        ";
        count.Parameters.AddWithValue("@template_id", templateId.ToString("D"));
        Assert.Equal(1L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public async Task SavePreference_ZoneOutsideTemplate_RejectsBeforeWrite()
    {
        var store = await CreateStoreAsync();
        var templateId = Guid.NewGuid();
        InsertTemplateWithZones(templateId, Guid.NewGuid());

        var preference = new DesktopCategoryZonePreference(
            Guid.NewGuid(), templateId, "Course", Guid.NewGuid(),
            1d, 1, Utc("2026-08-10T01:00:00Z"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SavePreferenceAsync(preference));
        Assert.Contains(templateId.ToString("D"), ex.Message);
        Assert.Null(await store.GetPreferenceAsync(templateId, "Course"));
    }

    [Fact]
    public async Task DeletePreference_ReturnsWhetherPreferenceExisted()
    {
        var store = await CreateStoreAsync();
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        InsertTemplateWithZones(templateId, zoneId);
        var preference = new DesktopCategoryZonePreference(
            Guid.NewGuid(), templateId, "Course", zoneId,
            1d, 1, Utc("2026-08-10T01:00:00Z"));
        await store.SavePreferenceAsync(preference);

        Assert.True(await store.DeletePreferenceAsync(preference.PreferenceId));
        Assert.False(await store.DeletePreferenceAsync(preference.PreferenceId));
        Assert.Null(await store.GetPreferenceAsync(templateId, "Course"));
    }

    [Fact]
    public async Task AppendEvidence_AcceptedCorrectedRejected_RoundTripAsImmutableHistory()
    {
        var store = await CreateStoreAsync();
        var templateId = Guid.NewGuid();
        var proposedZoneId = Guid.NewGuid();
        var correctedZoneId = Guid.NewGuid();

        var accepted = MakeEvidence(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Utc("2026-08-10T01:00:00Z"),
            DesktopClassificationFeedbackKind.Accepted,
            templateId,
            proposedZoneId,
            "Development",
            proposedZoneId);
        var corrected = MakeEvidence(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Utc("2026-08-10T02:00:00Z"),
            DesktopClassificationFeedbackKind.Corrected,
            templateId,
            proposedZoneId,
            "Course",
            correctedZoneId);
        var rejected = MakeEvidence(
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Utc("2026-08-10T03:00:00Z"),
            DesktopClassificationFeedbackKind.Rejected,
            null,
            null,
            null,
            null);

        await store.AppendEvidenceAsync(accepted);
        await store.AppendEvidenceAsync(corrected);
        await store.AppendEvidenceAsync(rejected);

        var loaded = await store.GetRecentEvidenceAsync(10);
        Assert.Equal(new[] { rejected.EvidenceId, corrected.EvidenceId, accepted.EvidenceId },
            loaded.Select(static item => item.EvidenceId));

        var loadedCorrected = loaded[1];
        Assert.Equal(templateId, loadedCorrected.TemplateId);
        Assert.Equal("desktop:item", loadedCorrected.StableId);
        Assert.Equal("demo.cs", loadedCorrected.DisplayName);
        Assert.Equal(".cs", loadedCorrected.FileExtension);
        Assert.Equal(ItemKind.File, loadedCorrected.ItemKind);
        Assert.Equal("Development", loadedCorrected.ProposedCategory);
        Assert.Equal(proposedZoneId, loadedCorrected.ProposedZoneId);
        Assert.Equal(DesktopItemClassificationSource.AiProvider, loadedCorrected.ProposedSource);
        Assert.Equal(DesktopClassificationFeedbackKind.Corrected, loadedCorrected.FeedbackKind);
        Assert.Equal("Course", loadedCorrected.FinalCategory);
        Assert.Equal(correctedZoneId, loadedCorrected.FinalZoneId);
    }

    [Fact]
    public async Task AppendEvidence_DuplicateEvidenceId_IsRejectedWithoutOverwrite()
    {
        var store = await CreateStoreAsync();
        var evidence = MakeEvidence(
            Guid.NewGuid(),
            Utc("2026-08-10T01:00:00Z"),
            DesktopClassificationFeedbackKind.Rejected,
            null,
            null,
            null,
            null);

        await store.AppendEvidenceAsync(evidence);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendEvidenceAsync(evidence));

        Assert.Contains(evidence.EvidenceId.ToString("D"), ex.Message);
        Assert.Single(await store.GetRecentEvidenceAsync(10));
    }

    [Fact]
    public async Task GetRecentEvidence_UsesSqlLimitAndNewestFirst()
    {
        var store = await CreateStoreAsync();
        for (var i = 0; i < 6; i++)
        {
            await store.AppendEvidenceAsync(MakeEvidence(
                Guid.NewGuid(),
                Utc($"2026-08-10T0{i}:00:00Z"),
                DesktopClassificationFeedbackKind.Rejected,
                null,
                null,
                null,
                null));
        }

        var loaded = await store.GetRecentEvidenceAsync(3);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(
            new[]
            {
                Utc("2026-08-10T05:00:00Z"),
                Utc("2026-08-10T04:00:00Z"),
                Utc("2026-08-10T03:00:00Z"),
            },
            loaded.Select(static item => item.ObservedAtUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SqliteDesktopUserProfileStore.MaxRecentEvidenceRead + 1)]
    public async Task GetRecentEvidence_InvalidLimit_Rejects(int limit)
    {
        var store = await CreateStoreAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetRecentEvidenceAsync(limit));
    }

    [Fact]
    public async Task ClearEvidence_DeletesHistoryButPreservesConfirmedPreference()
    {
        var store = await CreateStoreAsync();
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        InsertTemplateWithZones(templateId, zoneId);
        var preference = new DesktopCategoryZonePreference(
            Guid.NewGuid(), templateId, "Development", zoneId,
            0.9, 4, Utc("2026-08-10T01:00:00Z"));
        await store.SavePreferenceAsync(preference);
        await store.AppendEvidenceAsync(MakeEvidence(
            Guid.NewGuid(),
            Utc("2026-08-10T02:00:00Z"),
            DesktopClassificationFeedbackKind.Rejected,
            null,
            null,
            null,
            null));
        await store.AppendEvidenceAsync(MakeEvidence(
            Guid.NewGuid(),
            Utc("2026-08-10T03:00:00Z"),
            DesktopClassificationFeedbackKind.Rejected,
            null,
            null,
            null,
            null));

        Assert.Equal(2, await store.ClearEvidenceAsync());
        Assert.Empty(await store.GetRecentEvidenceAsync(10));
        Assert.NotNull(await store.GetPreferenceAsync(templateId, "Development"));
    }

    [Fact]
    public async Task GetRecentEvidence_CorruptAcceptedOutcome_FailsClosed()
    {
        var store = await CreateStoreAsync();
        var evidenceId = Guid.NewGuid();

        using (var connection = OpenRawConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                INSERT INTO desktop_classification_evidence
                    (evidence_id, observed_at_utc, template_id,
                     stable_id, display_name, file_extension, item_kind,
                     proposed_category, proposed_zone_id, proposed_source,
                     feedback_kind, final_category, final_zone_id)
                VALUES
                    (@evidence_id, '2026-08-10T01:00:00.0000000+00:00', NULL,
                     'desktop:item', 'demo.cs', '.cs', 0,
                     'Development', NULL, 3,
                     0, 'Course', NULL);
            ";
            command.Parameters.AddWithValue("@evidence_id", evidenceId.ToString("D"));
            command.ExecuteNonQuery();
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetRecentEvidenceAsync(10));
        Assert.Contains(evidenceId.ToString("D"), ex.Message);
    }

    private async Task<SqliteDesktopUserProfileStore> CreateStoreAsync()
    {
        var store = new SqliteDesktopUserProfileStore(_dbPath);
        await store.InitializeAsync();
        return store;
    }

    private static DesktopClassificationBehaviorEvidence MakeEvidence(
        Guid evidenceId,
        DateTimeOffset observedAtUtc,
        DesktopClassificationFeedbackKind feedbackKind,
        Guid? templateId,
        Guid? proposedZoneId,
        string? finalCategory,
        Guid? finalZoneId)
        => new(
            evidenceId,
            observedAtUtc,
            templateId,
            "desktop:item",
            "demo.cs",
            ".cs",
            ItemKind.File,
            "Development",
            proposedZoneId,
            DesktopItemClassificationSource.AiProvider,
            feedbackKind,
            finalCategory,
            finalZoneId);

    private void InsertTemplateWithZones(Guid templateId, params Guid[] zoneIds)
    {
        using var connection = OpenRawConnection();
        using var transaction = connection.BeginTransaction();

        using (var template = connection.CreateCommand())
        {
            template.Transaction = transaction;
            template.CommandText = @"
                INSERT INTO desktop_layout_templates
                    (template_id, name, version, target_display_binding_kind,
                     target_display_device_name, zone_count)
                VALUES
                    (@template_id, 'Test', 1, 1, NULL, @zone_count);
            ";
            template.Parameters.AddWithValue("@template_id", templateId.ToString("D"));
            template.Parameters.AddWithValue("@zone_count", zoneIds.Length);
            template.ExecuteNonQuery();
        }

        for (var i = 0; i < zoneIds.Length; i++)
        {
            using var zone = connection.CreateCommand();
            zone.Transaction = transaction;
            zone.CommandText = @"
                INSERT INTO desktop_layout_template_zones
                    (template_id, ordinal, zone_id, name,
                     left_value, top_value, right_value, bottom_value,
                     sort_order, placement_flow, max_item_count)
                VALUES
                    (@template_id, @ordinal, @zone_id, @name,
                     0, 0, 10000, 10000, @sort_order, 0, NULL);
            ";
            zone.Parameters.AddWithValue("@template_id", templateId.ToString("D"));
            zone.Parameters.AddWithValue("@ordinal", i);
            zone.Parameters.AddWithValue("@zone_id", zoneIds[i].ToString("D"));
            zone.Parameters.AddWithValue("@name", $"Zone {i}");
            zone.Parameters.AddWithValue("@sort_order", i);
            zone.ExecuteNonQuery();
        }

        transaction.Commit();
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
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static DateTimeOffset Utc(string value)
        => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
