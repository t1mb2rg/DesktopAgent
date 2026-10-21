using DesktopAgent.Core.Automation;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Automation;

public sealed class DesktopPendingActionContractTests
{
    [Fact]
    public void FromQueuedPolicyDecision_AcceptsQueueAndRejectsOtherDispositions()
    {
        var action = DesktopPendingAction.FromQueuedPolicyDecision(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            Item(),
            Decision(),
            DesktopAutomationAuthorizationBasis.AiOnly,
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Queue,
                DesktopAutomationPolicyReason.AiOnlyUnconfirmed));

        Assert.Equal(DesktopAutomationPolicyReason.AiOnlyUnconfirmed, action.QueueReason);

        Assert.Throws<ArgumentException>(() => DesktopPendingAction.FromQueuedPolicyDecision(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            Item(),
            Decision(),
            DesktopAutomationAuthorizationBasis.DirectUserRequest,
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Auto,
                DesktopAutomationPolicyReason.DirectUserRequest)));
    }

    [Fact]
    public void Constructor_NoExecutableDecisionRequiresNullDecision()
    {
        var action = new DesktopPendingAction(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            Item(),
            decision: null,
            DesktopAutomationAuthorizationBasis.None,
            DesktopAutomationPolicyReason.NoExecutableDecision);

        Assert.Null(action.Decision);

        Assert.Throws<ArgumentException>(() => new DesktopPendingAction(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            Item(),
            Decision(),
            DesktopAutomationAuthorizationBasis.None,
            DesktopAutomationPolicyReason.NoExecutableDecision));
    }

    [Fact]
    public void Constructor_ExecutableQueueReasonRequiresDecisionAndZoneRequiresTemplate()
    {
        Assert.Throws<ArgumentException>(() => new DesktopPendingAction(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            Item(),
            decision: null,
            DesktopAutomationAuthorizationBasis.AiOnly,
            DesktopAutomationPolicyReason.AiOnlyUnconfirmed));

        Assert.Throws<ArgumentException>(() => new DesktopPendingAction(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            templateId: null,
            Item(),
            Decision(Guid.NewGuid()),
            DesktopAutomationAuthorizationBasis.AiOnly,
            DesktopAutomationPolicyReason.AiOnlyUnconfirmed));
    }

    [Fact]
    public void Resolution_SeparatesOneShotAuthorityFromRememberedPolicy()
    {
        var action = Action();
        var once = new DesktopPendingActionResolution(
            action,
            DesktopPendingActionResolutionKind.ConfirmOnce,
            DesktopAutomationAuthorizationBasis.DirectUserRequest);

        Assert.Null(once.RememberedRuleId);
        Assert.Null(once.RememberedPreferenceId);

        var ruleId = Guid.NewGuid();
        var remembered = new DesktopPendingActionResolution(
            action,
            DesktopPendingActionResolutionKind.ConfirmAndRemember,
            DesktopAutomationAuthorizationBasis.DirectUserRequest,
            rememberedRuleId: ruleId);

        Assert.Equal(ruleId, remembered.RememberedRuleId);

        Assert.Throws<ArgumentException>(() => new DesktopPendingActionResolution(
            action,
            DesktopPendingActionResolutionKind.ConfirmAndRemember,
            DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
            rememberedRuleId: ruleId));
    }

    private static DesktopPendingAction Action()
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            Item(),
            Decision(),
            DesktopAutomationAuthorizationBasis.AiOnly,
            DesktopAutomationPolicyReason.AiOnlyUnconfirmed);

    private static DesktopItemFacts Item()
        => new("desktop:demo", "demo.pdf", ItemKind.File, ".pdf", false, true);

    private static DesktopItemClassificationDecision Decision(Guid? zoneId = null)
        => new("Course", zoneId, 0.8, DesktopItemClassificationSource.AiProvider, "AI suggestion");
}
