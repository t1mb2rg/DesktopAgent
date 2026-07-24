using System.Collections.ObjectModel;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

using static TargetDisplayLayoutExecutionStepKind;

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
        IReadOnlyList<TargetDisplayLayoutExecutionStep> steps,
        IReadOnlyList<TargetDisplayLayoutStagingAssignment> stagingAssignments,
        IReadOnlyList<TargetDisplayLayoutExecutionPlanIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(targetDeviceName);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(stagingAssignments);
        ArgumentNullException.ThrowIfNull(issues);

        TargetDeviceName = targetDeviceName;
        IsExecutable = isExecutable;
        IsNoOp = isNoOp;

        Steps = Array.AsReadOnly(steps.ToArray());
        StagingAssignments = Array.AsReadOnly(stagingAssignments.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());

        DirectMoveStepCount = Steps.Count(s => s.Kind == MoveToTarget);
        StagingMoveStepCount = Steps.Count(s => s.Kind == MoveToTemporaryStaging);
        TargetMoveStepCount = Steps.Count(s => s.Kind == MoveFromTemporaryStagingToTarget);
        UsesTemporaryStaging = StagingMoveStepCount > 0;
    }
}
