using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Tests.DesktopNotifications;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class DesktopAutomationCandidateRiskPolicyTests
{
    [Fact]
    public void EvidenceExactlyAtLimits_IsWithinBudget()
    {
        var policy = Policy(maxMoves: 5, maxSteps: 7, allowStaging: false);
        var evidence = Evidence(moves: 5, steps: 7, staging: false);

        var result = policy.Evaluate(evidence);

        Assert.True(result.WithinAutomationRiskBudget);
        Assert.Empty(result.BlockReasons);
        Assert.Same(evidence, result.Evidence);
    }

    [Fact]
    public void MoveCountOverLimit_IsBlocked()
    {
        var result = Policy(maxMoves: 5, maxSteps: 99, allowStaging: true)
            .Evaluate(Evidence(moves: 6, steps: 6, staging: false));

        Assert.False(result.WithinAutomationRiskBudget);
        Assert.Equal(
            new[] { DesktopAutomationCandidateRiskBlockReason.MoveCountLimitExceeded },
            result.BlockReasons);
    }

    [Fact]
    public void ExecutionStepCountOverLimit_IsBlockedIndependentlyOfMoveCount()
    {
        var result = Policy(maxMoves: 10, maxSteps: 5, allowStaging: true)
            .Evaluate(Evidence(moves: 3, steps: 6, staging: false));

        Assert.False(result.WithinAutomationRiskBudget);
        Assert.Equal(
            new[] { DesktopAutomationCandidateRiskBlockReason.ExecutionStepLimitExceeded },
            result.BlockReasons);
    }

    [Fact]
    public void TemporaryStaging_IsBlockedWhenUserBudgetDisallowsIt()
    {
        var result = Policy(maxMoves: 10, maxSteps: 10, allowStaging: false)
            .Evaluate(Evidence(moves: 2, steps: 4, staging: true));

        Assert.False(result.WithinAutomationRiskBudget);
        Assert.Equal(
            new[] { DesktopAutomationCandidateRiskBlockReason.TemporaryStagingDisallowed },
            result.BlockReasons);
    }

    [Fact]
    public void TemporaryStaging_IsAllowedWhenExplicitlyEnabled()
    {
        var result = Policy(maxMoves: 10, maxSteps: 10, allowStaging: true)
            .Evaluate(Evidence(moves: 2, steps: 4, staging: true));

        Assert.True(result.WithinAutomationRiskBudget);
        Assert.Empty(result.BlockReasons);
    }

    [Fact]
    public void MultipleRiskBudgetViolations_AreReturnedInDeterministicOrder()
    {
        var result = Policy(maxMoves: 2, maxSteps: 3, allowStaging: false)
            .Evaluate(Evidence(moves: 4, steps: 7, staging: true));

        Assert.Equal(
            new[]
            {
                DesktopAutomationCandidateRiskBlockReason.MoveCountLimitExceeded,
                DesktopAutomationCandidateRiskBlockReason.ExecutionStepLimitExceeded,
                DesktopAutomationCandidateRiskBlockReason.TemporaryStagingDisallowed,
            },
            result.BlockReasons);
    }

    [Fact]
    public async Task EvaluateSuggestion_ProjectsExistingM2CandidateEvidenceWithoutChangingIt()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var policy = Policy(
            maxMoves: Math.Max(1, suggestion.MoveRequiredCount),
            maxSteps: Math.Max(1, suggestion.ExecutionStepCount),
            allowStaging: suggestion.UsesTemporaryStaging);

        var result = policy.Evaluate(suggestion);

        Assert.Equal(suggestion.SuggestionId, result.Evidence.SuggestionId);
        Assert.Equal(suggestion.MoveRequiredCount, result.Evidence.MoveRequiredCount);
        Assert.Equal(suggestion.ExecutionStepCount, result.Evidence.ExecutionStepCount);
        Assert.Equal(suggestion.UsesTemporaryStaging, result.Evidence.UsesTemporaryStaging);
        Assert.True(result.WithinAutomationRiskBudget);
    }

    [Fact]
    public void RiskEvidence_DoesNotExposeCandidateOrExecutionPlan()
    {
        var properties = typeof(DesktopAutomationCandidateRiskEvidence)
            .GetProperties()
            .ToDictionary(property => property.Name, property => property.PropertyType);

        Assert.Equal(4, properties.Count);
        Assert.Equal(typeof(Guid), properties[nameof(DesktopAutomationCandidateRiskEvidence.SuggestionId)]);
        Assert.Equal(typeof(int), properties[nameof(DesktopAutomationCandidateRiskEvidence.MoveRequiredCount)]);
        Assert.Equal(typeof(int), properties[nameof(DesktopAutomationCandidateRiskEvidence.ExecutionStepCount)]);
        Assert.Equal(typeof(bool), properties[nameof(DesktopAutomationCandidateRiskEvidence.UsesTemporaryStaging)]);
    }

    [Fact]
    public void InvalidSettingsAndEvidence_FailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopAutomationCandidateRiskPolicySettings(0, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopAutomationCandidateRiskPolicySettings(1, 0, false));
        Assert.Throws<ArgumentException>(() =>
            new DesktopAutomationCandidateRiskEvidence(Guid.Empty, 1, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopAutomationCandidateRiskEvidence(Guid.NewGuid(), 0, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopAutomationCandidateRiskEvidence(Guid.NewGuid(), 1, 0, false));
    }

    private static DesktopAutomationCandidateRiskPolicy Policy(
        int maxMoves,
        int maxSteps,
        bool allowStaging) =>
        new(
            new DesktopAutomationCandidateRiskPolicySettings(
                maxMoves,
                maxSteps,
                allowStaging));

    private static DesktopAutomationCandidateRiskEvidence Evidence(
        int moves,
        int steps,
        bool staging) =>
        new(
            Guid.NewGuid(),
            moves,
            steps,
            staging);
}
