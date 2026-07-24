using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

using static TargetDisplayLayoutExecutionPlanIssueCode;
using static TargetDisplayLayoutExecutionStepKind;
using static TargetDisplayLayoutRestoreItemState;

public static class TargetDisplayLayoutExecutionPlanner
{
    public static TargetDisplayLayoutExecutionPlan CreatePlan(
        TargetDisplayLayoutRestorePlan restorePlan,
        DesktopIconReadResult currentReadResult,
        IReadOnlyList<DisplayDescriptor> currentDisplays,
        DesktopIconPositioningCapabilities capabilities)
    {
        var issues = new List<TargetDisplayLayoutExecutionPlanIssue>();

        // ═══ 前置验证 ═══
        ArgumentNullException.ThrowIfNull(restorePlan);
        ArgumentNullException.ThrowIfNull(currentReadResult);
        ArgumentNullException.ThrowIfNull(currentDisplays);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (currentReadResult.FailedItemCount != 0)
        {
            issues.Add(new(CurrentReadIncomplete,
                $"当前读取不完整（{currentReadResult.FailedItemCount} 个失败），不能生成执行计划。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        if (capabilities.IsAutoArrangeEnabled)
        {
            issues.Add(new(AutoArrangeEnabled, "自动排列图标已开启。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        if (capabilities.Spacing.X <= 0 || capabilities.Spacing.Y <= 0)
        {
            issues.Add(new(SourcePlanInconsistent, "图标间距无效。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        DisplayDescriptor targetDisplay;
        try
        {
            targetDisplay = UniqueDisplaySelector.FindByDeviceName(currentDisplays, restorePlan.TargetDeviceName);
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(new(TargetDisplayNotFound, ex.Message));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }
        catch (ArgumentException ex)
        {
            issues.Add(new(TargetDisplayNotFound, ex.Message));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        // ═══ 源计划摘要重新计算验证（优先于阻断检查） ═══
        var recalcAlready = 0;
        var recalcMove = 0;
        var recalcMissing = 0;
        var recalcBlocked = 0;

        foreach (var item in restorePlan.Items)
        {
            switch (item.State)
            {
                case AlreadyAtTarget: recalcAlready++; break;
                case MoveRequired: recalcMove++; break;
                case MissingFromCurrent: recalcMissing++; break;
                default: recalcBlocked++; break;
            }
        }

        if (recalcAlready != restorePlan.AlreadyAtTargetCount)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"重新计算 AlreadyAtTargetCount ({recalcAlready}) ≠ 摘要 ({restorePlan.AlreadyAtTargetCount})。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }
        if (recalcMove != restorePlan.MoveRequiredCount)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"重新计算 MoveRequiredCount ({recalcMove}) ≠ 摘要 ({restorePlan.MoveRequiredCount})。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }
        if (recalcMissing != restorePlan.MissingCount)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"重新计算 MissingCount ({recalcMissing}) ≠ 摘要 ({restorePlan.MissingCount})。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }
        if (recalcBlocked != restorePlan.BlockedCount)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"重新计算 BlockedCount ({recalcBlocked}) ≠ 摘要 ({restorePlan.BlockedCount})。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }
        if (restorePlan.Items.Count != restorePlan.SnapshotTargetIconCount)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"Items.Count ({restorePlan.Items.Count}) ≠ SnapshotTargetIconCount ({restorePlan.SnapshotTargetIconCount})。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        // 阻断状态一致性：HasBlockingErrors==false 但存在阻断状态 → 不一致
        if (!restorePlan.HasBlockingErrors && recalcBlocked > 0)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"HasBlockingErrors 为 false，但存在 {recalcBlocked} 个阻断状态项。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        // ═══ 源计划阻断错误 ═══
        if (restorePlan.HasBlockingErrors)
        {
            issues.Add(new(SourcePlanHasBlockingErrors, "源计划存在阻断错误，不能生成执行步骤。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        // ═══ 逐项内部一致性 ═══
        var sidSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in restorePlan.Items)
        {
            if (!sidSet.Add(item.StableId))
            {
                issues.Add(new(SourcePlanInconsistent, $"StableId \"{item.StableId}\" 在 Items 中重复。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            switch (item.State)
            {
                case AlreadyAtTarget:
                    if (item.CurrentPosition is null)
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"AlreadyAtTarget \"{item.StableId}\" 缺少 CurrentPosition。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    if (item.CurrentMonitorDeviceName is null)
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"AlreadyAtTarget \"{item.StableId}\" 缺少 CurrentMonitorDeviceName。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    if (item.CurrentPosition.Value != item.SnapshotTargetPosition)
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"AlreadyAtTarget \"{item.StableId}\" CurrentPosition ≠ SnapshotTargetPosition。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    if (!string.Equals(item.CurrentMonitorDeviceName, restorePlan.TargetDeviceName, StringComparison.Ordinal))
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"AlreadyAtTarget \"{item.StableId}\" CurrentMonitorDeviceName ≠ TargetDeviceName。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    break;

                case MoveRequired:
                    if (item.CurrentPosition is null)
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"MoveRequired \"{item.StableId}\" 缺少 CurrentPosition。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    if (item.CurrentMonitorDeviceName is null)
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"MoveRequired \"{item.StableId}\" 缺少 CurrentMonitorDeviceName。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    // 不能同时已经位于目标坐标和目标显示器
                    if (item.CurrentPosition.Value == item.SnapshotTargetPosition
                        && string.Equals(item.CurrentMonitorDeviceName, restorePlan.TargetDeviceName, StringComparison.Ordinal))
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"MoveRequired \"{item.StableId}\" 已经在目标位置和显示器上。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    break;

                case MissingFromCurrent:
                    // Must not exist in current — checked later in staleness
                    break;
            }
        }

        // IsNoOp 一致性（在逐项检查之后，确保先捕获单项不一致）
        var expectedIsNoOp = recalcMove == 0 && recalcMissing == 0 && recalcBlocked == 0;
        if (restorePlan.IsNoOp != expectedIsNoOp)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"IsNoOp ({restorePlan.IsNoOp}) 与 Items 不一致（期望 {expectedIsNoOp}）。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        // ═══ 当前桌面防陈旧验证 ═══
        var currentById = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < currentReadResult.Icons.Count; i++)
        {
            var sid = currentReadResult.Icons[i].StableId;
            if (!currentById.ContainsKey(sid)) currentById[sid] = new List<int>();
            currentById[sid].Add(i);
        }

        foreach (var (sid, idxs) in currentById)
        {
            if (idxs.Count > 1)
            {
                issues.Add(new(TargetDisplayLayoutExecutionPlanIssueCode.DuplicateCurrentStableId,
                    $"当前桌面 StableId \"{sid}\" 出现 {idxs.Count} 次。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }

        // 当前溢出检测
        for (var i = 0; i < currentReadResult.Icons.Count; i++)
        {
            if (!DesktopIconLayoutGeometry.TryCreateRect(currentReadResult.Icons[i].PixelPosition, capabilities.Spacing, out _))
            {
                issues.Add(new(CurrentRectangleOverflow,
                    $"当前图标 #{i} \"{currentReadResult.Icons[i].StableId}\" 坐标溢出。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }

        // 陈旧性检查
        foreach (var item in restorePlan.Items)
        {
            var exists = currentById.TryGetValue(item.StableId, out var idxs);

            if (item.State == MissingFromCurrent)
            {
                if (exists)
                {
                    issues.Add(new(SourcePlanStale,
                        $"StableId \"{item.StableId}\" 之前缺失但已重新出现在当前桌面。"));
                    return FailedPlan(restorePlan.TargetDeviceName, issues);
                }
                continue;
            }

            if (!exists)
            {
                issues.Add(new(SourcePlanStale,
                    $"StableId \"{item.StableId}\" 在当前桌面中不再存在。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            var ci = currentReadResult.Icons[idxs![0]];
            if (item.CurrentPosition.HasValue && ci.PixelPosition != item.CurrentPosition.Value)
            {
                issues.Add(new(SourcePlanStale,
                    $"StableId \"{item.StableId}\" 当前位置已变化。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            if (item.CurrentMonitorDeviceName is not null
                && !string.Equals(ci.MonitorDeviceName, item.CurrentMonitorDeviceName, StringComparison.Ordinal))
            {
                issues.Add(new(SourcePlanStale,
                    $"StableId \"{item.StableId}\" 显示器归属已变化。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }

        // ═══ 最终目标合法性 ═══
        foreach (var item in restorePlan.Items)
        {
            if (!DesktopIconLayoutGeometry.TryCreateRect(item.SnapshotTargetPosition, capabilities.Spacing, out var rect))
            {
                issues.Add(new(SourcePlanInconsistent,
                    $"StableId \"{item.StableId}\" 目标矩形溢出。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
            if (!DesktopIconLayoutGeometry.IsFullyContained(rect, targetDisplay.WorkArea))
            {
                issues.Add(new(SourcePlanInconsistent,
                    $"StableId \"{item.StableId}\" 目标越出工作区。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }

        // 重叠最终目标检测
        for (var i = 0; i < restorePlan.Items.Count; i++)
        {
            for (var j = i + 1; j < restorePlan.Items.Count; j++)
            {
                if (string.Equals(restorePlan.Items[i].StableId, restorePlan.Items[j].StableId, StringComparison.Ordinal))
                    continue;
                var ri = DesktopIconLayoutGeometry.CreateRectChecked(
                    restorePlan.Items[i].SnapshotTargetPosition, capabilities.Spacing);
                var rj = DesktopIconLayoutGeometry.CreateRectChecked(
                    restorePlan.Items[j].SnapshotTargetPosition, capabilities.Spacing);
                if (DesktopIconLayoutGeometry.Overlaps(ri, rj))
                {
                    issues.Add(new(OverlappingFinalTargets,
                        $"StableId \"{restorePlan.Items[i].StableId}\" 和 \"{restorePlan.Items[j].StableId}\" 的目标矩形重叠。"));
                    return FailedPlan(restorePlan.TargetDeviceName, issues);
                }
            }
        }

        // ═══ 无操作计划 ═══
        var missingCount = recalcMissing;
        if (recalcMove == 0)
        {
            var isNoOp = missingCount == 0;
            return new TargetDisplayLayoutExecutionPlan(
                restorePlan.TargetDeviceName, true, isNoOp,
                Array.Empty<TargetDisplayLayoutExecutionStep>(),
                Array.Empty<TargetDisplayLayoutStagingAssignment>(),
                issues);
        }

        // ═══ 路径分派：无环 vs 有环 ═══
        if (!restorePlan.RequiresTemporaryStaging)
            return ExecuteAcyclicPath(restorePlan, currentReadResult, capabilities, targetDisplay, issues);
        else
            return ExecuteCyclicPath(restorePlan, currentReadResult, capabilities, targetDisplay, issues);
    }

    // ═══════════════════════════════════════════════
    // 无环路径：严格遵循 MoveExecutionOrder
    // ═══════════════════════════════════════════════

    private static TargetDisplayLayoutExecutionPlan ExecuteAcyclicPath(
        TargetDisplayLayoutRestorePlan restorePlan,
        DesktopIconReadResult currentReadResult,
        DesktopIconPositioningCapabilities capabilities,
        DisplayDescriptor targetDisplay,
        List<TargetDisplayLayoutExecutionPlanIssue> issues)
    {
        var moveItems = restorePlan.Items
            .Where(it => it.State == MoveRequired).ToList();

        // MoveExecutionOrder 必须非 null
        if (restorePlan.MoveExecutionOrder is null)
        {
            issues.Add(new(SourcePlanInconsistent,
                "RequiresTemporaryStaging 为 false 但 MoveExecutionOrder 为 null。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        var order = restorePlan.MoveExecutionOrder;

        // 先检查重复（在检查数量之前）
        var orderSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sid in order)
        {
            if (!orderSet.Add(sid))
            {
                issues.Add(new(SourcePlanInconsistent,
                    $"MoveExecutionOrder 中 StableId \"{sid}\" 重复。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }

        // 数量必须等于 MoveRequiredCount
        if (order.Count != moveItems.Count)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"MoveExecutionOrder 数量 ({order.Count}) ≠ MoveRequiredCount ({moveItems.Count})。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        // 检查额外 StableId（含在 orderSet 但不在 moveSidSet 中）
        var moveSidSet = new HashSet<string>(moveItems.Select(it => it.StableId), StringComparer.Ordinal);
        foreach (var sid in orderSet)
        {
            if (!moveSidSet.Contains(sid))
            {
                issues.Add(new(SourcePlanInconsistent,
                    $"MoveExecutionOrder 含额外 StableId \"{sid}\"。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }

        // 建立模拟状态
        var simPositions = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
        foreach (var icon in currentReadResult.Icons)
        {
            simPositions[icon.StableId] = icon.PixelPosition;
        }

        var pendingBySid = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(StringComparer.Ordinal);
        foreach (var item in moveItems)
            pendingBySid[item.StableId] = item;

        var steps = new List<TargetDisplayLayoutExecutionStep>();
        var stepIndex = 0;

        // 严格按照 MoveExecutionOrder 逐项执行
        foreach (var sid in order)
        {
            if (!pendingBySid.ContainsKey(sid))
                continue; // already handled (should not happen)

            var item = pendingBySid[sid];
            var currentPos = simPositions[item.StableId];
            var targetPos = item.SnapshotTargetPosition;

            if (currentPos == targetPos)
            {
                // Already at target via prior moves (should not normally happen in acyclic)
                pendingBySid.Remove(item.StableId);
                continue;
            }

            var targetRect = DesktopIconLayoutGeometry.CreateRectChecked(targetPos, capabilities.Spacing);

            // 检查目标是否被其他图标占用
            var blocked = false;
            foreach (var (otherSid, otherPos) in simPositions)
            {
                if (string.Equals(otherSid, item.StableId, StringComparison.Ordinal)) continue;
                var otherRect = DesktopIconLayoutGeometry.CreateRectChecked(otherPos, capabilities.Spacing);
                if (DesktopIconLayoutGeometry.Overlaps(targetRect, otherRect))
                { blocked = true; break; }
            }

            if (blocked)
            {
                // 无环路径不能跳过被阻塞的项 — 要么是 SourcePlanStale 要么死锁
                issues.Add(new(SimulationDeadlock,
                    $"无环路径：\"{item.StableId}\" 的目标被占用，无法按 MoveExecutionOrder 移动。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            steps.Add(new TargetDisplayLayoutExecutionStep(
                stepIndex, item.StableId, item.DisplayName,
                currentPos, targetPos, MoveToTarget, restorePlan.TargetDeviceName));

            simPositions[item.StableId] = targetPos;
            pendingBySid.Remove(item.StableId);
            stepIndex++;
        }

        // 最终状态验证
        foreach (var item in moveItems)
        {
            if (!simPositions.TryGetValue(item.StableId, out var finalPos)
                || finalPos != item.SnapshotTargetPosition)
            {
                issues.Add(new(FinalStateMismatch,
                    $"\"{item.StableId}\" 最终位置 ({finalPos.X},{finalPos.Y}) ≠ 快照目标 ({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y})。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }

        return new TargetDisplayLayoutExecutionPlan(
            restorePlan.TargetDeviceName, true, false,
            steps,
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            issues);
    }

    // ═══════════════════════════════════════════════
    // 有环路径：通用 staging 模拟
    // ═══════════════════════════════════════════════

    private static TargetDisplayLayoutExecutionPlan ExecuteCyclicPath(
        TargetDisplayLayoutRestorePlan restorePlan,
        DesktopIconReadResult currentReadResult,
        DesktopIconPositioningCapabilities capabilities,
        DisplayDescriptor targetDisplay,
        List<TargetDisplayLayoutExecutionPlanIssue> issues)
    {
        // MoveExecutionOrder 必须为 null
        if (restorePlan.MoveExecutionOrder is not null)
        {
            issues.Add(new(SourcePlanInconsistent,
                "RequiresTemporaryStaging 为 true 但 MoveExecutionOrder 不为 null。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        // Cycles 至少包含一个真实环
        if (restorePlan.Cycles is null || restorePlan.Cycles.Count == 0)
        {
            issues.Add(new(SourcePlanInconsistent,
                "RequiresTemporaryStaging 为 true 但 Cycles 为空。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        var moveItems = restorePlan.Items
            .Where(it => it.State == MoveRequired).ToList();

        // 建立内存模拟状态
        var simPositions = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
        var pendingItems = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(StringComparer.Ordinal);
        var stagingOccupants = new HashSet<string>(StringComparer.Ordinal);

        foreach (var icon in currentReadResult.Icons)
            simPositions[icon.StableId] = icon.PixelPosition;

        foreach (var item in moveItems)
            pendingItems[item.StableId] = item;

        var steps = new List<TargetDisplayLayoutExecutionStep>();
        var stagingAssignments = new List<TargetDisplayLayoutStagingAssignment>();
        // StagingAssignment 索引表: StableId -> assignments 列表索引
        var stagingIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var stepIndex = 0;
        var stagingSeq = 0;
        var maxSteps = moveItems.Count * 3;

        // 优先级：Items 顺序稳定
        var itemOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < restorePlan.Items.Count; i++)
        {
            if (restorePlan.Items[i].State == MoveRequired)
                itemOrder.TryAdd(restorePlan.Items[i].StableId, i);
        }

        while (pendingItems.Count > 0)
        {
            if (stepIndex >= maxSteps)
            {
                issues.Add(new(SimulationDeadlock, "超过步骤上限，可能陷入无限循环。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            var orderedPending = pendingItems.Values
                .OrderBy(p => itemOrder.GetValueOrDefault(p.StableId, int.MaxValue))
                .ThenBy(p => p.StableId, StringComparer.Ordinal)
                .ToList();

            // A. 扫描直接可进入目标项
            TargetDisplayLayoutRestorePlanItem? directItem = null;
            foreach (var p in orderedPending)
            {
                var currentPos = simPositions[p.StableId];
                var targetPos = p.SnapshotTargetPosition;

                // 不能从临时位置直接移动到相同目标
                if (currentPos == targetPos) continue;

                var targetRect = DesktopIconLayoutGeometry.CreateRectChecked(targetPos, capabilities.Spacing);

                // 目标矩形不与任何其他图标重叠
                var blocked = false;
                foreach (var (sid, pos) in simPositions)
                {
                    if (string.Equals(sid, p.StableId, StringComparison.Ordinal)) continue;
                    var otherRect = DesktopIconLayoutGeometry.CreateRectChecked(pos, capabilities.Spacing);
                    if (DesktopIconLayoutGeometry.Overlaps(targetRect, otherRect))
                    { blocked = true; break; }
                }
                if (!blocked)
                {
                    directItem = p;
                    break;
                }
            }

            if (directItem is not null)
            {
                var currentPos = simPositions[directItem.StableId];
                var targetPos = directItem.SnapshotTargetPosition;
                var isFromStaging = stagingOccupants.Contains(directItem.StableId);
                var kind = isFromStaging ? MoveFromTemporaryStagingToTarget : MoveToTarget;

                if (isFromStaging)
                {
                    stagingOccupants.Remove(directItem.StableId);
                    // 通过字典更新 StagingAssignment 的 ExitStepIndex
                    if (stagingIndex.TryGetValue(directItem.StableId, out var saIdx))
                    {
                        var sa = stagingAssignments[saIdx];
                        stagingAssignments[saIdx] = sa with { ExitStepIndex = stepIndex };
                        stagingIndex.Remove(directItem.StableId);
                    }
                }

                steps.Add(new TargetDisplayLayoutExecutionStep(
                    stepIndex, directItem.StableId, directItem.DisplayName,
                    currentPos, targetPos, kind, restorePlan.TargetDeviceName));

                simPositions[directItem.StableId] = targetPos;
                pendingItems.Remove(directItem.StableId);

                stepIndex++;
                continue;
            }

            // B. 需要中转 — 选择第一个尚未在中转位置的 pending 项
            TargetDisplayLayoutRestorePlanItem? stageItem = null;
            foreach (var p in orderedPending)
            {
                if (!stagingOccupants.Contains(p.StableId))
                { stageItem = p; break; }
            }

            if (stageItem is null)
            {
                issues.Add(new(SimulationDeadlock, "全部待恢复项已在中转位置，但无可进入最终目标。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            // C. 搜索安全中转位置
            var stagingPos = FindStagingPosition(
                simPositions, restorePlan.Items, capabilities, targetDisplay, stageItem.StableId);
            if (stagingPos is null)
            {
                issues.Add(new(NoSafeStagingPosition,
                    $"无法为 \"{stageItem.StableId}\" 找到安全中转位置。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            var fromPos = simPositions[stageItem.StableId];
            steps.Add(new TargetDisplayLayoutExecutionStep(
                stepIndex, stageItem.StableId, stageItem.DisplayName,
                fromPos, stagingPos.Value, MoveToTemporaryStaging,
                restorePlan.TargetDeviceName, StagingSequence: stagingSeq));

            var saEntry = new TargetDisplayLayoutStagingAssignment(
                stageItem.StableId, stagingPos.Value, stepIndex, -1);
            stagingAssignments.Add(saEntry);
            stagingIndex[stageItem.StableId] = stagingAssignments.Count - 1;

            simPositions[stageItem.StableId] = stagingPos.Value;
            stagingOccupants.Add(stageItem.StableId);
            stagingSeq++;
            stepIndex++;
        }

        // ═══ 最终状态验证 ═══
        foreach (var item in moveItems)
        {
            if (!simPositions.TryGetValue(item.StableId, out var finalPos)
                || finalPos != item.SnapshotTargetPosition)
            {
                issues.Add(new(FinalStateMismatch,
                    $"\"{item.StableId}\" 最终位置 ({finalPos.X},{finalPos.Y}) ≠ 快照目标 ({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y})。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }

        return new TargetDisplayLayoutExecutionPlan(
            restorePlan.TargetDeviceName, true, false,
            steps, stagingAssignments, issues);
    }

    // ═══ 网格扫描中转位置 ═══

    private static PixelPoint? FindStagingPosition(
        Dictionary<string, PixelPoint> simPositions,
        IReadOnlyList<TargetDisplayLayoutRestorePlanItem> allItems,
        DesktopIconPositioningCapabilities capabilities,
        DisplayDescriptor targetDisplay,
        string excludeStableId)
    {
        if (!simPositions.TryGetValue(excludeStableId, out var currentPosition))
            return null;

        var finalTargetRects = new List<PixelRect>();
        foreach (var item in allItems)
        {
            finalTargetRects.Add(
                DesktopIconLayoutGeometry.CreateRectChecked(item.SnapshotTargetPosition, capabilities.Spacing));
        }

        var spacing = capabilities.Spacing;
        for (long y = targetDisplay.WorkArea.Top; y <= targetDisplay.WorkArea.Bottom - spacing.Y; y += spacing.Y)
        {
            for (long x = targetDisplay.WorkArea.Left; x <= targetDisplay.WorkArea.Right - spacing.X; x += spacing.X)
            {
                var candidate = new PixelPoint((int)x, (int)y);

                // 不得等于当前图标位置
                if (candidate == currentPosition) continue;

                var candidateRect = DesktopIconLayoutGeometry.CreateRectChecked(candidate, spacing);

                // 不与任何模拟占用重叠
                var overlaps = false;
                foreach (var (sid, pos) in simPositions)
                {
                    if (string.Equals(sid, excludeStableId, StringComparison.Ordinal)) continue;
                    var otherRect = DesktopIconLayoutGeometry.CreateRectChecked(pos, spacing);
                    if (DesktopIconLayoutGeometry.Overlaps(candidateRect, otherRect))
                    { overlaps = true; break; }
                }
                if (overlaps) continue;

                // 不与任何最终目标矩形重叠
                var hitsTarget = false;
                foreach (var tr in finalTargetRects)
                {
                    if (DesktopIconLayoutGeometry.Overlaps(candidateRect, tr))
                    { hitsTarget = true; break; }
                }
                if (hitsTarget) continue;

                // 候选矩形必须完整在 WorkArea 内
                if (!DesktopIconLayoutGeometry.IsFullyContained(candidateRect, targetDisplay.WorkArea))
                    continue;

                return candidate;
            }
        }
        return null;
    }

    // ═══ 失败辅助 ═══

    private static TargetDisplayLayoutExecutionPlan FailedPlan(
        string deviceName, List<TargetDisplayLayoutExecutionPlanIssue> issues) =>
        new(deviceName, false, false,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            issues);
}
