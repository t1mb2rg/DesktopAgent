using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Tests.Persistence;

public sealed class JsonGridCalibrationEnvironmentStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TesselCalibrationTests", Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_directory, "environment.json");

    [Fact]
    public async Task InitialValidEnvironment_CanReuseMatchingMeasurementsAcrossRestart()
    {
        Assert.Null(await new JsonGridCalibrationEnvironmentStore(StatePath).ObserveAsync("topology-a"));
        Assert.Null(await new JsonGridCalibrationEnvironmentStore(StatePath).ObserveAsync("topology-a"));
    }

    [Fact]
    public async Task EnvironmentChange_RestartAndReturningToOldEnvironment_DoNotReviveOldCalibration()
    {
        var store = new JsonGridCalibrationEnvironmentStore(StatePath);
        await store.ObserveAsync("topology-a");
        var invalidated = await store.ObserveAsync("topology-b");
        Assert.NotNull(invalidated);
        Assert.Equal(invalidated, await new JsonGridCalibrationEnvironmentStore(StatePath).ObserveAsync("topology-b"));
        Assert.True(await store.ObserveAsync("topology-a") >= invalidated);
    }

    [Fact]
    public async Task ExplicitRecalibration_InvalidatesOldFactsBeforeProbeAndPreservesEnvironment()
    {
        var store = new JsonGridCalibrationEnvironmentStore(StatePath);
        await store.ObserveAsync("topology-a");
        var invalidated = await store.InvalidateAsync();
        Assert.NotNull(invalidated);
        Assert.Equal(invalidated, await store.ObserveAsync("topology-a"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task CorruptState_RequiresFreshCalibrationInsteadOfTreatingItAsFirstRun(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(StatePath, content);
        Assert.NotNull(await new JsonGridCalibrationEnvironmentStore(StatePath).ObserveAsync("topology-a"));
    }

    [Fact]
    public async Task TemporarilyUnreadableEnvironment_DoesNotDestroyReusableCalibration()
    {
        var store = new JsonGridCalibrationEnvironmentStore(StatePath);
        await store.ObserveAsync("topology-a");
        Assert.Null(await store.ObserveAsync(null));
        Assert.Null(await new JsonGridCalibrationEnvironmentStore(StatePath).ObserveAsync("topology-a"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
