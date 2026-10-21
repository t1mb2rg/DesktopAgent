using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Tests.DesktopNotifications;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class DesktopAutomationPreflightDecisionPolicyTests
{
    [Fact]
    public void NoCurrentSuggestion_DoesNotInvokeRiskPolicy()
    {
        var risk = new FakeRiskPolicy(withinBudget: true);
        var policy = new DesktopAutomationPreflightDecisionPolicy(risk);
        var lifecycle = new DesktopLayoutSuggestionLifecycle().Snapshot;
        var runtime = AutomaticRuntime(DesktopAutomationUnattendedGate.ValidatedOpen);

        var result = policy.Evaluate(runtime, lifecycle);

        Assert.Equal(
            DesktopAutomationPreflightDecisionStatus.NoCurrentSuggestion,
            result.Status);
        Assert.False(result.MayProceedToFreshSafetyCheck);
        Assert.Null(result.RiskAssessment);
        Assert.Equal(0, risk.CallCount);
    }

    [Fact]
    public async Task SuspendedSuggestion_IsNotActionable_AndRiskIsNotEvaluated()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var suspended = lifecycle.Suspend(
            DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable);
        var risk = new FakeRiskPolicy(withinBudget: true);
        var policy = new DesktopAutomationPreflightDecisionPolicy(risk);

        var result = policy.Evaluate(
            AutomaticRuntime(DesktopAutomationUnattendedGate.ValidatedOpen),
            suspended);

        Assert.Equal(
            DesktopAutomationPreflightDecisionStatus.SuggestionNotActionable,
            result.Status);
        Assert.Equal(suggestion.SuggestionId, result.SuggestionId);
        Assert.False(result.MayProceedToFreshSafetyCheck);
        Assert.Null(result.RiskAssessment);
        Assert.Equal(0, risk.CallCount);
    }

    [Fact]
    public async Task CurrentM3AGate_ProducesRuntimeBlockedEvenWhenRiskBudgetPasses()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var active = Active(suggestion);
        var risk = new FakeRiskPolicy(withinBudget: true);
        var policy = new DesktopAutomationPreflightDecisionPolicy(risk);
        var runtime = AutomaticRuntime(
            DesktopAutomationUnattendedGate.M3AProvisionalClosed);

        var result = policy.Evaluate(runtime, active);

        Assert.Equal(
            DesktopAutomationPreflightDecisionStatus.RuntimeBlocked,
            result.Status);
        Assert.False(runtime.CanAttemptUnattendedExecution);
        Assert.True(result.RiskAssessment!.WithinAutomationRiskBudget);
        Assert.False(result.MayProceedToFreshSafetyCheck);
        Assert.Equal(1, risk.CallCount);
    }

    [Fact]
    public async Task OpenRuntimeGate_WithRiskBlocked_ProducesRiskBudgetBlocked()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var risk = new FakeRiskPolicy(withinBudget: false);
        var policy = new DesktopAutomationPreflightDecisionPolicy(risk);

        var result = policy.Evaluate(
            AutomaticRuntime(DesktopAutomationUnattendedGate.ValidatedOpen),
            Active(suggestion));

        Assert.Equal(
            DesktopAutomationPreflightDecisionStatus.RiskBudgetBlocked,
            result.Status);
        Assert.False(result.RiskAssessment!.WithinAutomationRiskBudget);
        Assert.False(result.MayProceedToFreshSafetyCheck);
    }

    [Fact]
    public async Task RuntimeAndRiskBlocked_AreReportedTogether()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var risk = new FakeRiskPolicy(withinBudget: false);
        var policy = new DesktopAutomationPreflightDecisionPolicy(risk);

        var result = policy.Evaluate(
            AutomaticRuntime(DesktopAutomationUnattendedGate.M3AProvisionalClosed),
            Active(suggestion));

        Assert.Equal(
            DesktopAutomationPreflightDecisionStatus.RuntimeAndRiskBlocked,
            result.Status);
        Assert.False(result.MayProceedToFreshSafetyCheck);
        Assert.Contains(
            DesktopAutomationBlockReason.UnattendedWriteGateClosed,
            result.Runtime.UnattendedBlockReasons);
        Assert.Equal(
            new[] { DesktopAutomationCandidateRiskBlockReason.MoveCountLimitExceeded },
            result.RiskAssessment!.BlockReasons);
    }

    [Fact]
    public async Task RuntimeAndRiskPass_OnlyReachesReadyForFreshSafetyCheck()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var risk = new FakeRiskPolicy(withinBudget: true);
        var policy = new DesktopAutomationPreflightDecisionPolicy(risk);

        var result = policy.Evaluate(
            AutomaticRuntime(DesktopAutomationUnattendedGate.ValidatedOpen),
            Active(suggestion));

        Assert.Equal(
            DesktopAutomationPreflightDecisionStatus.ReadyForFreshSafetyCheck,
            result.Status);
        Assert.True(result.MayProceedToFreshSafetyCheck);
        Assert.Equal(suggestion.SuggestionId, result.SuggestionId);
        Assert.True(result.Runtime.CanAttemptUnattendedExecution);
        Assert.True(result.RiskAssessment!.WithinAutomationRiskBudget);
    }

    [Fact]
    public async Task RequestedSuggestionMode_RemainsRuntimeBlockedEvenWithValidatedGate()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var runtimePolicy = RuntimePolicy(DesktopAutomationUnattendedGate.ValidatedOpen);
        var runtime = runtimePolicy.Snapshot;
        var risk = new FakeRiskPolicy(withinBudget: true);
        var policy = new DesktopAutomationPreflightDecisionPolicy(risk);

        var result = policy.Evaluate(runtime, Active(suggestion));

        Assert.Equal(
            DesktopAutomationPreflightDecisionStatus.RuntimeBlocked,
            result.Status);
        Assert.Equal(DesktopAutomationMode.Suggestion, runtime.RequestedMode);
        Assert.Contains(
            DesktopAutomationBlockReason.RequestedSuggestionMode,
            runtime.UnattendedBlockReasons);
    }

    [Fact]
    public async Task RiskPolicyReturningMismatchedEvidence_FailsClosed()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var risk = new FakeRiskPolicy(
            withinBudget: true,
            corruptEvidence: true);
        var policy = new DesktopAutomationPreflightDecisionPolicy(risk);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.Evaluate(
                AutomaticRuntime(DesktopAutomationUnattendedGate.ValidatedOpen),
                Active(suggestion)));

        Assert.Contains("evidence", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, risk.CallCount);
    }

    private static DesktopLayoutSuggestionLifecycleSnapshot Active(
        DesktopLayoutSuggestion suggestion)
    {
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        return lifecycle.Publish(suggestion);
    }

    private static DesktopAutomationRuntimeSnapshot AutomaticRuntime(
        DesktopAutomationUnattendedGate gate)
    {
        var policy = RuntimePolicy(gate);
        return policy.SetRequestedMode(DesktopAutomationMode.Automatic);
    }

    private static DesktopAutomationRuntimePolicy RuntimePolicy(
        DesktopAutomationUnattendedGate gate) =>
        new(
            new DesktopAutomationRuntimePolicySettings(
                TimeSpan.Zero,
                fuseFailureThreshold: 3),
            gate);

    private sealed class FakeRiskPolicy(
        bool withinBudget,
        bool corruptEvidence = false)
        : IDesktopAutomationCandidateRiskPolicy
    {
        public int CallCount { get; private set; }

        public DesktopAutomationCandidateRiskAssessment Evaluate(
            DesktopLayoutSuggestion suggestion)
        {
            CallCount++;

            var evidence = corruptEvidence
                ? new DesktopAutomationCandidateRiskEvidence(
                    Guid.NewGuid(),
                    suggestion.MoveRequiredCount,
                    suggestion.ExecutionStepCount,
                    suggestion.UsesTemporaryStaging)
                : DesktopAutomationCandidateRiskEvidence.FromSuggestion(suggestion);

            var settings = new DesktopAutomationCandidateRiskPolicySettings(
                maxMoveCount: Math.Max(1, suggestion.MoveRequiredCount),
                maxExecutionStepCount: Math.Max(1, suggestion.ExecutionStepCount),
                allowTemporaryStaging: true);

            var reasons = withinBudget
                ? Array.Empty<DesktopAutomationCandidateRiskBlockReason>()
                : new[]
                {
                    DesktopAutomationCandidateRiskBlockReason.MoveCountLimitExceeded,
                };

            return new DesktopAutomationCandidateRiskAssessment(
                evidence,
                settings,
                reasons);
        }
    }
}
