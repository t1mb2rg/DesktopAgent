namespace DesktopAgent.Core.DesktopLayouts.Restore;

/// <summary>
/// 恢复计划中与单个图标无关的全局问题。
/// 纯数据，不含 PIDL/COM/HWND/WPF。
/// </summary>
public sealed record TargetDisplayLayoutRestorePlanIssue(
    TargetDisplayLayoutRestorePlanIssueCode Code,
    string Message,
    string? RelatedStableId = null);
