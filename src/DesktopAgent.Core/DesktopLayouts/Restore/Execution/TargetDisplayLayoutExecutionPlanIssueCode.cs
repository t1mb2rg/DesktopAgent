namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

public enum TargetDisplayLayoutExecutionPlanIssueCode
{
    SourcePlanHasBlockingErrors,
    SourcePlanInconsistent,
    SourcePlanStale,
    CurrentReadIncomplete,
    AutoArrangeEnabled,
    TargetDisplayNotFound,
    DuplicateCurrentStableId,
    CurrentRectangleOverflow,
    OverlappingFinalTargets,
    NoSafeStagingPosition,
    SimulationDeadlock,
    FinalStateMismatch,
}
