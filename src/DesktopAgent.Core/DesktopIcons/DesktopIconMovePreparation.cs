namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 单图标移动准备结果。纯数据，不含 PIDL/COM/HWND/WPF。
/// </summary>
public sealed record DesktopIconMovePreparation(
    DesktopIconDescriptor SelectedIcon,
    DesktopIconMovePlan Plan);
