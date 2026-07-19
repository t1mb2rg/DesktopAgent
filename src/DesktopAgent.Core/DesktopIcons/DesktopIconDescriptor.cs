using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// Explorer 桌面视图中的一个图标项目的只读描述。
/// 不包含 PIDL 指针、COM 对象、HWND 或 WPF 类型。
/// </summary>
public sealed record DesktopIconDescriptor(
    /// <summary>
    /// 当前会话内稳定标识。
    /// PoC 阶段优先使用 ParsingName 或规范化路径；
    /// 若以上均不可用则退化为 DisplayName。
    /// </summary>
    string StableId,

    /// <summary>桌面显示名称。</summary>
    string DisplayName,

    /// <summary>
    /// Shell 解析名称（ParsingName）。
    /// 对于文件系统项目为完整路径；
    /// 对于系统项目为 Shell 命名空间路径（如 ::{...}）。
    /// 可能为 null（当 Shell 无法提供时）。
    /// </summary>
    string? ParsingName,

    /// <summary>图标项目种类。</summary>
    ItemKind ItemKind,

    /// <summary>图标在虚拟桌面物理像素坐标系中的位置。</summary>
    PixelPoint PixelPosition,

    /// <summary>图标所在显示器的 DeviceName；未归属时为 null。</summary>
    string? MonitorDeviceName,

    /// <summary>是否为系统项目（回收站、此电脑等）。</summary>
    bool IsSystemItem,

    /// <summary>是否为文件系统项目。</summary>
    bool IsFileSystemItem)
{
    /// <summary>
    /// 用于 DataGrid 显示的坐标文本。
    /// </summary>
    public string PositionText => $"({PixelPosition.X}, {PixelPosition.Y})";
}
