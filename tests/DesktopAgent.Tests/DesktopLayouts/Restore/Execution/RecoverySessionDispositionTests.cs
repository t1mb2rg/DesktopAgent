using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoverySessionDispositionTests
{
    [Theory]
    [InlineData(
        RecoverySessionLifecycle.Ready,
        RecoverySessionHandling.None,
        RecoverySessionSafetyClass.None,
        false,
        false,
        false)]
    [InlineData(
        RecoverySessionLifecycle.Executing,
        RecoverySessionHandling.AutomaticReconciliation,
        RecoverySessionSafetyClass.None,
        true,
        false,
        false)]
    [InlineData(
        RecoverySessionLifecycle.Completed,
        RecoverySessionHandling.None,
        RecoverySessionSafetyClass.None,
        false,
        false,
        false)]
    [InlineData(
        RecoverySessionLifecycle.FailedAndRolledBack,
        RecoverySessionHandling.None,
        RecoverySessionSafetyClass.None,
        false,
        false,
        false)]
    [InlineData(
        RecoverySessionLifecycle.FailedRollbackIncomplete,
        RecoverySessionHandling.ManualIntervention,
        RecoverySessionSafetyClass.KnownIncomplete,
        true,
        true,
        false)]
    [InlineData(
        RecoverySessionLifecycle.PositionUncertain,
        RecoverySessionHandling.ManualIntervention,
        RecoverySessionSafetyClass.PositionUncertain,
        true,
        true,
        true)]
    public void FromLifecycle_ProjectsOrthogonalHandlingAndSafety(
        RecoverySessionLifecycle lifecycle,
        RecoverySessionHandling expectedHandling,
        RecoverySessionSafetyClass expectedSafetyClass,
        bool expectedUnresolved,
        bool expectedManual,
        bool expectedPositionUncertain)
    {
        var disposition = RecoverySessionDisposition.FromLifecycle(lifecycle);

        Assert.Equal(expectedHandling, disposition.Handling);
        Assert.Equal(expectedSafetyClass, disposition.SafetyClass);
        Assert.Equal(expectedUnresolved, disposition.IsUnresolved);
        Assert.Equal(expectedManual, disposition.RequiresManualRecovery);
        Assert.Equal(expectedPositionUncertain, disposition.IsPositionUncertain);
    }

    [Fact]
    public void Executing_IsAutomaticReconciliation_NotManualAttention()
    {
        var disposition = RecoverySessionDisposition.FromLifecycle(
            RecoverySessionLifecycle.Executing);

        Assert.True(disposition.IsUnresolved);
        Assert.Equal(RecoverySessionHandling.AutomaticReconciliation, disposition.Handling);
        Assert.False(disposition.RequiresManualRecovery);
        Assert.Equal(RecoverySessionSafetyClass.None, disposition.SafetyClass);
    }

    [Fact]
    public void FailedRollbackIncomplete_IsKnownIncomplete_NotPositionUncertain()
    {
        var disposition = RecoverySessionDisposition.FromLifecycle(
            RecoverySessionLifecycle.FailedRollbackIncomplete);

        Assert.True(disposition.RequiresManualRecovery);
        Assert.Equal(RecoverySessionSafetyClass.KnownIncomplete, disposition.SafetyClass);
        Assert.False(disposition.IsPositionUncertain);
    }

    [Fact]
    public void PositionUncertain_IsPersistedSafetyClassification_NotAutomaticRecovery()
    {
        var disposition = RecoverySessionDisposition.FromLifecycle(
            RecoverySessionLifecycle.PositionUncertain);

        Assert.Equal(RecoverySessionHandling.ManualIntervention, disposition.Handling);
        Assert.Equal(RecoverySessionSafetyClass.PositionUncertain, disposition.SafetyClass);
        Assert.True(disposition.IsPositionUncertain);
    }

    [Fact]
    public void UnknownLifecycle_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecoverySessionDisposition.FromLifecycle((RecoverySessionLifecycle)999));
    }
}
