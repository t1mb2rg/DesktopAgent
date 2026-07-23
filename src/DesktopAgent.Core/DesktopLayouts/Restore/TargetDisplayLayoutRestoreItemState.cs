namespace DesktopAgent.Core.DesktopLayouts.Restore;

/// <summary>
/// 恢复计划中单个图标的状态。
/// </summary>
public enum TargetDisplayLayoutRestoreItemState
{
    /// <summary>图标当前已在快照目标位置，无需移动。</summary>
    AlreadyAtTarget,

    /// <summary>图标需要移动到快照目标位置。</summary>
    MoveRequired,

    /// <summary>快照中的 StableId 在当前桌面中不存在。</summary>
    MissingFromCurrent,

    /// <summary>快照中的 StableId 在当前桌面中有多个匹配（阻断）。</summary>
    DuplicateCurrentStableId,

    /// <summary>当前图标不是文件系统项目（阻断）。</summary>
    NotFileSystemItem,

    /// <summary>当前图标是系统项目（阻断）。</summary>
    SystemItem,

    /// <summary>当前图标没有显示器归属（阻断）。</summary>
    NoMonitorDeviceName,

    /// <summary>快照目标坐标超出当前工作区（阻断）。</summary>
    TargetOutsideCurrentWorkArea,

    /// <summary>快照目标集合中两个不同 StableId 指向同一坐标（阻断）。</summary>
    DuplicateSnapshotTargetPosition,

    /// <summary>目标位置被不可移动的静态图标占用（阻断）。</summary>
    BlockedByStationaryIcon,

    /// <summary>快照目标集合内 StableId 重复（阻断）。</summary>
    SnapshotStableIdDuplicate,
}
