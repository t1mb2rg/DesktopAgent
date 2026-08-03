namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 规则感知分配来源：区分 Override 优先分配和默认分配。
/// </summary>
public enum DesktopLayoutRuleAwareAssignmentSource
{
    /// <summary>ZoneOverride 规则驱动的优先分配。</summary>
    Override = 0,

    /// <summary>无规则默认图标的剩余槽位分配。</summary>
    Default = 1
}
