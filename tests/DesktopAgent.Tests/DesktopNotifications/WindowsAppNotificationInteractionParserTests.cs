using DesktopAgent.Application.DesktopNotifications;
using DesktopAgent.WindowsInterop.DesktopNotifications;

namespace DesktopAgent.Tests.DesktopNotifications;

public sealed class WindowsAppNotificationInteractionParserTests
{
    [Fact]
    public void OpenSuggestion_ParsesExactSuggestionId()
    {
        var suggestionId = Guid.NewGuid();
        var args = new Dictionary<string, string>
        {
            [WindowsAppNotificationInteractionParser.ActionKey] =
                WindowsAppNotificationInteractionParser.OpenAction,
            [WindowsAppNotificationInteractionParser.SuggestionIdKey] =
                suggestionId.ToString("D"),
        };

        var parsed = WindowsAppNotificationInteractionParser.TryParse(
            args,
            out var interaction);

        Assert.True(parsed);
        Assert.NotNull(interaction);
        Assert.Equal(suggestionId, interaction.SuggestionId);
        Assert.Equal(
            DesktopSuggestionNotificationActionKind.OpenSuggestion,
            interaction.Action);
    }

    [Fact]
    public void MissingAction_DefaultsNotificationBodyClickToOpen()
    {
        var suggestionId = Guid.NewGuid();
        var args = new Dictionary<string, string>
        {
            [WindowsAppNotificationInteractionParser.SuggestionIdKey] =
                suggestionId.ToString("D"),
        };

        var parsed = WindowsAppNotificationInteractionParser.TryParse(
            args,
            out var interaction);

        Assert.True(parsed);
        Assert.Equal(
            DesktopSuggestionNotificationActionKind.OpenSuggestion,
            interaction!.Action);
    }

    [Fact]
    public void IgnoreSuggestion_ParsesExactSuggestionId()
    {
        var suggestionId = Guid.NewGuid();
        var args = new Dictionary<string, string>
        {
            [WindowsAppNotificationInteractionParser.ActionKey] =
                WindowsAppNotificationInteractionParser.IgnoreAction,
            [WindowsAppNotificationInteractionParser.SuggestionIdKey] =
                suggestionId.ToString("N"),
        };

        var parsed = WindowsAppNotificationInteractionParser.TryParse(
            args,
            out var interaction);

        Assert.True(parsed);
        Assert.Equal(suggestionId, interaction!.SuggestionId);
        Assert.Equal(
            DesktopSuggestionNotificationActionKind.IgnoreSuggestion,
            interaction.Action);
    }

    [Theory]
    [InlineData("not-a-guid", "openSuggestion")]
    [InlineData("00000000-0000-0000-0000-000000000000", "openSuggestion")]
    [InlineData("00000000-0000-0000-0000-000000000001", "unknown")]
    public void InvalidActivation_FailsClosed(string suggestionId, string action)
    {
        var args = new Dictionary<string, string>
        {
            [WindowsAppNotificationInteractionParser.ActionKey] = action,
            [WindowsAppNotificationInteractionParser.SuggestionIdKey] = suggestionId,
        };

        Assert.False(
            WindowsAppNotificationInteractionParser.TryParse(
                args,
                out var interaction));
        Assert.Null(interaction);
    }

    [Fact]
    public void CreateTag_IsDeterministicAndWithinModernWindowsLimit()
    {
        var suggestionId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

        var tag = WindowsAppNotificationInteractionParser.CreateTag(suggestionId);

        Assert.Equal("da-0123456789abcdef0123456789abcdef", tag);
        Assert.True(tag.Length <= 64);
    }
}
