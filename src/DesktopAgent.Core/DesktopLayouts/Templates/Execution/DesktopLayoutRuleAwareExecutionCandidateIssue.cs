namespace DesktopAgent.Core.DesktopLayouts.Templates.Execution;

public sealed record DesktopLayoutRuleAwareExecutionCandidateIssue
{
    public DesktopLayoutRuleAwareExecutionCandidateIssueCode Code { get; }
    public string Message { get; }
    public string? StableId { get; }

    public DesktopLayoutRuleAwareExecutionCandidateIssue(
        DesktopLayoutRuleAwareExecutionCandidateIssueCode code,
        string message,
        string? stableId = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message 不能为空。", nameof(message));
        Code = code;
        Message = message;
        StableId = stableId;
    }
}
