using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoverySessionCompatibilityProjectionTests
{
    private const string TD = @"\\.\DISPLAY1";

    [Theory]
    [InlineData(
        TargetDisplayLayoutExecutionRunStatus.Completed,
        true,
        true,
        false,
        false)]
    [InlineData(
        TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
        false,
        true,
        true,
        false)]
    [InlineData(
        TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
        false,
        false,
        true,
        true)]
    public void FromExecutionResult_ProjectsCompatibilityFlagsFromLifecycle_NotRunResultBools(
        TargetDisplayLayoutExecutionRunStatus status,
        bool legacyRequiresManual,
        bool legacyPositionUncertain,
        bool expectedRequiresManual,
        bool expectedPositionUncertain)
    {
        var session = NoOpSession();
        var result = new TargetDisplayLayoutExecutionRunResult(
            status,
            isSuccess: status == TargetDisplayLayoutExecutionRunStatus.Completed,
            isNoOp: status == TargetDisplayLayoutExecutionRunStatus.Completed,
            session,
            session.SafetySnapshotId,
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            failedStepIndex: null,
            requiresManualRecovery: legacyRequiresManual,
            isPositionUncertain: legacyPositionUncertain,
            forwardFailureType: status is TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete
                or TargetDisplayLayoutExecutionRunStatus.PositionUncertain
                ? "Interrupted"
                : null,
            forwardFailureMessage: null,
            recoveryFailureType: null,
            recoveryFailureMessage: null,
            verificationResult: null);

        var record = RecoverySessionRecord.FromExecutionResult(session, result);

        Assert.Equal(
            RecoverySessionRecord.DeriveLifecycle(status),
            record.Lifecycle);
        Assert.Equal(expectedRequiresManual, record.RequiresManualRecovery);
        Assert.Equal(expectedPositionUncertain, record.IsPositionUncertain);
        Assert.True(record.HasConsistentCompatibilityFlags());

        // 即使 RunResult 带着矛盾的旧 bool，持久化聚合仍然由 Lifecycle 投影成合法状态。
        record.Validate();
    }

    [Theory]
    [InlineData(RecoverySessionLifecycle.Ready, false, false)]
    [InlineData(RecoverySessionLifecycle.Executing, false, false)]
    [InlineData(RecoverySessionLifecycle.Completed, false, false)]
    [InlineData(RecoverySessionLifecycle.FailedAndRolledBack, false, false)]
    [InlineData(RecoverySessionLifecycle.FailedRollbackIncomplete, true, false)]
    [InlineData(RecoverySessionLifecycle.PositionUncertain, true, true)]
    public void WithProjectedLifecycle_IsTheSingleTripletMutationPath(
        RecoverySessionLifecycle lifecycle,
        bool expectedRequiresManual,
        bool expectedPositionUncertain)
    {
        var source = new RecoverySessionRecord
        {
            Lifecycle = RecoverySessionLifecycle.PositionUncertain,
            RequiresManualRecovery = true,
            IsPositionUncertain = true,
        };

        var projected = source.WithProjectedLifecycle(lifecycle);

        Assert.Equal(lifecycle, projected.Lifecycle);
        Assert.Equal(expectedRequiresManual, projected.RequiresManualRecovery);
        Assert.Equal(expectedPositionUncertain, projected.IsPositionUncertain);
        Assert.True(projected.HasConsistentCompatibilityFlags());
    }

    [Fact]
    public void Validate_CompatibilityDrift_IsOneProjectionMismatch_NotLifecycleSpecificRules()
    {
        var valid = RecoverySessionRecord.FromExecutionResult(
            NoOpSession(),
            CompletedResult());
        var drifted = valid with
        {
            RequiresManualRecovery = true,
        };

        var ex = Assert.Throws<InvalidDataException>(() => drifted.Validate());

        Assert.Contains("兼容状态字段与 Lifecycle=Completed 的 Disposition 投影不一致", ex.Message);
        Assert.DoesNotContain("Completed 状态下", ex.Message);
    }

    [Fact]
    public void Validate_UnknownLifecycle_FailsAsInvalidData_WithoutProjectingUnknownEnum()
    {
        var record = new RecoverySessionRecord
        {
            Lifecycle = (RecoverySessionLifecycle)999,
        };

        var ex = Assert.Throws<InvalidDataException>(() => record.Validate());

        Assert.Contains("无效的 Lifecycle 值：999", ex.Message);
    }

    private static TargetDisplayLayoutExecutionRunResult CompletedResult()
    {
        var session = NoOpSession();
        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.Completed,
            isSuccess: true,
            isNoOp: true,
            session,
            session.SafetySnapshotId,
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            failedStepIndex: null,
            requiresManualRecovery: false,
            isPositionUncertain: false,
            forwardFailureType: null,
            forwardFailureMessage: null,
            recoveryFailureType: null,
            recoveryFailureMessage: null,
            verificationResult: null);
    }

    private static TargetDisplayLayoutRecoverySession NoOpSession()
    {
        var restorePlan = new TargetDisplayLayoutRestorePlan(
            TD,
            snapshotTargetIconCount: 0,
            alreadyAtTargetCount: 0,
            moveRequiredCount: 0,
            missingCount: 0,
            blockedCount: 0,
            hasBlockingErrors: false,
            requiresTemporaryStaging: false,
            isNoOp: true,
            items: Array.Empty<TargetDisplayLayoutRestorePlanItem>(),
            dependencies: Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            issues: Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: Array.Empty<string>());

        var executionPlan = new TargetDisplayLayoutExecutionPlan(
            TD,
            isExecutable: true,
            isNoOp: true,
            steps: Array.Empty<TargetDisplayLayoutExecutionStep>(),
            stagingAssignments: Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            issues: Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        return new TargetDisplayLayoutRecoverySession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TD,
            DateTimeOffset.UtcNow,
            restorePlan,
            executionPlan,
            Array.Empty<int>(),
            Array.Empty<int>(),
            lastFailedStepIndex: null,
            requiresManualRecovery: false,
            isPositionUncertain: false,
            status: "Ready");
    }
}
