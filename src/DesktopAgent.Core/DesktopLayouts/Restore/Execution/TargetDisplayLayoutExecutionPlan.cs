namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

public sealed record TargetDisplayLayoutExecutionPlan
{
    public string TargetDeviceName { get; }
    public bool IsExecutable { get; }
    public bool IsNoOp { get; }
    public bool UsesTemporaryStaging { get; }
    public int DirectMoveStepCount { get; }
    public int StagingMoveStepCount { get; }
    public int TargetMoveStepCount { get; }
    public IReadOnlyList<TargetDisplayLayoutExecutionStep> Steps { get; }
    public IReadOnlyList<TargetDisplayLayoutStagingAssignment> StagingAssignments { get; }
    public IReadOnlyList<TargetDisplayLayoutExecutionPlanIssue> Issues { get; }

    public TargetDisplayLayoutExecutionPlan(
        string targetDeviceName,
        bool isExecutable,
        bool isNoOp,
        bool usesTemporaryStaging,
        int directMoveStepCount,
        int stagingMoveStepCount,
        int targetMoveStepCount,
        IReadOnlyList<TargetDisplayLayoutExecutionStep> steps,
        IReadOnlyList<TargetDisplayLayoutStagingAssignment> stagingAssignments,
        IReadOnlyList<TargetDisplayLayoutExecutionPlanIssue> issues)
    {
        TargetDeviceName = targetDeviceName;
        IsExecutable = isExecutable;
        IsNoOp = isNoOp;
        UsesTemporaryStaging = usesTemporaryStaging;
        DirectMoveStepCount = directMoveStepCount;
        StagingMoveStepCount = stagingMoveStepCount;
        TargetMoveStepCount = targetMoveStepCount;
        Steps = steps;
        StagingAssignments = stagingAssignments;
        Issues = issues;
    }
}
