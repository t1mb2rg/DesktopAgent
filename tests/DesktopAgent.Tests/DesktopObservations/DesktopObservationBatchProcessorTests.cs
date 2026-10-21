using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopObservationBatchProcessorTests
{
    [Fact]
    public async Task ProcessAsync_ConcurrentBatches_RunDiagnosticsSingleFlight()
    {
        var source = new FirstCallBlockingStableSource();
        var processor = Processor(source);

        var firstTask = processor.ProcessAsync(Batch(1));
        await source.FirstEntered.Task;

        var secondTask = processor.ProcessAsync(Batch(2));

        Assert.Equal(1, source.CallCount);
        Assert.Equal(1, source.MaxConcurrentCalls);
        Assert.False(secondTask.IsCompleted);

        source.ReleaseFirst.TrySetResult();

        var first = await firstTask;
        var second = await secondTask;

        Assert.Equal(2, source.CallCount);
        Assert.Equal(1, source.MaxConcurrentCalls);
        Assert.Equal(DesktopObservationTriggeredRunStatus.Completed, first.Status);
        Assert.Equal(DesktopObservationTriggeredRunStatus.Completed, second.Status);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(1, first.TriggerBatch.TotalHintCount);
        Assert.Equal(2, second.TriggerBatch.TotalHintCount);
    }

    [Fact]
    public async Task ProcessAsync_EscapedException_ReturnsFailed_AndDoesNotPoisonNextRun()
    {
        var source = new ScriptedStableSource(
            _ => throw new InvalidOperationException("shell exploded"),
            _ => Task.FromResult(Stable(Observation(new PixelPoint(100, 100)))));
        var processor = Processor(source);

        var failed = await processor.ProcessAsync(Batch(1));
        var next = await processor.ProcessAsync(Batch(1));

        Assert.Equal(DesktopObservationTriggeredRunStatus.Failed, failed.Status);
        Assert.Null(failed.DiagnosticsResult);
        Assert.Contains("shell exploded", failed.EscapedException);

        Assert.Equal(DesktopObservationTriggeredRunStatus.Completed, next.Status);
        Assert.NotNull(next.DiagnosticsResult);
        Assert.Equal(
            DesktopObservationDiagnosticsStatus.BaselineEstablished,
            next.DiagnosticsResult.Status);
    }

    [Fact]
    public async Task ProcessAsync_ExplicitUnavailable_IsCompletedNotFailed()
    {
        var source = new ScriptedStableSource(
            _ => Task.FromResult(Unavailable()));
        var processor = Processor(source);

        var run = await processor.ProcessAsync(Batch(1));

        Assert.Equal(DesktopObservationTriggeredRunStatus.Completed, run.Status);
        Assert.Null(run.EscapedException);
        Assert.Equal(
            DesktopObservationDiagnosticsStatus.Unavailable,
            run.DiagnosticsResult!.Status);
    }

    [Fact]
    public async Task ProcessAsync_CancellationWhileWaitingForSingleFlight_PropagatesCancellation()
    {
        var source = new FirstCallBlockingStableSource();
        var processor = Processor(source);

        var firstTask = processor.ProcessAsync(Batch(1));
        await source.FirstEntered.Task;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(Batch(1), cts.Token));

        Assert.Equal(1, source.CallCount);
        source.ReleaseFirst.TrySetResult();
        await firstTask;
    }

    [Fact]
    public async Task ProcessAsync_CancellationInsideDiagnostics_PropagatesCancellation()
    {
        var source = new ScriptedStableSource(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Stable(Observation(new PixelPoint(100, 100)));
        });
        var processor = Processor(source);
        using var cts = new CancellationTokenSource();

        var task = processor.ProcessAsync(Batch(1), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static DesktopObservationBatchProcessor Processor(
        IStableDesktopObservationSource source)
    {
        var runner = new DesktopObservationDiagnosticsRunner(
            source,
            new DesktopStateBaselineTracker());
        return new DesktopObservationBatchProcessor(runner);
    }

    private static DesktopObservationHintBatch Batch(int hintCount)
    {
        var counts = new Dictionary<DesktopObservationHintKind, int>
        {
            [DesktopObservationHintKind.Shell] = hintCount,
        };
        var first = DateTimeOffset.UtcNow;
        return new DesktopObservationHintBatch(
            first,
            first,
            first,
            hintCount,
            counts,
            new[] { "test-shell" });
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

    private sealed class FirstCallBlockingStableSource : IStableDesktopObservationSource
    {
        private int _concurrentCalls;
        private int _maxConcurrentCalls;

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }
        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public async Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default)
        {
            var current = Interlocked.Increment(ref _concurrentCalls);
            UpdateMax(current);
            var call = Interlocked.Increment(ref CallCountBacking);
            CallCount = call;

            try
            {
                if (call == 1)
                {
                    FirstEntered.TrySetResult();
                    await ReleaseFirst.Task.WaitAsync(ct);
                }

                return Stable(Observation(new PixelPoint(100, 100)));
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentCalls);
            }
        }

        private int CallCountBacking;

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

    private sealed class ScriptedStableSource(
        params Func<CancellationToken, Task<StableDesktopObservationResult>>[] steps)
        : IStableDesktopObservationSource
    {
        private int _index;

        public Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default)
        {
            var index = Math.Min(_index++, steps.Length - 1);
            return steps[index](ct);
        }
    }
}
