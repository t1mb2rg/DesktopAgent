using System.Threading.Channels;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopObservationMonitoringRuntimeTests
{
    [Fact]
    public async Task Start_CleanSource_StartsPumpAndEstablishesStartupBaseline()
    {
        var source = new FakeHintSource(
            new DesktopObservationHintSourceStartResult(
                "fake",
                true,
                1,
                new[] { "endpoint" },
                Array.Empty<string>()));
        var runtime = CreateRuntime(new[] { source });
        var runs = CollectRuns(runtime);

        var status = runtime.Start();
        var run = await ReadRunAsync(runs.Reader);

        Assert.Equal(DesktopObservationMonitoringState.Running, status.State);
        Assert.True(status.IsRunning);
        Assert.Equal(1, source.StartCount);
        Assert.Equal(DesktopObservationTriggeredRunStatus.Completed, run.Status);
        Assert.Equal(
            DesktopObservationDiagnosticsStatus.BaselineEstablished,
            run.DiagnosticsResult!.Status);
        Assert.Contains(
            DesktopObservationMonitoringRuntime.StartupHintSource,
            run.TriggerBatch.Sources);

        await runtime.StopAsync();
        Assert.Equal(1, source.StopCount);
        Assert.Equal(DesktopObservationMonitoringState.Stopped, runtime.Status.State);
    }

    [Fact]
    public async Task Start_SourceWithIssues_IsDegradedButStillObserves()
    {
        var source = new FakeHintSource(
            new DesktopObservationHintSourceStartResult(
                "fake",
                true,
                1,
                new[] { "endpoint-a" },
                new[] { "endpoint-b failed" }));
        var runtime = CreateRuntime(new[] { source });
        var runs = CollectRuns(runtime);

        var status = runtime.Start();
        var run = await ReadRunAsync(runs.Reader);

        Assert.Equal(DesktopObservationMonitoringState.Degraded, status.State);
        Assert.True(status.IsRunning);
        Assert.Single(status.Sources);
        Assert.Single(status.Sources[0].Issues);
        Assert.Equal(DesktopObservationTriggeredRunStatus.Completed, run.Status);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task Start_NoActiveSource_IsDegradedButManualPipelineStillWorks()
    {
        var source = new FakeHintSource(
            new DesktopObservationHintSourceStartResult(
                "fake",
                false,
                0,
                Array.Empty<string>(),
                new[] { "failed" }));
        var runtime = CreateRuntime(new[] { source });
        var runs = CollectRuns(runtime);

        var status = runtime.Start();
        var startup = await ReadRunAsync(runs.Reader);

        Assert.Equal(DesktopObservationMonitoringState.Degraded, status.State);
        Assert.Contains(status.Issues, issue => issue.Contains("没有真实 hint source", StringComparison.Ordinal));
        Assert.Equal(
            DesktopObservationDiagnosticsStatus.BaselineEstablished,
            startup.DiagnosticsResult!.Status);

        Assert.True(runtime.RequestManualObservation());
        var manual = await ReadRunAsync(runs.Reader);
        Assert.Contains(
            DesktopObservationMonitoringRuntime.ManualHintSource,
            manual.TriggerBatch.Sources);
        Assert.Equal(
            DesktopObservationDiagnosticsStatus.NoChanges,
            manual.DiagnosticsResult!.Status);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task Start_SourceThrows_IsCapturedAsDegradedStatus()
    {
        var source = new ThrowingHintSource();
        var runtime = CreateRuntime(new IDesktopObservationHintSource[] { source });
        var runs = CollectRuns(runtime);

        var status = runtime.Start();
        _ = await ReadRunAsync(runs.Reader);

        Assert.Equal(DesktopObservationMonitoringState.Degraded, status.State);
        Assert.Single(status.Sources);
        Assert.False(status.Sources[0].IsRunning);
        Assert.Contains(status.Issues, issue => issue.Contains("启动失败", StringComparison.Ordinal));

        await runtime.StopAsync();
    }

    [Fact]
    public async Task Start_WhenAlreadyRunning_IsIdempotent_AndAfterStopCannotRestart()
    {
        var source = new FakeHintSource(
            new DesktopObservationHintSourceStartResult(
                "fake",
                true,
                1,
                new[] { "endpoint" },
                Array.Empty<string>()));
        var runtime = CreateRuntime(new[] { source });
        var runs = CollectRuns(runtime);

        var first = runtime.Start();
        var second = runtime.Start();
        _ = await ReadRunAsync(runs.Reader);

        Assert.Same(first, second);
        Assert.Equal(1, source.StartCount);

        await runtime.StopAsync();
        Assert.Throws<InvalidOperationException>(() => runtime.Start());
    }

    [Fact]
    public void RequestManualObservation_BeforeStart_ReturnsFalse()
    {
        var runtime = CreateRuntime(Array.Empty<IDesktopObservationHintSource>());

        Assert.False(runtime.RequestManualObservation());
    }

    private static DesktopObservationMonitoringRuntime CreateRuntime(
        IEnumerable<IDesktopObservationHintSource> sources)
    {
        var stableSource = new ImmediateStableSource();
        var baseline = new DesktopStateBaselineTracker();
        var runner = new DesktopObservationDiagnosticsRunner(stableSource, baseline);
        var processor = new DesktopObservationBatchProcessor(runner);
        var batcher = new DesktopObservationHintBatcher(
            new DesktopObservationHintBatchSettings(TimeSpan.Zero, TimeSpan.Zero));
        var pump = new DesktopObservationBackgroundPump(batcher, processor);

        return new DesktopObservationMonitoringRuntime(pump, baseline, sources);
    }

    private static Channel<DesktopObservationTriggeredRun> CollectRuns(
        DesktopObservationMonitoringRuntime runtime)
    {
        var channel = Channel.CreateUnbounded<DesktopObservationTriggeredRun>();
        runtime.RunCompleted += run => channel.Writer.TryWrite(run);
        return channel;
    }

    private static async Task<DesktopObservationTriggeredRun> ReadRunAsync(
        ChannelReader<DesktopObservationTriggeredRun> reader) =>
        await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

    private static StableDesktopObservationResult Stable(DesktopObservation observation) =>
        new(
            StableDesktopObservationStatus.Stable,
            observation,
            Array.Empty<DesktopObservationCaptureFailure>(),
            Array.Empty<DesktopObservationStabilityIssue>());

    private static DesktopObservation Observation()
    {
        var display = new DisplayDescriptor(
            "\\\\.\\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var capabilities = new DesktopIconPositioningCapabilities(
            false,
            new PixelPoint(93, 122),
            ConnectionMethod.FindWindowSW,
            null);
        var icon = new DesktopIconDescriptor(
            "a",
            "a.txt",
            "c:\\desktop\\a.txt",
            ItemKind.File,
            new PixelPoint(100, 100),
            display.DeviceName,
            false,
            true);

        return new DesktopObservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { display },
            capabilities,
            new[] { icon },
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5));
    }

    private sealed class ImmediateStableSource : IStableDesktopObservationSource
    {
        public Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Stable(Observation()));
        }
    }

    private sealed class FakeHintSource(
        DesktopObservationHintSourceStartResult startResult)
        : IDesktopObservationHintSource
    {
        public string Source => startResult.Source;
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public DesktopObservationHintSourceStartResult Start()
        {
            StartCount++;
            IsRunning = startResult.IsRunning;
            return startResult;
        }

        public void Stop()
        {
            StopCount++;
            IsRunning = false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingHintSource : IDesktopObservationHintSource
    {
        public string Source => "throwing";
        public bool IsRunning => false;

        public DesktopObservationHintSourceStartResult Start() =>
            throw new InvalidOperationException("boom");

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
