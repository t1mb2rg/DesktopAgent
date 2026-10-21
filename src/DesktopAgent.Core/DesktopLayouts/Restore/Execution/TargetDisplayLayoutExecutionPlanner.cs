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

        // ═══ 逐项内部一致性 & MoveRequired 资格 ═══
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
                    if (item.CurrentPosition.Value == item.SnapshotTargetPosition
                        && string.Equals(item.CurrentMonitorDeviceName, restorePlan.TargetDeviceName, StringComparison.Ordinal))
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"MoveRequired \"{item.StableId}\" 已经在目标位置和显示器上。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    // MoveRequired 计划项资格
                    if (!item.IsFileSystemItem)
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"MoveRequired \"{item.StableId}\" IsFileSystemItem 为 false。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    if (item.IsSystemItem)
                    {
                        issues.Add(new(SourcePlanInconsistent,
                            $"MoveRequired \"{item.StableId}\" IsSystemItem 为 true。"));
                        return FailedPlan(restorePlan.TargetDeviceName, issues);
                    }
                    break;

                case MissingFromCurrent:
                    break;
            }
        }

        // IsNoOp 一致性
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

        // 陈旧性检查（含 MoveRequired 当前资格）
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

            // MoveRequired 的当前资格检查
            if (item.State == MoveRequired)
            {
                if (!ci.IsFileSystemItem)
                {
                    issues.Add(new(SourcePlanStale,
                        $"StableId \"{item.StableId}\" 当前不是文件系统项目。"));
                    return FailedPlan(restorePlan.TargetDeviceName, issues);
                }
                if (ci.IsSystemItem)
                {
                    issues.Add(new(SourcePlanStale,
                        $"StableId \"{item.StableId}\" 当前是系统项目。"));
                    return FailedPlan(restorePlan.TargetDeviceName, issues);
                }
            }
        }

        // ═══ 最终目标合法性（仅 MoveRequired：AlreadyAtTarget 不写入，不需工作区检查） ═══
        foreach (var item in restorePlan.Items)
        {
            if (item.State != MoveRequired)
                continue;
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

        // 重叠最终目标检测（仅当至少一方为 MoveRequired 时报错）
        for (var i = 0; i < restorePlan.Items.Count; i++)
        {
            for (var j = i + 1; j < restorePlan.Items.Count; j++)
            {
                if (string.Equals(restorePlan.Items[i].StableId, restorePlan.Items[j].StableId, StringComparison.Ordinal))
                    continue;
                if (restorePlan.Items[i].State != MoveRequired && restorePlan.Items[j].State != MoveRequired)
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

        // ═══ SCC 重新验证（在无操作提前返回之前，始终执行） ═══
        var moveSidsForScc = restorePlan.Items
            .Where(it => it.State == MoveRequired)
            .Select(it => it.StableId)
            .ToList();

        TargetDisplayLayoutDependencyGraphAnalyzer.Result analysis;
        try
        {
            analysis = TargetDisplayLayoutDependencyGraphAnalyzer.Analyze(
                moveSidsForScc, restorePlan.Dependencies);
        }
        catch (ArgumentException ex)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"源计划依赖图不一致：{ex.Message}"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        var recomputedHasCycle = analysis.HasCycle;
        var recomputedCycles = analysis.Cycles;

        // RequiresTemporaryStaging 必须等于 recomputedHasCycle
        if (restorePlan.RequiresTemporaryStaging != recomputedHasCycle)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"RequiresTemporaryStaging ({restorePlan.RequiresTemporaryStaging}) 与重新计算的 HasCycle ({recomputedHasCycle}) 不一致。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        // 无环时 Cycles 必须为空
        if (!restorePlan.RequiresTemporaryStaging)
        {
            if (restorePlan.Cycles is not null && restorePlan.Cycles.Count > 0)
            {
                issues.Add(new(SourcePlanInconsistent,
                    "RequiresTemporaryStaging 为 false 但 Cycles 非空。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }
        else
        {
            // 有环时 MoveRequiredCount 必须 > 0
            if (recalcMove == 0)
            {
                issues.Add(new(SourcePlanInconsistent,
                    "RequiresTemporaryStaging 为 true 但 MoveRequiredCount 为 0。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            // Cycles 必须与重新计算的规范化 Cycles 完全一致
            var sourceCycles = restorePlan.Cycles ?? Array.Empty<TargetDisplayLayoutRestoreCycle>();
            if (recomputedCycles is null || !CyclesEqual(sourceCycles, recomputedCycles))
            {
                issues.Add(new(SourcePlanInconsistent,
                    "restorePlan.Cycles 与重新计算的 SCC 环不一致。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }
        }

        // ═══ 无操作计划 ═══
        if (recalcMove == 0)
        {
            var isNoOp = recalcMissing == 0;
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
    //  无环路径：严格遵循 MoveExecutionOrder
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

        // 先检查重复
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

        // 检查额外 StableId
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

        // 建立模拟状态（位置 + 显示器）
        var simPositions = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
        var simMonitors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var icon in currentReadResult.Icons)
        {
            simPositions[icon.StableId] = icon.PixelPosition;
            simMonitors[icon.StableId] = icon.MonitorDeviceName ?? string.Empty;
        }

        var itemBySid = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(StringComparer.Ordinal);
        foreach (var item in moveItems)
            itemBySid[item.StableId] = item;

        var steps = new List<TargetDisplayLayoutExecutionStep>();
        var stepIndex = 0;

        foreach (var sid in order)
        {
            if (!itemBySid.TryGetValue(sid, out var item))
            {
                // 精确排列验证后不应发生，若发生则内部错误
                issues.Add(new(SourcePlanInconsistent,
                    $"MoveExecutionOrder 中 \"{sid}\" 不在 MoveRequired 集合中。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            var currentPos = simPositions[item.StableId];
            var targetPos = item.SnapshotTargetPosition;
            var currentMonitor = simMonitors.GetValueOrDefault(item.StableId, string.Empty);

            // 同坐标但不同显示器：无环路径禁止 staging，必须失败
            if (currentPos == targetPos)
            {
                if (!string.Equals(currentMonitor, restorePlan.TargetDeviceName, StringComparison.Ordinal))
                {
                    issues.Add(new(SimulationDeadlock,
                        $"同坐标跨显示器：\"{item.StableId}\" 位于 ({currentPos.X},{currentPos.Y})，当前显示器 {currentMonitor}，" +
                        $"目标显示器 {restorePlan.TargetDeviceName}，但无环源计划禁止临时中转。",
                        item.StableId));
                    return FailedPlan(restorePlan.TargetDeviceName, issues);
                }
                // 同坐标同显示器：不应发生（MoveRequired 验证已排除）
                issues.Add(new(SourcePlanInconsistent,
                    $"无环路径：\"{item.StableId}\" 已在目标位置且同显示器，但标记为 MoveRequired。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            var targetRect = DesktopIconLayoutGeometry.CreateRectChecked(targetPos, capabilities.Spacing);

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
                issues.Add(new(SimulationDeadlock,
                    $"无环路径：\"{item.StableId}\" 的目标被占用，无法按 MoveExecutionOrder 移动。"));
                return FailedPlan(restorePlan.TargetDeviceName, issues);
            }

            steps.Add(new TargetDisplayLayoutExecutionStep(
                stepIndex, item.StableId, item.DisplayName,
                currentPos, targetPos, MoveToTarget, restorePlan.TargetDeviceName));

            simPositions[item.StableId] = targetPos;
            simMonitors[item.StableId] = restorePlan.TargetDeviceName;
            stepIndex++;
        }

        // 最终状态验证（位置 + 显示器）
        foreach (var item in moveItems)
        {
            if (!simPositions.TryGetValue(item.StableId, out var finalPos)
                || finalPos != item.SnapshotTargetPosition
                || !simMonitors.TryGetValue(item.StableId, out var finalMonitor)
                || !string.Equals(finalMonitor, restorePlan.TargetDeviceName, StringComparison.Ordinal))
            {
                var posStr = simPositions.TryGetValue(item.StableId, out var p)
                    ? $"({p.X},{p.Y})" : "缺失";
                var monStr = simMonitors.TryGetValue(item.StableId, out var m) ? m : "缺失";
                issues.Add(new(FinalStateMismatch,
                    $"\"{item.StableId}\" 最终 ({posStr}, {monStr}) ≠ 快照目标 " +
                    $"({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y}, {restorePlan.TargetDeviceName})。"));
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
    //  有环路径：通用 staging 模拟
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

        var moveItems = restorePlan.Items
            .Where(it => it.State == MoveRequired).ToList();

        // 建立内存模拟状态（位置 + 显示器）
        var simPositions = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
        var simMonitors = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingItems = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(StringComparer.Ordinal);
        var stagingOccupants = new HashSet<string>(StringComparer.Ordinal);

        foreach (var icon in currentReadResult.Icons)
        {
            simPositions[icon.StableId] = icon.PixelPosition;
            simMonitors[icon.StableId] = icon.MonitorDeviceName ?? string.Empty;
        }

        foreach (var item in moveItems)
            pendingItems[item.StableId] = item;

        var steps = new List<TargetDisplayLayoutExecutionStep>();
        var stagingAssignments = new List<TargetDisplayLayoutStagingAssignment>();
        var stagingIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var stepIndex = 0;
        var stagingSeq = 0;
        var maxSteps = moveItems.Count * 3;

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

                if (currentPos == targetPos) continue;

                var targetRect = DesktopIconLayoutGeometry.CreateRectChecked(targetPos, capabilities.Spacing);

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
                simMonitors[directItem.StableId] = restorePlan.TargetDeviceName;
                pendingItems.Remove(directItem.StableId);

                stepIndex++;
                continue;
            }

            // B. 需要中转
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
            simMonitors[stageItem.StableId] = restorePlan.TargetDeviceName;
            stagingOccupants.Add(stageItem.StableId);
            stagingSeq++;
            stepIndex++;
        }

        // ═══ 最终状态验证（位置 + 显示器） ═══
        foreach (var item in moveItems)
        {
            if (!simPositions.TryGetValue(item.StableId, out var finalPos)
                || finalPos != item.SnapshotTargetPosition
                || !simMonitors.TryGetValue(item.StableId, out var finalMonitor)
                || !string.Equals(finalMonitor, restorePlan.TargetDeviceName, StringComparison.Ordinal))
            {
                var posStr = simPositions.TryGetValue(item.StableId, out var p)
                    ? $"({p.X},{p.Y})" : "缺失";
                var monStr = simMonitors.TryGetValue(item.StableId, out var m) ? m : "缺失";
                issues.Add(new(FinalStateMismatch,
                    $"\"{item.StableId}\" 最终 ({posStr}, {monStr}) ≠ 快照目标 " +
                    $"({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y}, {restorePlan.TargetDeviceName})。"));
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

// staging 不能假定 Explorer 网格从 WorkArea 左上角开始。
// 真实桌面网格可能带稳定相位，例如 spacing=93x122，
// 但实际位置遵循 x=16+n*93, y=2+m*122。
// 使用当前已被 Shell 稳定观察到的位置作为网格相位参考。
        var firstX = FirstGridCoordinateAtOrAfter(
            targetDisplay.WorkArea.Left,
            currentPosition.X,
            spacing.X);

        var firstY = FirstGridCoordinateAtOrAfter(
            targetDisplay.WorkArea.Top,
            currentPosition.Y,
            spacing.Y);

for (long y = firstY; y <= targetDisplay.WorkArea.Bottom - spacing.Y; y += spacing.Y)
{
    for (long x = firstX; x <= targetDisplay.WorkArea.Right - spacing.X; x += spacing.X)
            {
                var candidate = new PixelPoint((int)x, (int)y);

                if (candidate == currentPosition) continue;

                var candidateRect = DesktopIconLayoutGeometry.CreateRectChecked(candidate, spacing);

                var overlaps = false;
                foreach (var (sid, pos) in simPositions)
                {
                    if (string.Equals(sid, excludeStableId, StringComparison.Ordinal)) continue;
                    var otherRect = DesktopIconLayoutGeometry.CreateRectChecked(pos, capabilities.Spacing);
                    if (DesktopIconLayoutGeometry.Overlaps(candidateRect, otherRect))
                    { overlaps = true; break; }
                }
                if (overlaps) continue;

                var hitsTarget = false;
                foreach (var tr in finalTargetRects)
                {
                    if (DesktopIconLayoutGeometry.Overlaps(candidateRect, tr))
                    { hitsTarget = true; break; }
                }
                if (hitsTarget) continue;

                if (!DesktopIconLayoutGeometry.IsFullyContained(candidateRect, targetDisplay.WorkArea))
                    continue;

                return candidate;
            }
        }
        return null;
    }
    private static long FirstGridCoordinateAtOrAfter(
    int minimum,
    int referenceCoordinate,
    int spacing)
{
    var offset = ((long)referenceCoordinate - minimum) % spacing;

    if (offset < 0)
        offset += spacing;

    return (long)minimum + offset;
}

    // ═══ Cycles 比较辅助 ═══

    private static bool CyclesEqual(
        IReadOnlyList<TargetDisplayLayoutRestoreCycle> a,
        IReadOnlyList<TargetDisplayLayoutRestoreCycle> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].StableIds.Count != b[i].StableIds.Count) return false;
            for (var j = 0; j < a[i].StableIds.Count; j++)
            {
                if (!string.Equals(a[i].StableIds[j], b[i].StableIds[j], StringComparison.Ordinal))
                    return false;
            }
        }
        return true;
    }

    // ═══ 失败辅助 ═══

    private static TargetDisplayLayoutExecutionPlan FailedPlan(
        string deviceName, List<TargetDisplayLayoutExecutionPlanIssue> issues) =>
        new(deviceName, false, false,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            issues);
}
