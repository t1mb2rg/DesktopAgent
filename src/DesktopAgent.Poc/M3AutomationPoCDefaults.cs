using DesktopAgent.Application.DesktopAutomation;

namespace DesktopAgent.Poc;

public static class M3AutomationPoCDefaults
{
    public static DesktopAutomationRuntimePolicySettings RuntimeSettings { get; } =
        new(
            executionCooldown: TimeSpan.FromSeconds(30),
            fuseFailureThreshold: 3);

    public static DesktopAutomationCandidateRiskPolicySettings RiskSettings { get; } =
        new(
            maxMoveCount: 5,
            maxExecutionStepCount: 10,
            allowTemporaryStaging: false);

    /// <summary>
    /// 普通 PoC composition root 的 unattended gate 永远保持 provisional closed。
    /// 未来真实 M3-B acceptance 必须使用独立、显式的 acceptance composition，
    /// 不允许悄悄修改这个默认值来“临时测试一下”。
    /// </summary>
    public static DesktopAutomationRuntimePolicy CreateRuntimePolicy(
        TimeProvider timeProvider) =>
        new(
            RuntimeSettings,
            DesktopAutomationUnattendedGate.M3AProvisionalClosed,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
}
