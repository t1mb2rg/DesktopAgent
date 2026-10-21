using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Persistence.Intelligence;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.Persistence;

public sealed class SqliteDesktopWorkspaceBlueprintStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDesktopWorkspaceBlueprintStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(), $"DesktopAgent.Tests.WorkspaceBlueprintStore.{Guid.NewGuid():N}");
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
    public void Serializer_RoundTrip_PreservesCompleteSemanticBlueprint()
    {
        var id = Guid.NewGuid();
        var source = MakeBlueprint(id, revision: 1, parentRevision: null, "三屏工作区 v1");

        var json = DesktopWorkspaceBlueprintJsonSerializer.Serialize(source);
        var restored = DesktopWorkspaceBlueprintJsonSerializer.Deserialize(
            json,
            DesktopWorkspaceBlueprintJsonSerializer.CurrentPayloadFormatVersion);

        AssertBlueprintEqual(source, restored);
    }

    [Fact]
    public void Serializer_PayloadContainsNoRuntimeExecutionAuthority()
    {
        var blueprint = MakeBlueprint(Guid.NewGuid(), 1, null, "纯设计语义");
        var json = DesktopWorkspaceBlueprintJsonSerializer.Serialize(blueprint);

        Assert.DoesNotContain("stable_id", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("device_name", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixel", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("native_slot", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("frozen_preview", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirmation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("execution", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAndGet_RoundTrip_PreservesAssetAndRevision()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 8, 28, 5, 30, 0, TimeSpan.Zero);
        var record = new DesktopWorkspaceBlueprintRevisionRecord(
            MakeBlueprint(id, 1, null, "工作模式"),
            createdAt,
            sourceUserIntent: "左屏保持，主屏留白，下方副屏集中整理");

        await store.CreateAsync("工作模式", record);

        var asset = await store.GetAssetAsync(id);
        Assert.NotNull(asset);
        Assert.Equal(id, asset!.BlueprintId);
        Assert.Equal("工作模式", asset.Name);
        Assert.Equal(1, asset.CurrentRevision);
        Assert.Equal(createdAt, asset.CreatedAtUtc);
        Assert.Equal(createdAt, asset.UpdatedAtUtc);
        Assert.False(asset.IsArchived);

        var loaded = await store.GetRevisionAsync(id, 1);
        Assert.NotNull(loaded);
        Assert.Equal(createdAt, loaded!.CreatedAtUtc);
        Assert.Null(loaded.SourceUnderstandingId);
        Assert.Equal("左屏保持，主屏留白，下方副屏集中整理", loaded.SourceUserIntent);
        AssertBlueprintEqual(record.Blueprint, loaded.Blueprint);

        var current = await store.GetCurrentRevisionAsync(id);
        Assert.NotNull(current);
        AssertBlueprintEqual(record.Blueprint, current!.Blueprint);
    }

    [Fact]
    public async Task AppendRevision_AdvancesCurrentWithoutMutatingPreviousRevision()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 8, 28, 5, 30, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(5);
        var v1 = new DesktopWorkspaceBlueprintRevisionRecord(
            MakeBlueprint(id, 1, null, "v1 初始设计"), t1);
        var v2 = new DesktopWorkspaceBlueprintRevisionRecord(
            MakeBlueprint(id, 2, 1, "v2 放大 Entertainment"), t2,
            sourceUserIntent: "Entertainment 再大一点");

        await store.CreateAsync("工作模式", v1);
        await store.AppendRevisionAsync(v2);

        var asset = await store.GetAssetAsync(id);
        Assert.Equal(2, asset!.CurrentRevision);
        Assert.Equal(t1, asset.CreatedAtUtc);
        Assert.Equal(t2, asset.UpdatedAtUtc);

        var old = await store.GetRevisionAsync(id, 1);
        var current = await store.GetCurrentRevisionAsync(id);
        Assert.Equal("v1 初始设计", old!.Blueprint.DesignSummary);
        Assert.Equal(1, old.Blueprint.Revision);
        Assert.Equal("v2 放大 Entertainment", current!.Blueprint.DesignSummary);
        Assert.Equal(2, current.Blueprint.Revision);
        Assert.Equal(1, current.Blueprint.ParentRevision);
    }

    [Fact]
    public async Task AppendRevision_SkipOrWrongParent_IsRejectedWithoutChangingCurrent()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        var t1 = DateTimeOffset.UtcNow;
        await store.CreateAsync(
            "工作模式",
            new DesktopWorkspaceBlueprintRevisionRecord(MakeBlueprint(id, 1, null, "v1"), t1));

        var skipped = new DesktopWorkspaceBlueprintRevisionRecord(
            MakeBlueprint(id, 3, 1, "非法跳到 v3"), t1.AddMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendRevisionAsync(skipped));

        var wrongParent = new DesktopWorkspaceBlueprintRevisionRecord(
            MakeBlueprint(id, 2, null, "非法 parent"), t1.AddMinutes(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendRevisionAsync(wrongParent));

        var asset = await store.GetAssetAsync(id);
        Assert.Equal(1, asset!.CurrentRevision);
        Assert.Null(await store.GetRevisionAsync(id, 2));
        Assert.Null(await store.GetRevisionAsync(id, 3));
    }

    [Fact]
    public async Task Create_MissingSourceUnderstanding_RollsBackWholeAsset()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        var record = new DesktopWorkspaceBlueprintRevisionRecord(
            MakeBlueprint(id, 1, null, "带不存在来源的 v1"),
            DateTimeOffset.UtcNow,
            sourceUnderstandingId: Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync("坏资产", record));

        Assert.Null(await store.GetAssetAsync(id));
        Assert.Null(await store.GetRevisionAsync(id, 1));
    }

    [Fact]
    public async Task SourceUnderstanding_Delete_SetsRevisionReferenceToNullWithoutDeletingBlueprint()
    {
        var understandingStore = new SqliteDesktopSemanticUnderstandingStore(_dbPath);
        var blueprintStore = CreateStore();
        await understandingStore.InitializeAsync();

        var understandingId = Guid.NewGuid();
        var understanding = new DesktopSemanticUnderstanding(
            "当前桌面包含开发和学习项目。",
            [new DesktopSemanticCategory("dev", "开发", "开发工具")],
            [new DesktopSemanticAssignment("local:item", "dev", 0.95, "测试")]);
        await understandingStore.AddAsync(new DesktopSemanticUnderstandingSnapshot(
            understandingId,
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            "state-fingerprint",
            1,
            understanding));

        var blueprintId = Guid.NewGuid();
        await blueprintStore.CreateAsync(
            "工作模式",
            new DesktopWorkspaceBlueprintRevisionRecord(
                MakeBlueprint(blueprintId, 1, null, "引用 Understanding"),
                DateTimeOffset.UtcNow,
                understandingId,
                "按当前理解生成"));

        using (var connection = OpenRawConnection())
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM desktop_semantic_understandings WHERE understanding_id = @id;";
            delete.Parameters.AddWithValue("@id", understandingId.ToString("D"));
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        var loaded = await blueprintStore.GetRevisionAsync(blueprintId, 1);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.SourceUnderstandingId);
        Assert.Equal("按当前理解生成", loaded.SourceUserIntent);
        Assert.NotNull(await blueprintStore.GetAssetAsync(blueprintId));
    }

    [Fact]
    public async Task Corrupted_RevisionMetadataDisagreesWithPayload_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        await store.CreateAsync(
            "工作模式",
            new DesktopWorkspaceBlueprintRevisionRecord(
                MakeBlueprint(id, 1, null, "原始摘要"), DateTimeOffset.UtcNow));

        using (var connection = OpenRawConnection())
        using (var update = connection.CreateCommand())
        {
            update.CommandText = @"
                UPDATE workspace_blueprint_revisions
                SET design_summary = '篡改摘要'
                WHERE blueprint_id = @id AND revision = 1;
            ";
            update.Parameters.AddWithValue("@id", id.ToString("D"));
            update.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetRevisionAsync(id, 1));
    }

    [Fact]
    public async Task Corrupted_CurrentRevisionPointsToMissingRevision_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        await store.CreateAsync(
            "工作模式",
            new DesktopWorkspaceBlueprintRevisionRecord(
                MakeBlueprint(id, 1, null, "v1"), DateTimeOffset.UtcNow));

        using (var connection = OpenRawConnection())
        using (var update = connection.CreateCommand())
        {
            update.CommandText = @"
                UPDATE workspace_blueprints
                SET current_revision = 2
                WHERE blueprint_id = @id;
            ";
            update.Parameters.AddWithValue("@id", id.ToString("D"));
            update.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAssetAsync(id));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetCurrentRevisionAsync(id));
    }

    [Fact]
    public async Task Corrupted_UnknownPayloadVersion_FailClosed()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        await store.CreateAsync(
            "工作模式",
            new DesktopWorkspaceBlueprintRevisionRecord(
                MakeBlueprint(id, 1, null, "v1"), DateTimeOffset.UtcNow));

        using (var connection = OpenRawConnection())
        using (var update = connection.CreateCommand())
        {
            update.CommandText = @"
                UPDATE workspace_blueprint_revisions
                SET payload_format_version = 99
                WHERE blueprint_id = @id AND revision = 1;
            ";
            update.Parameters.AddWithValue("@id", id.ToString("D"));
            update.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetRevisionAsync(id, 1));
    }

    [Fact]
    public async Task List_FiltersArchivedAssetsAndUsesLibraryOrdering()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddHours(1);

        await store.CreateAsync(
            "旧工作区",
            new DesktopWorkspaceBlueprintRevisionRecord(MakeBlueprint(oldId, 1, null, "old"), t1));
        await store.CreateAsync(
            "新工作区",
            new DesktopWorkspaceBlueprintRevisionRecord(MakeBlueprint(newId, 1, null, "new"), t2));

        var listed = await store.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.Equal(newId, listed[0].BlueprintId);
        Assert.Equal(oldId, listed[1].BlueprintId);

        using (var connection = OpenRawConnection())
        using (var archive = connection.CreateCommand())
        {
            archive.CommandText = "UPDATE workspace_blueprints SET is_archived = 1 WHERE blueprint_id = @id;";
            archive.Parameters.AddWithValue("@id", newId.ToString("D"));
            archive.ExecuteNonQuery();
        }

        var activeOnly = await store.ListAsync();
        Assert.Single(activeOnly);
        Assert.Equal(oldId, activeOnly[0].BlueprintId);

        var withArchived = await store.ListAsync(includeArchived: true);
        Assert.Equal(2, withArchived.Count);
        Assert.False(withArchived[0].IsArchived);
        Assert.True(withArchived[1].IsArchived);
    }

    [Fact]
    public async Task ArchiveAsync_HidesAssetFromLibraryButKeepsImmutableRevisions()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);
        await store.CreateAsync(
            "要删除的设计",
            new DesktopWorkspaceBlueprintRevisionRecord(MakeBlueprint(id, 1, null, "v1"), createdAt));

        Assert.True(await store.ArchiveAsync(id));

        Assert.Empty(await store.ListAsync());

        var withArchived = await store.ListAsync(includeArchived: true);
        var asset = Assert.Single(withArchived);
        Assert.True(asset.IsArchived);

        // 归档只翻转资产的 is_archived，不销毁历史版本，也不让 updated_at 倒退到 created_at 之前。
        var revision = await store.GetRevisionAsync(id, 1);
        Assert.NotNull(revision);
        Assert.True(asset.UpdatedAtUtc >= asset.CreatedAtUtc);

        // 重复归档是幂等的，返回 false 且不产生第二份记录。
        Assert.False(await store.ArchiveAsync(id));
        Assert.Single(await store.ListAsync(includeArchived: true));

        // 未归档的资产不受影响。
        var otherId = Guid.NewGuid();
        await store.CreateAsync(
            "保留的设计",
            new DesktopWorkspaceBlueprintRevisionRecord(MakeBlueprint(otherId, 1, null, "keep"), createdAt));
        var active = Assert.Single(await store.ListAsync());
        Assert.Equal(otherId, active.BlueprintId);
    }

    [Fact]
    public async Task NewV9Stores_UseSharedInitializer_Idempotently()
    {
        var understandingDb = Path.Combine(_tempDir, "understanding-shared.db");
        var blueprintDb = Path.Combine(_tempDir, "blueprint-shared.db");

        var understandingStore = new SqliteDesktopSemanticUnderstandingStore(understandingDb);
        await understandingStore.InitializeAsync();
        await understandingStore.InitializeAsync();

        var blueprintStore = new SqliteDesktopWorkspaceBlueprintStore(blueprintDb);
        await blueprintStore.InitializeAsync();
        await blueprintStore.InitializeAsync();

        Assert.Equal(13, ReadUserVersion(understandingDb));
        Assert.Equal(13, ReadUserVersion(blueprintDb));
        Assert.True(TableExists(understandingDb, "workspace_blueprints"));
        Assert.True(TableExists(blueprintDb, "desktop_semantic_understandings"));
    }

    private SqliteDesktopWorkspaceBlueprintStore CreateStore() => new(_dbPath);

    private static DesktopWorkspaceBlueprint MakeBlueprint(
        Guid id,
        int revision,
        int? parentRevision,
        string summary)
    {
        return new DesktopWorkspaceBlueprint(
            id,
            revision,
            summary,
            displayPolicies:
            [
                new("primary", DesktopWorkspaceDisplayPolicyKind.Design),
                new("secondary_left", DesktopWorkspaceDisplayPolicyKind.Preserve),
                new("secondary_bottom", DesktopWorkspaceDisplayPolicyKind.Design),
            ],
            zones:
            [
                new(
                    "tools",
                    "Tools",
                    "secondary_bottom",
                    new DesktopWorkspaceRelativeRect(0d, 0d, 0.5d, 0.6d),
                    DesktopWorkspaceAnchorKind.TopLeft,
                    DesktopWorkspacePackingOrder.ColumnMajor),
                new(
                    "study",
                    "Study & Work",
                    "secondary_bottom",
                    new DesktopWorkspaceRelativeRect(0.5d, 0d, 0.5d, 0.6d),
                    DesktopWorkspaceAnchorKind.TopRight,
                    DesktopWorkspacePackingOrder.RowMajor),
                new(
                    "entertainment",
                    "Entertainment",
                    "secondary_bottom",
                    new DesktopWorkspaceRelativeRect(0d, 0.6d, 1d, 0.4d),
                    DesktopWorkspaceAnchorKind.BottomLeft,
                    DesktopWorkspacePackingOrder.RowMajor),
            ],
            zoneRelations:
            [
                new("tools", DesktopWorkspaceZoneRelationKind.LeftOf, "study"),
            ],
            categoryRules:
            [
                new("development", "tools"),
                new("study-work", "study"),
                new("entertainment", "entertainment"),
            ],
            itemOverrides:
            [
                new(
                    "item_1",
                    DesktopWorkspaceItemOverrideKind.PlaceInZone,
                    "entertainment",
                    DesktopWorkspaceAnchorKind.BottomRight),
                new("item_2", DesktopWorkspaceItemOverrideKind.Keep),
            ],
            reservedRegions:
            [
                new(
                    "wallpaper",
                    "keep the wallpaper visible",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0.2d, 0.2d, 0.6d, 0.6d)),
            ],
            parentRevision: parentRevision,
            schemaVersion: DesktopWorkspaceBlueprint.CurrentSchemaVersion);
    }

    private static void AssertBlueprintEqual(
        DesktopWorkspaceBlueprint expected,
        DesktopWorkspaceBlueprint actual)
    {
        Assert.Equal(expected.BlueprintId, actual.BlueprintId);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.ParentRevision, actual.ParentRevision);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.DesignSummary, actual.DesignSummary);
        Assert.Equal(expected.DisplayPolicies, actual.DisplayPolicies);
        Assert.Equal(expected.Zones, actual.Zones);
        Assert.Equal(expected.ZoneRelations, actual.ZoneRelations);
        Assert.Equal(expected.CategoryRules, actual.CategoryRules);
        Assert.Equal(expected.ItemOverrides, actual.ItemOverrides);
        Assert.Equal(expected.ReservedRegions, actual.ReservedRegions);
    }

    private SqliteConnection OpenRawConnection() => OpenRawConnection(_dbPath);

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

    private static bool TableExists(string path, string name)
    {
        using var connection = OpenRawConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
        command.Parameters.AddWithValue("@name", name);
        return (long)command.ExecuteScalar()! > 0;
    }
}
