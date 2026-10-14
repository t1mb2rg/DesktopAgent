using DesktopAgent.Core.Automation;

namespace DesktopAgent.Tests.Automation;

public sealed class DesktopAutomationPolicyEvidenceGateTests
{
    [Fact]
    public void Context_OmittingGeometryEvidence_DefaultsToUnknown_AndReachesResolveAsUnknown()
    {
        // 七参数路径（省略第八个参数）此前没有覆盖。默认值必须是 Unknown（fail closed），
        // 不能是 Measured：调用方没提供证据时，策略不能替它假定"实测"。
        var context = new DesktopAutomationPolicyContext(
            DesktopAutomationMode.Automatic,
            DesktopAutomationAuthorizationBasis.DeterministicLocal,
            true,   // HasExecutableDecision
            false,  // RequiresUserResolution
            true,   // CanDefer
            false,  // IsLayoutEditing
            false); // IsSafetyBlocked —— 这里刻意省略 GeometryEvidence

        Assert.Equal(DesktopGeometryEvidence.Unknown, context.GeometryEvidence);

        var decision = DesktopAutomationPolicy.Resolve(context);
        Assert.Equal(DesktopAutomationDisposition.Queue, decision.Disposition);
        Assert.Equal(DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured, decision.Reason);
    }

    [Fact]
    public void UnknownEvidence_NeverReachesAuto()
    {
        Assert.Equal(
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Queue,
                DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured),
            Resolve(
                DesktopAutomationMode.Automatic,
                DesktopAutomationAuthorizationBasis.DeterministicLocal,
                DesktopGeometryEvidence.Unknown));

        Assert.Equal(
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Queue,
                DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured),
            Resolve(
                DesktopAutomationMode.Assisted,
                DesktopAutomationAuthorizationBasis.ConfirmedUserPolicy,
                DesktopGeometryEvidence.Unknown));

        // AI-only 与无授权来源本来就不进 Auto：保留各自真实原因，不伪装成几何证据问题。
        Assert.Equal(
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Queue,
                DesktopAutomationPolicyReason.AiOnlyUnconfirmed),
            Resolve(
                DesktopAutomationMode.Automatic,
                DesktopAutomationAuthorizationBasis.AiOnly,
                DesktopGeometryEvidence.Unknown));

        Assert.Equal(
            new DesktopAutomationPolicyDecision(
                DesktopAutomationDisposition.Queue,
                DesktopAutomationPolicyReason.NoExecutionAuthorization),
            Resolve(
                DesktopAutomationMode.Automatic,
                DesktopAutomationAuthorizationBasis.None,
                DesktopGeometryEvidence.Unknown));
    }

    [Fact]
    public void DirectUserRequest_WithUnknownEvidence_StillReachesAuto_DocumentedProductDecision()
    {
        // 已签字确认的产品决定：用户本次明确发起的动作已经获得执行授权，
        // 不因 Automation Mode 产生重复确认，所以这条豁免与几何证据等级无关。
        // 若将来产品决定"连明确的用户请求也必须有实测几何"，这条断言必须随之改变。
        var decision = Resolve(
            DesktopAutomationMode.Manual,
            DesktopAutomationAuthorizationBasis.DirectUserRequest,
            DesktopGeometryEvidence.Unknown);

        Assert.Equal(DesktopAutomationDisposition.Auto, decision.Disposition);
        Assert.Equal(DesktopAutomationPolicyReason.DirectUserRequest, decision.Reason);
    }

    [Fact]
    public void Resolve_OnlyMeasuredEvidenceOrDirectUserRequestMayReachAuto()
    {
        // 全组合性质：Auto 只可能来自实测证据，或来自用户本次明确发起的请求。
        var autoCount = 0;

        foreach (var evidence in Enum.GetValues<DesktopGeometryEvidence>())
        {
            foreach (var basis in Enum.GetValues<DesktopAutomationAuthorizationBasis>())
            {
                foreach (var mode in Enum.GetValues<DesktopAutomationMode>())
                {
                    var decision = Resolve(mode, basis, evidence);
                    if (decision.Disposition != DesktopAutomationDisposition.Auto)
                        continue;

                    autoCount++;
                    var allowed = evidence == DesktopGeometryEvidence.Measured ||
                        basis == DesktopAutomationAuthorizationBasis.DirectUserRequest;

                    Assert.True(
                        allowed,
                        $"Auto 出现在不允许的组合：evidence={evidence}, basis={basis}, mode={mode}, reason={decision.Reason}。");
                }
            }
        }

        // 防止这条性质在"没有任何组合能进 Auto"时变成空转。
        Assert.True(autoCount > 0, "全组合遍历没有覆盖到任何 Auto 结果，性质检查等于没有运行。");
    }

    private static DesktopAutomationPolicyDecision Resolve(
        DesktopAutomationMode mode,
        DesktopAutomationAuthorizationBasis authorization,
        DesktopGeometryEvidence geometryEvidence,
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
                isSafetyBlocked,
                geometryEvidence));
}
