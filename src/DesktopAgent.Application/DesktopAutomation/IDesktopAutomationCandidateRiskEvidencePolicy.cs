namespace DesktopAgent.Application.DesktopAutomation;

/// <summary>
/// 对最小 Candidate risk evidence 做预算判断的只读能力。
/// 供 M3-B fresh Candidate safety gate 使用，不暴露 Candidate / Writer。
/// </summary>
public interface IDesktopAutomationCandidateRiskEvidencePolicy
{
    DesktopAutomationCandidateRiskAssessment Evaluate(
        DesktopAutomationCandidateRiskEvidence evidence);
}
