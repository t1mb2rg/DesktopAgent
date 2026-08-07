using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

/// <summary>
/// cycle / temporary staging 真实执行的专用 ScopeGate。
///
/// 本 Gate 不取代或放宽 EvaluateNoStagingBatch；它只接受由 fresh planner 生成、
/// 且 SourceRestorePlan / ExecutionPlan / StagingAssignment 三者结构一致的 staging 计划。
/// 几何碰撞、工作区、IconSpacing、依赖图和当前桌面陈旧性仍由 Writer 前重新规划的
/// TargetDisplayLayoutExecutionPlanner 负责；本层只决定该最新计划是否属于用户已授权的 staging 执行范围。
/// </summary>
internal static class TargetDisplayLayoutStagingExecutionScopeGate
{
    public static TargetDisplayLayoutExecutionScopeGate.GateResult Evaluate(
        TargetDisplayLayoutRestorePlan restorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan)
    {
        ArgumentNullException.ThrowIfNull(restorePlan);
        ArgumentNullException.ThrowIfNull(executionPlan);

        var reasons = new List<string>();

        if (restorePlan.HasBlockingErrors)
            reasons.Add("HasBlockingErrors 为 true。");
        if (restorePlan.MissingCount != 0)
            reasons.Add($"MissingCount = {restorePlan.MissingCount}，预期 0。");
        if (restorePlan.BlockedCount != 0)
            reasons.Add($"BlockedCount = {restorePlan.BlockedCount}，预期 0。");
        if (restorePlan.MoveRequiredCount <= 0)
            reasons.Add($"MoveRequiredCount = {restorePlan.MoveRequiredCount}，预期 > 0。");
        if (restorePlan.IsNoOp)
            reasons.Add("RestorePlan.IsNoOp 为 true。");
        if (!restorePlan.RequiresTemporaryStaging)
            reasons.Add("RestorePlan.RequiresTemporaryStaging 为 false；staging Gate 只接受需要中转的计划。");
        if (restorePlan.Cycles is null || restorePlan.Cycles.Count == 0)
            reasons.Add("RestorePlan.Cycles 为空；需要中转的计划必须携带已验证的循环信息。");
        if (restorePlan.MoveExecutionOrder is not null)
            reasons.Add("RestorePlan.MoveExecutionOrder 不为 null；cycle/staging 计划不应携带无环执行顺序。");

        var moveRequiredItems = restorePlan.Items
            .Where(item => item.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
            .ToArray();

        if (moveRequiredItems.Length != restorePlan.MoveRequiredCount)
        {
            reasons.Add(
                $"MoveRequiredCount 字段 = {restorePlan.MoveRequiredCount}，" +
                $"但实际 MoveRequired item 数量 = {moveRequiredItems.Length}。");
        }

        var itemByStableId = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(StringComparer.Ordinal);
        foreach (var item in moveRequiredItems)
        {
            if (string.IsNullOrWhiteSpace(item.StableId))
            {
                reasons.Add("存在 StableId 为空白的 MoveRequired item。");
                continue;
            }

            if (!itemByStableId.TryAdd(item.StableId, item))
                reasons.Add($"MoveRequired items 中 StableId \"{item.StableId}\" 重复。");

            if (!item.IsFileSystemItem)
                reasons.Add($"MoveRequired item \"{item.StableId}\" IsFileSystemItem 为 false。");
            if (item.IsSystemItem)
                reasons.Add($"MoveRequired item \"{item.StableId}\" IsSystemItem 为 true。");
            if (item.CurrentPosition is null)
                reasons.Add($"MoveRequired item \"{item.StableId}\" CurrentPosition 为 null。");
            if (string.IsNullOrWhiteSpace(item.CurrentMonitorDeviceName))
                reasons.Add($"MoveRequired item \"{item.StableId}\" CurrentMonitorDeviceName 为空。");
        }

        var nonMoveStableIds = restorePlan.Items
            .Where(item => item.State != TargetDisplayLayoutRestoreItemState.MoveRequired)
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);

        if (!executionPlan.IsExecutable)
            reasons.Add("ExecutionPlan.IsExecutable 为 false。");
        if (executionPlan.IsNoOp)
            reasons.Add("ExecutionPlan.IsNoOp 为 true。");
        if (!executionPlan.UsesTemporaryStaging)
            reasons.Add("ExecutionPlan.UsesTemporaryStaging 为 false；staging Gate 不接受无中转计划。");
        if (executionPlan.Steps.Count == 0)
            reasons.Add("ExecutionPlan.Steps 为空。");
        if (executionPlan.StagingAssignments.Count == 0)
            reasons.Add("ExecutionPlan.StagingAssignments 为空。");

        if (!string.Equals(
                executionPlan.TargetDeviceName,
                restorePlan.TargetDeviceName,
                StringComparison.Ordinal))
        {
            reasons.Add("ExecutionPlan.TargetDeviceName ≠ RestorePlan.TargetDeviceName（Ordinal）。");
        }

        // 每个 MoveRequired 最少有一个最终目标 step；每个 staged item 额外多一个 enter step。
        var expectedStepCount = moveRequiredItems.Length + executionPlan.StagingAssignments.Count;
        if (executionPlan.Steps.Count != expectedStepCount)
        {
            reasons.Add(
                $"ExecutionPlan.Steps.Count = {executionPlan.Steps.Count}，" +
                $"预期 MoveRequired({moveRequiredItems.Length}) + StagingAssignments" +
                $"({executionPlan.StagingAssignments.Count}) = {expectedStepCount}。");
        }

        var stepsByStableId = new Dictionary<string, List<TargetDisplayLayoutExecutionStep>>(StringComparer.Ordinal);
        var enterSequences = new List<int>();

        for (var i = 0; i < executionPlan.Steps.Count; i++)
        {
            var step = executionPlan.Steps[i];

            if (step.StepIndex != i)
                reasons.Add($"Steps[{i}].StepIndex = {step.StepIndex}，预期 {i}。");

            if (string.IsNullOrWhiteSpace(step.StableId))
            {
                reasons.Add($"Steps[{i}] StableId 为空白。");
                continue;
            }

            if (!stepsByStableId.TryGetValue(step.StableId, out var perStableId))
            {
                perStableId = [];
                stepsByStableId.Add(step.StableId, perStableId);
            }
            perStableId.Add(step);

            if (!itemByStableId.ContainsKey(step.StableId))
                reasons.Add($"Steps[{i}] StableId \"{step.StableId}\" 不能映射到 MoveRequired item。");
            if (nonMoveStableIds.Contains(step.StableId))
                reasons.Add($"Steps[{i}] StableId \"{step.StableId}\" 属于非 MoveRequired item。");
            if (step.FromPosition == step.ToPosition)
                reasons.Add($"Steps[{i}] (\"{step.StableId}\") FromPosition == ToPosition。");
            if (!string.Equals(
                    step.ExpectedMonitorDeviceName,
                    restorePlan.TargetDeviceName,
                    StringComparison.Ordinal))
            {
                reasons.Add($"Steps[{i}] ExpectedMonitorDeviceName ≠ RestorePlan.TargetDeviceName（Ordinal）。");
            }

            switch (step.Kind)
            {
                case TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging:
                    if (step.StagingSequence is null || step.StagingSequence < 0)
                        reasons.Add($"Steps[{i}] staging enter 缺少合法 StagingSequence。");
                    else
                        enterSequences.Add(step.StagingSequence.Value);
                    break;

                case TargetDisplayLayoutExecutionStepKind.MoveToTarget:
                case TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget:
                    if (step.StagingSequence is not null)
                        reasons.Add($"Steps[{i}] Kind={step.Kind} 不应携带 StagingSequence。");
                    break;

                default:
                    reasons.Add($"Steps[{i}] 包含未知 StepKind：{step.Kind}。");
                    break;
            }
        }

        if (enterSequences.Count != enterSequences.Distinct().Count())
            reasons.Add("staging enter steps 的 StagingSequence 存在重复。");

        var expectedSequences = Enumerable.Range(0, executionPlan.StagingAssignments.Count).ToArray();
        var actualSequences = enterSequences.OrderBy(value => value).ToArray();
        if (!actualSequences.SequenceEqual(expectedSequences))
        {
            reasons.Add(
                $"staging enter StagingSequence 集合 [{string.Join(",", actualSequences)}] " +
                $"≠ 预期 [{string.Join(",", expectedSequences)}]。");
        }

        var assignmentStableIds = new HashSet<string>(StringComparer.Ordinal);
        var assignmentEnterIndexes = new HashSet<int>();
        var assignmentExitIndexes = new HashSet<int>();
        var assignmentStagingPositions = new HashSet<Core.Geometry.PixelPoint>();

        foreach (var assignment in executionPlan.StagingAssignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.StableId))
            {
                reasons.Add("存在 StableId 为空白的 StagingAssignment。");
                continue;
            }

            if (!assignmentStableIds.Add(assignment.StableId))
                reasons.Add($"StagingAssignments 中 StableId \"{assignment.StableId}\" 重复。");
            if (!assignmentStagingPositions.Add(assignment.StagingPosition))
                reasons.Add($"StagingAssignments 中 staging position {assignment.StagingPosition} 重复。");

            if (!itemByStableId.TryGetValue(assignment.StableId, out var item))
            {
                reasons.Add($"StagingAssignment \"{assignment.StableId}\" 不能映射到 MoveRequired item。");
                continue;
            }

            if (assignment.EnterStepIndex < 0
                || assignment.ExitStepIndex < 0
                || assignment.EnterStepIndex >= executionPlan.Steps.Count
                || assignment.ExitStepIndex >= executionPlan.Steps.Count
                || assignment.EnterStepIndex >= assignment.ExitStepIndex)
            {
                reasons.Add(
                    $"StagingAssignment \"{assignment.StableId}\" Enter/Exit index 非法：" +
                    $"{assignment.EnterStepIndex}/{assignment.ExitStepIndex}。");
                continue;
            }

            if (!assignmentEnterIndexes.Add(assignment.EnterStepIndex))
                reasons.Add($"多个 StagingAssignment 共享 EnterStepIndex={assignment.EnterStepIndex}。");
            if (!assignmentExitIndexes.Add(assignment.ExitStepIndex))
                reasons.Add($"多个 StagingAssignment 共享 ExitStepIndex={assignment.ExitStepIndex}。");

            var enter = executionPlan.Steps[assignment.EnterStepIndex];
            var exit = executionPlan.Steps[assignment.ExitStepIndex];

            if (enter.Kind != TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging
                || !string.Equals(enter.StableId, assignment.StableId, StringComparison.Ordinal)
                || enter.ToPosition != assignment.StagingPosition
                || (item.CurrentPosition.HasValue && enter.FromPosition != item.CurrentPosition.Value))
            {
                reasons.Add($"StagingAssignment \"{assignment.StableId}\" 与 enter step 不一致。");
            }

            if (exit.Kind != TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget
                || !string.Equals(exit.StableId, assignment.StableId, StringComparison.Ordinal)
                || exit.FromPosition != assignment.StagingPosition
                || exit.ToPosition != item.SnapshotTargetPosition)
            {
                reasons.Add($"StagingAssignment \"{assignment.StableId}\" 与 exit step 不一致。");
            }
        }

        // 每个 staged item 必须正好 enter + exit 两步；非 staged item 必须正好一个直接目标步。
        foreach (var item in moveRequiredItems)
        {
            stepsByStableId.TryGetValue(item.StableId, out var itemSteps);
            itemSteps ??= [];

            if (assignmentStableIds.Contains(item.StableId))
            {
                if (itemSteps.Count != 2
                    || itemSteps.Count(step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging) != 1
                    || itemSteps.Count(step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget) != 1)
                {
                    reasons.Add(
                        $"staged item \"{item.StableId}\" 必须且只能包含一条 enter 和一条 exit step。");
                }
            }
            else
            {
                if (itemSteps.Count != 1
                    || itemSteps[0].Kind != TargetDisplayLayoutExecutionStepKind.MoveToTarget)
                {
                    reasons.Add(
                        $"非 staged MoveRequired item \"{item.StableId}\" 必须且只能包含一条 MoveToTarget step。");
                }
                else
                {
                    var step = itemSteps[0];
                    if (item.CurrentPosition.HasValue && step.FromPosition != item.CurrentPosition.Value)
                        reasons.Add($"直接 step \"{item.StableId}\" FromPosition ≠ item.CurrentPosition。");
                    if (step.ToPosition != item.SnapshotTargetPosition)
                        reasons.Add($"直接 step \"{item.StableId}\" ToPosition ≠ item.SnapshotTargetPosition。");
                }
            }
        }

        return reasons.Count == 0
            ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
            : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(reasons);
    }
}
