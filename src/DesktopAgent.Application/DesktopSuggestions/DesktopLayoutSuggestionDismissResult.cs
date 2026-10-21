namespace DesktopAgent.Application.DesktopSuggestions;

/// <summary>
/// 带 expected SuggestionId 的原子 dismiss 结果。
/// 用于 notification / tray 等可能晚到的交互，防止旧 surface 误删后来出现的新 Suggestion。
/// </summary>
public sealed record DesktopLayoutSuggestionDismissResult(
    bool Dismissed,
    DesktopLayoutSuggestionLifecycleSnapshot Lifecycle);
