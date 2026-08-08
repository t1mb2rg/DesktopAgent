using DesktopAgent.Application.DesktopNotifications;
using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Tests.DesktopNotifications;

public sealed class DesktopSuggestionNotificationSchedulerTests
{
    [Fact]
    public async Task DeferredTimer_ReevaluatesLatestLifecycle_NotOldPendingPayload()
    {
        var firstSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var secondSuggestion = DesktopSuggestionNotificationTestData.CloneWithNewId(firstSuggestion);
        var thirdSuggestion = DesktopSuggestionNotificationTestData.CloneWithNewId(firstSuggestion);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var delay = new ControlledDelay();
        var sink = new RecordingSink();
        var dispatcher = Dispatcher(sink, clock, TimeSpan.FromMinutes(10));
        await using var scheduler = new DesktopSuggestionNotificationScheduler(
            dispatcher,
            clock,
            delay.DelayAsync);

        var first = await scheduler.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(firstSuggestion));
        Assert.Equal(DesktopSuggestionNotificationSchedulerStatus.Reconciled, first.Status);
        Assert.Equal(firstSuggestion.SuggestionId, Assert.Single(sink.Published).SuggestionId);

        clock.Advance(TimeSpan.FromMinutes(2));
        var second = await scheduler.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(secondSuggestion));
        Assert.Equal(
            DesktopSuggestionNotificationSchedulerStatus.DeferredScheduled,
            second.Status);
        Assert.Equal(clock.Origin.AddMinutes(10), second.DeferredUntilUtc);
        Assert.Equal(1, delay.RequestCount);

        clock.Advance(TimeSpan.FromMinutes(2));
        var third = await scheduler.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(thirdSuggestion));
        Assert.Equal(
            DesktopSuggestionNotificationSchedulerStatus.DeferredScheduled,
            third.Status);
        Assert.Equal(clock.Origin.AddMinutes(10), third.DeferredUntilUtc);
        Assert.Equal(2, delay.RequestCount);
        Assert.True(delay.Requests[0].Task.IsCanceled);

        var timerPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Reconciled += result =>
        {
            if (result.Status == DesktopSuggestionNotificationSchedulerStatus.Reconciled
                && result.DispatchResult?.Status == DesktopSuggestionNotificationDispatchStatus.Completed
                && sink.Published.Count == 2)
            {
                timerPublished.TrySetResult(true);
            }
        };

        clock.Advance(TimeSpan.FromMinutes(6));
        delay.ReleaseLatest();
        await timerPublished.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, sink.Published.Count);
        Assert.Equal(firstSuggestion.SuggestionId, sink.Published[0].SuggestionId);
        Assert.Equal(thirdSuggestion.SuggestionId, sink.Published[1].SuggestionId);
        Assert.DoesNotContain(
            sink.Published,
            payload => payload.SuggestionId == secondSuggestion.SuggestionId);
    }

    [Fact]
    public async Task NewSuspendedLifecycle_CancelsDeferredRetry()
    {
        var firstSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var secondSuggestion = DesktopSuggestionNotificationTestData.CloneWithNewId(firstSuggestion);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var delay = new ControlledDelay();
        var sink = new RecordingSink();
        var dispatcher = Dispatcher(sink, clock, TimeSpan.FromMinutes(10));
        await using var scheduler = new DesktopSuggestionNotificationScheduler(
            dispatcher,
            clock,
            delay.DelayAsync);

        await scheduler.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(firstSuggestion));

        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var activeSecond = lifecycle.Publish(secondSuggestion);
        clock.Advance(TimeSpan.FromMinutes(2));
        var deferred = await scheduler.ReconcileAsync(activeSecond);
        Assert.Equal(
            DesktopSuggestionNotificationSchedulerStatus.DeferredScheduled,
            deferred.Status);
        Assert.Equal(1, delay.RequestCount);

        var suspended = lifecycle.Suspend(
            DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable);
        var reconciled = await scheduler.ReconcileAsync(suspended);

        Assert.Equal(DesktopSuggestionNotificationSchedulerStatus.Reconciled, reconciled.Status);
        Assert.True(delay.Requests[0].Task.IsCanceled);
        Assert.Single(sink.Published);
        Assert.Equal(firstSuggestion.SuggestionId, sink.Published[0].SuggestionId);
        Assert.Null(scheduler.LatestLifecycle?.IsActionablePresentation == true
            ? scheduler.LatestLifecycle.CurrentSuggestion
            : null);
    }

    [Fact]
    public async Task DisposeAsync_CancelsPendingDeferredDelay()
    {
        var firstSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var secondSuggestion = DesktopSuggestionNotificationTestData.CloneWithNewId(firstSuggestion);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var delay = new ControlledDelay();
        var sink = new RecordingSink();
        var dispatcher = Dispatcher(sink, clock, TimeSpan.FromMinutes(10));
        var scheduler = new DesktopSuggestionNotificationScheduler(
            dispatcher,
            clock,
            delay.DelayAsync);

        await scheduler.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(firstSuggestion));
        clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(secondSuggestion));
        Assert.Equal(1, delay.RequestCount);

        await scheduler.DisposeAsync();

        Assert.True(delay.Requests[0].Task.IsCanceled);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await scheduler.ReconcileAsync(
                DesktopSuggestionNotificationTestData.Active(secondSuggestion)));
    }

    private static DesktopSuggestionNotificationDispatcher Dispatcher(
        IDesktopSuggestionNotificationSink sink,
        TimeProvider timeProvider,
        TimeSpan cooldown) =>
        new(
            new DesktopSuggestionNotificationPolicy(
                new DesktopSuggestionNotificationPolicySettings(cooldown)),
            sink,
            timeProvider);

    private sealed class ManualTimeProvider(DateTimeOffset origin) : TimeProvider
    {
        private DateTimeOffset _utcNow = origin;
        public DateTimeOffset Origin { get; } = origin;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class ControlledDelay
    {
        private readonly object _gate = new();
        private readonly List<Request> _requests = [];

        public IReadOnlyList<Request> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        public int RequestCount
        {
            get
            {
                lock (_gate)
                    return _requests.Count;
            }
        }

        public Task DelayAsync(
            TimeSpan delay,
            TimeProvider timeProvider,
            CancellationToken ct)
        {
            _ = timeProvider;
            var request = new Request(delay);
            lock (_gate)
                _requests.Add(request);

            ct.Register(() => request.Completion.TrySetCanceled(ct));
            return request.Task;
        }

        public void ReleaseLatest()
        {
            Request request;
            lock (_gate)
                request = _requests[^1];
            request.Completion.TrySetResult(true);
        }

        public sealed class Request
        {
            public TimeSpan Delay { get; }
            public TaskCompletionSource<bool> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Task => Completion.Task;

            public Request(TimeSpan delay)
            {
                Delay = delay;
            }
        }
    }

    private sealed class RecordingSink : IDesktopSuggestionNotificationSink
    {
        public List<DesktopSuggestionNotificationPayload> Published { get; } = [];
        public List<Guid> Withdrawn { get; } = [];

        public event Action<DesktopSuggestionNotificationInteraction>? InteractionReceived
        {
            add { }
            remove { }
        }

        public Task PublishAsync(
            DesktopSuggestionNotificationPayload payload,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add(payload);
            return Task.CompletedTask;
        }

        public Task WithdrawAsync(Guid suggestionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Withdrawn.Add(suggestionId);
            return Task.CompletedTask;
        }
    }
}
