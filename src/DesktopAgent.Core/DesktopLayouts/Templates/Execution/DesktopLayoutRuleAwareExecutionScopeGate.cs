using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Execution;

using static TargetDisplayLayoutExecutionStepKind;
using static TargetDisplayLayoutRestoreItemState;

/// <summary>
/// 批量无中转执行范围闸门。
/// 纯逻辑，不依赖 COM/PIDL/HWND/WPF。
/// Fail-closed：任何歧义或验证失败均拒绝。
/// 允许：单图标直接移动、多独立移动、DAG 依赖链（无中转）。
/// 禁止：staging、循环交换、阻断/缺失、非文件系统项。
/// </summary>
public static class DesktopLayoutRuleAwareExecutionScopeGate
{
    public sealed class GateResult
    {
        public bool IsAllowed { get; }
        public IReadOnlyList<string> RejectionReasons { get; }

        public GateResult(bool isAllowed, IReadOnlyList<string> rejectionReasons)
        {
            IsAllowed = isAllowed;
            RejectionReasons = rejectionReasons;
        }

        public static GateResult Allowed() => new(true, Array.Empty<string>());
        public static GateResult Rejected(List<string> reasons) =>
            new(false, reasons.ToArray());
    }

    /// <summary>
    /// 评估无中转批量执行计划是否安全可执行。
    /// 返回所有拒绝原因（不早退于第一条），以便诊断。
    /// </summary>
    public static GateResult EvaluateDirectExecution(
        TargetDisplayLayoutRestorePlan restorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan)
    {
        var reasons = new List<string>();

        // ═══ 1. RestorePlan 全局检查 ═══
        if (restorePlan.HasBlockingErrors)
            reasons.Add("RestorePlan.HasBlockingErrors 为 true。");

        if (restorePlan.MissingCount != 0)
            reasons.Add($"RestorePlan.MissingCount = {restorePlan.MissingCount}，预期 0。");

        if (restorePlan.BlockedCount != 0)
            reasons.Add($"RestorePlan.BlockedCount = {restorePlan.BlockedCount}，预期 0。");

        if (restorePlan.RequiresTemporaryStaging)
            reasons.Add("RestorePlan.RequiresTemporaryStaging 为 true，无中转批量执行不允许循环依赖。");

        if (restorePlan.IsNoOp)
            reasons.Add("RestorePlan.IsNoOp 为 true，无操作计划不应执行。");

        // ═══ 2. 枚举 MoveRequired 项（使用 .ToList()，避免 First/Single） ═══
        var moveRequiredItems = restorePlan.Items
            .Where(it => it.State == MoveRequired)
            .ToList();

        if (moveRequiredItems.Count == 0)
        {
            reasons.Add("没有 MoveRequired 状态的项。");
        }

        if (moveRequiredItems.Count != restorePlan.MoveRequiredCount)
        {
            reasons.Add($"实际 MoveRequired 项数 ({moveRequiredItems.Count}) ≠ " +
                $"MoveRequiredCount 字段 ({restorePlan.MoveRequiredCount})，摘要不一致。");
        }

        // ═══ 3. ExecutionPlan 全局检查 ═══
        if (!executionPlan.IsExecutable)
            reasons.Add("ExecutionPlan.IsExecutable 为 false。");

        if (executionPlan.IsNoOp)
            reasons.Add("ExecutionPlan.IsNoOp 为 true。");

        if (executionPlan.UsesTemporaryStaging)
            reasons.Add("ExecutionPlan.UsesTemporaryStaging 为 true，无中转批量执行不允许临时中转。");

        if (executionPlan.Steps.Count != moveRequiredItems.Count)
        {
            reasons.Add($"ExecutionPlan.Steps.Count ({executionPlan.Steps.Count}) ≠ " +
                $"MoveRequired 项数 ({moveRequiredItems.Count})。");
        }

        if (!string.Equals(executionPlan.TargetDeviceName, restorePlan.TargetDeviceName,
                StringComparison.Ordinal))
        {
            reasons.Add("ExecutionPlan.TargetDeviceName ≠ RestorePlan.TargetDeviceName。");
        }

        // ═══ 4. 逐项 MoveRequired 验证 ═══
        var moveSidSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in moveRequiredItems)
        {
            if (!moveSidSet.Add(item.StableId))
            {
                reasons.Add($"MoveRequired 项中 StableId \"{item.StableId}\" 重复。");
            }

            if (!item.IsFileSystemItem)
                reasons.Add($"MoveRequired \"{item.StableId}\" IsFileSystemItem 为 false。");

            if (item.IsSystemItem)
                reasons.Add($"MoveRequired \"{item.StableId}\" IsSystemItem 为 true。");

            if (item.CurrentPosition is null)
                reasons.Add($"MoveRequired \"{item.StableId}\" CurrentPosition 为 null。");

            if (item.CurrentMonitorDeviceName is null)
                reasons.Add($"MoveRequired \"{item.StableId}\" CurrentMonitorDeviceName 为 null。");
        }

        // ═══ 5. 逐 Step 验证 ═══
        var seenStepSids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < executionPlan.Steps.Count; i++)
        {
            var step = executionPlan.Steps[i];

            if (step.StepIndex != i)
                reasons.Add($"Step[{i}] StepIndex = {step.StepIndex}，预期 {i}。");

            if (string.IsNullOrWhiteSpace(step.StableId))
                reasons.Add($"Step[{i}] StableId 为空。");

            if (!seenStepSids.Add(step.StableId))
                reasons.Add($"Step[{i}] StableId \"{step.StableId}\" 在 Steps 中重复。");

            if (step.Kind != MoveToTarget)
                reasons.Add($"Step[{i}] Kind = {step.Kind}，预期 MoveToTarget。" +
                    $"无中转批量执行不允许临时中转步骤。");

            if (step.FromPosition == step.ToPosition)
                reasons.Add($"Step[{i}] FromPosition == ToPosition " +
                    $"({step.FromPosition.X},{step.FromPosition.Y})，零长度移动。");

            if (step.FromPosition.X == step.ToPosition.X && step.FromPosition.Y == step.ToPosition.Y)
                reasons.Add($"Step[{i}] FromPosition 与 ToPosition 同坐标。");

            if (!moveSidSet.Contains(step.StableId))
                reasons.Add($"Step[{i}] StableId \"{step.StableId}\" 不在 MoveRequired 项中。");

            if (!string.Equals(step.ExpectedMonitorDeviceName, restorePlan.TargetDeviceName,
                    StringComparison.Ordinal))
            {
                reasons.Add($"Step[{i}] ExpectedMonitorDeviceName " +
                    $"\"{step.ExpectedMonitorDeviceName}\" ≠ TargetDeviceName " +
                    $"\"{restorePlan.TargetDeviceName}\"。");
            }
        }

        // 检查是否有 MoveRequired 项无对应 Step
        foreach (var sid in moveSidSet)
        {
            if (!seenStepSids.Contains(sid))
                reasons.Add($"MoveRequired StableId \"{sid}\" 无对应 Step。");
        }

        // ═══ 6. Step ↔ Item 坐标匹配 ═══
        // 仅当 MoveRequired 项数一致且无重复 StableId 时执行
        if (moveRequiredItems.Count == executionPlan.Steps.Count
            && moveSidSet.Count == moveRequiredItems.Count
            && seenStepSids.Count == executionPlan.Steps.Count)
        {
            var itemBySid = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(
                StringComparer.Ordinal);
            foreach (var item in moveRequiredItems)
                itemBySid[item.StableId] = item;

            for (var i = 0; i < executionPlan.Steps.Count; i++)
            {
                var step = executionPlan.Steps[i];
                if (!itemBySid.TryGetValue(step.StableId, out var item))
                    continue; // 已在上面报告

                if (item.CurrentPosition.HasValue
                    && step.FromPosition != item.CurrentPosition.Value)
                {
                    reasons.Add($"Step[{i}] \"{step.StableId}\" FromPosition " +
                        $"({step.FromPosition.X},{step.FromPosition.Y}) ≠ " +
                        $"item.CurrentPosition ({item.CurrentPosition.Value.X}," +
                        $"{item.CurrentPosition.Value.Y})。");
                }

                if (step.ToPosition != item.SnapshotTargetPosition)
                {
                    reasons.Add($"Step[{i}] \"{step.StableId}\" ToPosition " +
                        $"({step.ToPosition.X},{step.ToPosition.Y}) ≠ " +
                        $"item.SnapshotTargetPosition ({item.SnapshotTargetPosition.X}," +
                        $"{item.SnapshotTargetPosition.Y})。");
                }
            }
        }

        // ═══ 7. DAG 无中转可行性模拟 ═══
        // 仅当前面所有检查都通过时才执行（reasons 为空）
        if (reasons.Count == 0)
        {
            var simError = SimulateNoStagingExecution(restorePlan, executionPlan, moveRequiredItems);
            if (simError is not null)
                reasons.Add(simError);
        }

        return reasons.Count == 0
            ? GateResult.Allowed()
            : GateResult.Rejected(reasons);
    }

    /// <summary>
    /// 模拟按 Step 顺序执行，验证每一步的目标位置在模拟时刻均未被占用。
    /// 不需要真实的图标间距/工作区，只做占用网格检测。
    /// 必须使用 RestorePlan 中声明的图标间距（从 ExecutionPlan 构造时传入）。
    /// </summary>
    private static string? SimulateNoStagingExecution(
        TargetDisplayLayoutRestorePlan restorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan,
        List<TargetDisplayLayoutRestorePlanItem> moveRequiredItems)
    {
        // 建立模拟占用状态：所有 MoveRequired 项从其 CurrentPosition 开始
        // 非 MoveRequired 项（AlreadyAtTarget 等）也占用其 SnapshotTargetPosition
        var occupied = new Dictionary<PixelPoint, string>(); // position → occupying StableId
        var itemBySid = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(
            StringComparer.Ordinal);

        foreach (var item in restorePlan.Items)
        {
            itemBySid[item.StableId] = item;

            if (item.State == MoveRequired && item.CurrentPosition.HasValue)
            {
                var pos = item.CurrentPosition.Value;
                if (occupied.ContainsKey(pos))
                {
                    return $"模拟初始状态：位置 ({pos.X},{pos.Y}) 被 " +
                        $"\"{occupied[pos]}\" 和 \"{item.StableId}\" 同时占用。";
                }
                occupied[pos] = item.StableId;
            }
            else if (item.State == AlreadyAtTarget)
            {
                var pos = item.SnapshotTargetPosition;
                if (occupied.ContainsKey(pos))
                {
                    return $"模拟初始状态：位置 ({pos.X},{pos.Y}) 被 " +
                        $"\"{occupied[pos]}\" 和 \"{item.StableId}\" (AlreadyAtTarget) 同时占用。";
                }
                occupied[pos] = item.StableId;
            }
            // Blocked/Missing/Duplicate 等已在前面被拒绝
        }

        // 按 Step 顺序逐步移动
        for (var i = 0; i < executionPlan.Steps.Count; i++)
        {
            var step = executionPlan.Steps[i];
            var fromPos = step.FromPosition;
            var toPos = step.ToPosition;

            // 验证当前位置仍被该图标占用
            if (!occupied.TryGetValue(fromPos, out var currentOccupant))
            {
                return $"模拟步骤 {i} \"{step.StableId}\"：FromPosition " +
                    $"({fromPos.X},{fromPos.Y}) 未被任何图标占用。";
            }
            if (!string.Equals(currentOccupant, step.StableId, StringComparison.Ordinal))
            {
                return $"模拟步骤 {i} \"{step.StableId}\"：FromPosition " +
                    $"({fromPos.X},{fromPos.Y}) 当前被 \"{currentOccupant}\" 占用，" +
                    $"非 \"{step.StableId}\"。";
            }

            // 目标位置必须空闲（排除自身当前占用的 fromPos）
            if (occupied.TryGetValue(toPos, out var targetOccupant))
            {
                // 如果目标占用者就是自己（fromPos == toPos），已在前面拒绝
                return $"模拟步骤 {i} \"{step.StableId}\"：ToPosition " +
                    $"({toPos.X},{toPos.Y}) 被 \"{targetOccupant}\" 占用，" +
                    "无法直接移动。该计划需要临时中转。";
            }

            // 执行移动
            occupied.Remove(fromPos);
            occupied[toPos] = step.StableId;
        }

        // 最终状态验证：所有 MoveRequired 项必须在目标位置
        foreach (var item in moveRequiredItems)
        {
            var expectedPos = item.SnapshotTargetPosition;
            if (!occupied.TryGetValue(expectedPos, out var finalOccupant))
            {
                return $"模拟最终状态：目标位置 ({expectedPos.X},{expectedPos.Y}) " +
                    $"未被占用，但 \"{item.StableId}\" 应在此。";
            }
            if (!string.Equals(finalOccupant, item.StableId, StringComparison.Ordinal))
            {
                return $"模拟最终状态：目标位置 ({expectedPos.X},{expectedPos.Y}) " +
                    $"被 \"{finalOccupant}\" 占用，但预期是 \"{item.StableId}\"。";
            }
        }

        return null; // 通过
    }
}
