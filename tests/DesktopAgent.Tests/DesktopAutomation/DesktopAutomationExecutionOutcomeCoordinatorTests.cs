using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Tests.DesktopNotifications;
using DesktopAgent.WindowsInterop.DesktopAutomation;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class DesktopAutomationExecutionOutcomeCoordinatorTests
{
    [Fact]
    public void OutcomeCoordinator_DoesNotDirectlyDependOnWriter()
    {
        var parameterTypes = typeof(DesktopAutomationExecutionOutcomeCoordinator)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
        Assert.Contains(typeof(DesktopAutomationCoordinatorExecutionBridge), parameterTypes);
        Assert.Contains(typeof(DesktopAutomationRuntimePolicy), parameterTypes);
    }

    [Fact]
    public async Task BridgeStopsBeforeCoordinator_DoesNotMutateRuntime()
    {
        var fixture = await FixtureAsync(executionCooldown: TimeSpan.FromMinutes(5));
        var before = fixture.RuntimePolicy.Snapshot;
        var bridgeResult = new DesktopAutomationCoordinatorExecutionBridgeResult(
            DesktopAutomationCoordinatorExecutionBridgeStatus.SafetyGateNotReady,
            fixture.SafetyGate,
            rejectionReasons: new[] { "test blocked" });
        var calls = 0;
        var sut = fixture.OutcomeCoordinator((_, _) =>
        {
            calls++;
            return Task.FromResult(bridgeResult);
        });

        var result = await sut.ExecuteAndRecordAsync(fixture.SafetyGate);

        Assert.Equal(
            DesktopAutomationExecutionOutcomeFeedbackStatus.BridgeStoppedBeforeCoordinator,
            result.Status);
        Assert.False(result.FeedbackApplied);
        Assert.Equal(1, calls);
        Assert.Equal(before.Revision, result.RuntimeAfter.Revision);
        Assert.Null(result.RuntimeAfter.LastExecutionOutcome);
        Assert.False(result.RuntimeAfter.IsCooldownActive);
        Assert.Equal(0, result.RuntimeAfter.ConsecutiveFailures);
    }

    [Fact]
    public async Task CompletedCoordinatorRun_RecordsSuccessAndCooldownExactlyOnce()
    {
        var fixture = await FixtureAsync(executionCooldown: TimeSpan.FromMinutes(5));
        var bridgeResult = CoordinatorReturned(
            fixture.SafetyGate,
            Run(TargetDisplayLayoutExecutionRunStatus.Completed, isSuccess: true));
        var calls = 0;
        var sut = fixture.OutcomeCoordinator((_, _) =>
        {
            calls++;
            return Task.FromResult(bridgeResult);
        });

        var result = await sut.ExecuteAndRecordAsync(fixture.SafetyGate);

        Assert.Equal(DesktopAutomationExecutionOutcomeFeedbackStatus.SuccessRecorded, result.Status);
        Assert.True(result.FeedbackApplied);
        Assert.Equal(1, calls);
        Assert.Equal(DesktopAutomationExecutionOutcome.Success, result.RuntimeAfter.LastExecutionOutcome);
        Assert.True(result.RuntimeAfter.IsCooldownActive);
        Assert.Equal(0, result.RuntimeAfter.ConsecutiveFailures);
        Assert.Equal(result.RuntimeBefore.Revision + 1, result.RuntimeAfter.Revision);
    }

    [Fact]
    public async Task CompletedNoOp_IsStillSuccessfulSafeAttempt()
    {
        var fixture = await FixtureAsync(executionCooldown: TimeSpan.FromMinutes(1));
        var bridgeResult = CoordinatorReturned(
            fixture.SafetyGate,
            TargetDisplayLayoutExecutionRunResult.NoOp());
        var sut = fixture.OutcomeCoordinator((_, _) => Task.FromResult(bridgeResult));

        var result = await sut.ExecuteAndRecordAsync(fixture.SafetyGate);

        Assert.Equal(DesktopAutomationExecutionOutcomeFeedbackStatus.SuccessRecorded, result.Status);
        Assert.Equal(DesktopAutomationExecutionOutcome.Success, result.RuntimeAfter.LastExecutionOutcome);
        Assert.True(result.RuntimeAfter.IsCooldownActive);
    }

    [Fact]
    public async Task FailedBeforeWrite_IsConservativelyRecordedAsFailure()
    {
        var fixture = await FixtureAsync(executionCooldown: TimeSpan.FromMinutes(1));
        var bridgeResult = CoordinatorReturned(
            fixture.SafetyGate,
            TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite(
                "ExecutionScopeRejected",
                "test stale policy"));
        var sut = fixture.OutcomeCoordinator((_, _) => Task.FromResult(bridgeResult));

        var result = await sut.ExecuteAndRecordAsync(fixture.SafetyGate);

        Assert.Equal(DesktopAutomationExecutionOutcomeFeedbackStatus.FailureRecorded, result.Status);
        Assert.Equal(DesktopAutomationExecutionOutcome.Failure, result.RuntimeAfter.LastExecutionOutcome);
        Assert.True(result.RuntimeAfter.IsCooldownActive);
        Assert.Equal(1, result.RuntimeAfter.ConsecutiveFailures);
    }

    [Fact]
    public async Task RepeatedCoordinatorFailures_TripFuseAtConfiguredThreshold()
    {
        var fixture = await FixtureAsync(
            executionCooldown: TimeSpan.Zero,
            fuseThreshold: 3);
        var bridgeResult = CoordinatorReturned(
            fixture.SafetyGate,
            Run(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, isSuccess: false));
        var calls = 0;
        var sut = fixture.OutcomeCoordinator((_, _) =>
        {
            calls++;
            return Task.FromResult(bridgeResult);
        });

        await sut.ExecuteAndRecordAsync(fixture.SafetyGate);
        await sut.ExecuteAndRecordAsync(fixture.SafetyGate);
        var third = await sut.ExecuteAndRecordAsync(fixture.SafetyGate);

        Assert.Equal(3, calls);
        Assert.Equal(DesktopAutomationExecutionOutcomeFeedbackStatus.FailureRecorded, third.Status);
        Assert.True(third.RuntimeAfter.IsFuseTripped);
        Assert.Equal(3, third.RuntimeAfter.ConsecutiveFailures);
        Assert.Equal(DesktopAutomationMode.Suggestion, third.RuntimeAfter.EffectiveMode);
    }

    [Fact]
    public async Task SuccessAfterFuseTrip_DoesNotSilentlyResetFuse()
    {
        var fixture = await FixtureAsync(
            executionCooldown: TimeSpan.Zero,
            fuseThreshold: 2);
        var failure = CoordinatorReturned(
            fixture.SafetyGate,
            Run(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, isSuccess: false));
        var success = CoordinatorReturned(
            fixture.SafetyGate,
            Run(TargetDisplayLayoutExecutionRunStatus.Completed, isSuccess: true));
        var next = failure;
        var sut = fixture.OutcomeCoordinator((_, _) => Task.FromResult(next));

        await sut.ExecuteAndRecordAsync(fixture.SafetyGate);
        await sut.ExecuteAndRecordAsync(fixture.SafetyGate);
        Assert.True(fixture.RuntimePolicy.Snapshot.IsFuseTripped);

        next = success;
        var successResult = await sut.ExecuteAndRecordAsync(fixture.SafetyGate);

        Assert.Equal(DesktopAutomationExecutionOutcome.Success, successResult.RuntimeAfter.LastExecutionOutcome);
        Assert.True(successResult.RuntimeAfter.IsFuseTripped);
        Assert.Equal(2, successResult.RuntimeAfter.ConsecutiveFailures);
    }

    [Fact]
    public async Task BridgeException_PropagatesWithoutRuntimeFeedback()
    {
        var fixture = await FixtureAsync(executionCooldown: TimeSpan.FromMinutes(1));
        var before = fixture.RuntimePolicy.Snapshot;
        var sut = fixture.OutcomeCoordinator((_, _) =>
            throw new InvalidOperationException("bridge escaped"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ExecuteAndRecordAsync(fixture.SafetyGate));

        var after = fixture.RuntimePolicy.Snapshot;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Null(after.LastExecutionOutcome);
    }

    private static async Task<Fixture> FixtureAsync(
        TimeSpan executionCooldown,
        int fuseThreshold = 3)
    {
        var originalSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var freshCandidateSource = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var active = lifecycle.Publish(originalSuggestion);
        var runtimePolicy = new DesktopAutomationRuntimePolicy(
            new DesktopAutomationRuntimePolicySettings(executionCooldown, fuseThreshold),
            DesktopAutomationUnattendedGate.ValidatedOpen);
        var runtime = runtimePolicy.SetRequestedMode(DesktopAutomationMode.Automatic);
        var riskPolicy = new DesktopAutomationCandidateRiskPolicy(
            new DesktopAutomationCandidateRiskPolicySettings(100, 100, true));
        var preflight = new DesktopAutomationPreflightDecisionPolicy(riskPolicy)
            .Evaluate(runtime, active);

        var observationId = Guid.NewGuid();
        var rebuild = new DesktopLayoutFreshCandidateRebuildResult(
            DesktopLayoutFreshCandidateRebuildStatus.Ready,
            observationId,
            freshCandidateSource.Candidate,
            Array.Empty<DesktopLayoutFreshCandidateRebuildIssue>());
        var handoff = new DesktopAutomationFreshExecutionHandoffResult(
            DesktopAutomationFreshExecutionHandoffStatus.ReadyForAutomaticSafetyGate,
            preflight,
            observationId,
            rebuild);
        var evidence = new DesktopAutomationCandidateRiskEvidence(
            originalSuggestion.SuggestionId,
            freshCandidateSource.Candidate.MoveRequiredCount,
            freshCandidateSource.Candidate.ExecutionStepCount,
            freshCandidateSource.Candidate.UsesTemporaryStaging);
        var safety = new DesktopAutomationAutomaticSafetyGateResult(
            DesktopAutomationAutomaticSafetyGateStatus.ReadyForCoordinatorPreWriteGate,
            handoff,
            runtime,
            riskPolicy.Evaluate(evidence),
            unresolvedRecoveryCount: 0);

        return new Fixture(runtimePolicy, safety);
    }

    private static DesktopAutomationCoordinatorExecutionBridgeResult CoordinatorReturned(
        DesktopAutomationAutomaticSafetyGateResult safetyGate,
        TargetDisplayLayoutExecutionRunResult run) =>
        new(
            DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned,
            safetyGate,
            coordinatorRun: run,
            unresolvedRecoveryCount: 0);

    private static TargetDisplayLayoutExecutionRunResult Run(
        TargetDisplayLayoutExecutionRunStatus status,
        bool isSuccess) =>
        new(
            status,
            isSuccess,
            isNoOp: status == TargetDisplayLayoutExecutionRunStatus.CompletedNoOp,
            session: null,
            safetySnapshotId: null,
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            failedStepIndex: null,
            requiresManualRecovery: status is TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete
                or TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
            isPositionUncertain: status == TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
            forwardFailureType: isSuccess ? null : "test failure",
            forwardFailureMessage: isSuccess ? null : "test failure",
            recoveryFailureType: null,
            recoveryFailureMessage: null);

    private sealed record Fixture(
        DesktopAutomationRuntimePolicy RuntimePolicy,
        DesktopAutomationAutomaticSafetyGateResult SafetyGate)
    {
        public DesktopAutomationExecutionOutcomeCoordinator OutcomeCoordinator(
            Func<DesktopAutomationAutomaticSafetyGateResult, CancellationToken,
                Task<DesktopAutomationCoordinatorExecutionBridgeResult>> execute) =>
            new(execute, RuntimePolicy);
    }
}
