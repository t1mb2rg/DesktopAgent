using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts;

/// <summary>
/// 快照中的单个图标项目。不包含 PIDL、COM、HWND 或 WPF 类型。
/// 使用显式构造函数执行验证，不使用 primary constructor。
/// </summary>
public sealed record DesktopIconSnapshotItem
{
    public int Ordinal { get; }
    public string StableId { get; }
    public string DisplayName { get; }
    public string? ParsingName { get; }
    public ItemKind ItemKind { get; }
    public PixelPoint PixelPosition { get; }
    public string? MonitorDeviceName { get; }
    public bool IsSystemItem { get; }
    public bool IsFileSystemItem { get; }

    public DesktopIconSnapshotItem(
        int ordinal,
        string stableId,
        string displayName,
        string? parsingName,
        ItemKind itemKind,
        PixelPoint pixelPosition,
        string? monitorDeviceName,
        bool isSystemItem,
        bool isFileSystemItem)
    {
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), "Ordinal 不能为负数。");
        Ordinal = ordinal;
        StableId = stableId ?? throw new ArgumentNullException(nameof(stableId));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        ParsingName = parsingName;
        ItemKind = itemKind;
        PixelPosition = pixelPosition;
        MonitorDeviceName = monitorDeviceName;
        IsSystemItem = isSystemItem;
        IsFileSystemItem = isFileSystemItem;
    }
}
