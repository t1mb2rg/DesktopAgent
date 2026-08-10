namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// M4 分类结论或建议的语义来源。
/// 枚举数值不表达优先级；最终优先级由后续 Policy 明确定义。
/// </summary>
public enum DesktopItemClassificationSource
{
    /// <summary>用户明确创建或确认的规则。</summary>
    UserRule,

    /// <summary>DesktopAgent 本地确定性规则。</summary>
    LocalRule,

    /// <summary>DesktopAgent 本地 User Profile / 已确认偏好。</summary>
    UserProfile,

    /// <summary>外部或本地 AI Provider 的语义建议。</summary>
    AiProvider,
}
