using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoverySessionRecordAuthorityTests
{
    private const string TargetDisplay = @"\\.\DISPLAY1";

    [Fact]
    public void PersistedRecord_DoesNotExposeLegacyBooleanState()
    {
        var type = typeof(RecoverySessionRecord);

        Assert.Null(type.GetProperty("RequiresManualRecovery"));
        Assert.Null(type.GetProperty("IsPositionUncertain"));
    }

    [Theory]
    [InlineData(RecoverySessionLifecycle.Ready,
        RecoverySessionHandling.None, RecoverySessionSafetyClass.None, false, false)]
    [InlineData(RecoverySessionLifecycle.Executing,
        RecoverySessionHandling.AutomaticReconciliation, RecoverySessionSafetyClass.None, false, false)]
    [InlineData(RecoverySessionLifecycle.Completed,
        RecoverySessionHandling.None, RecoverySessionSafetyClass.None, false, false)]
    [InlineData(RecoverySessionLifecycle.FailedAndRolledBack,
        RecoverySessionHandling.None, RecoverySessionSafetyClass.None, false, false)]
    [InlineData(RecoverySessionLifecycle.FailedRollbackIncomplete,
        RecoverySessionHandling.ManualIntervention, RecoverySessionSafetyClass.KnownIncomplete, true, false)]
    [InlineData(RecoverySessionLifecycle.PositionUncertain,
        RecoverySessionHandling.ManualIntervention, RecoverySessionSafetyClass.PositionUncertain, true, true)]
    public void Lifecycle_IsSinglePersistedSourceForDisposition(
        RecoverySessionLifecycle lifecycle,
        RecoverySessionHandling expectedHandling,
        RecoverySessionSafetyClass expectedSafety,
        bool expectedManual,
        bool expectedUncertain)
    {
        var record = MakeRecord(lifecycle);
        record.Validate();

        var disposition = record.GetDisposition();

        Assert.Equal(expectedHandling, disposition.Handling);
        Assert.Equal(expectedSafety, disposition.SafetyClass);
        Assert.Equal(expectedManual, disposition.RequiresManualRecovery);
        Assert.Equal(expectedUncertain, disposition.IsPositionUncertain);
    }

    [Fact]
    public void WithProjectedLifecycle_ChangesOnlyAuthoritativeLifecycleSemantics()
    {
        var record = MakeRecord(RecoverySessionLifecycle.Executing);

        var changed = record.WithProjectedLifecycle(RecoverySessionLifecycle.PositionUncertain) with
        {
            ForwardFailureType = "ObservationUnavailable",
        };

        changed.Validate();
        Assert.Equal(RecoverySessionLifecycle.PositionUncertain, changed.Lifecycle);
        Assert.True(changed.GetDisposition().RequiresManualRecovery);
        Assert.True(changed.GetDisposition().IsPositionUncertain);
    }

    private static RecoverySessionRecord MakeRecord(RecoverySessionLifecycle lifecycle)
    {
        var (restorePlan, executionPlan) = MakeNoOpPlans();
        return new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = Guid.NewGuid(),
            TargetDeviceName = TargetDisplay,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Lifecycle = lifecycle,
            SourceRestorePlan = restorePlan,
            ExecutionPlan = executionPlan,
            ForwardAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            RollbackAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            CompletedForwardStepIndexes = Array.Empty<int>(),
            CompletedRollbackSourceStepIndexes = Array.Empty<int>(),
            ForwardFailureType = lifecycle is RecoverySessionLifecycle.FailedRollbackIncomplete
                or RecoverySessionLifecycle.PositionUncertain
                ? "TestFailure"
                : null,
            Revision = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static (
        TargetDisplayLayoutRestorePlan RestorePlan,
        TargetDisplayLayoutExecutionPlan ExecutionPlan) MakeNoOpPlans()
    {
        var restorePlan = new TargetDisplayLayoutRestorePlan(
            TargetDisplay,
            snapshotTargetIconCount: 0,
            alreadyAtTargetCount: 0,
            moveRequiredCount: 0,
            missingCount: 0,
            blockedCount: 0,
            hasBlockingErrors: false,
            requiresTemporaryStaging: false,
            isNoOp: true,
            Array.Empty<TargetDisplayLayoutRestorePlanItem>(),
            Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            Array.Empty<string>());

        var executionPlan = new TargetDisplayLayoutExecutionPlan(
            TargetDisplay,
            isExecutable: true,
            isNoOp: true,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        return (restorePlan, executionPlan);
    }
}