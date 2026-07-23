namespace DesktopAgent.Core.DesktopLayouts.Restore;

/// <summary>
/// 一个移动依赖环。环中所有图标互相等待彼此先移动。
/// 纯数据，不含 PIDL/COM/HWND/WPF。
/// </summary>
public sealed record TargetDisplayLayoutRestoreCycle(
    IReadOnlyList<string> StableIds);
