namespace DesktopAgent.Core.DesktopLayouts.Templates.Execution;

public enum BuildResultOutcome { Success, Rejected }

public sealed record DesktopLayoutRuleAwareExecutionCandidateBuildResult
{
    public BuildResultOutcome Outcome { get; }
    public DesktopLayoutRuleAwareExecutionCandidate? Candidate { get; }
    public IReadOnlyList<DesktopLayoutRuleAwareExecutionCandidateIssue> Issues { get; }

    public bool IsSuccess => Outcome == BuildResultOutcome.Success;

    internal DesktopLayoutRuleAwareExecutionCandidateBuildResult(
        BuildResultOutcome outcome,
        DesktopLayoutRuleAwareExecutionCandidate? candidate,
        IReadOnlyList<DesktopLayoutRuleAwareExecutionCandidateIssue> issues)
    {
        Outcome = outcome;
        Candidate = candidate;
        Issues = (issues ?? Array.Empty<DesktopLayoutRuleAwareExecutionCandidateIssue>())
            .ToList().AsReadOnly();
    }

    internal static DesktopLayoutRuleAwareExecutionCandidateBuildResult Success(
        DesktopLayoutRuleAwareExecutionCandidate candidate)
        => new(BuildResultOutcome.Success, candidate, Array.Empty<DesktopLayoutRuleAwareExecutionCandidateIssue>());

    internal static DesktopLayoutRuleAwareExecutionCandidateBuildResult Rejected(
        IReadOnlyList<DesktopLayoutRuleAwareExecutionCandidateIssue> issues)
        => new(BuildResultOutcome.Rejected, null, issues);

    internal static DesktopLayoutRuleAwareExecutionCandidateBuildResult Rejected(
        DesktopLayoutRuleAwareExecutionCandidateIssue issue)
        => new(BuildResultOutcome.Rejected, null,
            new[] { issue });
}
