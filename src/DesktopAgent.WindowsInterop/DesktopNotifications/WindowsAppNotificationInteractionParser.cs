using DesktopAgent.Application.DesktopNotifications;

namespace DesktopAgent.WindowsInterop.DesktopNotifications;

internal static class WindowsAppNotificationInteractionParser
{
    internal const string ActionKey = "action";
    internal const string SuggestionIdKey = "suggestionId";
    internal const string OpenAction = "openSuggestion";
    internal const string IgnoreAction = "ignoreSuggestion";

    public static bool TryParse(
        IReadOnlyDictionary<string, string> arguments,
        out DesktopSuggestionNotificationInteraction? interaction)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        interaction = null;

        if (!arguments.TryGetValue(SuggestionIdKey, out var suggestionIdText)
            || !Guid.TryParse(suggestionIdText, out var suggestionId)
            || suggestionId == Guid.Empty)
        {
            return false;
        }

        arguments.TryGetValue(ActionKey, out var actionText);
        var action = actionText switch
        {
            null or "" or OpenAction => DesktopSuggestionNotificationActionKind.OpenSuggestion,
            IgnoreAction => DesktopSuggestionNotificationActionKind.IgnoreSuggestion,
            _ => (DesktopSuggestionNotificationActionKind?)null,
        };

        if (action is null)
            return false;

        interaction = new DesktopSuggestionNotificationInteraction(
            suggestionId,
            action.Value);
        return true;
    }

    public static string CreateTag(Guid suggestionId)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("suggestionId 不能为空。", nameof(suggestionId));

        // Windows 10 Creators Update 及以后 tag 上限已足够容纳 32 位 Guid。
        // 项目最低目标为 Windows 10 2004，因此使用无分隔符 Guid 可稳定反向删除。
        return "da-" + suggestionId.ToString("N");
    }
}
