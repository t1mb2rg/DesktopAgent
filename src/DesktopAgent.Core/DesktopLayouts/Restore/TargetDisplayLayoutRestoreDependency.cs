namespace DesktopAgent.Core.DesktopLayouts.Restore;

/// <summary>
/// 单个移动依赖：DependsOn 必须先移动，ActionItem 才能移动到目标位置。
/// 纯数据，不含 PIDL/COM/HWND/WPF。
/// </summary>
public sealed record TargetDisplayLayoutRestoreDependency(
    string DependsOn,
    string ActionItem);
