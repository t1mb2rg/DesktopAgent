using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class SqliteExplorerGridDisplayAuthorizationStoreTests : IDisposable
{
    private const string CurrentFingerprint = "topology-a";
    private const string OtherFingerprint = "topology-b";

    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteExplorerGridDisplayAuthorizationStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"DesktopAgent.Tests.ExplorerGridDisplayAuthorizationStore.{Guid.NewGuid():N}");
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
    public async Task InitializeAsync_UpgradesSchemaToV12_AndCreatesAuthorizationTable()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        Assert.Equal(13, ReadUserVersion());
        Assert.True(TableExists("explorer_grid_display_authorizations"));
        Assert.True(IndexExists("idx_grid_authorizations_device_recorded_desc"));
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndKeepsExistingData()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        var authorization = Grant(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 27, 10, 30, 0, TimeSpan.Zero));
        await store.AppendAsync(authorization);

        // 连续初始化既不能抛异常，也不能重复建表或丢掉已有记录。
        await store.InitializeAsync();
        await store.InitializeAsync();

        Assert.Equal(13, ReadUserVersion());
        Assert.Equal(1, CountRows());

        var read = await store.GetActiveAsync("DISPLAY1", CurrentFingerprint);
        Assert.Equal(authorization, read);
    }

    [Fact]
    public async Task AppendAndRead_RoundTripsEveryAuthorizationField()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        var authorizedAt = new DateTimeOffset(2026, 8, 27, 10, 30, 0, TimeSpan.Zero);
        var authorization = Grant("DISPLAY1", CurrentFingerprint, authorizedAt);
        await store.AppendAsync(authorization);

        var read = await store.GetActiveAsync("DISPLAY1", CurrentFingerprint);
        Assert.NotNull(read);
        Assert.Equal(ExplorerGridAuthorizationState.Granted, read.State);
        Assert.Equal("DISPLAY1", read.DeviceName);
        Assert.Equal(CurrentFingerprint, read.TopologyFingerprint);
        Assert.Equal(authorizedAt, read.AuthorizedAtUtc);
        Assert.Equal(authorization, read);
        Assert.True(read.AuthorizesUnattendedExecution(CurrentFingerprint));
    }

    [Fact]
    public async Task GetActiveAsync_ReturnsNull_WhenOnlyAnotherTopologyIsAuthorized()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        await store.AppendAsync(Grant(
            "DISPLAY1", OtherFingerprint, new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)));

        // 拓扑换了，旧授权与新环境无关：读不到，而不是退回一个更宽松的默认值。
        Assert.Null(await store.GetActiveAsync("DISPLAY1", CurrentFingerprint));
    }

    [Fact]
    public async Task ActiveAuthorization_IsNewestRecord_RevocationWinsOverEarlierGrant()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        await store.AppendAsync(Grant(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)));
        await store.AppendAsync(Revoke(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero)));

        var active = await store.GetActiveAsync("DISPLAY1", CurrentFingerprint);

        Assert.NotNull(active);
        Assert.Equal(ExplorerGridAuthorizationState.Revoked, active.State);
        Assert.False(active.AuthorizesUnattendedExecution(CurrentFingerprint));

        // 撤销是追加，不是删除历史：两条记录都还在。
        Assert.Equal(2, CountRows());
    }

    [Fact]
    public async Task ActiveAuthorization_IsNewestRecord_GrantWinsOverEarlierRevocation()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        await store.AppendAsync(Revoke(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)));
        await store.AppendAsync(Grant(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero)));

        var active = await store.GetActiveAsync("DISPLAY1", CurrentFingerprint);

        Assert.NotNull(active);
        Assert.Equal(ExplorerGridAuthorizationState.Granted, active.State);
        Assert.True(active.AuthorizesUnattendedExecution(CurrentFingerprint));
    }

    [Fact]
    public async Task GetLatestByDeviceAsync_IgnoresTopology_SoStaleAuthorizationCanBeExplicitlyDemoted()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        var stale = Grant(
            "DISPLAY1", OtherFingerprint, new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        await store.AppendAsync(stale);

        Assert.Null(await store.GetActiveAsync("DISPLAY1", CurrentFingerprint));

        // 调用方仍必须能看到「曾经授权过、但已随拓扑变化失效」，才能显式降级并解释原因。
        var latest = await store.GetLatestByDeviceAsync("DISPLAY1");
        Assert.NotNull(latest);
        Assert.Equal(stale, latest);
        Assert.False(latest.AuthorizesUnattendedExecution(CurrentFingerprint));
    }

    [Fact]
    public async Task ListActiveAsync_ReturnsLatestRecordPerDevice_OnlyForCurrentTopology()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        await store.AppendAsync(Grant(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)));
        await store.AppendAsync(Revoke(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero)));
        await store.AppendAsync(Grant(
            "DISPLAY2", CurrentFingerprint, new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero)));
        await store.AppendAsync(Grant(
            "DISPLAY3", OtherFingerprint, new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero)));

        var active = await store.ListActiveAsync(CurrentFingerprint);

        Assert.Equal(2, active.Count);

        Assert.Equal("DISPLAY1", active[0].DeviceName);
        Assert.Equal(ExplorerGridAuthorizationState.Revoked, active[0].State);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero), active[0].AuthorizedAtUtc);

        Assert.Equal("DISPLAY2", active[1].DeviceName);
        Assert.Equal(ExplorerGridAuthorizationState.Granted, active[1].State);
    }

    [Fact]
    public async Task EmptyDatabase_ReturnsNullAndEmptyList()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        Assert.Null(await store.GetActiveAsync("DISPLAY1", CurrentFingerprint));
        Assert.Null(await store.GetLatestByDeviceAsync("DISPLAY1"));

        var active = await store.ListActiveAsync(CurrentFingerprint);
        Assert.NotNull(active);
        Assert.Empty(active);
    }

    [Fact]
    public async Task QueryMethods_RejectBlankArguments()
    {
        var store = new SqliteExplorerGridDisplayAuthorizationStore(_dbPath);
        await store.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetActiveAsync("   ", CurrentFingerprint));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetActiveAsync("DISPLAY1", "   "));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetLatestByDeviceAsync("   "));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ListActiveAsync("   "));
    }

    [Fact]
    public void AuthorizationContract_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => new ExplorerGridDisplayAuthorization(
            ExplorerGridAuthorizationState.Granted,
            " ",
            CurrentFingerprint,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero)));

        Assert.Throws<ArgumentException>(() => new ExplorerGridDisplayAuthorization(
            ExplorerGridAuthorizationState.Granted,
            "DISPLAY1",
            CurrentFingerprint,
            new DateTimeOffset(2026, 8, 27, 8, 0, 0, TimeSpan.FromHours(8))));

        var granted = Grant(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        // 指纹为空的调用一律不构成授权。
        Assert.False(granted.AuthorizesUnattendedExecution("   "));
        Assert.False(granted.AuthorizesUnattendedExecution(OtherFingerprint));
    }

    private static ExplorerGridDisplayAuthorization Grant(
        string deviceName,
        string fingerprint,
        DateTimeOffset authorizedAtUtc) =>
        new(ExplorerGridAuthorizationState.Granted, deviceName, fingerprint, authorizedAtUtc);

    private static ExplorerGridDisplayAuthorization Revoke(
        string deviceName,
        string fingerprint,
        DateTimeOffset authorizedAtUtc) =>
        new(ExplorerGridAuthorizationState.Revoked, deviceName, fingerprint, authorizedAtUtc);

    private int CountRows()
    {
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM explorer_grid_display_authorizations;";
        return (int)(long)command.ExecuteScalar()!;
    }

    private bool TableExists(string name)
    {
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
        command.Parameters.AddWithValue("@name", name);
        return (long)command.ExecuteScalar()! > 0;
    }

    private bool IndexExists(string name)
    {
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@name;";
        command.Parameters.AddWithValue("@name", name);
        return (long)command.ExecuteScalar()! > 0;
    }

    private int ReadUserVersion()
    {
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (int)(long)command.ExecuteScalar()!;
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
