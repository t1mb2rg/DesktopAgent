namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

public sealed record TargetDisplayLayoutExecutionPlanIssue(
    TargetDisplayLayoutExecutionPlanIssueCode Code,
    string Message,
    string? RelatedStableId = null);
