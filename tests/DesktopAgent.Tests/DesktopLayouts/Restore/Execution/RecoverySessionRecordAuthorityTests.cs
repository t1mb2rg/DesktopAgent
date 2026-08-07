using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoverySessionRecordAuthorityTests
{
    private const string TargetDisplay = @"\\.\DISPLAY1";

    [Theory]
    [InlineData(RecoverySessionLifecycle.Ready, false, false)]
    [InlineData(RecoverySessionLifecycle.Executing, false, false)]
    [InlineData(RecoverySessionLifecycle.Completed, false, false)]
    [InlineData(RecoverySessionLifecycle.FailedAndRolledBack, false, false)]
    [InlineData(RecoverySessionLifecycle.FailedRollbackIncomplete, true, false)]
    [InlineData(RecoverySessionLifecycle.PositionUncertain, true, true)]
    public void Validate_AcceptsOnlyDispositionProjectedCompatibilityFlags(
        RecoverySessionLifecycle lifecycle,
        bool requiresManualRecovery,
        bool isPositionUncertain)
    {
        var record = MakeRecord(
            lifecycle,
            requiresManualRecovery,
            isPositionUncertain);

        record.Validate();

        Assert.True(record.HasConsistentCompatibilityFlags());
    }

    [Theory]
    [InlineData(RecoverySessionLifecycle.Ready, true, false)]
    [InlineData(RecoverySessionLifecycle.Executing, false, true)]
    [InlineData(RecoverySessionLifecycle.Completed, true, true)]
    [InlineData(RecoverySessionLifecycle.FailedAndRolledBack, true, false)]
    [InlineData(RecoverySessionLifecycle.FailedRollbackIncomplete, false, false)]
    [InlineData(RecoverySessionLifecycle.PositionUncertain, true, false)]
    public void Validate_CompatibilityDrift_UsesSingleDispositionRule(
        RecoverySessionLifecycle lifecycle,
        bool requiresManualRecovery,
        bool isPositionUncertain)
    {
        var record = MakeRecord(
            lifecycle,
            requiresManualRecovery,
            isPositionUncertain);

        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());

        Assert.Contains("兼容字段漂移", ex.Message);
        Assert.Contains($"Lifecycle={lifecycle}", ex.Message);
    }

    [Fact]
    public void FromExecutionResult_ConflictingRunFlags_FailsBeforePersistedRecordCanExist()
    {
        var (restorePlan, executionPlan) = MakeNoOpPlans();
        var session = new TargetDisplayLayoutRecoverySession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TargetDisplay,
            DateTimeOffset.UtcNow,
            restorePlan,
            executionPlan,
            Array.Empty<int>(),
            Array.Empty<int>(),
            null,
            false,
            false,
            "Ready");

        var result = new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.Completed,
            true,
            false,
            session,
            session.SafetySnapshotId,
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            null,
            requiresManualRecovery: true,
            isPositionUncertain: false,
            forwardFailureType: null,
            forwardFailureMessage: null,
            recoveryFailureType: null,
            recoveryFailureMessage: null);

        var ex = Assert.Throws<InvalidDataException>(
            () => RecoverySessionRecord.FromExecutionResult(session, result));

        Assert.Contains("RunResult", ex.Message);
        Assert.Contains("Lifecycle=Completed", ex.Message);
    }

    [Fact]
    public void FromExecutionResult_MatchingFlags_AreWrittenFromDispositionProjection()
    {
        var (restorePlan, executionPlan) = MakeNoOpPlans();
        var session = new TargetDisplayLayoutRecoverySession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TargetDisplay,
            DateTimeOffset.UtcNow,
            restorePlan,
            executionPlan,
            Array.Empty<int>(),
            Array.Empty<int>(),
            null,
            false,
            false,
            "Ready");

        var result = new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.Completed,
            true,
            false,
            session,
            session.SafetySnapshotId,
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            null,
            requiresManualRecovery: false,
            isPositionUncertain: false,
            forwardFailureType: null,
            forwardFailureMessage: null,
            recoveryFailureType: null,
            recoveryFailureMessage: null);

        var record = RecoverySessionRecord.FromExecutionResult(session, result);
        var disposition = RecoverySessionDisposition.FromLifecycle(record.Lifecycle);

        Assert.Equal(RecoverySessionLifecycle.Completed, record.Lifecycle);
        Assert.Equal(disposition.RequiresManualRecovery, record.RequiresManualRecovery);
        Assert.Equal(disposition.IsPositionUncertain, record.IsPositionUncertain);
    }

    private static RecoverySessionRecord MakeRecord(
        RecoverySessionLifecycle lifecycle,
        bool requiresManualRecovery,
        bool isPositionUncertain)
    {
        var (restorePlan, executionPlan) = MakeNoOpPlans();
        return new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = Guid.NewGuid(),
            TargetDeviceName = TargetDisplay,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Lifecycle = lifecycle,
            RequiresManualRecovery = requiresManualRecovery,
            IsPositionUncertain = isPositionUncertain,
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
