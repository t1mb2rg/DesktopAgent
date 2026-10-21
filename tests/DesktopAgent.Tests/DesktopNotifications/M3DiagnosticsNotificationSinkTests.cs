using DesktopAgent.Application.DesktopNotifications;
using DesktopAgent.Poc;

namespace DesktopAgent.Tests.DesktopNotifications;

public sealed class M3DiagnosticsNotificationSinkTests
{
    [Fact]
    public async Task PublishWithdraw_UpdatesOnlyMatchingCurrentSurface()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = DesktopSuggestionNotificationTestData.Active(suggestion);
        var payload = DesktopSuggestionNotificationPayload.FromLifecycle(lifecycle);
        using var sink = new M3DiagnosticsNotificationSink();

        await sink.PublishAsync(payload);
        Assert.Equal(suggestion.SuggestionId, sink.Snapshot.CurrentNotification!.SuggestionId);

        await sink.WithdrawAsync(Guid.NewGuid());
        Assert.Equal(suggestion.SuggestionId, sink.Snapshot.CurrentNotification!.SuggestionId);

        await sink.WithdrawAsync(suggestion.SuggestionId);
        Assert.Null(sink.Snapshot.CurrentNotification);
        Assert.Contains("Withdrawn", sink.Snapshot.LastEvent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulateInteraction_CanRepresentLateStaleNotificationAction()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        using var sink = new M3DiagnosticsNotificationSink();
        var received = new List<DesktopSuggestionNotificationInteraction>();
        sink.InteractionReceived += interaction => received.Add(interaction);

        sink.SimulateInteraction(
            suggestion.SuggestionId,
            DesktopSuggestionNotificationActionKind.IgnoreSuggestion);

        var interaction = Assert.Single(received);
        Assert.Equal(suggestion.SuggestionId, interaction.SuggestionId);
        Assert.Equal(
            DesktopSuggestionNotificationActionKind.IgnoreSuggestion,
            interaction.Action);
    }

    [Fact]
    public async Task InnerPublishFailure_DoesNotAdvanceMirror()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var payload = DesktopSuggestionNotificationPayload.FromLifecycle(
            DesktopSuggestionNotificationTestData.Active(suggestion));
        var inner = new ControlledInnerSink { PublishException = new InvalidOperationException("platform down") };
        using var sink = new M3DiagnosticsNotificationSink(inner);
        var before = sink.Snapshot;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sink.PublishAsync(payload));

        Assert.Equal("platform down", ex.Message);
        Assert.Equal(before.Revision, sink.Snapshot.Revision);
        Assert.Null(sink.Snapshot.CurrentNotification);
        Assert.Equal(before.LastEvent, sink.Snapshot.LastEvent);
        Assert.Single(inner.PublishAttempts);
    }

    [Fact]
    public async Task InnerWithdrawFailure_DoesNotClearMirror()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var payload = DesktopSuggestionNotificationPayload.FromLifecycle(
            DesktopSuggestionNotificationTestData.Active(suggestion));
        var inner = new ControlledInnerSink();
        using var sink = new M3DiagnosticsNotificationSink(inner);
        await sink.PublishAsync(payload);
        var before = sink.Snapshot;
        inner.WithdrawException = new InvalidOperationException("remove failed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sink.WithdrawAsync(suggestion.SuggestionId));

        Assert.Equal("remove failed", ex.Message);
        Assert.Equal(before.Revision, sink.Snapshot.Revision);
        Assert.Equal(
            suggestion.SuggestionId,
            sink.Snapshot.CurrentNotification!.SuggestionId);
        Assert.Single(inner.WithdrawAttempts);
    }

    [Fact]
    public async Task InnerInteraction_IsForwardedUnchanged()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var inner = new ControlledInnerSink();
        using var sink = new M3DiagnosticsNotificationSink(inner);
        var received = new List<DesktopSuggestionNotificationInteraction>();
        sink.InteractionReceived += interaction => received.Add(interaction);
        var interaction = new DesktopSuggestionNotificationInteraction(
            suggestion.SuggestionId,
            DesktopSuggestionNotificationActionKind.OpenSuggestion);

        inner.Raise(interaction);

        Assert.Equal(interaction, Assert.Single(received));
    }

    private sealed class ControlledInnerSink : IDesktopSuggestionNotificationSink
    {
        public List<DesktopSuggestionNotificationPayload> PublishAttempts { get; } = [];
        public List<Guid> WithdrawAttempts { get; } = [];
        public Exception? PublishException { get; set; }
        public Exception? WithdrawException { get; set; }

        public event Action<DesktopSuggestionNotificationInteraction>? InteractionReceived;

        public Task PublishAsync(
            DesktopSuggestionNotificationPayload payload,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PublishAttempts.Add(payload);
            if (PublishException is not null)
                throw PublishException;
            return Task.CompletedTask;
        }

        public Task WithdrawAsync(Guid suggestionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            WithdrawAttempts.Add(suggestionId);
            if (WithdrawException is not null)
                throw WithdrawException;
            return Task.CompletedTask;
        }

        public void Raise(DesktopSuggestionNotificationInteraction interaction) =>
            InteractionReceived?.Invoke(interaction);
    }
}
