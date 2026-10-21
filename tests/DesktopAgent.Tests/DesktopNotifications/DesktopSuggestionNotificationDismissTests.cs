using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Tests.DesktopNotifications;

public sealed class DesktopSuggestionNotificationDismissTests
{
    [Fact]
    public async Task DismissCurrent_WithStaleSuggestionId_DoesNotRetireReplacement()
    {
        var first = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var second = DesktopSuggestionNotificationTestData.CloneWithNewId(first);
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(first);
        lifecycle.Publish(second);

        var stale = lifecycle.DismissCurrent(first.SuggestionId);

        Assert.False(stale.Dismissed);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.Active, stale.Lifecycle.State);
        Assert.Same(second, stale.Lifecycle.CurrentSuggestion);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.ReplacedByNewSuggestion,
            stale.Lifecycle.LastRetiredSuggestion!.Reason);

        var current = lifecycle.DismissCurrent(second.SuggestionId);

        Assert.True(current.Dismissed);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, current.Lifecycle.State);
        Assert.Null(current.Lifecycle.CurrentSuggestion);
        Assert.Same(second, current.Lifecycle.LastRetiredSuggestion!.Suggestion);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.UserDismissed,
            current.Lifecycle.LastRetiredSuggestion.Reason);
    }
}
