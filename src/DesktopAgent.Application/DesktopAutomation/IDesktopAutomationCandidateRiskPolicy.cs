using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Application.DesktopAutomation;

/// <summary>
/// 只读 Candidate 风险预算判断 seam。
/// 不暴露 Writer / ExecutionCoordinator。
/// </summary>
public interface IDesktopAutomationCandidateRiskPolicy
{
    DesktopAutomationCandidateRiskAssessment Evaluate(
        DesktopLayoutSuggestion suggestion);
}
