using System.Threading.Channels;
using DesktopAgent.Application.DesktopObservations;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class PeriodicObservationHintSourceTests
{
    [Fact]
    public void Settings_ZeroOrNegativeInterval_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PeriodicObservationHintSettings(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PeriodicObservationHintSettings(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task RunAsync_DoesNotPublishUntilFirstIntervalCompletes()
    {
        var clock = new ManualPeriodicClock();
        var source = new PeriodicObservationHintSource(
            new PeriodicObservationHintSettings(TimeSpan.FromSeconds(15)),
            clock);
        var published = new TaskCompletionSource<PublishedHint>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var runTask = source.RunAsync(
            (kind, provider) => published.TrySetResult(new PublishedHint(kind, provider)),
            cts.Token);

        var firstDelay = await clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), firstDelay.Interval);
        Assert.False(published.Task.IsCompleted);

        firstDelay.Complete();
        var hint = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DesktopObservationHintKind.PeriodicFallback, hint.Kind);
        Assert.Equal(PeriodicObservationHintSource.ProviderName, hint.Provider);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RunAsync_PublishesOneHintPerCompletedInterval()
    {
        var clock = new ManualPeriodicClock();
        var source = new PeriodicObservationHintSource(
            new PeriodicObservationHintSettings(TimeSpan.FromSeconds(10)),
            clock);
        var hints = Channel.CreateUnbounded<PublishedHint>();
        using var cts = new CancellationTokenSource();

        var runTask = source.RunAsync(
            (kind, provider) => hints.Writer.TryWrite(new PublishedHint(kind, provider)),
            cts.Token);

        var first = await clock.NextDelayAsync();
        first.Complete();
        var firstHint = await hints.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var second = await clock.NextDelayAsync();
        second.Complete();
        var secondHint = await hints.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DesktopObservationHintKind.PeriodicFallback, firstHint.Kind);
        Assert.Equal(DesktopObservationHintKind.PeriodicFallback, secondHint.Kind);
        Assert.Equal(PeriodicObservationHintSource.ProviderName, firstHint.Provider);
        Assert.Equal(PeriodicObservationHintSource.ProviderName, secondHint.Provider);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RunAsync_PublishCallbackFailure_DoesNotKillNextTick()
    {
        var clock = new ManualPeriodicClock();
        var source = new PeriodicObservationHintSource(
            new PeriodicObservationHintSettings(TimeSpan.FromSeconds(10)),
            clock);
        var callCount = 0;
        var secondPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var runTask = source.RunAsync(
            (_, _) =>
            {
                var call = Interlocked.Increment(ref callCount);
                if (call == 1)
                    throw new InvalidOperationException("sink failed once");
                secondPublished.TrySetResult();
            },
            cts.Token);

        var first = await clock.NextDelayAsync();
        first.Complete();

        var second = await clock.NextDelayAsync();
        second.Complete();
        await secondPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref callCount));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringDelay_PropagatesWithoutPublishing()
    {
        var clock = new ManualPeriodicClock();
        var source = new PeriodicObservationHintSource(
            new PeriodicObservationHintSettings(TimeSpan.FromSeconds(10)),
            clock);
        var publishCount = 0;
        using var cts = new CancellationTokenSource();

        var runTask = source.RunAsync(
            (_, _) => Interlocked.Increment(ref publishCount),
            cts.Token);

        await clock.NextDelayAsync();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(0, Volatile.Read(ref publishCount));
    }

    private sealed record PublishedHint(
        DesktopObservationHintKind Kind,
        string Provider);

    private sealed class ManualPeriodicClock : IPeriodicObservationHintClock
    {
        private readonly Channel<DelayRequest> _requests =
            Channel.CreateUnbounded<DelayRequest>();

        public Task DelayAsync(TimeSpan interval, CancellationToken ct)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (ct.CanBeCanceled)
                ct.Register(() => completion.TrySetCanceled(ct));

            _requests.Writer.TryWrite(new DelayRequest(interval, completion));
            return completion.Task;
        }

        public async Task<DelayRequest> NextDelayAsync() =>
            await _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed record DelayRequest(
        TimeSpan Interval,
        TaskCompletionSource Completion)
    {
        public void Complete() => Completion.TrySetResult();
    }
}
