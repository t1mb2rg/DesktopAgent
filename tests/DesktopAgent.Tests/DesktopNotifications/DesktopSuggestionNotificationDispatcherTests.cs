using DesktopAgent.Application.DesktopNotifications;
using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Tests.DesktopNotifications;

public sealed class DesktopSuggestionNotificationDispatcherTests
{
    [Fact]
    public async Task Reconcile_FirstActive_PublishesOnce_ThenDeduplicates()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = DesktopSuggestionNotificationTestData.Active(suggestion);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var sink = new RecordingSink();
        var dispatcher = Dispatcher(sink, clock, TimeSpan.FromMinutes(10));

        var first = await dispatcher.ReconcileAsync(lifecycle);
        var second = await dispatcher.ReconcileAsync(lifecycle);

        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.Completed, first.Status);
        Assert.Single(sink.Published);
        Assert.Equal(suggestion.SuggestionId, sink.Published[0].SuggestionId);
        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.NoSinkWork, second.Status);
        Assert.Single(sink.Published);
        Assert.Equal(
            DesktopSuggestionNotificationSuppressionReason.AlreadyPublished,
            second.Plan.SuppressionReason);
    }

    [Fact]
    public async Task Reconcile_NewSuggestionDuringCooldown_WithdrawsOldAndDefersNew()
    {
        var firstSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var secondSuggestion = DesktopSuggestionNotificationTestData.CloneWithNewId(firstSuggestion);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var sink = new RecordingSink();
        var dispatcher = Dispatcher(sink, clock, TimeSpan.FromMinutes(10));

        await dispatcher.ReconcileAsync(DesktopSuggestionNotificationTestData.Active(firstSuggestion));
        clock.Advance(TimeSpan.FromMinutes(2));

        var deferred = await dispatcher.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(secondSuggestion));

        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.Deferred, deferred.Status);
        Assert.Equal(firstSuggestion.SuggestionId, Assert.Single(sink.Withdrawn));
        Assert.Single(sink.Published);
        Assert.Equal(clock.Origin.AddMinutes(10), deferred.NextEligibleAtUtc);

        clock.Advance(TimeSpan.FromMinutes(8));
        var published = await dispatcher.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(secondSuggestion));

        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.Completed, published.Status);
        Assert.Equal(2, sink.Published.Count);
        Assert.Equal(secondSuggestion.SuggestionId, sink.Published[1].SuggestionId);
    }

    [Fact]
    public async Task Reconcile_WithdrawFailure_DoesNotPublishReplacement_AndRetriesLater()
    {
        var firstSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var secondSuggestion = DesktopSuggestionNotificationTestData.CloneWithNewId(firstSuggestion);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var sink = new RecordingSink();
        var dispatcher = Dispatcher(sink, clock, TimeSpan.Zero);

        await dispatcher.ReconcileAsync(DesktopSuggestionNotificationTestData.Active(firstSuggestion));
        sink.FailNextWithdraw = true;

        var failed = await dispatcher.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(secondSuggestion));

        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.SinkFailed, failed.Status);
        Assert.Equal(DesktopSuggestionNotificationSinkOperation.Withdraw, failed.FailedOperation);
        Assert.Single(sink.Published);
        Assert.Empty(sink.Withdrawn);
        Assert.Equal(firstSuggestion.SuggestionId, failed.PolicyState.SurfacedSuggestionId);

        var retried = await dispatcher.ReconcileAsync(
            DesktopSuggestionNotificationTestData.Active(secondSuggestion));

        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.Completed, retried.Status);
        Assert.Single(sink.Withdrawn);
        Assert.Equal(firstSuggestion.SuggestionId, sink.Withdrawn[0]);
        Assert.Equal(2, sink.Published.Count);
        Assert.Equal(secondSuggestion.SuggestionId, sink.Published[1].SuggestionId);
    }

    [Fact]
    public async Task Reconcile_PublishFailure_IsNotAcknowledged_AndCanRetry()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = DesktopSuggestionNotificationTestData.Active(suggestion);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var sink = new RecordingSink { FailNextPublish = true };
        var dispatcher = Dispatcher(sink, clock, TimeSpan.Zero);

        var failed = await dispatcher.ReconcileAsync(lifecycle);

        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.SinkFailed, failed.Status);
        Assert.Equal(DesktopSuggestionNotificationSinkOperation.Publish, failed.FailedOperation);
        Assert.Null(failed.PolicyState.LastPublishedSuggestionId);
        Assert.Empty(sink.Published);

        var retried = await dispatcher.ReconcileAsync(lifecycle);

        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.Completed, retried.Status);
        Assert.Single(sink.Published);
        Assert.Equal(suggestion.SuggestionId, sink.Published[0].SuggestionId);
    }

    [Fact]
    public async Task Reconcile_SuspendedWithdrawsButResumeSameSuggestionDoesNotRepublish()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var active = lifecycle.Publish(suggestion);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var sink = new RecordingSink();
        var dispatcher = Dispatcher(sink, clock, TimeSpan.Zero);

        await dispatcher.ReconcileAsync(active);
        var suspended = lifecycle.Suspend(
            DesktopLayoutSuggestionSuspensionReason.ObservationRunFailed);
        var withdraw = await dispatcher.ReconcileAsync(suspended);

        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.Completed, withdraw.Status);
        Assert.Single(sink.Withdrawn);

        var resumed = lifecycle.ConfirmNoDesktopChanges();
        var afterResume = await dispatcher.ReconcileAsync(resumed);

        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.NoSinkWork, afterResume.Status);
        Assert.Single(sink.Published);
        Assert.Equal(
            DesktopSuggestionNotificationSuppressionReason.AlreadyPublished,
            afterResume.Plan.SuppressionReason);
    }

    [Fact]
    public async Task Reconcile_ConcurrentSameSnapshot_StillPublishesOnlyOnce()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = DesktopSuggestionNotificationTestData.Active(suggestion);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var sink = new BlockingPublishSink();
        var dispatcher = Dispatcher(sink, clock, TimeSpan.Zero);

        var first = dispatcher.ReconcileAsync(lifecycle);
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = dispatcher.ReconcileAsync(lifecycle);

        Assert.Equal(1, sink.PublishCount);
        sink.Release.TrySetResult(true);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, sink.PublishCount);
        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.Completed, results[0].Status);
        Assert.Equal(DesktopSuggestionNotificationDispatchStatus.NoSinkWork, results[1].Status);
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

    private sealed class RecordingSink : IDesktopSuggestionNotificationSink
    {
        public List<DesktopSuggestionNotificationPayload> Published { get; } = [];
        public List<Guid> Withdrawn { get; } = [];
        public bool FailNextPublish { get; set; }
        public bool FailNextWithdraw { get; set; }

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
            if (FailNextPublish)
            {
                FailNextPublish = false;
                throw new InvalidOperationException("publish failed");
            }

            Published.Add(payload);
            return Task.CompletedTask;
        }

        public Task WithdrawAsync(Guid suggestionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FailNextWithdraw)
            {
                FailNextWithdraw = false;
                throw new InvalidOperationException("withdraw failed");
            }

            Withdrawn.Add(suggestionId);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingPublishSink : IDesktopSuggestionNotificationSink
    {
        private int _publishCount;
        public int PublishCount => Volatile.Read(ref _publishCount);
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<DesktopSuggestionNotificationInteraction>? InteractionReceived
        {
            add { }
            remove { }
        }

        public async Task PublishAsync(
            DesktopSuggestionNotificationPayload payload,
            CancellationToken ct = default)
        {
            _ = payload;
            Interlocked.Increment(ref _publishCount);
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(ct);
        }

        public Task WithdrawAsync(Guid suggestionId, CancellationToken ct = default)
        {
            _ = suggestionId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
