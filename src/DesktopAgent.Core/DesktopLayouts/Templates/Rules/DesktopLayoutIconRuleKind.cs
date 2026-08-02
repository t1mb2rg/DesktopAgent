namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 桌面图标用户规则类型。
/// </summary>
public enum DesktopLayoutIconRuleKind
{
    /// <summary>图标不参与模板自动布局，未来执行时保持当前位置。</summary>
    Exclude = 0,

    /// <summary>图标由模板管理但锁定当前像素位置，不产生移动目标。</summary>
    Pin = 1,

    /// <summary>图标仍参与自动布局，强制分配到指定 Zone。</summary>
    ZoneOverride = 2
}
