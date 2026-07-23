namespace DesktopAgent.Core.DesktopLayouts.Restore;

/// <summary>
/// 计划级问题的类型代码。
/// </summary>
public enum TargetDisplayLayoutRestorePlanIssueCode
{
    /// <summary>当前桌面中存在重复的 StableId（计划级阻断）。</summary>
    DuplicateCurrentStableId,

    /// <summary>当前图标坐标构建占用矩形时发生 checked 算术溢出（计划级阻断）。</summary>
    CurrentRectangleOverflow,
}
