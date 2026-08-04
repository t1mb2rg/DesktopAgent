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

    /// <summary>
    /// 评估无中转批量执行条件 — 允许多图标、多步骤、DAG 依赖链，
    /// 但禁止 temporary staging、循环等需要中转的计划。
    /// 返回所有拒绝原因，不只第一条（fail-closed）。
    /// </summary>
    public static GateResult EvaluateNoStagingBatch(
        TargetDisplayLayoutRestorePlan restorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan)
    {
        var reasons = new List<string>();

        // ── RestorePlan 全局检查 ──
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

        // ── 实际 MoveRequired Items 数量与计数字段一致 ──
        var moveRequiredItems = restorePlan.Items
            .Where(it => it.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
            .ToList();

        if (moveRequiredItems.Count != restorePlan.MoveRequiredCount)
            reasons.Add($"MoveRequiredCount 字段 = {restorePlan.MoveRequiredCount}，" +
                $"但实际 MoveRequired 状态的 item 数量 = {moveRequiredItems.Count}，不一致。");

        // ── 每个 MoveRequired Item 的资格 ──
        foreach (var item in moveRequiredItems)
        {
            if (!item.IsFileSystemItem)
                reasons.Add($"MoveRequired item \"{item.StableId}\" IsFileSystemItem 为 false。");
            if (item.IsSystemItem)
                reasons.Add($"MoveRequired item \"{item.StableId}\" IsSystemItem 为 true。");
            if (item.CurrentPosition is null)
                reasons.Add($"MoveRequired item \"{item.StableId}\" CurrentPosition 为 null。");
        }

        // ── ExecutionPlan 全局检查 ──
        if (!executionPlan.IsExecutable)
            reasons.Add("ExecutionPlan.IsExecutable 为 false。");
        if (executionPlan.IsNoOp)
            reasons.Add("ExecutionPlan.IsNoOp 为 true。");
        if (executionPlan.UsesTemporaryStaging)
            reasons.Add("ExecutionPlan.UsesTemporaryStaging 为 true（需要中转，M2-04C 才开放）。");
        if (executionPlan.StagingAssignments.Count != 0)
            reasons.Add($"StagingAssignments.Count = {executionPlan.StagingAssignments.Count}，预期 0。");

        // ── Steps 数量检查 ──
        if (executionPlan.Steps.Count != restorePlan.MoveRequiredCount)
            reasons.Add($"ExecutionPlan.Steps.Count = {executionPlan.Steps.Count}，" +
                $"但 RestorePlan.MoveRequiredCount = {restorePlan.MoveRequiredCount}，不一致。");

        // ── MoveRequired Items StableId 唯一性 ──
        var moveSids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in moveRequiredItems)
        {
            if (!moveSids.Add(item.StableId))
                reasons.Add($"MoveRequired items 中存在重复 StableId \"{item.StableId}\"。");
        }

        // ── 收集非 MoveRequired 的 StableId（不可被 step 引用）──
        var nonMoveSids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in restorePlan.Items)
        {
            if (item.State != TargetDisplayLayoutRestoreItemState.MoveRequired)
                nonMoveSids.Add(item.StableId);
        }

        // ── 逐 Step 检查 ──
        var stepSids = new HashSet<string>(StringComparer.Ordinal);
        var itemBySid = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(StringComparer.Ordinal);
        foreach (var item in moveRequiredItems)
            itemBySid[item.StableId] = item;

        for (var i = 0; i < executionPlan.Steps.Count; i++)
        {
            var step = executionPlan.Steps[i];

            // StepIndex 从 0 连续
            if (step.StepIndex != i)
                reasons.Add($"Steps[{i}].StepIndex = {step.StepIndex}，预期 {i}。");

            // Kind == MoveToTarget
            if (step.Kind != TargetDisplayLayoutExecutionStepKind.MoveToTarget)
                reasons.Add($"Steps[{i}] (\"{step.StableId}\").Kind = {step.Kind}，预期 MoveToTarget。");

            // StableId 唯一
            if (!stepSids.Add(step.StableId))
                reasons.Add($"Steps[{i}] StableId \"{step.StableId}\" 重复。");

            // FromPosition != ToPosition
            if (step.FromPosition == step.ToPosition)
                reasons.Add($"Steps[{i}] (\"{step.StableId}\") FromPosition == ToPosition " +
                    $"({step.FromPosition.X},{step.FromPosition.Y})。");

            // 不能引用非 MoveRequired Item 的 StableId
            if (nonMoveSids.Contains(step.StableId))
                reasons.Add($"Steps[{i}] StableId \"{step.StableId}\" 属于非 MoveRequired item（AlreadyAtTarget/Blocked 等）。");

            // 能按 StableId 唯一映射到一个 MoveRequired Item
            if (!itemBySid.TryGetValue(step.StableId, out var item))
                reasons.Add($"Steps[{i}] StableId \"{step.StableId}\" 不能映射到任何 MoveRequired item。");
            else
            {
                // FromPosition == Item.CurrentPosition
                if (item.CurrentPosition.HasValue && step.FromPosition != item.CurrentPosition.Value)
                    reasons.Add($"Steps[{i}] (\"{step.StableId}\").FromPosition " +
                        $"({step.FromPosition.X},{step.FromPosition.Y}) ≠ " +
                        $"item.CurrentPosition ({item.CurrentPosition.Value.X},{item.CurrentPosition.Value.Y})。");

                // ToPosition == Item.SnapshotTargetPosition
                if (step.ToPosition != item.SnapshotTargetPosition)
                    reasons.Add($"Steps[{i}] (\"{step.StableId}\").ToPosition " +
                        $"({step.ToPosition.X},{step.ToPosition.Y}) ≠ " +
                        $"item.SnapshotTargetPosition ({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y})。");
            }
        }

        // ── 目标显示器一致 ──
        if (!string.Equals(executionPlan.TargetDeviceName, restorePlan.TargetDeviceName,
            StringComparison.Ordinal))
            reasons.Add($"ExecutionPlan.TargetDeviceName ≠ RestorePlan.TargetDeviceName（Ordinal）。");

        return reasons.Count == 0
            ? GateResult.Allowed()
            : GateResult.Rejected(reasons);
    }
}
