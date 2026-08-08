using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Application.DesktopAutomation;

public enum DesktopAutomationPreflightDecisionStatus
{
    NoCurrentSuggestion = 0,
    SuggestionNotActionable = 1,
    RuntimeBlocked = 2,
    RiskBudgetBlocked = 3,
    RuntimeAndRiskBlocked = 4,
    ReadyForFreshSafetyCheck = 5,
}

/// <summary>
/// 05A runtime policy + 当前 Suggestion lifecycle + 05B risk budget 的只读组合结果。
///
/// ReadyForFreshSafetyCheck 只表示可以继续进入未来 fresh M2 safety preflight，
/// 绝不等于执行授权。
/// </summary>
public sealed record DesktopAutomationPreflightDecision
{
    public DesktopAutomationPreflightDecisionStatus Status { get; }
    public DesktopAutomationRuntimeSnapshot Runtime { get; }
    public DesktopLayoutSuggestionLifecycleSnapshot SuggestionLifecycle { get; }
    public DesktopAutomationCandidateRiskAssessment? RiskAssessment { get; }

    public Guid? SuggestionId => SuggestionLifecycle.CurrentSuggestion?.SuggestionId;
    public bool MayProceedToFreshSafetyCheck =>
        Status == DesktopAutomationPreflightDecisionStatus.ReadyForFreshSafetyCheck;

    public DesktopAutomationPreflightDecision(
        DesktopAutomationPreflightDecisionStatus status,
        DesktopAutomationRuntimeSnapshot runtime,
        DesktopLayoutSuggestionLifecycleSnapshot suggestionLifecycle,
        DesktopAutomationCandidateRiskAssessment? riskAssessment)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(suggestionLifecycle);

        switch (status)
        {
            case DesktopAutomationPreflightDecisionStatus.NoCurrentSuggestion:
                if (suggestionLifecycle.State != DesktopLayoutSuggestionPresentationState.None
                    || suggestionLifecycle.CurrentSuggestion is not null
                    || riskAssessment is not null)
                {
                    throw new ArgumentException(
                        "NoCurrentSuggestion 必须对应 None lifecycle 且不得携带 RiskAssessment。",
                        nameof(status));
                }
                break;

            case DesktopAutomationPreflightDecisionStatus.SuggestionNotActionable:
                if (suggestionLifecycle.State != DesktopLayoutSuggestionPresentationState.Suspended
                    || suggestionLifecycle.CurrentSuggestion is null
                    || riskAssessment is not null)
                {
                    throw new ArgumentException(
                        "SuggestionNotActionable 必须对应 Suspended lifecycle 且不得携带 RiskAssessment。",
                        nameof(status));
                }
                break;

            case DesktopAutomationPreflightDecisionStatus.RuntimeBlocked:
                ValidateActiveDecisionInputs(suggestionLifecycle, riskAssessment);
                if (runtime.CanAttemptUnattendedExecution
                    || !riskAssessment!.WithinAutomationRiskBudget)
                {
                    throw new ArgumentException(
                        "RuntimeBlocked 要求 runtime blocked 且 risk budget 通过。",
                        nameof(status));
                }
                break;

            case DesktopAutomationPreflightDecisionStatus.RiskBudgetBlocked:
                ValidateActiveDecisionInputs(suggestionLifecycle, riskAssessment);
                if (!runtime.CanAttemptUnattendedExecution
                    || riskAssessment!.WithinAutomationRiskBudget)
                {
                    throw new ArgumentException(
                        "RiskBudgetBlocked 要求 runtime 通过且 risk budget blocked。",
                        nameof(status));
                }
                break;

            case DesktopAutomationPreflightDecisionStatus.RuntimeAndRiskBlocked:
                ValidateActiveDecisionInputs(suggestionLifecycle, riskAssessment);
                if (runtime.CanAttemptUnattendedExecution
                    || riskAssessment!.WithinAutomationRiskBudget)
                {
                    throw new ArgumentException(
                        "RuntimeAndRiskBlocked 要求 runtime 与 risk budget 同时 blocked。",
                        nameof(status));
                }
                break;

            case DesktopAutomationPreflightDecisionStatus.ReadyForFreshSafetyCheck:
                ValidateActiveDecisionInputs(suggestionLifecycle, riskAssessment);
                if (!runtime.CanAttemptUnattendedExecution
                    || !riskAssessment!.WithinAutomationRiskBudget)
                {
                    throw new ArgumentException(
                        "ReadyForFreshSafetyCheck 要求 runtime 与 risk budget 同时通过。",
                        nameof(status));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "未知 preflight status。");
        }

        Status = status;
        Runtime = runtime;
        SuggestionLifecycle = suggestionLifecycle;
        RiskAssessment = riskAssessment;
    }

    private static void ValidateActiveDecisionInputs(
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle,
        DesktopAutomationCandidateRiskAssessment? riskAssessment)
    {
        if (lifecycle.State != DesktopLayoutSuggestionPresentationState.Active
            || lifecycle.CurrentSuggestion is null
            || riskAssessment is null)
        {
            throw new ArgumentException(
                "Active preflight decision 必须携带 Active Suggestion 与 RiskAssessment。",
                nameof(lifecycle));
        }
    }
}

/// <summary>
/// M3-05C 只读 automation preflight 组合层。
///
/// 它不会读取 Shell、不会 fresh rebuild Candidate、不会调用 Writer。
/// 最终最多输出 ReadyForFreshSafetyCheck，表示未来可以进入 M2 fresh safety preflight。
/// </summary>
public sealed class DesktopAutomationPreflightDecisionPolicy
{
    private readonly IDesktopAutomationCandidateRiskPolicy _riskPolicy;

    public DesktopAutomationPreflightDecisionPolicy(
        IDesktopAutomationCandidateRiskPolicy riskPolicy)
    {
        _riskPolicy = riskPolicy ?? throw new ArgumentNullException(nameof(riskPolicy));
    }

    public DesktopAutomationPreflightDecision Evaluate(
        DesktopAutomationRuntimeSnapshot runtime,
        DesktopLayoutSuggestionLifecycleSnapshot suggestionLifecycle)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(suggestionLifecycle);

        if (suggestionLifecycle.State == DesktopLayoutSuggestionPresentationState.None)
        {
            return new DesktopAutomationPreflightDecision(
                DesktopAutomationPreflightDecisionStatus.NoCurrentSuggestion,
                runtime,
                suggestionLifecycle,
                null);
        }

        if (suggestionLifecycle.State != DesktopLayoutSuggestionPresentationState.Active
            || suggestionLifecycle.CurrentSuggestion is null)
        {
            return new DesktopAutomationPreflightDecision(
                DesktopAutomationPreflightDecisionStatus.SuggestionNotActionable,
                runtime,
                suggestionLifecycle,
                null);
        }

        var suggestion = suggestionLifecycle.CurrentSuggestion;
        var risk = _riskPolicy.Evaluate(suggestion);
        ValidateRiskEvidenceMatchesSuggestion(suggestion, risk);

        var runtimeReady = runtime.CanAttemptUnattendedExecution;
        var riskReady = risk.WithinAutomationRiskBudget;

        var status = (runtimeReady, riskReady) switch
        {
            (false, true) => DesktopAutomationPreflightDecisionStatus.RuntimeBlocked,
            (true, false) => DesktopAutomationPreflightDecisionStatus.RiskBudgetBlocked,
            (false, false) => DesktopAutomationPreflightDecisionStatus.RuntimeAndRiskBlocked,
            (true, true) => DesktopAutomationPreflightDecisionStatus.ReadyForFreshSafetyCheck,
        };

        return new DesktopAutomationPreflightDecision(
            status,
            runtime,
            suggestionLifecycle,
            risk);
    }

    private static void ValidateRiskEvidenceMatchesSuggestion(
        DesktopLayoutSuggestion suggestion,
        DesktopAutomationCandidateRiskAssessment risk)
    {
        ArgumentNullException.ThrowIfNull(risk);
        var evidence = risk.Evidence;

        if (evidence.SuggestionId != suggestion.SuggestionId
            || evidence.MoveRequiredCount != suggestion.MoveRequiredCount
            || evidence.ExecutionStepCount != suggestion.ExecutionStepCount
            || evidence.UsesTemporaryStaging != suggestion.UsesTemporaryStaging)
        {
            throw new InvalidOperationException(
                "Candidate risk policy 返回的 evidence 与当前 Active Suggestion 不一致。"
                + "Preflight fail closed，禁止继续进入 fresh safety check。");
        }
    }
}
