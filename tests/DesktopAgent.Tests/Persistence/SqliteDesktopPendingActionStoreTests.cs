using System.Text.RegularExpressions;
using DesktopAgent.Core.Automation;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.Automation;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.Persistence;

/// <summary>
/// 覆盖共享 schema v11：desktop_pending_actions.queue_reason 的允许列表
/// 必须接纳 DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured = 24；
/// 并且 V10 旧库升级到 V11 时必须完整保留已有 Pending Action 数据。
/// </summary>
public sealed class SqliteDesktopPendingActionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _bootstrapPath;

    public SqliteDesktopPendingActionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DesktopAgent.Tests.PendingV11.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "pending.db");
        _bootstrapPath = Path.Combine(_tempDir, "bootstrap.db");
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
    public async Task SaveAndGet_QueueReason24_RoundTripsThroughStore()
    {
        var store = new SqliteDesktopPendingActionStore(_dbPath);
        await store.InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));

        var templateId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        var expected = new DesktopPendingAction(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 1, 8, 30, 0, TimeSpan.Zero),
            templateId,
            new DesktopItemFacts("desktop:geometry", "geometry.pdf", ItemKind.File, ".pdf", false, true),
            new DesktopItemClassificationDecision(
                "Course",
                zoneId,
                0.64,
                DesktopItemClassificationSource.AiProvider,
                "网格位置尚未实测"),
            DesktopAutomationAuthorizationBasis.AiOnly,
            DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured);

        await store.SaveAsync(expected);
        var loaded = await store.GetAsync(expected.PendingActionId);

        Assert.NotNull(loaded);
        Assert.Equal(DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured, loaded.QueueReason);
        AssertSameAction(expected, loaded);
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact]
    public async Task QueueReasonAllowList_MatchesEnumAndStaysRestrictive()
    {
        var store = new SqliteDesktopPendingActionStore(_dbPath);
        await store.InitializeAsync();

        // schema 允许列表与 Queue reason 的取值必须与 DesktopPendingAction.IsQueueReason 的集合完全一致。
        var allowed = new[] { 10, 11, 20, 21, 22, 23, 24 };
        var rejected = new[] { 0, 1, 12, 30, 31, 32 };

        foreach (var reason in allowed)
            Assert.True(TryInsertRawQueueReason(_dbPath, reason), $"queue_reason={reason} 应被 schema 接受");

        foreach (var reason in rejected)
            Assert.False(TryInsertRawQueueReason(_dbPath, reason), $"queue_reason={reason} 应被 schema 拒绝");

        // 允许 + 拒绝必须恰好覆盖枚举全集，避免将来新增枚举值时静默漏掉迁移。
        var defined = Enum.GetValues<DesktopAutomationPolicyReason>()
            .Select(value => (int)value)
            .OrderBy(value => value)
            .ToArray();
        Assert.Equal(defined, allowed.Concat(rejected).OrderBy(value => value).ToArray());

        // 宽化后仍然是精确允许列表，而不是开放式范围。
        var tableSql = ReadTableSql(_dbPath, "desktop_pending_actions");
        Assert.Contains("queue_reason IN (10,11,20,21,22,23,24)", tableSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V10Database_UpgradePreservesExistingRowsAndAcceptsReason24()
    {
        // 夹具表结构不手写：先让真实 schema 建一次表，取出生产代码写出的建表语句；
        // 再把 queue_reason 允许列表还原成 V10 形态，从而保证夹具与生产 DDL 不会漂移。
        var bootstrap = new SqliteDesktopPendingActionStore(_bootstrapPath);
        await bootstrap.InitializeAsync();

        var v11TableSql = ReadTableSql(_bootstrapPath, "desktop_pending_actions");
        Assert.Contains("24", v11TableSql, StringComparison.Ordinal);

        var v10TableSql = Regex.Replace(
            v11TableSql,
            @"queue_reason\s+IN\s*\(\s*10\s*,\s*11\s*,\s*20\s*,\s*21\s*,\s*22\s*,\s*23\s*,\s*24\s*\)",
            "queue_reason IN (10,11,20,21,22,23)");
        Assert.NotEqual(v11TableSql, v10TableSql);
        Assert.DoesNotContain("24", v10TableSql, StringComparison.Ordinal);

        using (var connection = OpenRawConnection(_dbPath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $@"
                {v10TableSql};
                CREATE INDEX IF NOT EXISTS idx_pending_actions_created_at_utc
                    ON desktop_pending_actions (created_at_utc ASC, pending_action_id ASC);
                PRAGMA user_version = 10;
            ";
            command.ExecuteNonQuery();
        }

        Assert.Equal(10, ReadUserVersion(_dbPath));

        // 用真实写入路径往 V10 表里放两行旧数据（一行无 Decision，一行带完整 Decision）。
        var store = new SqliteDesktopPendingActionStore(_dbPath);

        var withoutDecision = new DesktopPendingAction(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            null,
            new DesktopItemFacts("desktop:legacy-a", "legacy-a.txt", ItemKind.File, null, false, true),
            null,
            DesktopAutomationAuthorizationBasis.None,
            DesktopAutomationPolicyReason.NoExecutableDecision);

        var legacyTemplateId = Guid.NewGuid();
        var legacyZoneId = Guid.NewGuid();
        var withDecision = new DesktopPendingAction(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 1, 0, 5, 0, TimeSpan.Zero),
            legacyTemplateId,
            new DesktopItemFacts("desktop:legacy-b", "legacy-b.pdf", ItemKind.File, ".pdf", false, true),
            new DesktopItemClassificationDecision(
                "Development",
                legacyZoneId,
                0.37,
                DesktopItemClassificationSource.LocalRule,
                "旧库遗留建议"),
            DesktopAutomationAuthorizationBasis.AiOnly,
            DesktopAutomationPolicyReason.AssistedRequiresConfirmedPolicy);

        await store.SaveAsync(withoutDecision);
        await store.SaveAsync(withDecision);

        // 缺陷现场：V10 约束不允许 24，真实写入路径必须失败。
        var reason24 = new DesktopPendingAction(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 1, 0, 10, 0, TimeSpan.Zero),
            legacyTemplateId,
            new DesktopItemFacts("desktop:geometry", "geometry.pdf", ItemKind.File, ".pdf", false, true),
            new DesktopItemClassificationDecision(
                "Course",
                legacyZoneId,
                0.5,
                DesktopItemClassificationSource.AiProvider,
                "网格位置尚未实测"),
            DesktopAutomationAuthorizationBasis.AiOnly,
            DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured);

        await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync(reason24));

        // 走真实的 Initialize 升级路径。
        await store.InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.False(TableExists(_dbPath, "desktop_pending_actions_v11"));
        Assert.True(IndexExists(_dbPath, "idx_pending_actions_created_at_utc"));
        Assert.Contains(
            "queue_reason IN (10,11,20,21,22,23,24)",
            ReadTableSql(_dbPath, "desktop_pending_actions"),
            StringComparison.Ordinal);

        // 旧数据必须逐字段存活，且没有重复行。
        var loadedWithoutDecision = await store.GetAsync(withoutDecision.PendingActionId);
        Assert.NotNull(loadedWithoutDecision);
        AssertSameAction(withoutDecision, loadedWithoutDecision);

        var loadedWithDecision = await store.GetAsync(withDecision.PendingActionId);
        Assert.NotNull(loadedWithDecision);
        AssertSameAction(withDecision, loadedWithDecision);

        // 升级后同一行可以正常写入 24。
        await store.SaveAsync(reason24);
        var loadedReason24 = await store.GetAsync(reason24.PendingActionId);
        Assert.NotNull(loadedReason24);
        Assert.Equal(DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured, loadedReason24.QueueReason);
        AssertSameAction(reason24, loadedReason24);

        Assert.Equal(3, await store.CountAsync());
    }

    [Fact]
    public async Task RepeatedInitialize_KeepsV11ConstraintAndDoesNotDuplicateRows()
    {
        var store = new SqliteDesktopPendingActionStore(_dbPath);
        await store.InitializeAsync();
        await store.InitializeAsync();
        await store.InitializeAsync();

        Assert.Equal(13, ReadUserVersion(_dbPath));
        Assert.Contains(
            "queue_reason IN (10,11,20,21,22,23,24)",
            ReadTableSql(_dbPath, "desktop_pending_actions"),
            StringComparison.Ordinal);

        var action = new DesktopPendingAction(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero),
            null,
            new DesktopItemFacts("desktop:geometry", "geometry.pdf", ItemKind.File, ".pdf", false, true),
            null,
            DesktopAutomationAuthorizationBasis.None,
            DesktopAutomationPolicyReason.NoExecutableDecision);

        await store.SaveAsync(action);
        await store.InitializeAsync();

        Assert.Equal(1, await store.CountAsync());
        var loaded = await store.GetAsync(action.PendingActionId);
        Assert.NotNull(loaded);
        AssertSameAction(action, loaded);
    }

    private static void AssertSameAction(DesktopPendingAction expected, DesktopPendingAction actual)
    {
        Assert.Equal(expected.PendingActionId, actual.PendingActionId);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.TemplateId, actual.TemplateId);
        Assert.Equal(expected.AuthorizationBasis, actual.AuthorizationBasis);
        Assert.Equal(expected.QueueReason, actual.QueueReason);

        Assert.Equal(expected.Item.StableId, actual.Item.StableId);
        Assert.Equal(expected.Item.DisplayName, actual.Item.DisplayName);
        Assert.Equal(expected.Item.ItemKind, actual.Item.ItemKind);
        Assert.Equal(expected.Item.FileExtension, actual.Item.FileExtension);
        Assert.Equal(expected.Item.IsSystemItem, actual.Item.IsSystemItem);
        Assert.Equal(expected.Item.IsFileSystemItem, actual.Item.IsFileSystemItem);

        Assert.Equal(expected.Decision?.Category, actual.Decision?.Category);
        Assert.Equal(expected.Decision?.TargetZoneId, actual.Decision?.TargetZoneId);
        Assert.Equal(expected.Decision?.Confidence, actual.Decision?.Confidence);
        Assert.Equal(expected.Decision?.Source, actual.Decision?.Source);
        Assert.Equal(expected.Decision?.Explanation, actual.Decision?.Explanation);
    }

    /// <summary>
    /// 绕过领域模型直接探测 schema 约束：其它列都取满足 CHECK 的最小值，
    /// 只有 queue_reason 是变量。
    /// </summary>
    private static bool TryInsertRawQueueReason(string path, int queueReason)
    {
        using var connection = OpenRawConnection(path);
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
                (@id, '2026-09-01T00:00:00.0000000+00:00', NULL,
                 'desktop:probe', 'probe.txt', NULL, 0,
                 0, 0,
                 NULL, NULL, NULL,
                 NULL, NULL,
                 0, @queue_reason);
        ";
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@queue_reason", queueReason);

        try
        {
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static SqliteConnection OpenRawConnection(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static int ReadUserVersion(string path)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (int)(long)command.ExecuteScalar()!;
    }

    private static string ReadTableSql(string path, string name)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", name);
        return (string)command.ExecuteScalar()!;
    }

    private static bool TableExists(string path, string name)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", name);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static bool IndexExists(string path, string name)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name;";
        command.Parameters.AddWithValue("@name", name);
        return (long)command.ExecuteScalar()! > 0;
    }
}
