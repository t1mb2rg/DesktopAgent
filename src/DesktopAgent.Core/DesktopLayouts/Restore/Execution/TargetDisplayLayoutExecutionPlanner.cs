using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

using static TargetDisplayLayoutExecutionPlanIssueCode;
using static TargetDisplayLayoutExecutionStepKind;

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
        try { targetDisplay = UniqueDisplaySelector.FindByDeviceName(currentDisplays, restorePlan.TargetDeviceName); }
        catch (Exception ex) { issues.Add(new(TargetDisplayNotFound, ex.Message)); return FailedPlan(restorePlan.TargetDeviceName, issues); }

        // ═══ 源计划一致性 ═══
        if (restorePlan.HasBlockingErrors)
        {
            issues.Add(new(SourcePlanHasBlockingErrors, "源计划存在阻断错误，不能生成执行步骤。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        if (restorePlan.Items.Count != restorePlan.SnapshotTargetIconCount)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"Items.Count ({restorePlan.Items.Count}) ≠ SnapshotTargetIconCount ({restorePlan.SnapshotTargetIconCount})。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        var moveItems = restorePlan.Items
            .Where(it => it.State == TargetDisplayLayoutRestoreItemState.MoveRequired).ToList();
        if (moveItems.Count != restorePlan.MoveRequiredCount)
        {
            issues.Add(new(SourcePlanInconsistent,
                $"实际 MoveRequired 项 ({moveItems.Count}) ≠ 摘要 MoveRequiredCount ({restorePlan.MoveRequiredCount})。"));
            return FailedPlan(restorePlan.TargetDeviceName, issues);
        }

        // ═══ 源计划内部验证 ═══
        var sidSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in restorePlan.Items)
        {
            if (!sidSet.Add(item.StableId))
            { issues.Add(new(SourcePlanInconsistent, $"StableId \"{item.StableId}\" 在 Items 中重复。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }

            if (item.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
            {
                if (item.CurrentPosition is null || item.CurrentMonitorDeviceName is null)
                { issues.Add(new(SourcePlanInconsistent, $"MoveRequired 项 \"{item.StableId}\" 缺少 CurrentPosition 或 CurrentMonitorDeviceName。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }
            }
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
            { issues.Add(new(DuplicateCurrentStableId, $"当前桌面 StableId \"{sid}\" 出现 {idxs.Count} 次。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }
        }

        // 当前溢出检测
        for (var i = 0; i < currentReadResult.Icons.Count; i++)
        {
            if (!DesktopIconLayoutGeometry.TryCreateRect(currentReadResult.Icons[i].PixelPosition, capabilities.Spacing, out _))
            { issues.Add(new(CurrentRectangleOverflow, $"当前图标 #{i} \"{currentReadResult.Icons[i].StableId}\" 坐标溢出。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }
        }

        // 陈旧性检查
        foreach (var item in restorePlan.Items)
        {
            var exists = currentById.TryGetValue(item.StableId, out var idxs);

            if (item.State == TargetDisplayLayoutRestoreItemState.MissingFromCurrent)
            {
                // MissingFromCurrent 但重新出现 → stale
                if (exists)
                { issues.Add(new(SourcePlanStale, $"StableId \"{item.StableId}\" 之前缺失但已重新出现在当前桌面。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }
                continue;
            }

            if (!exists)
            { issues.Add(new(SourcePlanStale, $"StableId \"{item.StableId}\" 在当前桌面中不再存在。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }

            var ci = currentReadResult.Icons[idxs![0]];
            if (item.CurrentPosition.HasValue && ci.PixelPosition != item.CurrentPosition.Value)
            { issues.Add(new(SourcePlanStale, $"StableId \"{item.StableId}\" 当前位置已变化。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }

            if (item.CurrentMonitorDeviceName is not null
                && !string.Equals(ci.MonitorDeviceName, item.CurrentMonitorDeviceName, StringComparison.Ordinal))
            { issues.Add(new(SourcePlanStale, $"StableId \"{item.StableId}\" 显示器归属已变化。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }
        }

        // ═══ 最终目标合法性 ═══
        foreach (var item in restorePlan.Items)
        {
            if (!DesktopIconLayoutGeometry.TryCreateRect(item.SnapshotTargetPosition, capabilities.Spacing, out var rect))
            { issues.Add(new(SourcePlanInconsistent, $"StableId \"{item.StableId}\" 目标矩形溢出。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }
            if (!DesktopIconLayoutGeometry.IsFullyContained(rect, targetDisplay.WorkArea))
            { issues.Add(new(SourcePlanInconsistent, $"StableId \"{item.StableId}\" 目标越出工作区。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }
        }

        // 重叠最终目标检测
        for (var i = 0; i < restorePlan.Items.Count; i++)
        {
            for (var j = i + 1; j < restorePlan.Items.Count; j++)
            {
                if (string.Equals(restorePlan.Items[i].StableId, restorePlan.Items[j].StableId, StringComparison.Ordinal)) continue;
                var ri = DesktopIconLayoutGeometry.CreateRectChecked(restorePlan.Items[i].SnapshotTargetPosition, capabilities.Spacing);
                var rj = DesktopIconLayoutGeometry.CreateRectChecked(restorePlan.Items[j].SnapshotTargetPosition, capabilities.Spacing);
                if (DesktopIconLayoutGeometry.Overlaps(ri, rj))
                { issues.Add(new(OverlappingFinalTargets, $"StableId \"{restorePlan.Items[i].StableId}\" 和 \"{restorePlan.Items[j].StableId}\" 的目标矩形重叠。")); return FailedPlan(restorePlan.TargetDeviceName, issues); }
            }
        }

        // ═══ 无操作计划 ═══
        var missingCount = restorePlan.MissingCount;
        if (moveItems.Count == 0)
        {
            var isNoOp = missingCount == 0;
            return new TargetDisplayLayoutExecutionPlan(
                restorePlan.TargetDeviceName, true, isNoOp, false,
                0, 0, 0,
                Array.Empty<TargetDisplayLayoutExecutionStep>(),
                Array.Empty<TargetDisplayLayoutStagingAssignment>(),
                issues);
        }

        // ═══ 建立内存模拟状态 ═══
        // 每个图标当前像素坐标: stableId -> PixelPoint
        var simPositions = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
        // 需要恢复的计划项: stableId -> planItem
        var pendingItems = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(StringComparer.Ordinal);
        // 当前额外图标（不恢复）
        var extraIcons = new List<(string StableId, PixelPoint Position)>();
        // 当前在中转位置的 StableId 集合
        var stagingOccupants = new HashSet<string>(StringComparer.Ordinal);

        foreach (var icon in currentReadResult.Icons)
        {
            simPositions[icon.StableId] = icon.PixelPosition;
        }

        foreach (var item in moveItems)
        {
            pendingItems[item.StableId] = item;
        }

        foreach (var icon in currentReadResult.Icons)
        {
            if (!pendingItems.ContainsKey(icon.StableId)
                && !restorePlan.Items.Any(i => string.Equals(i.StableId, icon.StableId, StringComparison.Ordinal)
                    && i.State != TargetDisplayLayoutRestoreItemState.MoveRequired))
            {
                extraIcons.Add((icon.StableId, icon.PixelPosition));
            }
        }

        // ═══ 确定性执行规划 ═══
        var steps = new List<TargetDisplayLayoutExecutionStep>();
        var stagingAssignments = new List<TargetDisplayLayoutStagingAssignment>();
        var stepIndex = 0;
        var stagingSeq = 0;
        var maxSteps = moveItems.Count * 3;

        // 优先级：MoveExecutionOrder > Items 顺序 > Ordinal
        var itemOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        if (restorePlan.MoveExecutionOrder is not null)
        {
            for (var i = 0; i < restorePlan.MoveExecutionOrder.Count; i++)
                itemOrder[restorePlan.MoveExecutionOrder[i]] = i;
        }
        else
        {
            for (var i = 0; i < restorePlan.Items.Count; i++)
                if (restorePlan.Items[i].State == TargetDisplayLayoutRestoreItemState.MoveRequired)
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
                var targetRect = DesktopIconLayoutGeometry.CreateRectChecked(targetPos, capabilities.Spacing);

                // 不能从临时位置直接移动到相同目标
                if (currentPos == targetPos) continue;

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
                    // 更新 StagingAssignment 的 ExitStepIndex
                    var sa = stagingAssignments.First(sa =>
                        string.Equals(sa.StableId, directItem.StableId, StringComparison.Ordinal));
                    stagingAssignments.Remove(sa);
                    stagingAssignments.Add(sa with { ExitStepIndex = stepIndex });
                }

                steps.Add(new TargetDisplayLayoutExecutionStep(
                    stepIndex, directItem.StableId, directItem.DisplayName,
                    currentPos, targetPos, kind, restorePlan.TargetDeviceName));

                simPositions[directItem.StableId] = targetPos;
                pendingItems.Remove(directItem.StableId);

                // 检查是否释放了某个中转位置
                // (下一个循环会自然复用)
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
                // 所有 pending 在中转但无直接可进入目标
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

            stagingAssignments.Add(new TargetDisplayLayoutStagingAssignment(
                stageItem.StableId, stagingPos.Value, stepIndex, -1));

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

        var directCount = steps.Count(s => s.Kind == MoveToTarget);
        var stagingCount = steps.Count(s => s.Kind == MoveToTemporaryStaging);
        var targetFromStagingCount = steps.Count(s => s.Kind == MoveFromTemporaryStagingToTarget);

        return new TargetDisplayLayoutExecutionPlan(
            restorePlan.TargetDeviceName, true, false, stagingCount > 0,
            directCount, stagingCount, targetFromStagingCount,
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
        var finalTargetRects = new List<PixelRect>();
        foreach (var item in allItems)
        {
            finalTargetRects.Add(
                DesktopIconLayoutGeometry.CreateRectChecked(item.SnapshotTargetPosition, capabilities.Spacing));
        }

        var spacing = capabilities.Spacing;
        // 使用 long 避免 int 递增溢出
        for (long y = targetDisplay.WorkArea.Top; y <= targetDisplay.WorkArea.Bottom - spacing.Y; y += spacing.Y)
        {
            for (long x = targetDisplay.WorkArea.Left; x <= targetDisplay.WorkArea.Right - spacing.X; x += spacing.X)
            {
                var candidate = new PixelPoint((int)x, (int)y);
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
        new(deviceName, false, false, false, 0, 0, 0,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            issues);
}
