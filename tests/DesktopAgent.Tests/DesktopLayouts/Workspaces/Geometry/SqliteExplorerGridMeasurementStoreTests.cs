using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class SqliteExplorerGridMeasurementStoreTests : IDisposable
{
    private const string CurrentFingerprint = "topology-a";
    private const string OtherFingerprint = "topology-b";

    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteExplorerGridMeasurementStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"DesktopAgent.Tests.ExplorerGridMeasurementStore.{Guid.NewGuid():N}");
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
    public async Task Initialize_UsesSharedSchemaInitializer_AndIsIdempotent()
    {
        var store = new SqliteExplorerGridMeasurementStore(_dbPath);
        await store.InitializeAsync();
        await store.InitializeAsync();

        Assert.Equal(13, ReadUserVersion());
        Assert.True(TableExists("explorer_grid_measurements"));
        Assert.True(IndexExists("idx_grid_measurements_device_observed_desc"));
    }

    [Fact]
    public async Task AppendAndRead_RoundTripsEveryEvidenceField()
    {
        var store = new SqliteExplorerGridMeasurementStore(_dbPath);
        await store.InitializeAsync();

        var observedAt = new DateTimeOffset(2026, 8, 27, 10, 30, 0, TimeSpan.Zero);
        var measurement = new ExplorerGridMeasurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY1",
            new PixelPoint(93, 122),
            new PixelPoint(16, 2),
            sampleCount: 4,
            consensusCount: 4,
            spacingConstant: true,
            phaseConstant: true,
            CurrentFingerprint,
            observedAt);

        await store.AppendObservationAsync(measurement);

        var read = await store.GetActiveAsync("DISPLAY1", CurrentFingerprint);
        Assert.NotNull(read);
        Assert.Equal(measurement, read);
    }

    [Fact]
    public async Task StaleFact_IsNotActive_ButRemainsReadableSoItCanBeExplicitlyDemoted()
    {
        var store = new SqliteExplorerGridMeasurementStore(_dbPath);
        await store.InitializeAsync();

        var measured = new ExplorerGridMeasurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY1",
            new PixelPoint(93, 122),
            new PixelPoint(16, 2),
            sampleCount: 3,
            consensusCount: 3,
            spacingConstant: true,
            phaseConstant: true,
            OtherFingerprint,
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        await store.AppendObservationAsync(measured);

        var active = await store.GetActiveAsync("DISPLAY1", CurrentFingerprint);
        Assert.Null(active);

        // 调用方仍必须能看到「曾经实测过、但已失效」，才能显式降级而不是假装没有历史。
        var stale = await store.GetLatestByDeviceAsync("DISPLAY1");
        Assert.NotNull(stale);
        Assert.Equal(measured, stale);
        Assert.False(stale.CanAuthorizeExecution(CurrentFingerprint));
    }

    [Fact]
    public async Task AppendObservation_IsAppendOnly_AndActiveFactIsTheNewestOne()
    {
        var store = new SqliteExplorerGridMeasurementStore(_dbPath);
        await store.InitializeAsync();

        var older = new ExplorerGridMeasurement(
            ExplorerGridEvidenceSource.Inferred,
            "DISPLAY1",
            new PixelPoint(93, 122),
            new PixelPoint(16, 2),
            sampleCount: 5,
            consensusCount: 5,
            spacingConstant: true,
            phaseConstant: true,
            CurrentFingerprint,
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        var newer = new ExplorerGridMeasurement(
            ExplorerGridEvidenceSource.Measured,
            "DISPLAY1",
            new PixelPoint(93, 122),
            new PixelPoint(16, 2),
            sampleCount: 3,
            consensusCount: 3,
            spacingConstant: true,
            phaseConstant: true,
            CurrentFingerprint,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        await store.AppendObservationAsync(older);
        await store.AppendObservationAsync(newer);

        var active = await store.GetActiveAsync("DISPLAY1", CurrentFingerprint);
        Assert.NotNull(active);
        Assert.Equal(newer, active);
        Assert.True(active.CanAuthorizeExecution(CurrentFingerprint));

        // 新事实不覆盖历史证据：旧的推断仍然可查。
        Assert.Equal(2, CountRows());
    }

    [Fact]
    public async Task ListActiveAsync_ReturnsLatestFactPerDevice_OnlyForCurrentTopology()
    {
        var store = new SqliteExplorerGridMeasurementStore(_dbPath);
        await store.InitializeAsync();

        await store.AppendObservationAsync(Measurement(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)));
        await store.AppendObservationAsync(Measurement(
            "DISPLAY1", CurrentFingerprint, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero)));
        await store.AppendObservationAsync(Measurement(
            "DISPLAY2", CurrentFingerprint, new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero)));
        await store.AppendObservationAsync(Measurement(
            "DISPLAY3", OtherFingerprint, new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero)));

        var active = await store.ListActiveAsync(CurrentFingerprint);

        Assert.Equal(2, active.Count);
        Assert.Equal("DISPLAY1", active[0].DeviceName);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero), active[0].ObservedAtUtc);
        Assert.Equal("DISPLAY2", active[1].DeviceName);
    }

    [Fact]
    public async Task GetActiveAsync_RejectsBlankFingerprint()
    {
        var store = new SqliteExplorerGridMeasurementStore(_dbPath);
        await store.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetActiveAsync("DISPLAY1", "   "));
    }

    private static ExplorerGridMeasurement Measurement(
        string deviceName,
        string fingerprint,
        DateTimeOffset observedAtUtc) =>
        new(
            ExplorerGridEvidenceSource.Measured,
            deviceName,
            new PixelPoint(93, 122),
            new PixelPoint(16, 2),
            sampleCount: 3,
            consensusCount: 3,
            spacingConstant: true,
            phaseConstant: true,
            fingerprint,
            observedAtUtc);

    private int CountRows()
    {
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM explorer_grid_measurements;";
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
