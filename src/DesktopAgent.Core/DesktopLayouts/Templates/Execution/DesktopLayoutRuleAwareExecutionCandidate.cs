using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Execution;

public sealed class DesktopLayoutRuleAwareExecutionCandidate
{
    public DesktopLayoutRuleAwarePreviewPlan SourcePreviewPlan { get; }
    public DesktopLayoutSnapshot TargetSnapshot { get; }
    public TargetDisplayLayoutRestorePlan RestorePlan { get; }
    public TargetDisplayLayoutExecutionPlan ExecutionPlan { get; }
    public DisplayDescriptor TargetDisplay { get; }
    public string TargetDeviceName { get; }

    public int InputIconCount { get; }
    public int FixedCount { get; }
    public int AssignedCount { get; }
    public int AlreadyAtTargetCount { get; }
    public int MoveRequiredCount { get; }
    public int RestoreBlockedCount { get; }
    public int RestoreMissingCount { get; }
    public int ExecutionStepCount { get; }
    public bool UsesTemporaryStaging { get; }
    public bool IsNoOp { get; }
    public bool IsExecutable { get; }
    public bool RequiresUserConfirmation { get; }

    internal DesktopLayoutRuleAwareExecutionCandidate(
        DesktopLayoutRuleAwarePreviewPlan sourcePreviewPlan,
        DesktopLayoutSnapshot targetSnapshot,
        TargetDisplayLayoutRestorePlan restorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan,
        DisplayDescriptor targetDisplay)
    {
        if (sourcePreviewPlan is null) throw new ArgumentNullException(nameof(sourcePreviewPlan));
        if (targetSnapshot is null) throw new ArgumentNullException(nameof(targetSnapshot));
        if (restorePlan is null) throw new ArgumentNullException(nameof(restorePlan));
        if (executionPlan is null) throw new ArgumentNullException(nameof(executionPlan));
        if (targetDisplay is null) throw new ArgumentNullException(nameof(targetDisplay));

        // 引用链验证
        if (!ReferenceEquals(sourcePreviewPlan.TargetDisplay, targetDisplay))
            throw new ArgumentException("TargetDisplay 与 SourcePreviewPlan.TargetDisplay 不是同一实例。");
        if (!string.Equals(targetSnapshot.TargetMonitorDeviceName,
                sourcePreviewPlan.TargetDisplay.DeviceName, StringComparison.Ordinal))
            throw new ArgumentException(
                $"TargetSnapshot.TargetMonitorDeviceName ({targetSnapshot.TargetMonitorDeviceName}) " +
                $"与 Preview TargetDisplay ({sourcePreviewPlan.TargetDisplay.DeviceName}) 不一致。");
        if (!string.Equals(restorePlan.TargetDeviceName, sourcePreviewPlan.TargetDisplay.DeviceName, StringComparison.Ordinal))
            throw new ArgumentException("RestorePlan.TargetDeviceName 不一致。");
        if (!string.Equals(executionPlan.TargetDeviceName, sourcePreviewPlan.TargetDisplay.DeviceName, StringComparison.Ordinal))
            throw new ArgumentException("ExecutionPlan.TargetDeviceName 不一致。");

        // NoOp 自洽
        bool isNoOp = sourcePreviewPlan.MoveRequiredCount == 0
            && restorePlan.IsNoOp && executionPlan.IsNoOp;
        if (isNoOp && (executionPlan.Steps.Count > 0 || restorePlan.MoveRequiredCount > 0))
            throw new ArgumentException("NoOp 状态不一致：ExecutionPlan 包含步骤或 RestorePlan 包含 MoveRequired。");

        // IsExecutable 自洽
        bool isExecutable = sourcePreviewPlan.IsComplete
            && !restorePlan.HasBlockingErrors
            && executionPlan.IsExecutable;
        if (isExecutable && executionPlan.Steps.Count == 0 && !isNoOp)
            throw new ArgumentException("Executable 但无步骤且非 NoOp。");

        SourcePreviewPlan = sourcePreviewPlan;
        TargetSnapshot = targetSnapshot;
        RestorePlan = restorePlan;
        ExecutionPlan = executionPlan;
        TargetDisplay = targetDisplay;
        TargetDeviceName = targetDisplay.DeviceName;

        InputIconCount = sourcePreviewPlan.InputIconCount;
        FixedCount = sourcePreviewPlan.ExcludedCount + sourcePreviewPlan.PinnedCount;
        AssignedCount = sourcePreviewPlan.OverrideAssignedCount + sourcePreviewPlan.DefaultAssignedCount;
        AlreadyAtTargetCount = sourcePreviewPlan.AlreadyAtTargetCount;
        MoveRequiredCount = sourcePreviewPlan.MoveRequiredCount;
        RestoreBlockedCount = restorePlan.BlockedCount;
        RestoreMissingCount = restorePlan.MissingCount;
        ExecutionStepCount = executionPlan.Steps.Count;
        UsesTemporaryStaging = executionPlan.UsesTemporaryStaging;
        IsNoOp = isNoOp;
        IsExecutable = isExecutable;
        RequiresUserConfirmation = IsExecutable && !IsNoOp;
    }
}
