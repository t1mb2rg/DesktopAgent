namespace DesktopAgent.Core.Automation;

public enum DesktopAutomationMode
{
    Manual = 0,
    Assisted = 1,
    Automatic = 2,
}

public enum DesktopAutomationDisposition
{
    Auto = 0,
    Queue = 1,
    Ask = 2,
    Block = 3,
}

public enum DesktopAutomationAuthorizationBasis
{
    None = 0,
    AiOnly = 1,
    DeterministicLocal = 2,
    ConfirmedUserPolicy = 3,
    DirectUserRequest = 4,
}

public enum DesktopAutomationPolicyReason
{
    SafetyBlocked = 0,
    LayoutEditing = 1,

    NoExecutableDecision = 10,
    UserResolutionDeferred = 11,
    UserResolutionRequired = 12,

    AiOnlyUnconfirmed = 20,
    NoExecutionAuthorization = 21,
    ManualMode = 22,
    AssistedRequiresConfirmedPolicy = 23,

    DirectUserRequest = 30,
    ConfirmedUserAuthorization = 31,
    DeterministicAutomaticPolicy = 32,
}

public sealed record DesktopAutomationPolicyContext(
    DesktopAutomationMode Mode,
    DesktopAutomationAuthorizationBasis AuthorizationBasis,
    bool HasExecutableDecision,
    bool RequiresUserResolution,
    bool CanDefer,
    bool IsLayoutEditing,
    bool IsSafetyBlocked);

public sealed record DesktopAutomationPolicyDecision(
    DesktopAutomationDisposition Disposition,
    DesktopAutomationPolicyReason Reason);

/// <summary>
/// M5-01 自动化策略边界。
///
/// 本 Policy 不负责分类、不生成 WriterCommand，也不执行任何物理桌面操作；
/// 它只回答：对于一个已经形成的候选动作，当前允许 Auto / Queue / Ask / Block 中的哪一种处理。
/// </summary>
public static class DesktopAutomationPolicy
{
    /// <summary>
    /// v1.0 产品默认自动化模式。
    /// Settings / first-run 后续必须显式使用该合同，而不是依赖 enum 的零值。
    /// </summary>
    public const DesktopAutomationMode DefaultMode = DesktopAutomationMode.Assisted;

    public static DesktopAutomationPolicyDecision Resolve(
        DesktopAutomationPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Enum.IsDefined(context.Mode))
            throw new ArgumentOutOfRangeException(nameof(context), "Automation Mode 无效。");

        if (!Enum.IsDefined(context.AuthorizationBasis))
            throw new ArgumentOutOfRangeException(nameof(context), "Authorization Basis 无效。");

        // 硬安全边界永远优先，任何模式或普通确认都不能绕过。
        if (context.IsSafetyBlocked)
        {
            return new(
                DesktopAutomationDisposition.Block,
                DesktopAutomationPolicyReason.SafetyBlocked);
        }

        // 布局编辑期间冻结自动化，避免已提交布局语义与 LayoutDraft 并发执行。
        if (context.IsLayoutEditing)
        {
            return new(
                DesktopAutomationDisposition.Block,
                DesktopAutomationPolicyReason.LayoutEditing);
        }

        // 连候选执行决定都没有时，不能进入 Auto。
        // 若当前交互必须立即得到答案则 Ask，否则进入 Queue 等待后续处理。
        if (!context.HasExecutableDecision)
        {
            if (context.RequiresUserResolution && !context.CanDefer)
            {
                return new(
                    DesktopAutomationDisposition.Ask,
                    DesktopAutomationPolicyReason.UserResolutionRequired);
            }

            return new(
                DesktopAutomationDisposition.Queue,
                DesktopAutomationPolicyReason.NoExecutableDecision);
        }

        // 已有候选决定，但仍存在需要用户解决的问题。
        if (context.RequiresUserResolution)
        {
            return context.CanDefer
                ? new(
                    DesktopAutomationDisposition.Queue,
                    DesktopAutomationPolicyReason.UserResolutionDeferred)
                : new(
                    DesktopAutomationDisposition.Ask,
                    DesktopAutomationPolicyReason.UserResolutionRequired);
        }

        // 当前用户明确发起的动作已经获得本次执行授权，
        // 不应再因 Automation Mode 产生重复确认。
        if (context.AuthorizationBasis ==
            DesktopAutomationAuthorizationBasis.DirectUserRequest)
        {
            return new(
                DesktopAutomationDisposition.Auto,
                DesktopAutomationPolicyReason.DirectUserRequest);
        }

        // 用户此前确认过的长期策略：
        // Manual 仍不允许后台自动执行；Assisted / Automatic 可以直接通过。
        if (context.AuthorizationBasis ==
            DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy)
        {
            return context.Mode == DesktopAutomationMode.Manual
                ? new(
                    DesktopAutomationDisposition.Queue,
                    DesktopAutomationPolicyReason.ManualMode)
                : new(
                    DesktopAutomationDisposition.Auto,
                    DesktopAutomationPolicyReason.ConfirmedUserAuthorization);
        }

        // 本地确定性策略只有 Automatic 模式获得后台执行权限。
        if (context.AuthorizationBasis ==
            DesktopAutomationAuthorizationBasis.DeterministicLocal)
        {
            return context.Mode switch
            {
                DesktopAutomationMode.Automatic => new(
                    DesktopAutomationDisposition.Auto,
                    DesktopAutomationPolicyReason.DeterministicAutomaticPolicy),

                DesktopAutomationMode.Assisted => new(
                    DesktopAutomationDisposition.Queue,
                    DesktopAutomationPolicyReason.AssistedRequiresConfirmedPolicy),

                DesktopAutomationMode.Manual => new(
                    DesktopAutomationDisposition.Queue,
                    DesktopAutomationPolicyReason.ManualMode),

                _ => throw new ArgumentOutOfRangeException(nameof(context)),
            };
        }

        // AI-only 永远不能因为 Automatic 模式而获得 Writer authority。
        if (context.AuthorizationBasis ==
            DesktopAutomationAuthorizationBasis.AiOnly)
        {
            return new(
                DesktopAutomationDisposition.Queue,
                DesktopAutomationPolicyReason.AiOnlyUnconfirmed);
        }

        // 有候选动作，但不存在任何执行授权来源。
        return new(
            DesktopAutomationDisposition.Queue,
            DesktopAutomationPolicyReason.NoExecutionAuthorization);
    }
}
