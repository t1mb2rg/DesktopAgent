using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Tests.DesktopNotifications;
using DesktopAgent.WindowsInterop.DesktopAutomation;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class DesktopAutomationExecutionCycleCoordinatorTests
{
    [Fact]
    public void CycleCoordinator_DoesNotDirectlyDependOnWriter()
    {
        var parameterTypes = typeof(DesktopAutomationExecutionCycleCoordinator)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
        Assert.Contains(typeof(DesktopAutomationRuntimePolicy), parameterTypes);
        Assert.Contains(typeof(DesktopLayoutSuggestionLifecycle), parameterTypes);
        Assert.Contains(typeof(DesktopAutomationPreflightDecisionPolicy), parameterTypes);
        Assert.Contains(typeof(DesktopAutomationFreshExecutionHandoff), parameterTypes);
        Assert.Contains(typeof(DesktopAutomationAutomaticSafetyGate), parameterTypes);
        Assert.Contains(typeof(DesktopAutomationExecutionOutcomeCoordinator), parameterTypes);
    }

    [Fact]
    public async Task ProvisionalClosedRuntime_StopsAtPreflightWithoutCallingLaterStages()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var runtime = RuntimePolicy(DesktopAutomationUnattendedGate.M3AProvisionalClosed);
        runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        var risk = PermissiveRisk();
        var preflightPolicy = new DesktopAutomationPreflightDecisionPolicy(risk);
        var laterCalls = 0;

        using var sut = new DesktopAutomationExecutionCycleCoordinator(
            runtime,
            lifecycle,
            (r, l) => preflightPolicy.Evaluate(r, l),
            (_, _) =>
            {
                laterCalls++;
                throw new InvalidOperationException("B01 must not run");
            },
            (_, _) =>
            {
                laterCalls++;
                throw new InvalidOperationException("B02 must not run");
            },
            (_, _) =>
            {
                laterCalls++;
                throw new InvalidOperationException("B04 must not run");
            });

        var result = await sut.RunCurrentAsync();

        Assert.Equal(DesktopAutomationExecutionCycleStatus.PreflightStopped, result.Status);
        Assert.Equal(0, laterCalls);
        Assert.NotNull(result.Preflight);
        Assert.Equal(DesktopAutomationPreflightDecisionStatus.RuntimeBlocked, result.Preflight!.Status);
        Assert.False(result.ReachedCoordinatorStage);
        Assert.Null(result.RuntimeAfter.LastExecutionOutcome);
    }

    [Fact]
    public async Task B01Stops_CycleDoesNotCallB02OrB04()
    {
        var fixture = await ReadyFixtureAsync();
        var calls = new List<string>();
        var stoppedHandoff = new DesktopAutomationFreshExecutionHandoffResult(
            DesktopAutomationFreshExecutionHandoffStatus.ObservationUnstable,
            fixture.Preflight);

        using var sut = fixture.CreateCycle(
            prepareHandoff: (_, _) =>
            {
                calls.Add("B01");
                return Task.FromResult(stoppedHandoff);
            },
            evaluateSafety: (_, _) =>
            {
                calls.Add("B02");
                throw new InvalidOperationException("must not run");
            },
            executeOutcome: (_, _) =>
            {
                calls.Add("B04");
                throw new InvalidOperationException("must not run");
            });

        var result = await sut.RunCurrentAsync();

        Assert.Equal(DesktopAutomationExecutionCycleStatus.FreshHandoffStopped, result.Status);
        Assert.Equal(new[] { "B01" }, calls);
        Assert.Same(stoppedHandoff, result.Handoff);
        Assert.Null(result.SafetyGate);
        Assert.Null(result.Outcome);
    }

    [Fact]
    public async Task B02Stops_CycleDoesNotCallB04()
    {
        var fixture = await ReadyFixtureAsync();
        var calls = new List<string>();
        var stoppedSafety = new DesktopAutomationAutomaticSafetyGateResult(
            DesktopAutomationAutomaticSafetyGateStatus.FreshRiskBudgetBlocked,
            fixture.Handoff,
            fixture.RuntimePolicy.Snapshot,
            unresolvedRecoveryCount: 0,
            rejectionReasons: new[] { "test risk blocked" });

        using var sut = fixture.CreateCycle(
            prepareHandoff: (_, _) =>
            {
                calls.Add("B01");
                return Task.FromResult(fixture.Handoff);
            },
            evaluateSafety: (_, _) =>
            {
                calls.Add("B02");
                return Task.FromResult(stoppedSafety);
            },
            executeOutcome: (_, _) =>
            {
                calls.Add("B04");
                throw new InvalidOperationException("must not run");
            });

        var result = await sut.RunCurrentAsync();

        Assert.Equal(
            DesktopAutomationExecutionCycleStatus.AutomaticSafetyGateStopped,
            result.Status);
        Assert.Equal(new[] { "B01", "B02" }, calls);
        Assert.Same(stoppedSafety, result.SafetyGate);
        Assert.Null(result.Outcome);
    }

    [Fact]
    public async Task FullReadyPath_RunsInFixedOrderAndReturnsOutcome()
    {
        var fixture = await ReadyFixtureAsync(executionCooldown: TimeSpan.FromMinutes(2));
        var calls = new List<string>();

        using var sut = fixture.CreateCycle(
            prepareHandoff: (_, _) =>
            {
                calls.Add("B01");
                return Task.FromResult(fixture.Handoff);
            },
            evaluateSafety: (_, _) =>
            {
                calls.Add("B02");
                return Task.FromResult(fixture.SafetyGate);
            },
            executeOutcome: (_, _) =>
            {
                calls.Add("B04");
                var before = fixture.RuntimePolicy.Snapshot;
                var after = fixture.RuntimePolicy.RecordExecutionSuccess();
                return Task.FromResult(SuccessOutcome(fixture.SafetyGate, before, after));
            });

        var result = await sut.RunCurrentAsync();

        Assert.Equal(DesktopAutomationExecutionCycleStatus.ExecutionStageReturned, result.Status);
        Assert.Equal(new[] { "B01", "B02", "B04" }, calls);
        Assert.True(result.ReachedCoordinatorStage);
        Assert.NotNull(result.Outcome);
        Assert.Equal(
            DesktopAutomationExecutionOutcomeFeedbackStatus.SuccessRecorded,
            result.Outcome!.Status);
        Assert.Equal(DesktopAutomationExecutionOutcome.Success, result.RuntimeAfter.LastExecutionOutcome);
        Assert.True(result.RuntimeAfter.IsCooldownActive);
        Assert.Equal(
            fixture.OriginalSuggestion.SuggestionId,
            fixture.Lifecycle.Snapshot.CurrentSuggestion?.SuggestionId);
    }

    [Fact]
    public async Task EscapedFault_PausesAutomaticWithoutGuessingExecutionOutcome()
    {
        var fixture = await ReadyFixtureAsync();
        using var sut = fixture.CreateCycle(
            prepareHandoff: (_, _) => throw new InvalidOperationException("unexpected bridge fault"));

        var result = await sut.RunCurrentAsync();

        Assert.Equal(DesktopAutomationExecutionCycleStatus.FaultedAndPaused, result.Status);
        Assert.NotNull(result.Fault);
        Assert.Contains("InvalidOperationException", result.Fault!, StringComparison.Ordinal);
        Assert.True(result.RuntimeAfter.IsPaused);
        Assert.Equal(DesktopAutomationMode.Suggestion, result.RuntimeAfter.EffectiveMode);
        Assert.Null(result.RuntimeAfter.LastExecutionOutcome);
        Assert.False(result.ReachedCoordinatorStage);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAndDoesNotPauseRuntime()
    {
        var fixture = await ReadyFixtureAsync();
        using var cts = new CancellationTokenSource();
        using var sut = fixture.CreateCycle(
            prepareHandoff: (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("unreachable");
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.RunCurrentAsync(cts.Token));

        Assert.False(fixture.RuntimePolicy.Snapshot.IsPaused);
        Assert.Null(fixture.RuntimePolicy.Snapshot.LastExecutionOutcome);
    }

    [Fact]
    public async Task ConcurrentCycle_IsRejectedInsteadOfQueued()
    {
        var fixture = await ReadyFixtureAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sut = fixture.CreateCycle(
            prepareHandoff: async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return fixture.Handoff;
            });

        var first = sut.RunCurrentAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await sut.RunCurrentAsync();
        Assert.Equal(DesktopAutomationExecutionCycleStatus.CycleAlreadyRunning, second.Status);
        Assert.Null(second.Preflight);
        Assert.False(second.ReachedCoordinatorStage);

        release.TrySetResult();
        var firstResult = await first;
        Assert.Equal(DesktopAutomationExecutionCycleStatus.ExecutionStageReturned, firstResult.Status);
    }

    private static async Task<ReadyFixture> ReadyFixtureAsync(
        TimeSpan? executionCooldown = null)
    {
        var originalSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var freshCandidateSource = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var active = lifecycle.Publish(originalSuggestion);
        var runtime = RuntimePolicy(
            DesktopAutomationUnattendedGate.ValidatedOpen,
            executionCooldown ?? TimeSpan.Zero);
        var runtimeSnapshot = runtime.SetRequestedMode(DesktopAutomationMode.Automatic);
        var risk = PermissiveRisk();
        var preflight = new DesktopAutomationPreflightDecisionPolicy(risk)
            .Evaluate(runtimeSnapshot, active);
        Assert.True(preflight.MayProceedToFreshSafetyCheck);

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
        var safetyGate = new DesktopAutomationAutomaticSafetyGateResult(
            DesktopAutomationAutomaticSafetyGateStatus.ReadyForCoordinatorPreWriteGate,
            handoff,
            runtimeSnapshot,
            risk.Evaluate(evidence),
            unresolvedRecoveryCount: 0);

        return new ReadyFixture(
            originalSuggestion,
            runtime,
            lifecycle,
            preflight,
            handoff,
            safetyGate);
    }

    private static DesktopAutomationRuntimePolicy RuntimePolicy(
        DesktopAutomationUnattendedGate gate,
        TimeSpan? cooldown = null) =>
        new(
            new DesktopAutomationRuntimePolicySettings(cooldown ?? TimeSpan.Zero, 3),
            gate);

    private static DesktopAutomationCandidateRiskPolicy PermissiveRisk() =>
        new(new DesktopAutomationCandidateRiskPolicySettings(100, 100, true));

    private static DesktopAutomationExecutionOutcomeFeedbackResult SuccessOutcome(
        DesktopAutomationAutomaticSafetyGateResult safetyGate,
        DesktopAutomationRuntimeSnapshot before,
        DesktopAutomationRuntimeSnapshot after)
    {
        var run = TargetDisplayLayoutExecutionRunResult.NoOp();
        var bridge = new DesktopAutomationCoordinatorExecutionBridgeResult(
            DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned,
            safetyGate,
            coordinatorRun: run,
            unresolvedRecoveryCount: 0);
        return new DesktopAutomationExecutionOutcomeFeedbackResult(
            DesktopAutomationExecutionOutcomeFeedbackStatus.SuccessRecorded,
            bridge,
            before,
            after);
    }

    private sealed record ReadyFixture(
        DesktopLayoutSuggestion OriginalSuggestion,
        DesktopAutomationRuntimePolicy RuntimePolicy,
        DesktopLayoutSuggestionLifecycle Lifecycle,
        DesktopAutomationPreflightDecision Preflight,
        DesktopAutomationFreshExecutionHandoffResult Handoff,
        DesktopAutomationAutomaticSafetyGateResult SafetyGate)
    {
        public DesktopAutomationExecutionCycleCoordinator CreateCycle(
            Func<DesktopAutomationPreflightDecision, CancellationToken,
                Task<DesktopAutomationFreshExecutionHandoffResult>>? prepareHandoff = null,
            Func<DesktopAutomationFreshExecutionHandoffResult, CancellationToken,
                Task<DesktopAutomationAutomaticSafetyGateResult>>? evaluateSafety = null,
            Func<DesktopAutomationAutomaticSafetyGateResult, CancellationToken,
                Task<DesktopAutomationExecutionOutcomeFeedbackResult>>? executeOutcome = null) =>
            new(
                RuntimePolicy,
                Lifecycle,
                (_, _) => Preflight,
                prepareHandoff ?? ((_, _) => Task.FromResult(Handoff)),
                evaluateSafety ?? ((_, _) => Task.FromResult(SafetyGate)),
                executeOutcome ?? ((_, _) =>
                {
                    var before = RuntimePolicy.Snapshot;
                    var after = RuntimePolicy.RecordExecutionSuccess();
                    return Task.FromResult(SuccessOutcome(SafetyGate, before, after));
                }));
    }
}
