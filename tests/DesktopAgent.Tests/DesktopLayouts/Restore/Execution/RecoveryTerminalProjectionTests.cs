using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoveryTerminalProjectionTests
{
    [Theory]
    [InlineData(
        RecoverySessionLifecycle.Completed,
        false,
        TargetDisplayLayoutExecutionRunStatus.Completed,
        true,
        false,
        false,
        false)]
    [InlineData(
        RecoverySessionLifecycle.Completed,
        true,
        TargetDisplayLayoutExecutionRunStatus.CompletedNoOp,
        true,
        true,
        false,
        false)]
    [InlineData(
        RecoverySessionLifecycle.FailedAndRolledBack,
        false,
        TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack,
        false,
        false,
        false,
        false)]
    [InlineData(
        RecoverySessionLifecycle.FailedRollbackIncomplete,
        false,
        TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
        false,
        false,
        true,
        false)]
    [InlineData(
        RecoverySessionLifecycle.PositionUncertain,
        false,
        TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
        false,
        false,
        true,
        true)]
    public void FromLifecycle_ProjectsCompleteTerminalContract(
        RecoverySessionLifecycle lifecycle,
        bool isNoOp,
        TargetDisplayLayoutExecutionRunStatus expectedRunStatus,
        bool expectedSuccess,
        bool expectedNoOp,
        bool expectedManual,
        bool expectedUncertain)
    {
        var projection = RecoveryTerminalProjection.FromLifecycle(lifecycle, isNoOp);

        Assert.Equal(lifecycle, projection.Lifecycle);
        Assert.Equal(expectedRunStatus, projection.RunStatus);
        Assert.Equal(expectedSuccess, projection.IsSuccess);
        Assert.Equal(expectedNoOp, projection.IsNoOp);
        Assert.Equal(expectedManual, projection.RequiresManualRecovery);
        Assert.Equal(expectedUncertain, projection.IsPositionUncertain);
        Assert.Equal(RecoverySessionDisposition.FromLifecycle(lifecycle), projection.Disposition);
    }

    [Theory]
    [InlineData(RecoverySessionLifecycle.Ready)]
    [InlineData(RecoverySessionLifecycle.Executing)]
    public void FromLifecycle_NonTerminalLifecycle_Throws(
        RecoverySessionLifecycle lifecycle)
    {
        Assert.Throws<InvalidOperationException>(() =>
            RecoveryTerminalProjection.FromLifecycle(lifecycle));
    }

    [Theory]
    [InlineData(RecoverySessionLifecycle.FailedAndRolledBack)]
    [InlineData(RecoverySessionLifecycle.FailedRollbackIncomplete)]
    [InlineData(RecoverySessionLifecycle.PositionUncertain)]
    public void FromLifecycle_NoOpOnNonCompletedTerminal_Throws(
        RecoverySessionLifecycle lifecycle)
    {
        Assert.Throws<ArgumentException>(() =>
            RecoveryTerminalProjection.FromLifecycle(lifecycle, isNoOp: true));
    }

    [Fact]
    public void FromLifecycle_UnknownLifecycle_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecoveryTerminalProjection.FromLifecycle((RecoverySessionLifecycle)999));
    }

    [Fact]
    public void RunResultFactory_DerivesAllTerminalSummaryFromLifecycle()
    {
        var snapshotId = Guid.NewGuid();
        var record = new RecoverySessionRecord
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            SafetySnapshotId = snapshotId,
            ForwardFailureType = "InterruptedExecution",
            ForwardFailureMessage = "forward",
            RecoveryFailureType = "RecoveryObservationNotStable",
            RecoveryFailureMessage = "recovery",
        };

        var result = TargetDisplayLayoutExecutionRunResult.FromPersistedRecoveryTerminal(record);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsNoOp);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.IsPositionUncertain);
        Assert.Equal(snapshotId, result.SafetySnapshotId);
        Assert.Equal(record.ForwardFailureType, result.ForwardFailureType);
        Assert.Equal(record.ForwardFailureMessage, result.ForwardFailureMessage);
        Assert.Equal(record.RecoveryFailureType, result.RecoveryFailureType);
        Assert.Equal(record.RecoveryFailureMessage, result.RecoveryFailureMessage);
    }

    [Fact]
    public void RunResultFactory_CompletedNoOp_IsOnlyExplicitNoOpTerminal()
    {
        var record = new RecoverySessionRecord
        {
            Lifecycle = RecoverySessionLifecycle.Completed,
            SafetySnapshotId = Guid.NewGuid(),
        };

        var result = TargetDisplayLayoutExecutionRunResult.FromPersistedRecoveryTerminal(
            record,
            isNoOp: true);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.CompletedNoOp, result.Status);
        Assert.True(result.IsSuccess);
        Assert.True(result.IsNoOp);
        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
    }
}
