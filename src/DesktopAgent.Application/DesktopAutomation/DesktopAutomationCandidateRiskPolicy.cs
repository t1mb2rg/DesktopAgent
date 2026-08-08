using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Application.DesktopAutomation;

public enum DesktopAutomationCandidateRiskBlockReason
{
    MoveCountLimitExceeded = 0,
    ExecutionStepLimitExceeded = 1,
    TemporaryStagingDisallowed = 2,
}

/// <summary>
/// 从 M3 Suggestion 投影出的最小自动化风险证据。
/// 不包含 RestorePlan / ExecutionPlan / Writer；只保留用户风险预算真正需要的量。
/// </summary>
public sealed record DesktopAutomationCandidateRiskEvidence
{
    public Guid SuggestionId { get; }
    public int MoveRequiredCount { get; }
    public int ExecutionStepCount { get; }
    public bool UsesTemporaryStaging { get; }

    public DesktopAutomationCandidateRiskEvidence(
        Guid suggestionId,
        int moveRequiredCount,
        int executionStepCount,
        bool usesTemporaryStaging)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("SuggestionId 不能为空。", nameof(suggestionId));
        if (moveRequiredCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(moveRequiredCount),
                moveRequiredCount,
                "Risk evidence 必须来自非 NoOp Suggestion，MoveRequiredCount 必须大于 0。");
        if (executionStepCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(executionStepCount),
                executionStepCount,
                "Risk evidence 必须来自可执行 Suggestion，ExecutionStepCount 必须大于 0。");

        SuggestionId = suggestionId;
        MoveRequiredCount = moveRequiredCount;
        ExecutionStepCount = executionStepCount;
        UsesTemporaryStaging = usesTemporaryStaging;
    }

    public static DesktopAutomationCandidateRiskEvidence FromSuggestion(
        DesktopLayoutSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        return new DesktopAutomationCandidateRiskEvidence(
            suggestion.SuggestionId,
            suggestion.MoveRequiredCount,
            suggestion.ExecutionStepCount,
            suggestion.UsesTemporaryStaging);
    }
}

public sealed record DesktopAutomationCandidateRiskPolicySettings
{
    public int MaxMoveCount { get; }
    public int MaxExecutionStepCount { get; }
    public bool AllowTemporaryStaging { get; }

    public DesktopAutomationCandidateRiskPolicySettings(
        int maxMoveCount,
        int maxExecutionStepCount,
        bool allowTemporaryStaging)
    {
        if (maxMoveCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMoveCount));
        if (maxExecutionStepCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExecutionStepCount));

        MaxMoveCount = maxMoveCount;
        MaxExecutionStepCount = maxExecutionStepCount;
        AllowTemporaryStaging = allowTemporaryStaging;
    }
}

public sealed record DesktopAutomationCandidateRiskAssessment
{
    public DesktopAutomationCandidateRiskEvidence Evidence { get; }
    public DesktopAutomationCandidateRiskPolicySettings Settings { get; }
    public IReadOnlyList<DesktopAutomationCandidateRiskBlockReason> BlockReasons { get; }

    public bool WithinAutomationRiskBudget => BlockReasons.Count == 0;

    public DesktopAutomationCandidateRiskAssessment(
        DesktopAutomationCandidateRiskEvidence evidence,
        DesktopAutomationCandidateRiskPolicySettings settings,
        IEnumerable<DesktopAutomationCandidateRiskBlockReason> blockReasons)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(blockReasons);

        var reasons = blockReasons.ToArray();
        if (reasons.Any(reason => !Enum.IsDefined(reason)))
        {
            throw new ArgumentException(
                "包含未知 candidate risk block reason。",
                nameof(blockReasons));
        }
        if (reasons.Distinct().Count() != reasons.Length)
        {
            throw new ArgumentException(
                "Candidate risk block reasons 不得重复。",
                nameof(blockReasons));
        }

        Evidence = evidence;
        Settings = settings;
        BlockReasons = Array.AsReadOnly(reasons);
    }
}

/// <summary>
/// M3-05B 纯自动化风险预算策略。
///
/// 它只回答“这份已经由 M2 Planner 生成并被 M3 Suggestion 接纳的 Candidate，
/// 是否落在用户配置的自动化风险预算内”。
///
/// WithinAutomationRiskBudget=true 仍然不等于执行授权：
/// 还必须同时满足 05A runtime policy、未来 fresh M2 safety chain 等门禁。
/// </summary>
public sealed class DesktopAutomationCandidateRiskPolicy
{
    private readonly DesktopAutomationCandidateRiskPolicySettings _settings;

    public DesktopAutomationCandidateRiskPolicy(
        DesktopAutomationCandidateRiskPolicySettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public DesktopAutomationCandidateRiskAssessment Evaluate(
        DesktopLayoutSuggestion suggestion) =>
        Evaluate(DesktopAutomationCandidateRiskEvidence.FromSuggestion(suggestion));

    public DesktopAutomationCandidateRiskAssessment Evaluate(
        DesktopAutomationCandidateRiskEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var reasons = new List<DesktopAutomationCandidateRiskBlockReason>(capacity: 3);

        if (evidence.MoveRequiredCount > _settings.MaxMoveCount)
        {
            reasons.Add(
                DesktopAutomationCandidateRiskBlockReason.MoveCountLimitExceeded);
        }

        if (evidence.ExecutionStepCount > _settings.MaxExecutionStepCount)
        {
            reasons.Add(
                DesktopAutomationCandidateRiskBlockReason.ExecutionStepLimitExceeded);
        }

        if (evidence.UsesTemporaryStaging && !_settings.AllowTemporaryStaging)
        {
            reasons.Add(
                DesktopAutomationCandidateRiskBlockReason.TemporaryStagingDisallowed);
        }

        return new DesktopAutomationCandidateRiskAssessment(
            evidence,
            _settings,
            reasons);
    }
}
