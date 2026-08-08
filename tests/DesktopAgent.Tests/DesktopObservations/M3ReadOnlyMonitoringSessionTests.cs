using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Poc;
using DesktopAgent.WindowsInterop.DesktopObservations;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class M3ReadOnlyMonitoringSessionTests
{
    [Fact]
    public async Task StartAsync_HintSourceUnavailable_DoesNotRunBaseline()
    {
        var stableSource = new ScriptedStableSource(
            Stable(Observation(new PixelPoint(100, 100))));
        var fake = new FakeHintSource(isRunning: false);
        using var session = Session(stableSource, _ => fake);

        var result = await session.StartAsync();

        Assert.Equal(M3ReadOnlyMonitoringStartStatus.HintSourceUnavailable, result.Status);
        Assert.False(session.IsRunning);
        Assert.Equal(0, stableSource.CallCount);
        Assert.Equal(1, fake.StartCount);
        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public async Task StartAsync_BaselineUnavailable_StopsAndDisposesSource()
    {
        var fake = new FakeHintSource(isRunning: true);
        using var session = Session(
            new ScriptedStableSource(Unavailable()),
            _ => fake);

        var result = await session.StartAsync();

        Assert.Equal(M3ReadOnlyMonitoringStartStatus.BaselineUnavailable, result.Status);
        Assert.False(session.IsRunning);
        Assert.Equal(1, fake.StartCount);
        Assert.Equal(1, fake.StopCount);
        Assert.Equal(1, fake.DisposeCount);
        Assert.NotNull(result.BaselinePrime);
        Assert.Equal(
            DesktopObservationDiagnosticsStatus.Unavailable,
            result.BaselinePrime.Status);
    }

    [Fact]
    public async Task StartAsync_BaselineUnstable_StopsAndDisposesSource()
    {
        var fake = new FakeHintSource(isRunning: true);
        using var session = Session(
            new ScriptedStableSource(Unstable()),
            _ => fake);

        var result = await session.StartAsync();

        Assert.Equal(M3ReadOnlyMonitoringStartStatus.BaselineUnstable, result.Status);
        Assert.False(session.IsRunning);
        Assert.Equal(1, fake.StopCount);
        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public async Task StartAsync_StablePrime_StartsSession_AndStopCleansUp()
    {
        var fake = new FakeHintSource(isRunning: true);
        using var session = Session(
            new ScriptedStableSource(Stable(Observation(new PixelPoint(100, 100)))),
            _ => fake);

        var result = await session.StartAsync();

        Assert.Equal(M3ReadOnlyMonitoringStartStatus.Started, result.Status);
        Assert.True(session.IsRunning);
        Assert.Equal(
            DesktopObservationDiagnosticsStatus.BaselineEstablished,
            result.BaselinePrime!.Status);

        await session.StopAsync();

        Assert.False(session.IsRunning);
        Assert.Equal(1, fake.StopCount);
        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public async Task StopThenStart_CreatesFreshHintSource_AndKeepsSessionBaseline()
    {
        var created = new List<FakeHintSource>();
        var stableSource = new ScriptedStableSource(
            Stable(Observation(new PixelPoint(100, 100))),
            Stable(Observation(new PixelPoint(100, 100))));
        using var session = Session(
            stableSource,
            _ =>
            {
                var source = new FakeHintSource(isRunning: true);
                created.Add(source);
                return source;
            });

        var first = await session.StartAsync();
        await session.StopAsync();
        var second = await session.StartAsync();

        Assert.Equal(M3ReadOnlyMonitoringStartStatus.Started, first.Status);
        Assert.Equal(M3ReadOnlyMonitoringStartStatus.Started, second.Status);
        Assert.Equal(2, created.Count);
        Assert.Equal(1, created[0].DisposeCount);
        Assert.Equal(
            DesktopObservationDiagnosticsStatus.NoChanges,
            second.BaselinePrime!.Status);

        await session.StopAsync();
        Assert.Equal(1, created[1].DisposeCount);
    }

    [Fact]
    public async Task HintArrivingDuringBaselinePrime_IsBufferedUntilPumpStarts()
    {
        var stableSource = new ScriptedStableSource(
            Stable(Observation(new PixelPoint(100, 100))),
            Stable(Observation(new PixelPoint(100, 100))));
        FakeHintSource? fake = null;
        using var session = Session(
            stableSource,
            publish => fake = new FakeHintSource(
                isRunning: true,
                onStart: () => publish(
                    DesktopObservationHintKind.FileSystem,
                    "fake-filesystem")),
            quietPeriod: TimeSpan.Zero,
            maxBatchDelay: TimeSpan.Zero);
        var backgroundRun = new TaskCompletionSource<DesktopObservationTriggeredRun>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.RunCompleted += run => backgroundRun.TrySetResult(run);

        var start = await session.StartAsync();
        var run = await backgroundRun.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(M3ReadOnlyMonitoringStartStatus.Started, start.Status);
        Assert.NotNull(fake);
        Assert.Equal(2, stableSource.CallCount);
        Assert.Equal(DesktopObservationTriggeredRunStatus.Completed, run.Status);
        Assert.Equal(1, run.TriggerBatch.TotalHintCount);
        Assert.Equal(1, run.TriggerBatch.HintCounts[DesktopObservationHintKind.FileSystem]);
        Assert.Equal(
            DesktopObservationDiagnosticsStatus.NoChanges,
            run.DiagnosticsResult!.Status);

        await session.StopAsync();
    }

    [Fact]
    public async Task StartAsync_WhileRunning_IsIdempotent()
    {
        var createCount = 0;
        using var session = Session(
            new ScriptedStableSource(Stable(Observation(new PixelPoint(100, 100)))),
            _ =>
            {
                createCount++;
                return new FakeHintSource(isRunning: true);
            });

        var first = await session.StartAsync();
        var second = await session.StartAsync();

        Assert.Equal(M3ReadOnlyMonitoringStartStatus.Started, first.Status);
        Assert.Equal(M3ReadOnlyMonitoringStartStatus.AlreadyRunning, second.Status);
        Assert.Equal(1, createCount);

        await session.StopAsync();
    }

    private static M3ReadOnlyMonitoringSession Session(
        IStableDesktopObservationSource stableSource,
        Func<Action<DesktopObservationHintKind, string>, IM3ReadOnlyMonitoringHintSource>
            sourceFactory,
        TimeSpan? quietPeriod = null,
        TimeSpan? maxBatchDelay = null)
    {
        var runner = new DesktopObservationDiagnosticsRunner(
            stableSource,
            new DesktopStateBaselineTracker());
        var settings = new DesktopObservationHintBatchSettings(
            quietPeriod ?? TimeSpan.FromMilliseconds(10),
            maxBatchDelay ?? TimeSpan.FromMilliseconds(50));

        return new M3ReadOnlyMonitoringSession(
            runner,
            settings,
            new DelegateHintSourceFactory(sourceFactory));
    }

    private static StableDesktopObservationResult Stable(DesktopObservation observation) =>
        new(
            StableDesktopObservationStatus.Stable,
            observation,
            Array.Empty<DesktopObservationCaptureFailure>(),
            Array.Empty<DesktopObservationStabilityIssue>());

    private static StableDesktopObservationResult Unavailable() =>
        new(
            StableDesktopObservationStatus.Unavailable,
            null,
            new[]
            {
                new DesktopObservationCaptureFailure(
                    DesktopObservationSamplePhase.First,
                    new DesktopObservationCaptureIssue(
                        DesktopObservationCaptureIssueCode.IncompleteDesktopRead,
                        "incomplete")),
            },
            Array.Empty<DesktopObservationStabilityIssue>());

    private static StableDesktopObservationResult Unstable() =>
        new(
            StableDesktopObservationStatus.Unstable,
            null,
            Array.Empty<DesktopObservationCaptureFailure>(),
            new[]
            {
                new DesktopObservationStabilityIssue(
                    DesktopObservationStabilityIssueCode.IconChanged,
                    "still moving",
                    "a"),
            });

    private static DesktopObservation Observation(PixelPoint position)
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
            position,
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

    private sealed class ScriptedStableSource(
        params StableDesktopObservationResult[] results)
        : IStableDesktopObservationSource
    {
        private int _index;
        public int CallCount { get; private set; }

        public Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            var index = Math.Min(_index++, results.Length - 1);
            return Task.FromResult(results[index]);
        }
    }

    private sealed class DelegateHintSourceFactory(
        Func<Action<DesktopObservationHintKind, string>, IM3ReadOnlyMonitoringHintSource> create)
        : IM3ReadOnlyMonitoringHintSourceFactory
    {
        public IM3ReadOnlyMonitoringHintSource Create(
            Action<DesktopObservationHintKind, string> publishHint) =>
            create(publishHint);
    }

    private sealed class FakeHintSource(
        bool isRunning,
        Action? onStart = null)
        : IM3ReadOnlyMonitoringHintSource
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public DesktopFileSystemHintSourceStartResult Start()
        {
            StartCount++;
            onStart?.Invoke();
            return new DesktopFileSystemHintSourceStartResult(
                isRunning,
                isRunning ? 1 : 0,
                isRunning ? new[] { "fake-desktop" } : Array.Empty<string>(),
                isRunning ? Array.Empty<string>() : new[] { "fake source unavailable" });
        }

        public void Stop() => StopCount++;

        public void Dispose() => DisposeCount++;
    }
}
