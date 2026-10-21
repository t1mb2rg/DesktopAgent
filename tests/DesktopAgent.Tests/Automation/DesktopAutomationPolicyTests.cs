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
            hasExecutableDecision: false,
            requiresUserResolution: true,
            canDefer: false,
            isSafetyBlocked: true,
            isLayoutEditing: true);

        Assert.Equal(DesktopAutomationDisposition.Block, safety.Disposition);
        Assert.Equal(DesktopAutomationPolicyReason.SafetyBlocked, safety.Reason);

        var layoutEditing = Resolve(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.DirectUserRequest,
            hasExecutableDecision: false,
            requiresUserResolution: true,
            canDefer: false,
            isLayoutEditing: true);

        Assert.Equal(DesktopAutomationDisposition.Block, layoutEditing.Disposition);
        Assert.Equal(DesktopAutomationPolicyReason.LayoutEditing, layoutEditing.Reason);
    }

    [Fact]
    public void Resolve_DirectUserRequestIsAutoInEveryModeButCannotBypassResolution()
    {
        foreach (var mode in Enum.GetValues<DesktopAutomationMode>())
        {
            var result = Resolve(
                mode,
                DesktopAutomationAuthorizationBasis.DirectUserRequest);

            Assert.Equal(DesktopAutomationDisposition.Auto, result.Disposition);
            Assert.Equal(DesktopAutomationPolicyReason.DirectUserRequest, result.Reason);
        }

        var immediateConflict = Resolve(
            DesktopAutomationMode.Manual,
            DesktopAutomationAuthorizationBasis.DirectUserRequest,
            requiresUserResolution: true,
            canDefer: false);

        Assert.Equal(DesktopAutomationDisposition.Ask, immediateConflict.Disposition);
        Assert.Equal(
            DesktopAutomationPolicyReason.UserResolutionRequired,
            immediateConflict.Reason);
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
    public void Resolve_DefaultModeNoAuthorizationAndInvalidEnumsFollowContract()
    {
        Assert.Equal(
            DesktopAutomationMode.Assisted,
            DesktopAutomationPolicy.DefaultMode);

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

    [Fact]
    public void Resolve_EvidenceNotMeasured_NeverReachesAutoForUnattendedBases()
    {
        foreach (var evidence in new[]
                 {
                     DesktopGeometryEvidence.Inferred,
                     DesktopGeometryEvidence.Unknown,
                 })
        {
            Assert.Equal(
                new DesktopAutomationPolicyDecision(
                    DesktopAutomationDisposition.Queue,
                    DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured),
                Resolve(
                    DesktopAutomationMode.Automatic,
                    DesktopAutomationAuthorizationBasis.DeterministicLocal,
                    geometryEvidence: evidence));

            Assert.Equal(
                new DesktopAutomationPolicyDecision(
                    DesktopAutomationDisposition.Queue,
                    DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured),
                Resolve(
                    DesktopAutomationMode.Assisted,
                    DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
                    geometryEvidence: evidence));

            Assert.Equal(
                new DesktopAutomationPolicyDecision(
                    DesktopAutomationDisposition.Queue,
                    DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured),
                Resolve(
                    DesktopAutomationMode.Automatic,
                    DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
                    geometryEvidence: evidence));
        }
    }

    [Fact]
    public void Resolve_MeasuredEvidence_KeepsTheAutomaticMatrixUnchanged()
    {
        Assert.Equal(
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Auto,
                DesktopAutomationPolicyReason.DeterministicAutomaticPolicy),
            Resolve(
                DesktopAutomationMode.Automatic,
                DesktopAutomationAuthorizationBasis.DeterministicLocal,
                geometryEvidence: DesktopGeometryEvidence.Measured));

        Assert.Equal(
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Auto,
                DesktopAutomationPolicyReason.ConfirmedUserAuthorization),
            Resolve(
                DesktopAutomationMode.Assisted,
                DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
                geometryEvidence: DesktopGeometryEvidence.Measured));
    }

    /// <summary>
    /// 这是一条刻意保留的边界：DirectUserRequest 是"用户此刻亲自授权"，
    /// 不是"推断在自我授权"。因此它不受网格实测门槛限制。
    /// 若产品决定连显式请求也必须实测，这条断言必须一起改。
    /// </summary>
    [Fact]
    public void Resolve_DirectUserRequest_IsAuthorizedByTheUserNotByTheGeometryEvidence()
    {
        Assert.Equal(
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Auto,
                DesktopAutomationPolicyReason.DirectUserRequest),
            Resolve(
                DesktopAutomationMode.Automatic,
                DesktopAutomationAuthorizationBasis.DirectUserRequest,
                geometryEvidence: DesktopGeometryEvidence.Inferred));
    }

    [Fact]
    public void Resolve_OtherBases_KeepTheirOwnReasonWhenEvidenceIsNotMeasured()
    {
        Assert.Equal(
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Queue,
                DesktopAutomationPolicyReason.AiOnlyUnconfirmed),
            Resolve(
                DesktopAutomationMode.Automatic,
                DesktopAutomationAuthorizationBasis.AiOnly,
                geometryEvidence: DesktopGeometryEvidence.Inferred));

        Assert.Equal(
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Queue,
                DesktopAutomationPolicyReason.NoExecutionAuthorization),
            Resolve(
                DesktopAutomationMode.Automatic,
                DesktopAutomationAuthorizationBasis.None,
                geometryEvidence: DesktopGeometryEvidence.Inferred));
    }

    [Fact]
    public void Resolve_InvalidGeometryEvidence_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.DeterministicLocal,
            geometryEvidence: (DesktopGeometryEvidence)999));
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

    /// <summary>
    /// 既有授权矩阵描述的是"网格证据可用（实测）"时的行为；
    /// 证据不足时的行为由 Resolve_EvidenceNotMeasured... 系列单独覆盖。
    /// </summary>
    private static DesktopAutomationPolicyDecision Resolve(
        DesktopAutomationMode mode,
        DesktopAutomationAuthorizationBasis authorization,
        bool hasExecutableDecision = true,
        bool requiresUserResolution = false,
        bool canDefer = true,
        bool isLayoutEditing = false,
        bool isSafetyBlocked = false,
        DesktopGeometryEvidence geometryEvidence = DesktopGeometryEvidence.Measured)
        => DesktopAutomationPolicy.Resolve(
            new DesktopAutomationPolicyContext(
                mode,
                authorization,
                hasExecutableDecision,
                requiresUserResolution,
                canDefer,
                isLayoutEditing,
                isSafetyBlocked,
                geometryEvidence));
}
