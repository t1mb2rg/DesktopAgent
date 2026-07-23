using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore;

/// <summary>
/// 恢复计划中的单个图标项。纯数据，不含 PIDL/COM/HWND/WPF。
/// </summary>
public sealed record TargetDisplayLayoutRestorePlanItem
{
    public string StableId { get; init; }
    public string DisplayName { get; init; }
    public PixelPoint SnapshotTargetPosition { get; init; }
    public PixelPoint? CurrentPosition { get; init; }
    public string? CurrentMonitorDeviceName { get; init; }
    public TargetDisplayLayoutRestoreItemState State { get; init; }
    public bool IsFileSystemItem { get; init; }
    public bool IsSystemItem { get; init; }
    public string? BlockReason { get; init; }

    public TargetDisplayLayoutRestorePlanItem(
        string stableId,
        string displayName,
        PixelPoint snapshotTargetPosition,
        PixelPoint? currentPosition,
        string? currentMonitorDeviceName,
        TargetDisplayLayoutRestoreItemState state,
        bool isFileSystemItem,
        bool isSystemItem,
        string? blockReason = null)
    {
        StableId = stableId;
        DisplayName = displayName;
        SnapshotTargetPosition = snapshotTargetPosition;
        CurrentPosition = currentPosition;
        CurrentMonitorDeviceName = currentMonitorDeviceName;
        State = state;
        IsFileSystemItem = isFileSystemItem;
        IsSystemItem = isSystemItem;
        BlockReason = blockReason;
    }
}
