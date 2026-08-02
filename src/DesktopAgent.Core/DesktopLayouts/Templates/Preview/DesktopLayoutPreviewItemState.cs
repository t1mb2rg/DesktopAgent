namespace DesktopAgent.Core.DesktopLayouts.Templates.Preview;

/// <summary>
/// Preview Item 状态。
/// </summary>
public enum DesktopLayoutPreviewItemState
{
    /// <summary>已分配且当前位置等于目标位置。</summary>
    AlreadyAtTarget = 0,

    /// <summary>已分配且当前位置不等于目标位置。</summary>
    MoveRequired = 1,

    /// <summary>未分配（容量不足）。</summary>
    Unassigned = 2
}
