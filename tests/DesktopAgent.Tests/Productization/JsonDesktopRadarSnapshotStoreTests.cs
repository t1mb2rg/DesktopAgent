using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Productization;
using DesktopAgent.Persistence.Productization;

namespace DesktopAgent.Tests.Productization;

public sealed class JsonDesktopRadarSnapshotStoreTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), $"DesktopAgent.Tests.Radar.{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsPresentationOnlySnapshot()
    {
        var path = Path.Combine(_tempDirectory, "cache", "desktop-radar.json");
        var store = new JsonDesktopRadarSnapshotStore(path);
        var observationId = Guid.NewGuid();
        var layoutId = Guid.NewGuid();
        var snapshot = new DesktopRadarSnapshot(
            observationId,
            new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero),
            "TOPOLOGY-FINGERPRINT",
            layoutId,
            3,
            new[]
            {
                new DesktopRadarDisplaySnapshot(
                    "DISPLAY-A",
                    new PixelRect(0, 0, 1920, 1080),
                    true,
                    7,
                    8,
                    5,
                    new[]
                    {
                        new DesktopRadarDensityCell(1, 1, DesktopRadarDensityLevel.Low),
                        new DesktopRadarDensityCell(4, 3, DesktopRadarDensityLevel.High),
                    }),
            });

        await store.SaveAsync(snapshot);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(observationId, loaded.ObservationId);
        Assert.Equal(layoutId, loaded.LayoutTemplateId);
        Assert.Equal(3, loaded.LayoutVersion);
        Assert.Equal(7, loaded.TotalItemCount);
        Assert.Equal("DISPLAY-A", loaded.Displays[0].DisplayKey);
        Assert.Equal(DesktopRadarDensityLevel.High, loaded.Displays[0].DensityCells[1].Density);

        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("StableId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ParsingName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PixelPosition", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Writer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_ReturnsNullWhenPresentationCacheDoesNotExist()
    {
        var store = new JsonDesktopRadarSnapshotStore(
            Path.Combine(_tempDirectory, "missing", "desktop-radar.json"));

        Assert.Null(await store.LoadAsync());
    }
}
