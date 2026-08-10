using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Core.Automation;

/// <summary>
/// M5-02 中等待用户后续处理的一条桌面候选动作。
/// Pending Action 只保存语义候选与 Policy 依据，不包含物理坐标、WriterCommand 或 Shell 对象。
/// </summary>
public sealed class DesktopPendingAction
{
    public const int MaxExplanationLength = 2048;

    public Guid PendingActionId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public Guid? TemplateId { get; }
    public DesktopItemFacts Item { get; }
    public DesktopItemClassificationDecision? Decision { get; }
    public DesktopAutomationAuthorizationBasis AuthorizationBasis { get; }
    public DesktopAutomationPolicyReason QueueReason { get; }

    public DesktopPendingAction(
        Guid pendingActionId,
        DateTimeOffset createdAtUtc,
        Guid? templateId,
        DesktopItemFacts item,
        DesktopItemClassificationDecision? decision,
        DesktopAutomationAuthorizationBasis authorizationBasis,
        DesktopAutomationPolicyReason queueReason)
    {
        if (pendingActionId == Guid.Empty)
            throw new ArgumentException("PendingActionId 不能为 Guid.Empty。", nameof(pendingActionId));
        if (createdAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("CreatedAtUtc 必须使用 UTC。", nameof(createdAtUtc));
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为 Guid.Empty。", nameof(templateId));
        Item = item ?? throw new ArgumentNullException(nameof(item));
        if (!Enum.IsDefined(authorizationBasis))
            throw new ArgumentOutOfRangeException(nameof(authorizationBasis));
        if (!IsQueueReason(queueReason))
            throw new ArgumentException($"{queueReason} 不是有效的 Queue reason。", nameof(queueReason));

        if (queueReason == DesktopAutomationPolicyReason.NoExecutableDecision)
        {
            if (decision is not null)
                throw new ArgumentException("NoExecutableDecision 的 Pending Action 不应携带 Decision。", nameof(decision));
        }
        else if (decision is null)
        {
            throw new ArgumentException("除 NoExecutableDecision 外的 Pending Action 必须携带 Decision。", nameof(decision));
        }

        if (decision?.TargetZoneId.HasValue == true && !templateId.HasValue)
            throw new ArgumentException("Decision 包含 TargetZoneId 时必须同时记录 TemplateId。", nameof(templateId));
        if (decision is not null && decision.Explanation.Length > MaxExplanationLength)
            throw new ArgumentOutOfRangeException(nameof(decision), $"Decision Explanation 最多 {MaxExplanationLength} 字符。");

        PendingActionId = pendingActionId;
        CreatedAtUtc = createdAtUtc;
        TemplateId = templateId;
        Decision = decision;
        AuthorizationBasis = authorizationBasis;
        QueueReason = queueReason;
    }

    public static DesktopPendingAction FromQueuedPolicyDecision(
        Guid pendingActionId,
        DateTimeOffset createdAtUtc,
        Guid? templateId,
        DesktopItemFacts item,
        DesktopItemClassificationDecision? decision,
        DesktopAutomationAuthorizationBasis authorizationBasis,
        DesktopAutomationPolicyDecision policyDecision)
    {
        ArgumentNullException.ThrowIfNull(policyDecision);
        if (policyDecision.Disposition != DesktopAutomationDisposition.Queue)
        {
            throw new ArgumentException(
                $"只有 Queue disposition 可以进入 Pending Queue，实际为 {policyDecision.Disposition}。",
                nameof(policyDecision));
        }

        return new DesktopPendingAction(
            pendingActionId,
            createdAtUtc,
            templateId,
            item,
            decision,
            authorizationBasis,
            policyDecision.Reason);
    }

    private static bool IsQueueReason(DesktopAutomationPolicyReason reason)
        => reason is
            DesktopAutomationPolicyReason.NoExecutableDecision or
            DesktopAutomationPolicyReason.UserResolutionDeferred or
            DesktopAutomationPolicyReason.AiOnlyUnconfirmed or
            DesktopAutomationPolicyReason.NoExecutionAuthorization or
            DesktopAutomationPolicyReason.ManualMode or
            DesktopAutomationPolicyReason.AssistedRequiresConfirmedPolicy;
}

public enum DesktopPendingActionResolutionKind
{
    ConfirmOnce = 0,
    ConfirmAndRemember = 1,
    Reject = 2,
}

/// <summary>
/// Confirm & Remember 必须由调用方显式选择记忆范围。
/// DesktopAgent 不会把一次确认偷偷泛化成更宽规则。
/// </summary>
public enum DesktopPendingActionRememberScope
{
    ExactItem = 0,
    FileExtension = 1,
    CategoryZonePreference = 2,
}

public sealed record DesktopPendingActionRememberDirective(
    DesktopPendingActionRememberScope Scope)
{
    public DesktopPendingActionRememberDirective Validate()
    {
        if (!Enum.IsDefined(Scope))
            throw new ArgumentOutOfRangeException(nameof(Scope));
        return this;
    }
}

/// <summary>
/// 用户处理 Pending Action 后交给后续执行层的语义结果。
/// 本结果仍不包含 WriterCommand；Confirm 只授予“这一次”的 DirectUserRequest authority。
/// </summary>
public sealed class DesktopPendingActionResolution
{
    public DesktopPendingAction Action { get; }
    public DesktopPendingActionResolutionKind Kind { get; }
    public DesktopAutomationAuthorizationBasis ExecutionAuthorizationBasis { get; }
    public Guid? RememberedRuleId { get; }
    public Guid? RememberedPreferenceId { get; }

    public DesktopPendingActionResolution(
        DesktopPendingAction action,
        DesktopPendingActionResolutionKind kind,
        DesktopAutomationAuthorizationBasis executionAuthorizationBasis,
        Guid? rememberedRuleId = null,
        Guid? rememberedPreferenceId = null)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(executionAuthorizationBasis))
            throw new ArgumentOutOfRangeException(nameof(executionAuthorizationBasis));
        if (rememberedRuleId == Guid.Empty)
            throw new ArgumentException("RememberedRuleId 不能为 Guid.Empty。", nameof(rememberedRuleId));
        if (rememberedPreferenceId == Guid.Empty)
            throw new ArgumentException("RememberedPreferenceId 不能为 Guid.Empty。", nameof(rememberedPreferenceId));

        switch (kind)
        {
            case DesktopPendingActionResolutionKind.ConfirmOnce:
                if (executionAuthorizationBasis != DesktopAutomationAuthorizationBasis.DirectUserRequest ||
                    rememberedRuleId.HasValue || rememberedPreferenceId.HasValue)
                {
                    throw new ArgumentException("ConfirmOnce 只授予本次 DirectUserRequest authority，且不应产生长期记忆。");
                }
                break;

            case DesktopPendingActionResolutionKind.ConfirmAndRemember:
                if (executionAuthorizationBasis != DesktopAutomationAuthorizationBasis.DirectUserRequest ||
                    rememberedRuleId.HasValue == rememberedPreferenceId.HasValue)
                {
                    throw new ArgumentException("ConfirmAndRemember 必须授予本次 DirectUserRequest authority，并且恰好产生一种长期记忆。");
                }
                break;

            case DesktopPendingActionResolutionKind.Reject:
                if (executionAuthorizationBasis != DesktopAutomationAuthorizationBasis.None ||
                    rememberedRuleId.HasValue || rememberedPreferenceId.HasValue)
                {
                    throw new ArgumentException("Reject 不授予执行 authority，也不应产生长期记忆。");
                }
                break;
        }

        Kind = kind;
        ExecutionAuthorizationBasis = executionAuthorizationBasis;
        RememberedRuleId = rememberedRuleId;
        RememberedPreferenceId = rememberedPreferenceId;
    }
}
