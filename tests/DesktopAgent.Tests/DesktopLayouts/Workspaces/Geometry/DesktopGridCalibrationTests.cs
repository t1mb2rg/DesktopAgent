using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class DesktopGridCalibrationTests
{
    private static readonly PixelPoint Spacing = new(80, 80);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AllDisplaysMeasured_CurrentEnvironment_IsReady()
    {
        var observation = Observation();
        Assert.True(DesktopGridCalibration.Evaluate(observation, Facts(observation)).IsReady);
    }

    [Fact]
    public void OneDisplayMissing_DoesNotTreatPartialCalibrationAsReady()
    {
        var observation = Observation();
        var result = DesktopGridCalibration.Evaluate(observation, Facts(observation).Take(1).ToArray());
        Assert.False(result.IsReady);
        Assert.Contains("DISPLAY2", result.Message);
    }

    [Fact]
    public void InferredFacts_DoNotSatisfyCalibration()
    {
        var observation = Observation();
        var facts = Facts(observation, source: ExplorerGridEvidenceSource.Inferred);
        Assert.False(DesktopGridCalibration.Evaluate(observation, facts).IsReady);
    }

    [Theory]
    [InlineData(120, 0, 80)]
    [InlineData(96, 100, 80)]
    [InlineData(96, 0, 90)]
    public void ChangedDpiArrangementOrSpacing_RejectsOldMeasurements(uint dpi, int offset, int spacing)
    {
        var old = Observation();
        var changed = Observation(dpi, offset, new(spacing, spacing));
        Assert.False(DesktopGridCalibration.Evaluate(changed, Facts(old)).IsReady);
    }

    [Fact]
    public void NoDisplays_CannotPassEvenWithHistoricalFacts()
    {
        Assert.False(DesktopGridCalibration.Evaluate(Observation(noDisplays: true), Facts(Observation())).IsReady);
    }

    [Fact]
    public async Task RecalibrationWatermark_IsMonotonic_AndRequiresNewFacts()
    {
        var observation = Observation();
        var store = new MemoryStore { Facts = Facts(observation) };
        var calibration = new DesktopGridCalibration(new DesktopExplorerGridFactReader(store));
        Assert.True((await calibration.CheckAsync(observation)).IsReady);
        calibration.RequireMeasurementsAfter(Now);
        calibration.RequireMeasurementsAfter(Now.AddDays(-1));
        Assert.False((await calibration.CheckAsync(observation)).IsReady);
        store.Facts = Facts(observation, at: Now.AddMinutes(1));
        Assert.True((await calibration.CheckAsync(observation)).IsReady);
    }

    [Fact]
    public async Task FactStoreFailure_IsVisibleAndFailClosed()
    {
        var calibration = new DesktopGridCalibration(new DesktopExplorerGridFactReader(new MemoryStore { Throw = true }));
        var result = await calibration.CheckAsync(Observation());
        Assert.False(result.IsReady);
        Assert.Contains("store unavailable", result.Message);
    }

    [Fact]
    public async Task PersistedEnvironment_RestartReusesMeasuredFacts_ChangeRequiresNewCalibration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TesselCalibrationRestart", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "environment.json");
        try
        {
            var observation = Observation();
            var facts = new MemoryStore { Facts = Facts(observation) };
            DesktopGridCalibration Start()
            {
                var environment = new JsonGridCalibrationEnvironmentStore(path);
                DesktopGridCalibration? calibration = null;
                calibration = new(new DesktopExplorerGridFactReader(facts), async (current, ct) =>
                {
                    var watermark = await environment.ObserveAsync(ExplorerGridTopologyIdentity.TryComputeFor(current), ct);
                    if (watermark is { } value) calibration!.RequireMeasurementsAfter(value);
                });
                return calibration;
            }
            Assert.True((await Start().CheckAsync(observation)).IsReady);
            Assert.True((await Start().CheckAsync(observation)).IsReady);
            var changed = Observation(dpi: 120);
            Assert.False((await Start().CheckAsync(changed)).IsReady);
            facts.Facts = Facts(changed, at: DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.True((await Start().CheckAsync(changed)).IsReady);
            Assert.True((await Start().CheckAsync(changed)).IsReady);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ProtectedSource_NeverExposesUncalibratedObservation()
    {
        var observation = Observation();
        var calibration = new DesktopGridCalibration(new DesktopExplorerGridFactReader(new MemoryStore()));
        var source = new CalibratedDesktopObservationSource(new Source(observation), calibration);
        var result = await source.CaptureAsync();
        Assert.False(result.IsCaptured);
        Assert.Null(result.Observation);
        Assert.Equal(DesktopObservationCaptureIssueCode.CalibrationRequired, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task ProtectedSource_MissingVerifierCannotBypassCalibration()
    {
        Assert.False((await new CalibratedDesktopObservationSource(new Source(Observation()), null).CaptureAsync()).IsCaptured);
    }

    [Fact]
    public async Task ProtectedSource_RechecksFactsOnEveryCapture()
    {
        var observation = Observation();
        var store = new MemoryStore { Facts = Facts(observation) };
        var source = new CalibratedDesktopObservationSource(new Source(observation),
            new DesktopGridCalibration(new DesktopExplorerGridFactReader(store)));
        Assert.True((await source.CaptureAsync()).IsCaptured);
        store.Facts = [];
        Assert.False((await source.CaptureAsync()).IsCaptured);
    }

    [Fact]
    public async Task Cancellation_IsNotTurnedIntoACalibrationFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calibration = new DesktopGridCalibration(new DesktopExplorerGridFactReader(new MemoryStore()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calibration.CheckAsync(Observation(), cts.Token));
    }

    private static DesktopObservation Observation(uint dpi = 96, int offset = 0, PixelPoint? spacing = null, bool noDisplays = false)
        => new(Guid.NewGuid(), Now, noDisplays ? [] : [
            new DisplayDescriptor("DISPLAY1", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), dpi, dpi, true),
            new DisplayDescriptor("DISPLAY2", new(-1920, offset, 0, 1080 + offset),
                new(-1920, offset, 0, 1040 + offset), 96, 96, false)],
            new DesktopIconPositioningCapabilities(false, spacing ?? Spacing, ConnectionMethod.FindWindowSW, null),
            [], ConnectionMethod.FindWindowSW, TimeSpan.Zero);

    private static IReadOnlyList<ExplorerGridMeasurement> Facts(DesktopObservation observation,
        ExplorerGridEvidenceSource source = ExplorerGridEvidenceSource.Measured, DateTimeOffset? at = null)
        => observation.Displays.Select(display => new ExplorerGridMeasurement(source, display.DeviceName,
            Spacing, new(20, 30), 4, 4, true, true, ExplorerGridTopologyIdentity.TryComputeFor(observation)!, at ?? Now)).ToArray();

    private sealed class Source(DesktopObservation observation) : IDesktopObservationSource
    {
        public Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
            => Task.FromResult(DesktopObservationCaptureResult.Captured(observation));
    }

    private sealed class MemoryStore : IExplorerGridMeasurementStore
    {
        public IReadOnlyList<ExplorerGridMeasurement> Facts { get; set; } = [];
        public bool Throw { get; init; }
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw) throw new IOException("store unavailable");
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ExplorerGridMeasurement>> ListActiveAsync(string fingerprint, CancellationToken cancellationToken = default)
            => Task.FromResult(Facts);
        public Task AppendObservationAsync(ExplorerGridMeasurement observation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ExplorerGridMeasurement?> GetActiveAsync(string deviceName, string fingerprint, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ExplorerGridMeasurement?> GetLatestByDeviceAsync(string deviceName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
