using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.DesktopLayouts.Templates.Rules;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Rules;

public sealed class SqliteDesktopLayoutIconRuleStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDesktopLayoutIconRuleStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            $"DesktopAgent.Tests.Rules.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* 尽力清理 */ }
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
        var c = new SqliteConnection(builder.ConnectionString);
        c.Open();
        return c;
    }

    // ═══ Helpers ═══

    private SqliteDesktopLayoutIconRuleStore CreateRuleStore()
    {
        var store = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();
        return store;
    }

    private SqliteDesktopLayoutTemplateStore CreateTemplateStore()
    {
        var store = new SqliteDesktopLayoutTemplateStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();
        return store;
    }

    private DesktopLayoutTemplate SaveTemplate(Guid tid, Guid? zid = null)
    {
        zid ??= Guid.NewGuid();
        var zone = new DesktopLayoutZone(zid.Value, "Z",
            new NormalizedRect(0, 0, 10000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        var t = new DesktopLayoutTemplate(tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(), new[] { zone });
        CreateTemplateStore().SaveAsync(t).GetAwaiter().GetResult();
        return t;
    }

    // ═══════════════════════════ B. Normal paths ═══════════════════════════

    [Fact] public void B1_Constructor_PathValidation()
    {
        Assert.Throws<ArgumentException>(() =>
            new SqliteDesktopLayoutIconRuleStore(""));
        Assert.Throws<ArgumentException>(() =>
            new SqliteDesktopLayoutIconRuleStore("  "));
        Assert.Throws<ArgumentException>(() =>
            new SqliteDesktopLayoutIconRuleStore(null!));
    }

    [Fact] public void B2_Init_Idempotent()
    {
        var s = CreateRuleStore();
        s.InitializeAsync().GetAwaiter().GetResult();
        s.InitializeAsync().GetAwaiter().GetResult();
        // Should not throw
    }

    [Fact] public void B3_AllThreeKinds_RoundTrip()
    {
        var tid = Guid.NewGuid();
        var zid = Guid.NewGuid();
        SaveTemplate(tid, zid);

        var s = CreateRuleStore();
        var original = new DesktopLayoutIconRuleSet(tid, new[]
        {
            DesktopLayoutIconRule.ForExclude("exclude-me"),
            DesktopLayoutIconRule.ForPin("pin-me"),
            DesktopLayoutIconRule.ForZoneOverride("override-me", zid),
        });
        s.SaveAsync(original).GetAwaiter().GetResult();

        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.RuleCount);

        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, loaded.Rules[0].Kind);
        Assert.Equal("exclude-me", loaded.Rules[0].StableId);
        Assert.Null(loaded.Rules[0].TargetZoneId);

        Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, loaded.Rules[1].Kind);
        Assert.Equal("override-me", loaded.Rules[1].StableId);
        Assert.Equal(zid, loaded.Rules[1].TargetZoneId);

        Assert.Equal(DesktopLayoutIconRuleKind.Pin, loaded.Rules[2].Kind);
        Assert.Equal("pin-me", loaded.Rules[2].StableId);
        Assert.Null(loaded.Rules[2].TargetZoneId);
    }

    [Fact] public void B4_StableId_CasePreserved()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();
        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("MyApp.EXE") }))
            .GetAwaiter().GetResult();
        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Equal("MyApp.EXE", loaded!.Rules[0].StableId);
    }

    [Fact] public void B5_StableId_WhitespacePreserved()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();
        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("  app  ") }))
            .GetAwaiter().GetResult();
        Assert.Equal("  app  ", s.GetAsync(tid).GetAwaiter().GetResult()!.Rules[0].StableId);
    }

    [Fact] public void B6_StableId_PureWhitespace_Preserved()
    {
        // Pure whitespace StableId is allowed by DesktopLayoutIconRule
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();
        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("   ") }))
            .GetAwaiter().GetResult();
        Assert.Equal("   ", s.GetAsync(tid).GetAwaiter().GetResult()!.Rules[0].StableId);
    }

    [Fact] public void B7_BinaryOrdinalOrder()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();
        s.SaveAsync(new DesktopLayoutIconRuleSet(tid, new[]
        {
            DesktopLayoutIconRule.ForExclude("c"),
            DesktopLayoutIconRule.ForExclude("A"),
            DesktopLayoutIconRule.ForExclude("b"),
        })).GetAwaiter().GetResult();

        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Equal("A", loaded!.Rules[0].StableId); // ASCII: A(65) < b(98) < c(99)
        Assert.Equal("b", loaded.Rules[1].StableId);
        Assert.Equal("c", loaded.Rules[2].StableId);
    }

    [Fact] public void B8_Save_FullReplace()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();

        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("a"), DesktopLayoutIconRule.ForExclude("b") }))
            .GetAwaiter().GetResult();

        // Replace with single rule
        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForPin("c") }))
            .GetAwaiter().GetResult();

        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Single(loaded!.Rules);
        Assert.Equal("c", loaded.Rules[0].StableId);
        Assert.Equal(DesktopLayoutIconRuleKind.Pin, loaded.Rules[0].Kind);
    }

    [Fact] public void B9_Save_EmptyRuleSet()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();

        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("a") }))
            .GetAwaiter().GetResult();

        // Save empty
        s.SaveAsync(DesktopLayoutIconRuleSet.Empty(tid)).GetAwaiter().GetResult();

        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.RuleCount);
        Assert.False(loaded.HasRules);
    }

    [Fact] public void B10_Get_ExistingTemplateEmptyRules()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();

        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.RuleCount);
        Assert.Equal(tid, loaded.TemplateId);
    }

    [Fact] public void B11_Get_NonExistentTemplate_Null()
    {
        var s = CreateRuleStore();
        Assert.Null(s.GetAsync(Guid.NewGuid()).GetAwaiter().GetResult());
    }

    [Fact] public void B12_Save_NonExistentTemplate_Rejected()
    {
        var s = CreateRuleStore();
        var ex = Assert.Throws<ArgumentException>(() =>
            s.SaveAsync(new DesktopLayoutIconRuleSet(Guid.NewGuid(),
                new[] { DesktopLayoutIconRule.ForExclude("a") }))
                .GetAwaiter().GetResult());
        Assert.Contains("不存在", ex.Message);
    }

    [Fact] public void B13_Save_EmptyRuleSet_NonExistentTemplate_Rejected()
    {
        // Even empty RuleSet requires template to exist
        var s = CreateRuleStore();
        Assert.Throws<ArgumentException>(() =>
            s.SaveAsync(DesktopLayoutIconRuleSet.Empty(Guid.NewGuid()))
                .GetAwaiter().GetResult());
    }

    [Fact] public void B14_Delete_HasData_ReturnsTrue()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();
        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("a") }))
            .GetAwaiter().GetResult();

        Assert.True(s.DeleteAsync(tid).GetAwaiter().GetResult());
        // After delete, empty rule set
        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Equal(0, loaded!.RuleCount);
    }

    [Fact] public void B15_Delete_NoData_ReturnsFalse()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();
        Assert.False(s.DeleteAsync(tid).GetAwaiter().GetResult());
    }

    [Fact] public void B16_Delete_DoesNotDeleteTemplate()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();
        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("a") }))
            .GetAwaiter().GetResult();
        s.DeleteAsync(tid).GetAwaiter().GetResult();

        // Template still exists, rules empty
        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.RuleCount);
    }

    [Fact] public void B17_TemplateDelete_CascadeRules()
    {
        var tStore = CreateTemplateStore();
        var rStore = CreateRuleStore();
        var tid = Guid.NewGuid();
        SaveTemplate(tid);

        rStore.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("a") }))
            .GetAwaiter().GetResult();

        tStore.DeleteAsync(tid).GetAwaiter().GetResult();

        // Rules cascade-deleted
        Assert.Null(rStore.GetAsync(tid).GetAwaiter().GetResult());
    }

    [Fact] public void B18_TemplateResave_ZoneIdUnchanged_RulesNotLost()
    {
        var tid = Guid.NewGuid();
        var zid = Guid.NewGuid();
        var tStore = CreateTemplateStore();
        var rStore = CreateRuleStore();

        // Save template with zone + rule
        var zone = new DesktopLayoutZone(zid, "Z",
            new NormalizedRect(0, 0, 10000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        var t = new DesktopLayoutTemplate(tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(), new[] { zone });
        tStore.SaveAsync(t).GetAwaiter().GetResult();

        rStore.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForZoneOverride("a", zid) }))
            .GetAwaiter().GetResult();

        // Resave same template (same ZoneId, same zones)
        tStore.SaveAsync(t).GetAwaiter().GetResult();

        // Rules should still exist
        var loaded = rStore.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Equal(1, loaded!.RuleCount);
        Assert.Equal(zid, loaded.Rules[0].TargetZoneId);
    }

    // ═══════════════════════════ C. ZoneOverride validation ═══════════════════════════

    [Fact] public void C1_ZoneOverride_ValidRoundTrip()
    {
        var tid = Guid.NewGuid();
        var zid = Guid.NewGuid();
        SaveTemplate(tid, zid);
        var s = CreateRuleStore();

        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForZoneOverride("a", zid) }))
            .GetAwaiter().GetResult();

        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Equal(DesktopLayoutIconRuleKind.ZoneOverride, loaded!.Rules[0].Kind);
        Assert.Equal(zid, loaded.Rules[0].TargetZoneId);
    }

    [Fact] public void C2_ZoneOverride_NonExistentZone_Rejected()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();

        var ex = Assert.Throws<ArgumentException>(() =>
            s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
                new[] { DesktopLayoutIconRule.ForZoneOverride("a", Guid.NewGuid()) }))
                .GetAwaiter().GetResult());
        Assert.Contains("不存在", ex.Message);
    }

    [Fact] public void C3_ZoneOverride_OtherTemplateZone_Rejected()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var otherZid = Guid.NewGuid();
        var otherTid = Guid.NewGuid();
        SaveTemplate(otherTid, otherZid);

        var s = CreateRuleStore();
        var ex = Assert.Throws<ArgumentException>(() =>
            s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
                new[] { DesktopLayoutIconRule.ForZoneOverride("a", otherZid) }))
                .GetAwaiter().GetResult());
        Assert.Contains("不存在", ex.Message);
    }

    [Fact] public void C4_ValidationFails_OldRulesPreserved()
    {
        var tid = Guid.NewGuid();
        var zid = Guid.NewGuid();
        SaveTemplate(tid, zid);
        var s = CreateRuleStore();

        // Save valid rule first
        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("keep-me") }))
            .GetAwaiter().GetResult();

        // Try to save with invalid ZoneOverride
        Assert.Throws<ArgumentException>(() =>
            s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
                new[] { DesktopLayoutIconRule.ForZoneOverride("bad", Guid.NewGuid()) }))
                .GetAwaiter().GetResult());

        // Old rule still there
        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Equal(1, loaded!.RuleCount);
        Assert.Equal("keep-me", loaded.Rules[0].StableId);
        Assert.Equal(DesktopLayoutIconRuleKind.Exclude, loaded.Rules[0].Kind);
    }

    [Fact] public void C5_ZoneDeleted_GetFailsClosed()
    {
        var tid = Guid.NewGuid();
        var zid = Guid.NewGuid();
        var tStore = CreateTemplateStore();
        var rStore = CreateRuleStore();

        var zone = new DesktopLayoutZone(zid, "Z",
            new NormalizedRect(0, 0, 10000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        var t = new DesktopLayoutTemplate(tid, "T", 1,
            TargetDisplayBinding.ForPrimaryDisplay(), new[] { zone });
        tStore.SaveAsync(t).GetAwaiter().GetResult();

        rStore.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForZoneOverride("a", zid) }))
            .GetAwaiter().GetResult();

        // Save template WITHOUT the zone
        var newZone = new DesktopLayoutZone(Guid.NewGuid(), "Z2",
            new NormalizedRect(0, 0, 10000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        var t2 = new DesktopLayoutTemplate(tid, "T", 2,
            TargetDisplayBinding.ForPrimaryDisplay(), new[] { newZone });
        tStore.SaveAsync(t2).GetAwaiter().GetResult();

        // GetAsync should fail-closed — dangling ZoneOverride
        Assert.Throws<InvalidDataException>(() =>
            rStore.GetAsync(tid).GetAwaiter().GetResult());
    }

    // ═══════════════════════════ D. Corrupt data ═══════════════════════════

    private void InsertCorruptRule(Guid tid, string stableId, int kind, string? zoneIdStr)
    {
        // Use a connection with ForeignKeys=off to bypass FK, and create a
        // minimal table without the CHECK constraints for corruption testing.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = false, Pooling = false,
        };
        using var c = new SqliteConnection(builder.ConnectionString);
        c.Open();

        // Ensure the template exists first so FK doesn't matter
        // Then drop and recreate the rules table without CHECKs
        using var drop = c.CreateCommand();
        drop.CommandText = "DROP TABLE IF EXISTS desktop_layout_template_icon_rules;";
        drop.ExecuteNonQuery();

        using var create = c.CreateCommand();
        create.CommandText = @"
            CREATE TABLE desktop_layout_template_icon_rules (
                template_id     TEXT NOT NULL,
                stable_id       TEXT NOT NULL,
                rule_kind       INTEGER NOT NULL,
                target_zone_id  TEXT NULL,
                PRIMARY KEY (template_id, stable_id)
            );
        ";
        create.ExecuteNonQuery();

        using var ins = c.CreateCommand();
        ins.CommandText = @"
            INSERT INTO desktop_layout_template_icon_rules
                (template_id, stable_id, rule_kind, target_zone_id)
            VALUES (@tid, @sid, @kind, @zid);
        ";
        ins.Parameters.AddWithValue("@tid", tid.ToString("D"));
        ins.Parameters.AddWithValue("@sid", stableId);
        ins.Parameters.AddWithValue("@kind", kind);
        ins.Parameters.AddWithValue("@zid",
            zoneIdStr is not null ? (object)zoneIdStr : DBNull.Value);
        ins.ExecuteNonQuery();
    }

    [Fact] public void D1_UndefinedRuleKind_GetFails()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        CreateRuleStore(); // Ensure schema

        InsertCorruptRule(tid, "bad-kind", 99, null);

        var s = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        var ex = Assert.Throws<InvalidDataException>(() =>
            s.GetAsync(tid).GetAwaiter().GetResult());
        Assert.Contains("rule_kind", ex.Message);
        Assert.Contains("99", ex.Message);
    }

    [Fact] public void D2_ExcludeWithTargetZoneId_GetFails()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        CreateRuleStore();

        InsertCorruptRule(tid, "ex-bad", 0, Guid.NewGuid().ToString("D"));

        var s = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        Assert.Throws<InvalidDataException>(() =>
            s.GetAsync(tid).GetAwaiter().GetResult());
    }

    [Fact] public void D3_PinWithTargetZoneId_GetFails()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        CreateRuleStore();

        InsertCorruptRule(tid, "pin-bad", 1, Guid.NewGuid().ToString("D"));

        var s = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        Assert.Throws<InvalidDataException>(() =>
            s.GetAsync(tid).GetAwaiter().GetResult());
    }

    [Fact] public void D4_ZoneOverrideNullTargetZoneId_GetFails()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        CreateRuleStore();

        InsertCorruptRule(tid, "ovr-null", 2, null);

        var s = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        Assert.Throws<InvalidDataException>(() =>
            s.GetAsync(tid).GetAwaiter().GetResult());
    }

    [Fact] public void D5_ZoneOverrideInvalidGuid_GetFails()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        CreateRuleStore();

        InsertCorruptRule(tid, "ovr-badguid", 2, "not-a-guid");

        var s = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        Assert.Throws<InvalidDataException>(() =>
            s.GetAsync(tid).GetAwaiter().GetResult());
    }

    [Fact] public void D6_ZoneOverrideNonExistentZone_GetFails()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        CreateRuleStore();

        InsertCorruptRule(tid, "ovr-nozone", 2,
            Guid.NewGuid().ToString("D"));

        var s = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        Assert.Throws<InvalidDataException>(() =>
            s.GetAsync(tid).GetAwaiter().GetResult());
    }

    [Fact] public void D7_ZoneOverrideOtherTemplateZone_GetFails()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var otherZid = Guid.NewGuid();
        var otherTid = Guid.NewGuid();
        SaveTemplate(otherTid, otherZid);
        CreateRuleStore();

        InsertCorruptRule(tid, "ovr-other", 2, otherZid.ToString("D"));

        var s = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        Assert.Throws<InvalidDataException>(() =>
            s.GetAsync(tid).GetAwaiter().GetResult());
    }

    [Fact] public void D8_InvalidTemplateId_GetReturnsNull()
    {
        var s = CreateRuleStore();
        Assert.Null(s.GetAsync(Guid.NewGuid()).GetAwaiter().GetResult());
    }

    [Fact] public void D9_DomainConstructFailure_WrapsAsInvalidData()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        CreateRuleStore();

        // StableId empty string is rejected by DesktopLayoutIconRule constructor
        InsertCorruptRule(tid, "", 0, null);

        var s = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        var ex = Assert.Throws<InvalidDataException>(() =>
            s.GetAsync(tid).GetAwaiter().GetResult());
        Assert.Contains("StableId", ex.Message);
    }

    [Fact] public void D10_DuplicateStableId_FailsClosed()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        CreateRuleStore(); // Initialize v3 schema

        // Recreate rules table WITHOUT PRIMARY KEY to allow duplicate StableId rows
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = false, Pooling = false,
            };
            using var c = new SqliteConnection(builder.ConnectionString);
            c.Open();

            using var drop = c.CreateCommand();
            drop.CommandText = "DROP TABLE IF EXISTS desktop_layout_template_icon_rules;";
            drop.ExecuteNonQuery();

            using var create = c.CreateCommand();
            create.CommandText = @"
                CREATE TABLE desktop_layout_template_icon_rules (
                    template_id     TEXT NOT NULL,
                    stable_id       TEXT NOT NULL,
                    rule_kind       INTEGER NOT NULL,
                    target_zone_id  TEXT NULL
                );
            ";
            create.ExecuteNonQuery();

            // Insert two rows with identical template_id + stable_id (no PK to stop us)
            for (int i = 0; i < 2; i++)
            {
                using var ins = c.CreateCommand();
                ins.CommandText = @"
                    INSERT INTO desktop_layout_template_icon_rules
                        (template_id, stable_id, rule_kind, target_zone_id)
                    VALUES (@tid, @sid, @kind, @zid);
                ";
                ins.Parameters.AddWithValue("@tid", tid.ToString("D"));
                ins.Parameters.AddWithValue("@sid", "dup");
                ins.Parameters.AddWithValue("@kind", 0);
                ins.Parameters.AddWithValue("@zid", DBNull.Value);
                ins.ExecuteNonQuery();
            }
        }

        // DesktopLayoutIconRuleSet constructor must reject the duplicate StableId
        var s = new SqliteDesktopLayoutIconRuleStore(_dbPath);
        var ex = Assert.Throws<InvalidDataException>(() =>
            s.GetAsync(tid).GetAwaiter().GetResult());
        Assert.Contains(tid.ToString("D"), ex.Message);
        Assert.Contains("dup", ex.Message);
    }

    // ═══════════════════════════ E. Atomicity & cancellation ═══════════════════════════

    [Fact] public void E1_ReplaceFailure_OldRulesPreserved()
    {
        // Tested by C4 above — reaffirm here
        var tid = Guid.NewGuid();
        var zid = Guid.NewGuid();
        SaveTemplate(tid, zid);
        var s = CreateRuleStore();

        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("survivor") }))
            .GetAwaiter().GetResult();

        // Attempt invalid save — old rule survives
        Assert.Throws<ArgumentException>(() =>
            s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
                new[] { DesktopLayoutIconRule.ForZoneOverride("bad", Guid.NewGuid()) }))
                .GetAwaiter().GetResult());

        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Equal(1, loaded!.RuleCount);
        Assert.Equal("survivor", loaded.Rules[0].StableId);
    }

    [Fact] public void E2_Save_PreCanceled()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
                new[] { DesktopLayoutIconRule.ForExclude("a") }),
                cts.Token).GetAwaiter().GetResult());
    }

    [Fact] public void E3_Get_PreCanceled()
    {
        var s = CreateRuleStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            s.GetAsync(Guid.NewGuid(), cts.Token).GetAwaiter().GetResult());
    }

    [Fact] public void E4_Delete_PreCanceled()
    {
        var s = CreateRuleStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            s.DeleteAsync(Guid.NewGuid(), cts.Token).GetAwaiter().GetResult());
    }

    [Fact] public void E5_Init_PreCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new SqliteDesktopLayoutIconRuleStore(_dbPath)
                .InitializeAsync(cts.Token).GetAwaiter().GetResult());
    }

    [Fact] public void E6_Cancel_NoPartialData()
    {
        var tid = Guid.NewGuid();
        SaveTemplate(tid);
        var s = CreateRuleStore();

        s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
            new[] { DesktopLayoutIconRule.ForExclude("a") }))
            .GetAwaiter().GetResult();

        // Cancel during save — old data preserved
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            s.SaveAsync(new DesktopLayoutIconRuleSet(tid,
                new[] { DesktopLayoutIconRule.ForPin("b") }),
                cts.Token).GetAwaiter().GetResult());

        var loaded = s.GetAsync(tid).GetAwaiter().GetResult();
        Assert.Equal(1, loaded!.RuleCount);
        Assert.Equal("a", loaded.Rules[0].StableId);
    }

    // ═══════════════════════════ Additional ═══════════════════════════

    [Fact] public void Save_NullRuleSet_Rejected()
    {
        var s = CreateRuleStore();
        Assert.Throws<ArgumentNullException>(() =>
            s.SaveAsync(null!).GetAwaiter().GetResult());
    }

    [Fact] public void Delete_EmptyGuid_Rejected()
    {
        var s = CreateRuleStore();
        Assert.Throws<ArgumentException>(() =>
            s.DeleteAsync(Guid.Empty).GetAwaiter().GetResult());
    }

    [Fact] public void Get_EmptyGuid_Rejected()
    {
        var s = CreateRuleStore();
        Assert.Throws<ArgumentException>(() =>
            s.GetAsync(Guid.Empty).GetAwaiter().GetResult());
    }
}
