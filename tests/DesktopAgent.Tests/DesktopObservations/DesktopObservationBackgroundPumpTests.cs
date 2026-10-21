using System.Threading.Channels;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopObservationBackgroundPumpTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_FirstHint_WaitsUntilQuietDueThenProcesses()
    {
        var clock = new ManualPumpClock(T0);
        var pump = Pump(clock, new ImmediateStableSource());
        var runs = CollectRuns(pump);
        using var cts = new CancellationTokenSource();
        var pumpTask = pump.RunAsync(cts.Token);

        Assert.True(pump.TryPublishHint(DesktopObservationHintKind.FileSystem, "fs"));
        clock.AdvanceTo(T0.AddMilliseconds(499));
        Assert.False(runs.Reader.TryRead(out _));

        clock.AdvanceTo(T0.AddMilliseconds(500));
        var run = await ReadRunAsync(runs.Reader);

        Assert.Equal(DesktopObservationTriggeredRunStatus.Completed, run.Status);
        Assert.Equal(1, run.TriggerBatch.TotalHintCount);
        Assert.Equal(1, run.TriggerBatch.HintCounts[DesktopObservationHintKind.FileSystem]);

        await StopPumpAsync(cts, pumpTask);
    }

    [Fact]
    public async Task RunAsync_NewHint_RecalculatesQuietDueWithoutLosingWake()
    {
        var clock = new ManualPumpClock(T0);
        var pump = Pump(clock, new ImmediateStableSource());
        var runs = CollectRuns(pump);
        using var cts = new CancellationTokenSource();
        var pumpTask = pump.RunAsync(cts.Token);

        pump.TryPublishHint(DesktopObservationHintKind.FileSystem, "fs");
        clock.AdvanceTo(T0.AddMilliseconds(300));
        pump.TryPublishHint(DesktopObservationHintKind.Shell, "shell");

        clock.AdvanceTo(T0.AddMilliseconds(500));
        Assert.False(runs.Reader.TryRead(out _));

        clock.AdvanceTo(T0.AddMilliseconds(799));
        Assert.False(runs.Reader.TryRead(out _));

        clock.AdvanceTo(T0.AddMilliseconds(800));
        var run = await ReadRunAsync(runs.Reader);

        Assert.Equal(2, run.TriggerBatch.TotalHintCount);
        Assert.Equal(1, run.TriggerBatch.HintCounts[DesktopObservationHintKind.FileSystem]);
        Assert.Equal(1, run.TriggerBatch.HintCounts[DesktopObservationHintKind.Shell]);

        await StopPumpAsync(cts, pumpTask);
    }

    [Fact]
    public async Task RunAsync_HintsArrivingDuringProcessing_AreRetainedForNextBatch()
    {
        var clock = new ManualPumpClock(T0);
        var source = new FirstCallBlockingStableSource();
        var pump = Pump(clock, source);
        var runs = CollectRuns(pump);
        using var cts = new CancellationTokenSource();
        var pumpTask = pump.RunAsync(cts.Token);

        pump.TryPublishHint(DesktopObservationHintKind.FileSystem, "fs");
        clock.AdvanceTo(T0.AddMilliseconds(500));
        await source.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        clock.AdvanceTo(T0.AddMilliseconds(600));
        pump.TryPublishHint(DesktopObservationHintKind.Shell, "shell");
        clock.AdvanceTo(T0.AddMilliseconds(700));
        pump.TryPublishHint(DesktopObservationHintKind.FileSystem, "fs");

        source.ReleaseFirst.TrySetResult();
        var first = await ReadRunAsync(runs.Reader);
        Assert.Equal(1, first.TriggerBatch.TotalHintCount);

        clock.AdvanceTo(T0.AddMilliseconds(1200));
        var second = await ReadRunAsync(runs.Reader);

        Assert.Equal(2, second.TriggerBatch.TotalHintCount);
        Assert.Equal(1, second.TriggerBatch.HintCounts[DesktopObservationHintKind.Shell]);
        Assert.Equal(1, second.TriggerBatch.HintCounts[DesktopObservationHintKind.FileSystem]);
        Assert.Equal(1, source.MaxConcurrentCalls);

        await StopPumpAsync(cts, pumpTask);
    }

    [Fact]
    public async Task RunCompleted_ThrowingSubscriber_DoesNotKillPumpOrOtherSubscribers()
    {
        var clock = new ManualPumpClock(T0);
        var pump = Pump(clock, new ImmediateStableSource());
        var runs = Channel.CreateUnbounded<DesktopObservationTriggeredRun>();
        pump.RunCompleted += _ => throw new InvalidOperationException("observer failed");
        pump.RunCompleted += run => runs.Writer.TryWrite(run);
        using var cts = new CancellationTokenSource();
        var pumpTask = pump.RunAsync(cts.Token);

        pump.TryPublishHint(DesktopObservationHintKind.Manual, "manual");
        clock.AdvanceTo(T0.AddMilliseconds(500));
        await ReadRunAsync(runs.Reader);

        clock.AdvanceTo(T0.AddMilliseconds(600));
        pump.TryPublishHint(DesktopObservationHintKind.Manual, "manual");
        clock.AdvanceTo(T0.AddMilliseconds(1100));
        var second = await ReadRunAsync(runs.Reader);

        Assert.Equal(DesktopObservationTriggeredRunStatus.Completed, second.Status);
        Assert.Same(second, pump.LastRun);

        await StopPumpAsync(cts, pumpTask);
    }

    [Fact]
    public async Task RunAsync_CanOnlyStartOnce()
    {
        var clock = new ManualPumpClock(T0);
        var pump = Pump(clock, new ImmediateStableSource());
        using var cts = new CancellationTokenSource();
        var first = pump.RunAsync(cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pump.RunAsync(CancellationToken.None));

        await StopPumpAsync(cts, first);
    }

    [Fact]
    public async Task RunAsync_AfterStop_RejectsNewHints()
    {
        var clock = new ManualPumpClock(T0);
        var pump = Pump(clock, new ImmediateStableSource());
        using var cts = new CancellationTokenSource();
        var task = pump.RunAsync(cts.Token);

        await StopPumpAsync(cts, task);

        Assert.False(pump.TryPublishHint(DesktopObservationHintKind.Manual, "manual"));
    }

    private static DesktopObservationBackgroundPump Pump(
        ManualPumpClock clock,
        IStableDesktopObservationSource source)
    {
        var batcher = new DesktopObservationHintBatcher(
            new DesktopObservationHintBatchSettings(
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(2000)));
        var runner = new DesktopObservationDiagnosticsRunner(
            source,
            new DesktopStateBaselineTracker());
        var processor = new DesktopObservationBatchProcessor(runner);
        return new DesktopObservationBackgroundPump(batcher, processor, clock);
    }

    private static Channel<DesktopObservationTriggeredRun> CollectRuns(
        DesktopObservationBackgroundPump pump)
    {
        var runs = Channel.CreateUnbounded<DesktopObservationTriggeredRun>();
        pump.RunCompleted += run => runs.Writer.TryWrite(run);
        return runs;
    }

    private static async Task<DesktopObservationTriggeredRun> ReadRunAsync(
        ChannelReader<DesktopObservationTriggeredRun> reader) =>
        await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

    private static async Task StopPumpAsync(
        CancellationTokenSource cts,
        Task pumpTask)
    {
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumpTask);
    }

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

    private sealed class FirstCallBlockingStableSource : IStableDesktopObservationSource
    {
        private int _callCount;
        private int _concurrentCalls;
        private int _maxConcurrentCalls;

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public async Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default)
        {
            var concurrent = Interlocked.Increment(ref _concurrentCalls);
            UpdateMax(concurrent);
            var call = Interlocked.Increment(ref _callCount);

            try
            {
                if (call == 1)
                {
                    FirstEntered.TrySetResult();
                    await ReleaseFirst.Task.WaitAsync(ct);
                }

                return Stable(Observation());
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentCalls);
            }
        }

        private void UpdateMax(int current)
        {
            while (true)
            {
                var snapshot = Volatile.Read(ref _maxConcurrentCalls);
                if (current <= snapshot)
                    return;
                if (Interlocked.CompareExchange(ref _maxConcurrentCalls, current, snapshot) == snapshot)
                    return;
            }
        }
    }

    private sealed class ManualPumpClock(DateTimeOffset initial) : IDesktopObservationPumpClock
    {
        private readonly object _gate = new();
        private readonly List<DelayWaiter> _waiters = [];
        private DateTimeOffset _now = initial.ToUniversalTime();

        public DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public Task DelayUntilAsync(DateTimeOffset dueAtUtc, CancellationToken ct)
        {
            var due = dueAtUtc.ToUniversalTime();
            TaskCompletionSource tcs;

            lock (_gate)
            {
                if (due <= _now)
                    return Task.CompletedTask;

                tcs = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(new DelayWaiter(due, tcs));
            }

            if (ct.CanBeCanceled)
                ct.Register(() => tcs.TrySetCanceled(ct));

            return tcs.Task;
        }

        public void AdvanceTo(DateTimeOffset value)
        {
            var target = value.ToUniversalTime();
            List<DelayWaiter> ready;

            lock (_gate)
            {
                if (target < _now)
                    throw new ArgumentOutOfRangeException(nameof(value));

                _now = target;
                ready = _waiters.Where(waiter => waiter.DueAtUtc <= _now).ToList();
                _waiters.RemoveAll(waiter => waiter.DueAtUtc <= _now);
            }

            foreach (var waiter in ready)
                waiter.Completion.TrySetResult();
        }

        private sealed record DelayWaiter(
            DateTimeOffset DueAtUtc,
            TaskCompletionSource Completion);
    }
}
