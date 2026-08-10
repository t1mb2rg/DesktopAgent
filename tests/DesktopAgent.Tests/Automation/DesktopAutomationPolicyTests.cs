using DesktopAgent.Core.Automation;

namespace DesktopAgent.Tests.Automation;

public sealed class DesktopAutomationPolicyTests
{
    [Fact]
    public void Resolve_HardBlocksAlwaysWin()
    {
        var safety = Resolve(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.DirectUserRequest,
            isSafetyBlocked: true,
            isLayoutEditing: true);

        Assert.Equal(DesktopAutomationDisposition.Block, safety.Disposition);
        Assert.Equal(DesktopAutomationPolicyReason.SafetyBlocked, safety.Reason);

        var layoutEditing = Resolve(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.DirectUserRequest,
            isLayoutEditing: true);

        Assert.Equal(DesktopAutomationDisposition.Block, layoutEditing.Disposition);
        Assert.Equal(DesktopAutomationPolicyReason.LayoutEditing, layoutEditing.Reason);
    }

    [Fact]
    public void Resolve_DirectUserRequestIsAutoInEveryMode()
    {
        foreach (var mode in Enum.GetValues<DesktopAutomationMode>())
        {
            var result = Resolve(
                mode,
                DesktopAutomationAuthorizationBasis.DirectUserRequest);

            Assert.Equal(DesktopAutomationDisposition.Auto, result.Disposition);
            Assert.Equal(DesktopAutomationPolicyReason.DirectUserRequest, result.Reason);
        }
    }

    [Fact]
    public void Resolve_AuthorizationMatrixMatchesAutomationModes()
    {
        AssertDisposition(
            DesktopAutomationMode.Manual,
            DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
            DesktopAutomationDisposition.Queue,
            DesktopAutomationPolicyReason.ManualMode);
        AssertDisposition(
            DesktopAutomationMode.Assisted,
            DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
            DesktopAutomationDisposition.Auto,
            DesktopAutomationPolicyReason.ConfirmedUserAuthorization);
        AssertDisposition(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
            DesktopAutomationDisposition.Auto,
            DesktopAutomationPolicyReason.ConfirmedUserAuthorization);

        AssertDisposition(
            DesktopAutomationMode.Manual,
            DesktopAutomationAuthorizationBasis.DeterministicLocal,
            DesktopAutomationDisposition.Queue,
            DesktopAutomationPolicyReason.ManualMode);
        AssertDisposition(
            DesktopAutomationMode.Assisted,
            DesktopAutomationAuthorizationBasis.DeterministicLocal,
            DesktopAutomationDisposition.Queue,
            DesktopAutomationPolicyReason.AssistedRequiresConfirmedPolicy);
        AssertDisposition(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.DeterministicLocal,
            DesktopAutomationDisposition.Auto,
            DesktopAutomationPolicyReason.DeterministicAutomaticPolicy);
    }

    [Fact]
    public void Resolve_AiOnlyNeverBecomesAutoAndResolutionUsesQueueOrAsk()
    {
        foreach (var mode in Enum.GetValues<DesktopAutomationMode>())
        {
            var aiOnly = Resolve(
                mode,
                DesktopAutomationAuthorizationBasis.AiOnly);

            Assert.Equal(DesktopAutomationDisposition.Queue, aiOnly.Disposition);
            Assert.Equal(DesktopAutomationPolicyReason.AiOnlyUnconfirmed, aiOnly.Reason);
        }

        var deferred = Resolve(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
            requiresUserResolution: true,
            canDefer: true);

        Assert.Equal(DesktopAutomationDisposition.Queue, deferred.Disposition);
        Assert.Equal(DesktopAutomationPolicyReason.UserResolutionDeferred, deferred.Reason);

        var immediate = Resolve(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
            requiresUserResolution: true,
            canDefer: false);

        Assert.Equal(DesktopAutomationDisposition.Ask, immediate.Disposition);
        Assert.Equal(DesktopAutomationPolicyReason.UserResolutionRequired, immediate.Reason);
    }

    [Fact]
    public void Resolve_NoExecutableDecisionNeverAutoExecutes()
    {
        foreach (var mode in Enum.GetValues<DesktopAutomationMode>())
        {
            foreach (var authorization in
                     Enum.GetValues<DesktopAutomationAuthorizationBasis>())
            {
                var result = Resolve(
                    mode,
                    authorization,
                    hasExecutableDecision: false);

                Assert.Equal(DesktopAutomationDisposition.Queue, result.Disposition);
                Assert.Equal(
                    DesktopAutomationPolicyReason.NoExecutableDecision,
                    result.Reason);
            }
        }

        var immediateMissingDecision = Resolve(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.DirectUserRequest,
            hasExecutableDecision: false,
            requiresUserResolution: true,
            canDefer: false);

        Assert.Equal(
            DesktopAutomationDisposition.Ask,
            immediateMissingDecision.Disposition);
        Assert.Equal(
            DesktopAutomationPolicyReason.UserResolutionRequired,
            immediateMissingDecision.Reason);
    }

    [Fact]
    public void Resolve_NoAuthorizationQueuesAndInvalidEnumsFailClosed()
    {
        foreach (var mode in Enum.GetValues<DesktopAutomationMode>())
        {
            var noAuthorization = Resolve(
                mode,
                DesktopAutomationAuthorizationBasis.None);

            Assert.Equal(DesktopAutomationDisposition.Queue, noAuthorization.Disposition);
            Assert.Equal(
                DesktopAutomationPolicyReason.NoExecutionAuthorization,
                noAuthorization.Reason);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(
            (DesktopAutomationMode)999,
            DesktopAutomationAuthorizationBasis.DirectUserRequest));
        Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(
            DesktopAutomationMode.Automatic,
            (DesktopAutomationAuthorizationBasis)999));
    }

    private static void AssertDisposition(
        DesktopAutomationMode mode,
        DesktopAutomationAuthorizationBasis authorization,
        DesktopAutomationDisposition expectedDisposition,
        DesktopAutomationPolicyReason expectedReason)
    {
        var result = Resolve(mode, authorization);
        Assert.Equal(expectedDisposition, result.Disposition);
        Assert.Equal(expectedReason, result.Reason);
    }

    private static DesktopAutomationPolicyDecision Resolve(
        DesktopAutomationMode mode,
        DesktopAutomationAuthorizationBasis authorization,
        bool hasExecutableDecision = true,
        bool requiresUserResolution = false,
        bool canDefer = true,
        bool isLayoutEditing = false,
        bool isSafetyBlocked = false)
        => DesktopAutomationPolicy.Resolve(
            new DesktopAutomationPolicyContext(
                mode,
                authorization,
                hasExecutableDecision,
                requiresUserResolution,
                canDefer,
                isLayoutEditing,
                isSafetyBlocked));
}
