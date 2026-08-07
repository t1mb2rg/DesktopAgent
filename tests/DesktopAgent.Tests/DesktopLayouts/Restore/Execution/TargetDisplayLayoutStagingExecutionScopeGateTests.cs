using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class TargetDisplayLayoutStagingExecutionScopeGateTests
{
    private const string TD = @"\\.\DISPLAY1";
    private const string TD2 = @"\\.\DISPLAY2";
    private static readonly PixelPoint Spacing = new(80, 80);

    [Fact]
    public void PlannerGeneratedTwoItemSwap_IsAllowed()
    {
        var (restore, execution) = MakeValidSwap();

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, execution);

        Assert.True(result.IsAllowed);
        Assert.Empty(result.RejectionReasons);
    }

    [Fact]
    public void RestoreWithoutStaging_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var changed = CopyRestore(restore, requiresTemporaryStaging: false);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(changed, execution);

        AssertRejected(result, "RequiresTemporaryStaging");
    }

    [Fact]
    public void RestoreWithoutCycles_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var changed = CopyRestore(restore, cycles: Array.Empty<TargetDisplayLayoutRestoreCycle>());

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(changed, execution);

        AssertRejected(result, "Cycles");
    }

    [Fact]
    public void RestoreWithAcyclicMoveOrder_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var changed = CopyRestore(
            restore,
            moveExecutionOrder: new[] { "a", "b" },
            replaceMoveExecutionOrder: true);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(changed, execution);

        AssertRejected(result, "MoveExecutionOrder");
    }

    [Fact]
    public void MissingOrBlockingRestore_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var changed = CopyRestore(
            restore,
            missingCount: 1,
            blockedCount: 1,
            hasBlockingErrors: true);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(changed, execution);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("MissingCount", StringComparison.Ordinal));
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("BlockedCount", StringComparison.Ordinal));
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("HasBlockingErrors", StringComparison.Ordinal));
    }

    [Fact]
    public void MoveRequiredCountDrift_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var changed = CopyRestore(restore, moveRequiredCount: restore.MoveRequiredCount + 1);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(changed, execution);

        AssertRejected(result, "MoveRequiredCount");
    }

    [Fact]
    public void IneligibleMoveItem_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var items = restore.Items.ToArray();
        var index = Array.FindIndex(items, item => item.State == TargetDisplayLayoutRestoreItemState.MoveRequired);
        items[index] = items[index] with
        {
            IsFileSystemItem = false,
            IsSystemItem = true,
            CurrentMonitorDeviceName = null,
        };
        var changed = CopyRestore(restore, items: items);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(changed, execution);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("IsFileSystemItem", StringComparison.Ordinal));
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("IsSystemItem", StringComparison.Ordinal));
        Assert.Contains(result.RejectionReasons, reason => reason.Contains("CurrentMonitorDeviceName", StringComparison.Ordinal));
    }

    [Fact]
    public void ExecutionWithoutStaging_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var directOnlySteps = execution.Steps
            .Where(step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTarget)
            .Select((step, index) => step with { StepIndex = index })
            .ToArray();
        var changed = CopyExecution(
            execution,
            steps: directOnlySteps,
            stagingAssignments: Array.Empty<TargetDisplayLayoutStagingAssignment>());

        Assert.False(changed.UsesTemporaryStaging);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "UsesTemporaryStaging");
    }

    [Fact]
    public void ExecutionWithoutAssignments_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var changed = CopyExecution(
            execution,
            stagingAssignments: Array.Empty<TargetDisplayLayoutStagingAssignment>());

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "StagingAssignments");
    }

    [Fact]
    public void TargetDisplayMismatch_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var changed = CopyExecution(execution, targetDeviceName: TD2);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "TargetDeviceName");
    }

    [Fact]
    public void StepCountMismatch_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var changed = CopyExecution(execution, steps: execution.Steps.Take(execution.Steps.Count - 1).ToArray());

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "Steps.Count");
    }

    [Fact]
    public void DiscontinuousStepIndex_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var steps = execution.Steps.ToArray();
        steps[1] = steps[1] with { StepIndex = 99 };
        var changed = CopyExecution(execution, steps: steps);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "StepIndex");
    }

    [Fact]
    public void UnknownStepStableId_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var steps = execution.Steps.ToArray();
        steps[1] = steps[1] with { StableId = "unknown" };
        var changed = CopyExecution(execution, steps: steps);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "不能映射到 MoveRequired");
    }

    [Fact]
    public void ZeroLengthStep_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var steps = execution.Steps.ToArray();
        steps[1] = steps[1] with { ToPosition = steps[1].FromPosition };
        var changed = CopyExecution(execution, steps: steps);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "FromPosition == ToPosition");
    }

    [Fact]
    public void WrongExpectedMonitor_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var steps = execution.Steps.ToArray();
        steps[0] = steps[0] with { ExpectedMonitorDeviceName = TD2 };
        var changed = CopyExecution(execution, steps: steps);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "ExpectedMonitorDeviceName");
    }

    [Fact]
    public void StagingEnterWithoutSequence_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var steps = execution.Steps.ToArray();
        var enterIndex = Array.FindIndex(
            steps,
            step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging);
        steps[enterIndex] = steps[enterIndex] with { StagingSequence = null };
        var changed = CopyExecution(execution, steps: steps);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "StagingSequence");
    }

    [Fact]
    public void StagingSequenceGap_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var steps = execution.Steps.ToArray();
        var enterIndex = Array.FindIndex(
            steps,
            step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging);
        steps[enterIndex] = steps[enterIndex] with { StagingSequence = 3 };
        var changed = CopyExecution(execution, steps: steps);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "StagingSequence 集合");
    }

    [Fact]
    public void AssignmentStagingPositionMismatch_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var assignments = execution.StagingAssignments.ToArray();
        assignments[0] = assignments[0] with { StagingPosition = new PixelPoint(999, 999) };
        var changed = CopyExecution(execution, stagingAssignments: assignments);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "enter step 不一致");
    }

    [Fact]
    public void AssignmentIndexesOutOfOrder_AreRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var assignments = execution.StagingAssignments.ToArray();
        assignments[0] = assignments[0] with
        {
            EnterStepIndex = assignments[0].ExitStepIndex,
            ExitStepIndex = assignments[0].EnterStepIndex,
        };
        var changed = CopyExecution(execution, stagingAssignments: assignments);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        AssertRejected(result, "Enter/Exit index 非法");
    }

    [Fact]
    public void StagedItemWithWrongExitKind_IsRejected()
    {
        var (restore, execution) = MakeValidSwap();
        var steps = execution.Steps.ToArray();
        var exitIndex = Array.FindIndex(
            steps,
            step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget);
        steps[exitIndex] = steps[exitIndex] with { Kind = TargetDisplayLayoutExecutionStepKind.MoveToTarget };
        var changed = CopyExecution(execution, steps: steps);

        var result = TargetDisplayLayoutStagingExecutionScopeGate.Evaluate(restore, changed);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons,
            reason => reason.Contains("exit step 不一致", StringComparison.Ordinal)
                      || reason.Contains("staged item", StringComparison.Ordinal));
    }

    private static void AssertRejected(
        TargetDisplayLayoutExecutionScopeGate.GateResult result,
        string expectedFragment)
    {
        Assert.False(result.IsAllowed);
        Assert.Contains(result.RejectionReasons,
            reason => reason.Contains(expectedFragment, StringComparison.Ordinal));
    }

    private static (TargetDisplayLayoutRestorePlan Restore, TargetDisplayLayoutExecutionPlan Execution)
        MakeValidSwap()
    {
        var display = new DisplayDescriptor(
            TD,
            new PixelRect(0, 0, 320, 160),
            new PixelRect(0, 0, 320, 160),
            96,
            96,
            true);
        var caps = new DesktopIconPositioningCapabilities(
            false,
            Spacing,
            ConnectionMethod.FindWindowSW,
            null);

        var snapshot = new DesktopLayoutSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            TD,
            ConnectionMethod.FindWindowSW,
            1,
            new[]
            {
                new DesktopIconSnapshotItem(
                    0, "a", "A", null, ItemKind.File,
                    new PixelPoint(80, 0), TD, false, true),
                new DesktopIconSnapshotItem(
                    1, "b", "B", null, ItemKind.File,
                    new PixelPoint(0, 0), TD, false, true),
            });
        var read = new DesktopIconReadResult(
            new[]
            {
                new DesktopIconDescriptor(
                    "a", "A", null, ItemKind.File,
                    new PixelPoint(0, 0), TD, false, true),
                new DesktopIconDescriptor(
                    "b", "B", null, ItemKind.File,
                    new PixelPoint(80, 0), TD, false, true),
            },
            0,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);

        var displays = new[] { display };
        var restore = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snapshot,
            read,
            displays,
            caps,
            TD);
        var execution = TargetDisplayLayoutExecutionPlanner.CreatePlan(
            restore,
            read,
            displays,
            caps);

        Assert.True(restore.RequiresTemporaryStaging);
        Assert.True(execution.UsesTemporaryStaging);
        Assert.NotEmpty(execution.StagingAssignments);
        return (restore, execution);
    }

    private static TargetDisplayLayoutRestorePlan CopyRestore(
        TargetDisplayLayoutRestorePlan source,
        int? moveRequiredCount = null,
        int? missingCount = null,
        int? blockedCount = null,
        bool? hasBlockingErrors = null,
        bool? requiresTemporaryStaging = null,
        IReadOnlyList<TargetDisplayLayoutRestorePlanItem>? items = null,
        IReadOnlyList<TargetDisplayLayoutRestoreCycle>? cycles = null,
        IReadOnlyList<string>? moveExecutionOrder = null,
        bool replaceMoveExecutionOrder = false)
        => new(
            source.TargetDeviceName,
            source.SnapshotTargetIconCount,
            source.AlreadyAtTargetCount,
            moveRequiredCount ?? source.MoveRequiredCount,
            missingCount ?? source.MissingCount,
            blockedCount ?? source.BlockedCount,
            hasBlockingErrors ?? source.HasBlockingErrors,
            requiresTemporaryStaging ?? source.RequiresTemporaryStaging,
            source.IsNoOp,
            items ?? source.Items,
            source.Dependencies,
            cycles ?? source.Cycles,
            source.Issues,
            replaceMoveExecutionOrder ? moveExecutionOrder : source.MoveExecutionOrder);

    private static TargetDisplayLayoutExecutionPlan CopyExecution(
        TargetDisplayLayoutExecutionPlan source,
        string? targetDeviceName = null,
        IReadOnlyList<TargetDisplayLayoutExecutionStep>? steps = null,
        IReadOnlyList<TargetDisplayLayoutStagingAssignment>? stagingAssignments = null)
        => new(
            targetDeviceName ?? source.TargetDeviceName,
            source.IsExecutable,
            source.IsNoOp,
            steps ?? source.Steps,
            stagingAssignments ?? source.StagingAssignments,
            source.Issues);
}
