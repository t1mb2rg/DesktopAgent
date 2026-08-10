using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Intelligence;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.Intelligence;

public sealed class SqliteDesktopClassificationRuleStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDesktopClassificationRuleStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(), $"DesktopAgent.Tests.M4RuleStore.{Guid.NewGuid():N}");
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
    public async Task SaveGetAll_CategoryOnlyRule_RoundTripsWithoutTemplateBinding()
    {
        var store = await CreateStoreAsync();
        var rule = new DesktopClassificationRule(
            Guid.NewGuid(),
            "PDF 课程资料",
            new DesktopClassificationRuleCondition(
                fileExtension: ".PDF",
                fileNamePattern: "Course*",
                itemKind: ItemKind.File),
            "Course",
            null,
            null,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.AiProposedAndConfirmed,
            priority: 23,
            isEnabled: false);

        await store.SaveAsync(rule);
        var loaded = Assert.Single(await store.GetAllAsync());

        Assert.Equal(rule.RuleId, loaded.RuleId);
        Assert.Equal("PDF 课程资料", loaded.Name);
        Assert.Null(loaded.Condition.StableId);
        Assert.Equal(".pdf", loaded.Condition.FileExtension);
        Assert.Equal("Course*", loaded.Condition.FileNamePattern);
        Assert.Equal(ItemKind.File, loaded.Condition.ItemKind);
        Assert.Equal("Course", loaded.Category);
        Assert.Null(loaded.TargetTemplateId);
        Assert.Null(loaded.TargetZoneId);
        Assert.Equal(DesktopClassificationRuleAuthority.ExplicitUser, loaded.Authority);
        Assert.Equal(DesktopClassificationRuleProvenance.AiProposedAndConfirmed, loaded.Provenance);
        Assert.Equal(23, loaded.Priority);
        Assert.False(loaded.IsEnabled);
    }

    [Fact]
    public async Task SaveGetAll_TemplateBoundZone_RoundTripsAndValidatesMembership()
    {
        var store = await CreateStoreAsync();
        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        InsertTemplateWithZone(templateId, zoneId);

        var rule = MakeRule(
            targetTemplateId: templateId,
            targetZoneId: zoneId,
            category: "Development");

        await store.SaveAsync(rule);
        var loaded = Assert.Single(await store.GetAllAsync());

        Assert.Equal(templateId, loaded.TargetTemplateId);
        Assert.Equal(zoneId, loaded.TargetZoneId);
        Assert.Equal(zoneId, loaded.GetTargetZoneForTemplate(templateId));
        Assert.Null(loaded.GetTargetZoneForTemplate(Guid.NewGuid()));
    }

    [Fact]
    public async Task Save_TemplateBoundZoneOutsideTemplate_RejectsBeforeWrite()
    {
        var store = await CreateStoreAsync();
        var templateId = Guid.NewGuid();
        InsertTemplateWithZone(templateId, Guid.NewGuid());

        var rule = MakeRule(
            targetTemplateId: templateId,
            targetZoneId: Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(rule));
        Assert.Contains(templateId.ToString("D"), ex.Message);
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task Save_SameRuleId_ReplacesCompleteRule()
    {
        var store = await CreateStoreAsync();
        var ruleId = Guid.NewGuid();

        await store.SaveAsync(MakeRule(ruleId: ruleId, category: "Old", priority: 1));
        await store.SaveAsync(new DesktopClassificationRule(
            ruleId,
            "Updated",
            new DesktopClassificationRuleCondition(fileNamePattern: "*.cs"),
            "Development",
            null,
            null,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.UserAuthored,
            priority: 99));

        var loaded = Assert.Single(await store.GetAllAsync());
        Assert.Equal("Updated", loaded.Name);
        Assert.Equal("Development", loaded.Category);
        Assert.Equal("*.cs", loaded.Condition.FileNamePattern);
        Assert.Equal(99, loaded.Priority);
        Assert.Null(loaded.Condition.StableId);
    }

    [Fact]
    public async Task GetAll_UsesStableRuleIdOrder()
    {
        var store = await CreateStoreAsync();
        var ids = new[]
        {
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
        };

        foreach (var id in ids)
            await store.SaveAsync(MakeRule(ruleId: id));

        var loaded = await store.GetAllAsync();
        Assert.Equal(
            ids.OrderBy(static id => id.ToString("D"), StringComparer.Ordinal),
            loaded.Select(static rule => rule.RuleId));
    }

    [Fact]
    public async Task GetAll_MoreThanBoundedMaximum_RejectsInsteadOfLoadingUnboundedHistory()
    {
        var store = await CreateStoreAsync();

        using (var connection = OpenRawConnection())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO desktop_classification_rules
                    (rule_id, name, stable_id, category,
                     authority, provenance, priority, is_enabled)
                VALUES
                    (@rule_id, 'bulk', @stable_id, 'Development', 1, 1, 0, 1);
            ";
            var ruleIdParameter = command.Parameters.Add("@rule_id", SqliteType.Text);
            var stableIdParameter = command.Parameters.Add("@stable_id", SqliteType.Text);

            for (var i = 0; i <= SqliteDesktopClassificationRuleStore.MaxPersistedRules; i++)
            {
                ruleIdParameter.Value = Guid.NewGuid().ToString("D");
                stableIdParameter.Value = $"bulk:{i}";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync());
        Assert.Contains(
            SqliteDesktopClassificationRuleStore.MaxPersistedRules.ToString(),
            ex.Message);
    }

    [Fact]
    public async Task GetAll_CorruptEnumValue_FailsClosed()
    {
        var store = await CreateStoreAsync();
        var rule = MakeRule();
        await store.SaveAsync(rule);

        using (var connection = OpenRawConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                PRAGMA ignore_check_constraints = ON;
                UPDATE desktop_classification_rules
                SET authority = 99
                WHERE rule_id = @rule_id;
            ";
            command.Parameters.AddWithValue("@rule_id", rule.RuleId.ToString("D"));
            command.ExecuteNonQuery();
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync());
        Assert.Contains("authority", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_ReturnsWhetherRuleExisted()
    {
        var store = await CreateStoreAsync();
        var rule = MakeRule();
        await store.SaveAsync(rule);

        Assert.True(await store.DeleteAsync(rule.RuleId));
        Assert.False(await store.DeleteAsync(rule.RuleId));
        Assert.Empty(await store.GetAllAsync());
    }

    private async Task<SqliteDesktopClassificationRuleStore> CreateStoreAsync()
    {
        var store = new SqliteDesktopClassificationRuleStore(_dbPath);
        await store.InitializeAsync();
        return store;
    }

    private static DesktopClassificationRule MakeRule(
        Guid? ruleId = null,
        Guid? targetTemplateId = null,
        Guid? targetZoneId = null,
        string category = "Development",
        int priority = 0)
        => new(
            ruleId ?? Guid.NewGuid(),
            "Rule",
            new DesktopClassificationRuleCondition(stableId: "desktop:item"),
            category,
            targetTemplateId,
            targetZoneId,
            DesktopClassificationRuleAuthority.DeterministicLocal,
            DesktopClassificationRuleProvenance.BuiltIn,
            priority);

    private void InsertTemplateWithZone(Guid templateId, Guid zoneId)
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
                    (@template_id, 'Test', 1, 1, NULL, 1);
            ";
            template.Parameters.AddWithValue("@template_id", templateId.ToString("D"));
            template.ExecuteNonQuery();
        }

        using (var zone = connection.CreateCommand())
        {
            zone.Transaction = transaction;
            zone.CommandText = @"
                INSERT INTO desktop_layout_template_zones
                    (template_id, ordinal, zone_id, name,
                     left_value, top_value, right_value, bottom_value,
                     sort_order, placement_flow, max_item_count)
                VALUES
                    (@template_id, 0, @zone_id, 'Main',
                     0, 0, 10000, 10000, 0, 0, NULL);
            ";
            zone.Parameters.AddWithValue("@template_id", templateId.ToString("D"));
            zone.Parameters.AddWithValue("@zone_id", zoneId.ToString("D"));
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
}
