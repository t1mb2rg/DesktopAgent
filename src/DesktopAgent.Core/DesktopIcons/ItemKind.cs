namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 桌面图标项目的种类。
/// </summary>
public enum ItemKind
{
    /// <summary>普通文件（含 .lnk 快捷方式文件本身）。</summary>
    File,

    /// <summary>文件夹。</summary>
    Folder,

    /// <summary>Windows Shell 快捷方式（.lnk）。</summary>
    Shortcut,

    /// <summary>系统图标（回收站、此电脑、控制面板等）。</summary>
    SystemItem,

    /// <summary>未能识别类型。</summary>
    Unknown,
}
