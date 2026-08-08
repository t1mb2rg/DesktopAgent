using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Poc;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class M3AutomationPoCDefaultsTests
{
    [Fact]
    public void OrdinaryPoCRuntime_IsProvisionalClosedByConstruction()
    {
        var runtime = M3AutomationPoCDefaults.CreateRuntimePolicy(TimeProvider.System);
        var initial = runtime.Snapshot;

        Assert.False(initial.UnattendedGate.IsOpen);
        Assert.Equal(
            DesktopAutomationUnattendedGateReason.M2RealDualMonitorAcceptancePending,
            initial.UnattendedGate.Reason);
        Assert.Equal(DesktopAutomationMode.Suggestion, initial.RequestedMode);
        Assert.Equal(DesktopAutomationMode.Suggestion, initial.EffectiveMode);

        var requestedAutomatic = runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        Assert.Equal(DesktopAutomationMode.Automatic, requestedAutomatic.RequestedMode);
        Assert.Equal(DesktopAutomationMode.Suggestion, requestedAutomatic.EffectiveMode);
        Assert.False(requestedAutomatic.CanAttemptUnattendedExecution);
        Assert.Contains(
            DesktopAutomationBlockReason.UnattendedWriteGateClosed,
            requestedAutomatic.BlockReasons);
    }

    [Fact]
    public void OrdinaryPoCRiskDefaults_AreConservativeAndExplicit()
    {
        var settings = M3AutomationPoCDefaults.RiskSettings;

        Assert.Equal(5, settings.MaxMoveCount);
        Assert.Equal(10, settings.MaxExecutionStepCount);
        Assert.False(settings.AllowTemporaryStaging);
    }

    [Fact]
    public void OrdinaryPoCRuntimeDefaults_KeepCooldownAndFuseEnabled()
    {
        var settings = M3AutomationPoCDefaults.RuntimeSettings;

        Assert.Equal(TimeSpan.FromSeconds(30), settings.ExecutionCooldown);
        Assert.Equal(3, settings.FuseFailureThreshold);
    }
}
