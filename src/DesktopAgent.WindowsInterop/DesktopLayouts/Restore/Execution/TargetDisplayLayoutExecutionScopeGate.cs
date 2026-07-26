using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

using static TargetDisplayLayoutExecutionStepKind;

/// <summary>
/// 受控执行范围闸门 — 只允许单图标、单步骤、无中转的恢复计划。
/// 纯逻辑，不依赖 COM/PIDL/HWND/WPF。
/// </summary>
internal static class TargetDisplayLayoutExecutionScopeGate
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
    /// 评估是否符合单图标、单步骤、无中转的执行条件。
    /// 返回所有拒绝原因，不只第一条。
    /// </summary>
    public static GateResult EvaluateSingleDirectMove(
        TargetDisplayLayoutRestorePlan restorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan)
    {
        var reasons = new List<string>();

        // 一次性建立 MoveRequired item 集合，严禁 First / Single
        var moveRequiredItems = restorePlan.Items
            .Where(it => it.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
            .ToList();

        // RestorePlan 检查
        if (restorePlan.HasBlockingErrors)
            reasons.Add("HasBlockingErrors 为 true。");
        if (restorePlan.MissingCount != 0)
            reasons.Add($"MissingCount = {restorePlan.MissingCount}，预期 0。");
        if (restorePlan.BlockedCount != 0)
            reasons.Add($"BlockedCount = {restorePlan.BlockedCount}，预期 0。");
        if (restorePlan.MoveRequiredCount != 1)
            reasons.Add($"MoveRequiredCount = {restorePlan.MoveRequiredCount}，预期 1。");

        if (moveRequiredItems.Count != 1)
            reasons.Add($"实际 MoveRequired 状态的 item 数量 = {moveRequiredItems.Count}，预期 1。");

        if (moveRequiredItems.Count != restorePlan.MoveRequiredCount)
            reasons.Add($"MoveRequiredCount 字段 = {restorePlan.MoveRequiredCount}，" +
                $"但实际 MoveRequired 状态的 item 数量 = {moveRequiredItems.Count}，不一致。");

        if (restorePlan.IsNoOp)
            reasons.Add("IsNoOp 为 true。");

        // item 校验 —— 只在恰好 1 个 MoveRequired 时执行，使用 moveRequiredItems[0]
        if (moveRequiredItems.Count == 1)
        {
            var item = moveRequiredItems[0];
            if (!item.IsFileSystemItem)
                reasons.Add($"MoveRequired item \"{item.StableId}\" IsFileSystemItem 为 false。");
            if (item.IsSystemItem)
                reasons.Add($"MoveRequired item \"{item.StableId}\" IsSystemItem 为 true。");
            if (item.CurrentPosition is null)
                reasons.Add($"MoveRequired item \"{item.StableId}\" CurrentPosition 为 null。");
        }

        // ExecutionPlan 检查
        if (!executionPlan.IsExecutable)
            reasons.Add("ExecutionPlan.IsExecutable 为 false。");
        if (executionPlan.IsNoOp)
            reasons.Add("ExecutionPlan.IsNoOp 为 true。");
        if (executionPlan.UsesTemporaryStaging)
            reasons.Add("ExecutionPlan.UsesTemporaryStaging 为 true。");
        if (executionPlan.Steps.Count != 1)
            reasons.Add($"ExecutionPlan.Steps.Count = {executionPlan.Steps.Count}，预期 1。");

        // Step 与 item 匹配 —— 使用同一个局部 item，不再次查询集合
        if (executionPlan.Steps.Count == 1)
        {
            var step = executionPlan.Steps[0];
            if (step.StepIndex != 0)
                reasons.Add($"StepIndex = {step.StepIndex}，预期 0。");
            if (step.Kind != MoveToTarget)
                reasons.Add($"Step.Kind = {step.Kind}，预期 MoveToTarget。");

            if (moveRequiredItems.Count == 1)
            {
                var item = moveRequiredItems[0];
                if (!string.Equals(step.StableId, item.StableId, StringComparison.Ordinal))
                    reasons.Add($"Step.StableId \"{step.StableId}\" ≠ item.StableId \"{item.StableId}\"（Ordinal）。");
                if (item.CurrentPosition.HasValue && step.FromPosition != item.CurrentPosition.Value)
                    reasons.Add($"Step.FromPosition ({step.FromPosition.X},{step.FromPosition.Y}) ≠ " +
                        $"item.CurrentPosition ({item.CurrentPosition.Value.X},{item.CurrentPosition.Value.Y})。");
                if (step.ToPosition != item.SnapshotTargetPosition)
                    reasons.Add($"Step.ToPosition ({step.ToPosition.X},{step.ToPosition.Y}) ≠ " +
                        $"item.SnapshotTargetPosition ({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y})。");
            }

            if (step.FromPosition == step.ToPosition)
                reasons.Add("Step.FromPosition == Step.ToPosition。");
        }

        if (!string.Equals(executionPlan.TargetDeviceName, restorePlan.TargetDeviceName,
            StringComparison.Ordinal))
            reasons.Add($"ExecutionPlan.TargetDeviceName ≠ RestorePlan.TargetDeviceName（Ordinal）。");

        // 防御性复制在 GateResult.Rejected 内部执行
        return reasons.Count == 0
            ? GateResult.Allowed()
            : GateResult.Rejected(reasons);
    }
}
