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
        var sink = new M3DiagnosticsNotificationSink();

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
        var sink = new M3DiagnosticsNotificationSink();
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
}
