using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore;

public static class TargetDisplayLayoutRestorePlanner
{
    public static TargetDisplayLayoutRestorePlan CreatePlan(
        DesktopLayoutSnapshot snapshot,
        DesktopIconReadResult currentReadResult,
        IReadOnlyList<DisplayDescriptor> currentDisplays,
        DesktopIconPositioningCapabilities capabilities,
        string targetDeviceName)
        => CreatePlan(
            snapshot,
            currentReadResult,
            currentDisplays,
            capabilities,
            targetDeviceName,
            allowSnapshotFromAnotherDisplay: false);

    /// <summary>
    /// 与上面同一个规划器，但允许用<strong>标注在别的显示器上</strong>的整桌面快照来为
    /// <paramref name="targetDeviceName"/> 排还原计划。快照条目自带每一项所属显示器，
    /// 因此跨屏整理（把别的屏的项目挪进来）也能按同一份检查点把每一项送回它原来的屏。
    ///
    /// 只有「撤销上次整理」这条路径传 true：它拿到的是自己写盘前存的检查点，
    /// 而其它路径必须保持原来的 fail-closed 语义（快照与目标屏不一致就拒绝）。
    /// 判定与规划其余部分完全不变——计划项仍然只由快照坐标产生，不引入任何推断。
    /// </summary>
    public static TargetDisplayLayoutRestorePlan CreatePlan(
        DesktopLayoutSnapshot snapshot,
        DesktopIconReadResult currentReadResult,
        IReadOnlyList<DisplayDescriptor> currentDisplays,
        DesktopIconPositioningCapabilities capabilities,
        string targetDeviceName,
        bool allowSnapshotFromAnotherDisplay)
    {
        // ═══ 阶段 1: 参数和全局前置验证 ═══
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentReadResult);
        ArgumentNullException.ThrowIfNull(currentDisplays);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (targetDeviceName is null)
            throw new ArgumentNullException(nameof(targetDeviceName));
        if (string.IsNullOrWhiteSpace(targetDeviceName))
            throw new ArgumentException("targetDeviceName 不能为空白。", nameof(targetDeviceName));

        if (currentReadResult.FailedItemCount != 0)
            throw new InvalidOperationException(
                $"当前读取不完整（{currentReadResult.FailedItemCount} 个失败），不能生成恢复计划。");

        if (capabilities.IsAutoArrangeEnabled)
            throw new InvalidOperationException(
                "自动排列图标已开启，不能生成恢复计划。请先关闭「自动排列图标」。");

        var targetDisplay = UniqueDisplaySelector.FindByDeviceName(currentDisplays, targetDeviceName);

        // 整桌面检查点（例如工作区设计应用写盘前存的那份）没有「目标显示器」这一说：
        // 每一项自带所属显示器，撤销时由调用方逐屏指定目标屏。只有允许跨屏快照的
        // 撤销路径能走这条路；其它路径仍旧 fail closed。
        if (snapshot.TargetMonitorDeviceName is null && !allowSnapshotFromAnotherDisplay)
            throw new InvalidOperationException("快照的 TargetMonitorDeviceName 为 null，不能用于指定显示器恢复。");
        if (!allowSnapshotFromAnotherDisplay
            && !string.Equals(snapshot.TargetMonitorDeviceName, targetDeviceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"快照目标显示器 {snapshot.TargetMonitorDeviceName} 与请求的 {targetDeviceName} 不一致。");

        var issues = new List<TargetDisplayLayoutRestorePlanIssue>();

        // ═══ 阶段 2: 建立完整快照目标集合 ═══
        var snapshotTargetIcons = new List<DesktopIconSnapshotItem>();
        foreach (var icon in snapshot.Icons)
        {
            if (icon.MonitorDeviceName is not null
                && string.Equals(icon.MonitorDeviceName, targetDeviceName, StringComparison.Ordinal))
                snapshotTargetIcons.Add(icon);
        }

        // ═══ 阶段 3: 快照 StableId 重复分组 ═══
        var sidCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sidIndices = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < snapshotTargetIcons.Count; i++)
        {
            var sid = snapshotTargetIcons[i].StableId;
            sidCounts.TryGetValue(sid, out var c);
            sidCounts[sid] = c + 1;
            if (!sidIndices.ContainsKey(sid))
                sidIndices[sid] = new List<int>();
            sidIndices[sid].Add(i);
        }

        // ═══ 阶段 4: 快照目标坐标重复分组 ═══
        var posIndices = new Dictionary<PixelPoint, List<int>>();
        for (var i = 0; i < snapshotTargetIcons.Count; i++)
        {
            var pos = snapshotTargetIcons[i].PixelPosition;
            if (!posIndices.ContainsKey(pos))
                posIndices[pos] = new List<int>();
            posIndices[pos].Add(i);
        }

        // ═══ 阶段 5: 当前完整桌面 StableId 分组（全局唯一性）═══
        var currentBySid = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < currentReadResult.Icons.Count; i++)
        {
            var sid = currentReadResult.Icons[i].StableId;
            if (!currentBySid.ContainsKey(sid))
                currentBySid[sid] = new List<int>();
            currentBySid[sid].Add(i);
        }

        foreach (var (sid, idxs) in currentBySid)
        {
            if (idxs.Count > 1)
                issues.Add(new TargetDisplayLayoutRestorePlanIssue(
                    TargetDisplayLayoutRestorePlanIssueCode.DuplicateCurrentStableId,
                    $"当前桌面中 StableId \"{sid}\" 出现 {idxs.Count} 次（预期恰好 1 次）。",
                    sid));
        }

        // 当前桌面坐标溢出检测
        for (var i = 0; i < currentReadResult.Icons.Count; i++)
        {
            if (!DesktopIconLayoutGeometry.TryCreateRect(currentReadResult.Icons[i].PixelPosition, capabilities.Spacing, out _))
            {
                issues.Add(new TargetDisplayLayoutRestorePlanIssue(
                    TargetDisplayLayoutRestorePlanIssueCode.CurrentRectangleOverflow,
                    $"当前图标 #{i} \"{currentReadResult.Icons[i].DisplayName}\" ({currentReadResult.Icons[i].StableId}) 坐标 " +
                    $"({currentReadResult.Icons[i].PixelPosition.X},{currentReadResult.Icons[i].PixelPosition.Y}) 计算占用矩形时溢出。",
                    currentReadResult.Icons[i].StableId));
            }
        }

        var hasGlobalBlockingIssue = issues.Any(issue =>
            issue.Code == TargetDisplayLayoutRestorePlanIssueCode.DuplicateCurrentStableId
            || issue.Code == TargetDisplayLayoutRestorePlanIssueCode.CurrentRectangleOverflow);

        // 目标集合为空：仍必须完成全局校验
        if (snapshotTargetIcons.Count == 0)
        {
            return new TargetDisplayLayoutRestorePlan(
                targetDeviceName, 0, 0, 0, 0, 0,
                hasGlobalBlockingIssue, false, !hasGlobalBlockingIssue,
                new List<TargetDisplayLayoutRestorePlanItem>(),
                new List<TargetDisplayLayoutRestoreDependency>(),
                new List<TargetDisplayLayoutRestoreCycle>(),
                issues,
                null);
        }

        // ═══ 阶段 6: 为每个快照目标条目建立一个计划项 ═══
        var items = new List<TargetDisplayLayoutRestorePlanItem>();
        for (var i = 0; i < snapshotTargetIcons.Count; i++)
        {
            var snapIcon = snapshotTargetIcons[i];
            var sidDupCount = sidCounts[snapIcon.StableId];
            var posDupCount = posIndices[snapIcon.PixelPosition].Count;

            // 快照级错误收集
            var errorParts = new List<string>();

            if (sidDupCount > 1)
                errorParts.Add($"快照目标集合中 StableId \"{snapIcon.StableId}\" 出现 {sidDupCount} 次（包括索引 {i}）。");

            if (posDupCount > 1 && posIndices[snapIcon.PixelPosition].FindIndex(idx => idx != i
                && string.Equals(snapshotTargetIcons[idx].StableId, snapIcon.StableId, StringComparison.Ordinal) == false) >= 0)
            {
                var otherSids = posIndices[snapIcon.PixelPosition]
                    .Where(idx => !string.Equals(snapshotTargetIcons[idx].StableId, snapIcon.StableId, StringComparison.Ordinal))
                    .Select(idx => snapshotTargetIcons[idx].StableId).Distinct(StringComparer.Ordinal);
                errorParts.Add($"快照目标位置 ({snapIcon.PixelPosition.X},{snapIcon.PixelPosition.Y}) 被 {otherSids.Count() + 1} 个不同 StableId 指向（索引 {i}）。");
            }

            if (errorParts.Count > 0)
            {
                // 优先 SidDuplicate，其次坐标重复
                var dupState = sidDupCount > 1
                    ? TargetDisplayLayoutRestoreItemState.SnapshotStableIdDuplicate
                    : TargetDisplayLayoutRestoreItemState.DuplicateSnapshotTargetPosition;

                items.Add(new TargetDisplayLayoutRestorePlanItem(
                    snapIcon.StableId, snapIcon.DisplayName,
                    snapIcon.PixelPosition, null, null,
                    dupState,
                    snapIcon.IsFileSystemItem, snapIcon.IsSystemItem,
                    string.Join(" ", errorParts)));
                continue;
            }

            items.Add(new TargetDisplayLayoutRestorePlanItem(
                snapIcon.StableId, snapIcon.DisplayName,
                snapIcon.PixelPosition, null, null,
                TargetDisplayLayoutRestoreItemState.MissingFromCurrent, // 临时占位
                snapIcon.IsFileSystemItem, snapIcon.IsSystemItem));
        }

        // 不变量：items.Count == snapshotTargetIcons.Count

        // ═══ 阶段 7: 当前匹配和基础状态分类 ═══
        // 保存每个快照项 -> 当前匹配索引
        var snapToCurrentIndex = new Dictionary<int, int>(); // snapshotIndex -> currentIndex

        for (var si = 0; si < snapshotTargetIcons.Count; si++)
        {
            if (items[si].State == TargetDisplayLayoutRestoreItemState.SnapshotStableIdDuplicate
                || items[si].State == TargetDisplayLayoutRestoreItemState.DuplicateSnapshotTargetPosition)
                continue; // 已在阶段 6 处理

            var snapIcon = snapshotTargetIcons[si];

            if (!currentBySid.TryGetValue(snapIcon.StableId, out var cidxes))
            {
                items[si] = items[si] with
                {
                    State = TargetDisplayLayoutRestoreItemState.MissingFromCurrent,
                    BlockReason = $"StableId \"{snapIcon.StableId}\" 在当前桌面中未找到。"
                };
                continue;
            }

            if (cidxes.Count > 1)
            {
                items[si] = items[si] with
                {
                    State = TargetDisplayLayoutRestoreItemState.DuplicateCurrentStableId,
                    BlockReason = $"StableId \"{snapIcon.StableId}\" 在当前桌面中有 {cidxes.Count} 个匹配。"
                };
                continue;
            }

            var ci = cidxes[0];
            snapToCurrentIndex[si] = ci;
            var currentIcon = currentReadResult.Icons[ci];

            // 「无需移动」必须先于可移动性验证：已经位于快照目标且显示器一致的项目
            // 根本不需要 Writer，所以它不是可恢复性的前提。真机踩到过：回收站
            // （既不是文件系统项目、也不是系统项目）停在检查点原格时，整块屏的
            // 还原计划曾被它一个项目挡下；它不需要被移动，也就不该阻断别的项目。
            if (currentIcon.PixelPosition == snapIcon.PixelPosition
                && string.Equals(currentIcon.MonitorDeviceName, targetDeviceName, StringComparison.Ordinal))
            {
                items[si] = items[si] with
                {
                    CurrentPosition = currentIcon.PixelPosition,
                    CurrentMonitorDeviceName = currentIcon.MonitorDeviceName,
                    State = TargetDisplayLayoutRestoreItemState.AlreadyAtTarget,
                    IsFileSystemItem = currentIcon.IsFileSystemItem,
                    IsSystemItem = currentIcon.IsSystemItem,
                };
                continue;
            }

            // 可移动性验证
            if (!currentIcon.IsFileSystemItem)
            {
                items[si] = items[si] with
                {
                    CurrentPosition = currentIcon.PixelPosition,
                    CurrentMonitorDeviceName = currentIcon.MonitorDeviceName,
                    State = TargetDisplayLayoutRestoreItemState.NotFileSystemItem,
                    BlockReason = $"当前图标 \"{currentIcon.DisplayName}\" 不是文件系统项目。"
                };
                continue;
            }

            if (currentIcon.IsSystemItem)
            {
                items[si] = items[si] with
                {
                    CurrentPosition = currentIcon.PixelPosition,
                    CurrentMonitorDeviceName = currentIcon.MonitorDeviceName,
                    State = TargetDisplayLayoutRestoreItemState.SystemItem,
                    BlockReason = $"当前图标 \"{currentIcon.DisplayName}\" 是系统项目。"
                };
                continue;
            }

            if (currentIcon.MonitorDeviceName is null)
            {
                items[si] = items[si] with
                {
                    CurrentPosition = currentIcon.PixelPosition,
                    State = TargetDisplayLayoutRestoreItemState.NoMonitorDeviceName,
                    BlockReason = $"当前图标 \"{currentIcon.DisplayName}\" 没有显示器归属。"
                };
                continue;
            }

            // 目标坐标验证（仅真正需要移动的项目）
            if (!DesktopIconLayoutGeometry.TryCreateRect(snapIcon.PixelPosition, capabilities.Spacing, out var targetRect))
            {
                items[si] = items[si] with
                {
                    CurrentPosition = currentIcon.PixelPosition,
                    CurrentMonitorDeviceName = currentIcon.MonitorDeviceName,
                    State = TargetDisplayLayoutRestoreItemState.TargetOutsideCurrentWorkArea,
                    BlockReason = $"快照目标位置 ({snapIcon.PixelPosition.X},{snapIcon.PixelPosition.Y}) 计算占用矩形时溢出，视为越出工作区。"
                };
                continue;
            }

            if (!DesktopIconLayoutGeometry.IsFullyContained(targetRect, targetDisplay.WorkArea))
            {
                items[si] = items[si] with
                {
                    CurrentPosition = currentIcon.PixelPosition,
                    CurrentMonitorDeviceName = currentIcon.MonitorDeviceName,
                    State = TargetDisplayLayoutRestoreItemState.TargetOutsideCurrentWorkArea,
                    BlockReason = $"快照目标位置 ({snapIcon.PixelPosition.X},{snapIcon.PixelPosition.Y}) 越出工作区 " +
                        $"({targetDisplay.WorkArea.Left},{targetDisplay.WorkArea.Top})-({targetDisplay.WorkArea.Right},{targetDisplay.WorkArea.Bottom})。"
                };
                continue;
            }

            // 需要移动且目标位于工作区
            items[si] = items[si] with
            {
                CurrentPosition = currentIcon.PixelPosition,
                CurrentMonitorDeviceName = currentIcon.MonitorDeviceName,
                State = TargetDisplayLayoutRestoreItemState.MoveRequired,
                IsFileSystemItem = currentIcon.IsFileSystemItem,
                IsSystemItem = currentIcon.IsSystemItem,
            };
        }

        // ═══ 阶段 8: 占用关系收集（全量扫描）═══
        // (actionSi, occupierSi) -> 占用者在目标位置
        var occupierPairs = new List<(int ActionSi, int OccupierSi)>();
        // (actionSi, extraCurrentIndex) -> 非计划图标占用
        var extraOccupierPairs = new List<(int ActionSi, int ExtraCurrentIndex)>();

        for (var si = 0; si < snapshotTargetIcons.Count; si++)
        {
            if (items[si].State != TargetDisplayLayoutRestoreItemState.MoveRequired)
                continue;

            if (!DesktopIconLayoutGeometry.TryCreateRect(items[si].SnapshotTargetPosition, capabilities.Spacing, out var targetRect))
                continue;

            if (!snapToCurrentIndex.TryGetValue(si, out var selfCurrentIdx))
                continue;

            for (var ci = 0; ci < currentReadResult.Icons.Count; ci++)
            {
                if (ci == selfCurrentIdx) continue; // 排除自身

                var currentIcon = currentReadResult.Icons[ci];
                // PixelPosition is only an occupancy fact within its resolved monitor.
                // A known icon on another display cannot occupy this target display's
                // restore rectangle.  Unknown monitor ownership remains fail-closed.
                if (currentIcon.MonitorDeviceName is not null
                    && !string.Equals(
                        currentIcon.MonitorDeviceName,
                        targetDeviceName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!DesktopIconLayoutGeometry.TryCreateRect(currentIcon.PixelPosition, capabilities.Spacing, out var currentRect))
                    continue;

                if (DesktopIconLayoutGeometry.Overlaps(targetRect, currentRect))
                {
                    // 查找该当前图标对应的快照项
                    var occupierSi = -1;
                    for (var osi = 0; osi < snapshotTargetIcons.Count; osi++)
                    {
                        if (snapToCurrentIndex.TryGetValue(osi, out var oci) && oci == ci)
                        {
                            occupierSi = osi;
                            break;
                        }
                    }

                    if (occupierSi >= 0)
                        occupierPairs.Add((si, occupierSi));
                    else
                        extraOccupierPairs.Add((si, ci));
                }
            }
        }

        // ═══ 阶段 9: 阻断状态固定点传播 ═══
        var changed = true;
        while (changed)
        {
            changed = false;

            for (var si = 0; si < snapshotTargetIcons.Count; si++)
            {
                if (items[si].State != TargetDisplayLayoutRestoreItemState.MoveRequired)
                    continue;

                // 收集该动作项的全部占用者状态
                var blockers = FindAllBlockers(si, occupierPairs, extraOccupierPairs,
                    currentReadResult, items, snapshotTargetIcons, snapToCurrentIndex);

                if (blockers.stationary.Count > 0 || blockers.blocked.Count > 0 || blockers.alreadyAtTarget.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"被恢复项 \"{items[si].DisplayName}\" ({items[si].StableId}) 的目标位置 ");
                    sb.AppendLine($"({items[si].SnapshotTargetPosition.X},{items[si].SnapshotTargetPosition.Y}) 被以下不可移动图标占用：");
                    foreach (var (bSi, _) in blockers.blocked)
                        sb.AppendLine($"  被阻断项 \"{items[bSi].DisplayName}\" ({items[bSi].StableId}) — 已不会移动");
                    foreach (var (_, ci) in blockers.stationary)
                        sb.AppendLine($"  非计划图标 \"{currentReadResult.Icons[ci].DisplayName}\" ({currentReadResult.Icons[ci].StableId})");
                    foreach (var (bSi, _) in blockers.alreadyAtTarget)
                        sb.AppendLine($"  已就位图标 \"{items[bSi].DisplayName}\" ({items[bSi].StableId}) — 不会移动");

                    items[si] = items[si] with
                    {
                        State = TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon,
                        BlockReason = sb.ToString().TrimEnd()
                    };
                    changed = true;
                }
            }
        }

        // ═══ 阶段 10: 建立最终依赖图 ═══
        var dependencies = new List<TargetDisplayLayoutRestoreDependency>();
        for (var si = 0; si < snapshotTargetIcons.Count; si++)
        {
            if (items[si].State != TargetDisplayLayoutRestoreItemState.MoveRequired)
                continue;

            foreach (var (actionSi, occupierSi) in occupierPairs)
            {
                if (actionSi != si) continue;
                if (items[occupierSi].State != TargetDisplayLayoutRestoreItemState.MoveRequired)
                    continue; // 占用者已不是 MoveRequired（已被阻断或就位）
                dependencies.Add(new TargetDisplayLayoutRestoreDependency(
                    items[occupierSi].StableId, items[actionSi].StableId));
            }
        }

        // ═══ 阶段 11: SCC 环检测 (Tarjan) ═══
        var moveItems = items
            .Select((it, idx) => (Item: it, SnapIdx: idx))
            .Where(x => x.Item.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
            .ToList();

        var cycles = new List<TargetDisplayLayoutRestoreCycle>();
        bool hasCycle;
        IReadOnlyList<string>? topoFromAnalyzer = null;

        if (moveItems.Count > 0)
        {
            var moveSids = moveItems.Select(m => m.Item.StableId).ToList();
            var analysis = TargetDisplayLayoutDependencyGraphAnalyzer.Analyze(moveSids, dependencies);
            hasCycle = analysis.HasCycle;
            cycles = analysis.Cycles.ToList();
            topoFromAnalyzer = analysis.TopologicalOrder;
        }
        else
        {
            hasCycle = false;
        }

        // ═══ 阶段 12: 确定性拓扑排序 ═══
        IReadOnlyList<string>? executionOrder = null;

        if (!hasCycle)
        {
            var anyBlocking = items.Any(it =>
                it.State != TargetDisplayLayoutRestoreItemState.AlreadyAtTarget
                && it.State != TargetDisplayLayoutRestoreItemState.MoveRequired
                && it.State != TargetDisplayLayoutRestoreItemState.MissingFromCurrent);

            if (!anyBlocking && !hasGlobalBlockingIssue && moveItems.Count > 0)
            {
                executionOrder = topoFromAnalyzer;
            }
            else if (moveItems.Count == 0)
            {
                executionOrder = new List<string>();
            }
        }

        // ═══ 阶段 13: 生成摘要 ═══
        var alreadyCount = items.Count(it => it.State == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget);
        var moveCount = items.Count(it => it.State == TargetDisplayLayoutRestoreItemState.MoveRequired);
        var missingCount = items.Count(it => it.State == TargetDisplayLayoutRestoreItemState.MissingFromCurrent);
        var blockedCount = items.Count - alreadyCount - moveCount - missingCount;

        var hasBlocking = items.Any(it =>
            it.State != TargetDisplayLayoutRestoreItemState.AlreadyAtTarget
            && it.State != TargetDisplayLayoutRestoreItemState.MoveRequired
            && it.State != TargetDisplayLayoutRestoreItemState.MissingFromCurrent)
            || hasGlobalBlockingIssue;

        var isNoOp = moveCount == 0 && !hasBlocking && missingCount == 0;

        return new TargetDisplayLayoutRestorePlan(
            targetDeviceName,
            snapshotTargetIcons.Count,
            alreadyCount, moveCount, missingCount, blockedCount,
            hasBlocking, hasCycle, isNoOp,
            items, dependencies, cycles, issues,
            (hasBlocking || hasCycle) ? null : executionOrder);
    }

    // ═══════════════════════════════════════════════════
    //  辅助类型和方法
    // ═══════════════════════════════════════════════════

    private static (
        List<(int snapIdx, int currentIdx)> stationary,
        List<(int snapIdx, int currentIdx)> alreadyAtTarget,
        List<(int snapIdx, int currentIdx)> blocked)
        FindAllBlockers(
            int actionSi,
            List<(int ActionSi, int OccupierSi)> occupierPairs,
            List<(int ActionSi, int ExtraCurrentIndex)> extraOccupierPairs,
            DesktopIconReadResult currentReadResult,
            List<TargetDisplayLayoutRestorePlanItem> items,
            List<DesktopIconSnapshotItem> snapshotTargetIcons,
            Dictionary<int, int> snapToCurrentIndex)
    {
        var stationary = new List<(int, int)>();
        var alreadyAtTarget = new List<(int, int)>();
        var blocked = new List<(int, int)>();

        foreach (var (asi, osi) in occupierPairs)
        {
            if (asi != actionSi) continue;
            var state = items[osi].State;
            if (state == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget)
                alreadyAtTarget.Add((osi, snapToCurrentIndex.GetValueOrDefault(osi, -1)));
            else if (state != TargetDisplayLayoutRestoreItemState.MoveRequired)
                blocked.Add((osi, snapToCurrentIndex.GetValueOrDefault(osi, -1)));
        }

        foreach (var (asi, eci) in extraOccupierPairs)
        {
            if (asi != actionSi) continue;
            stationary.Add((siByCurrentIndex(eci), eci));
        }

        return (stationary, alreadyAtTarget, blocked);

        int siByCurrentIndex(int ci)
        {
            foreach (var kvp in snapToCurrentIndex)
                if (kvp.Value == ci) return kvp.Key;
            return -1;
        }
    }
}
