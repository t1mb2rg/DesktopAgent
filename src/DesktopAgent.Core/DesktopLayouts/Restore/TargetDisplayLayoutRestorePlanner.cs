using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore;

/// <summary>
/// 目标显示器布局恢复规划器。纯逻辑，不访问 Shell COM、SQLite、WPF、WindowsInterop。
/// 只生成计划，不执行计划。
/// </summary>
public static class TargetDisplayLayoutRestorePlanner
{
    public static TargetDisplayLayoutRestorePlan CreatePlan(
        DesktopLayoutSnapshot snapshot,
        DesktopIconReadResult currentReadResult,
        IReadOnlyList<DisplayDescriptor> currentDisplays,
        DesktopIconPositioningCapabilities capabilities,
        string targetDeviceName)
    {
        // ── 参数验证 ──
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentReadResult);
        ArgumentNullException.ThrowIfNull(currentDisplays);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (targetDeviceName is null)
            throw new ArgumentNullException(nameof(targetDeviceName));
        if (string.IsNullOrWhiteSpace(targetDeviceName))
            throw new ArgumentException("targetDeviceName 不能为空白。", nameof(targetDeviceName));

        // ── 全局前置验证 ──
        if (currentReadResult.FailedItemCount != 0)
            throw new InvalidOperationException(
                $"当前读取不完整（{currentReadResult.FailedItemCount} 个失败），不能生成恢复计划。");

        if (capabilities.IsAutoArrangeEnabled)
            throw new InvalidOperationException(
                "自动排列图标已开启，不能生成恢复计划。请先关闭「自动排列图标」。");

        var targetDisplay = UniqueDisplaySelector.FindByDeviceName(currentDisplays, targetDeviceName);

        // 快照目标显示器必须一致
        if (snapshot.TargetMonitorDeviceName is null)
            throw new InvalidOperationException("快照的 TargetMonitorDeviceName 为 null，不能用于指定显示器恢复。");
        if (!string.Equals(snapshot.TargetMonitorDeviceName, targetDeviceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"快照目标显示器 {snapshot.TargetMonitorDeviceName} 与请求的 {targetDeviceName} 不一致。");

        // ── 快照目标集合：仅属于目标显示器的图标 ──
        var snapshotTargetIcons = new List<DesktopIconSnapshotItem>();
        foreach (var icon in snapshot.Icons)
        {
            if (icon.MonitorDeviceName is not null
                && string.Equals(icon.MonitorDeviceName, targetDeviceName, StringComparison.Ordinal))
            {
                snapshotTargetIcons.Add(icon);
            }
        }

        var items = new List<TargetDisplayLayoutRestorePlanItem>();
        var dependencies = new List<TargetDisplayLayoutRestoreDependency>();
        var cycles = new List<TargetDisplayLayoutRestoreCycle>();

        if (snapshotTargetIcons.Count == 0)
        {
            return new TargetDisplayLayoutRestorePlan(
                targetDeviceName, 0, 0, 0, 0, 0,
                false, false, true,
                items, dependencies, cycles, null);
        }

        // ── 快照目标集合 StableId 唯一性 ──
        var snapshotStableIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < snapshotTargetIcons.Count; i++)
        {
            var sid = snapshotTargetIcons[i].StableId;
            if (snapshotStableIds.ContainsKey(sid))
            {
                // 标记重复项
                var firstIdx = snapshotStableIds[sid];
                items.Add(new TargetDisplayLayoutRestorePlanItem(
                    sid, snapshotTargetIcons[i].DisplayName,
                    snapshotTargetIcons[i].PixelPosition, null, null,
                    TargetDisplayLayoutRestoreItemState.SnapshotStableIdDuplicate,
                    snapshotTargetIcons[i].IsFileSystemItem,
                    snapshotTargetIcons[i].IsSystemItem,
                    $"快照目标集合中 StableId \"{sid}\" 重复（索引 {firstIdx} 和 {i}）。"));
                snapshotStableIds[sid] = -1; // 标记为已处理
                continue;
            }
            snapshotStableIds[sid] = i;
        }

        // ── 当前桌面 StableId 索引 ──
        var currentByStableId = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < currentReadResult.Icons.Count; i++)
        {
            var sid = currentReadResult.Icons[i].StableId;
            if (!currentByStableId.ContainsKey(sid))
                currentByStableId[sid] = new List<int>();
            currentByStableId[sid].Add(i);
        }

        // ── 快照目标坐标重复检测 ──
        var snapshotPosUsage = new Dictionary<PixelPoint, string>();
        var duplicateSnapshotPositions = new HashSet<PixelPoint>();

        // ── 对每个快照目标图标分类 ──
        var moveRequiredItems = new List<(TargetDisplayLayoutRestorePlanItem Item, int ItemIndex)>();
        var blockReasonsForStableId = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < snapshotTargetIcons.Count; i++)
        {
            var snapIcon = snapshotTargetIcons[i];
            if (snapshotStableIds[snapIcon.StableId] < 0) continue; // 已处理重复

            // 当前匹配
            if (!currentByStableId.TryGetValue(snapIcon.StableId, out var currentMatches))
            {
                items.Add(new TargetDisplayLayoutRestorePlanItem(
                    snapIcon.StableId, snapIcon.DisplayName,
                    snapIcon.PixelPosition, null, null,
                    TargetDisplayLayoutRestoreItemState.MissingFromCurrent,
                    snapIcon.IsFileSystemItem, snapIcon.IsSystemItem,
                    $"StableId \"{snapIcon.StableId}\" 在当前桌面中未找到。"));
                continue;
            }

            if (currentMatches.Count > 1)
            {
                items.Add(new TargetDisplayLayoutRestorePlanItem(
                    snapIcon.StableId, snapIcon.DisplayName,
                    snapIcon.PixelPosition, null, null,
                    TargetDisplayLayoutRestoreItemState.DuplicateCurrentStableId,
                    snapIcon.IsFileSystemItem, snapIcon.IsSystemItem,
                    $"StableId \"{snapIcon.StableId}\" 在当前桌面中有 {currentMatches.Count} 个匹配。"));
                continue;
            }

            var currentIcon = currentReadResult.Icons[currentMatches[0]];

            // 可移动性验证
            if (!currentIcon.IsFileSystemItem)
            {
                items.Add(new TargetDisplayLayoutRestorePlanItem(
                    snapIcon.StableId, snapIcon.DisplayName,
                    snapIcon.PixelPosition, currentIcon.PixelPosition,
                    currentIcon.MonitorDeviceName,
                    TargetDisplayLayoutRestoreItemState.NotFileSystemItem,
                    currentIcon.IsFileSystemItem, currentIcon.IsSystemItem,
                    $"当前图标 \"{currentIcon.DisplayName}\" 不是文件系统项目。"));
                continue;
            }

            if (currentIcon.IsSystemItem)
            {
                items.Add(new TargetDisplayLayoutRestorePlanItem(
                    snapIcon.StableId, snapIcon.DisplayName,
                    snapIcon.PixelPosition, currentIcon.PixelPosition,
                    currentIcon.MonitorDeviceName,
                    TargetDisplayLayoutRestoreItemState.SystemItem,
                    currentIcon.IsFileSystemItem, currentIcon.IsSystemItem,
                    $"当前图标 \"{currentIcon.DisplayName}\" 是系统项目。"));
                continue;
            }

            if (currentIcon.MonitorDeviceName is null)
            {
                items.Add(new TargetDisplayLayoutRestorePlanItem(
                    snapIcon.StableId, snapIcon.DisplayName,
                    snapIcon.PixelPosition, currentIcon.PixelPosition,
                    null,
                    TargetDisplayLayoutRestoreItemState.NoMonitorDeviceName,
                    currentIcon.IsFileSystemItem, currentIcon.IsSystemItem,
                    $"当前图标 \"{currentIcon.DisplayName}\" 没有显示器归属。"));
                continue;
            }

            // 目标坐标边界验证
            var targetRect = RectFromPosition(snapIcon.PixelPosition, capabilities.Spacing);
            if (!RectsFullyContained(targetRect, targetDisplay.WorkArea))
            {
                items.Add(new TargetDisplayLayoutRestorePlanItem(
                    snapIcon.StableId, snapIcon.DisplayName,
                    snapIcon.PixelPosition, currentIcon.PixelPosition,
                    currentIcon.MonitorDeviceName,
                    TargetDisplayLayoutRestoreItemState.TargetOutsideCurrentWorkArea,
                    currentIcon.IsFileSystemItem, currentIcon.IsSystemItem,
                    $"快照目标位置 ({snapIcon.PixelPosition.X},{snapIcon.PixelPosition.Y}) 越出当前工作区 " +
                    $"({targetDisplay.WorkArea.Left},{targetDisplay.WorkArea.Top})-({targetDisplay.WorkArea.Right},{targetDisplay.WorkArea.Bottom})。"));
                continue;
            }

            // 检查快照目标坐标重复
            if (snapshotPosUsage.TryGetValue(snapIcon.PixelPosition, out var otherStableId))
            {
                duplicateSnapshotPositions.Add(snapIcon.PixelPosition);
                blockReasonsForStableId[snapIcon.StableId] =
                    $"快照目标位置 ({snapIcon.PixelPosition.X},{snapIcon.PixelPosition.Y}) 与 StableId \"{otherStableId}\" 的目标位置重复。";
                blockReasonsForStableId[otherStableId] =
                    $"快照目标位置 ({snapIcon.PixelPosition.X},{snapIcon.PixelPosition.Y}) 与 StableId \"{snapIcon.StableId}\" 的目标位置重复。";
            }
            else
            {
                snapshotPosUsage[snapIcon.PixelPosition] = snapIcon.StableId;
            }

            // 已在目标位置？
            if (currentIcon.PixelPosition == snapIcon.PixelPosition)
            {
                items.Add(new TargetDisplayLayoutRestorePlanItem(
                    snapIcon.StableId, snapIcon.DisplayName,
                    snapIcon.PixelPosition, currentIcon.PixelPosition,
                    currentIcon.MonitorDeviceName,
                    TargetDisplayLayoutRestoreItemState.AlreadyAtTarget,
                    currentIcon.IsFileSystemItem, currentIcon.IsSystemItem));
            }
            else
            {
                var item = new TargetDisplayLayoutRestorePlanItem(
                    snapIcon.StableId, snapIcon.DisplayName,
                    snapIcon.PixelPosition, currentIcon.PixelPosition,
                    currentIcon.MonitorDeviceName,
                    TargetDisplayLayoutRestoreItemState.MoveRequired,
                    currentIcon.IsFileSystemItem, currentIcon.IsSystemItem);
                items.Add(item);
                moveRequiredItems.Add((item, items.Count - 1));
            }
        }

        // ── 处理快照目标坐标重复 ──
        for (var i = 0; i < items.Count; i++)
        {
            if (duplicateSnapshotPositions.Contains(items[i].SnapshotTargetPosition)
                && items[i].State != TargetDisplayLayoutRestoreItemState.SnapshotStableIdDuplicate)
            {
                items[i] = items[i] with
                {
                    State = TargetDisplayLayoutRestoreItemState.DuplicateSnapshotTargetPosition,
                    BlockReason = blockReasonsForStableId.TryGetValue(items[i].StableId, out var r)
                        ? r : items[i].BlockReason
                };
            }
        }

        // ── 占用分析：对每个 MoveRequired 项检查目标位置占用 ──
        foreach (var (item, idx) in moveRequiredItems)
        {
            var targetPos = item.SnapshotTargetPosition;
            var targetRect = RectFromPosition(targetPos, capabilities.Spacing);

            // 检查是否由另一个 MoveRequired 项占用
            DesktopIconDescriptor? occupyingCurrent = null;
            foreach (var currentIcon in currentReadResult.Icons)
            {
                if (currentIcon.PixelPosition == item.CurrentPosition) continue; // 自身

                var currentRect = RectFromPosition(currentIcon.PixelPosition, capabilities.Spacing);
                if (RectsOverlap(targetRect, currentRect))
                {
                    occupyingCurrent = currentIcon;
                    break;
                }
            }

            if (occupyingCurrent is not null)
            {
                // 占用者是否也是 MoveRequired 或 AlreadyAtTarget？
                var occupierPlanItem = items.FirstOrDefault(
                    pi => string.Equals(pi.StableId, occupyingCurrent.StableId, StringComparison.Ordinal));

                if (occupierPlanItem is not null
                    && (occupierPlanItem.State == TargetDisplayLayoutRestoreItemState.MoveRequired
                        || occupierPlanItem.State == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget))
                {
                    if (occupierPlanItem.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
                    {
                        // 依赖：占用者必须先移动
                        dependencies.Add(new TargetDisplayLayoutRestoreDependency(
                            occupierPlanItem.StableId, item.StableId));
                    }
                    else
                    {
                        // AlreadyAtTarget 占用 → 静态阻断
                        items[idx] = item with
                        {
                            State = TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon,
                            BlockReason = $"目标位置被已就位图标 \"{occupierPlanItem.DisplayName}\" ({occupierPlanItem.StableId}) 占用，不会移动。"
                        };
                    }
                }
                else
                {
                    // 占用者不在恢复集合中或不可移动 → 静态阻断
                    items[idx] = item with
                    {
                        State = TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon,
                        BlockReason = $"目标位置被非计划图标 \"{occupyingCurrent.DisplayName}\" ({occupyingCurrent.StableId}) 占用。"
                    };
                }
            }
        }

        // ── 拓扑排序与环检测 ──
        IReadOnlyList<string>? executionOrder = null;
        var hasCycle = false;

        var moveItems = items
            .Where(it => it.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
            .ToList();

        if (moveItems.Count > 0 && dependencies.Count > 0)
        {
            var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var mi in moveItems)
            {
                inDegree[mi.StableId] = 0;
                adj[mi.StableId] = new List<string>();
            }

            foreach (var dep in dependencies)
            {
                if (inDegree.ContainsKey(dep.DependsOn) && inDegree.ContainsKey(dep.ActionItem))
                {
                    adj[dep.DependsOn].Add(dep.ActionItem);
                    inDegree[dep.ActionItem] = inDegree[dep.ActionItem] + 1;
                }
            }

            var queue = new Queue<string>();
            foreach (var mi in moveItems)
            {
                if (inDegree[mi.StableId] == 0)
                    queue.Enqueue(mi.StableId);
            }

            var sorted = new List<string>();
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                sorted.Add(current);
                foreach (var neighbor in adj[current])
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                        queue.Enqueue(neighbor);
                }
            }

            if (sorted.Count < moveItems.Count)
            {
                // 存在环
                hasCycle = true;
                var remaining = new List<string>();
                foreach (var mi in moveItems)
                {
                    if (!sorted.Contains(mi.StableId))
                        remaining.Add(mi.StableId);
                }
                cycles.Add(new TargetDisplayLayoutRestoreCycle(remaining));
            }
            else
            {
                executionOrder = sorted;
            }
        }
        else if (moveItems.Count > 0)
        {
            // 无依赖，执行顺序为快照顺序（确定性）
            executionOrder = moveItems.Select(mi => mi.StableId).ToList();
        }

        // ── 统计摘要 ──
        var alreadyCount = items.Count(it => it.State == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget);
        var moveCount = moveItems.Count(it => it.State == TargetDisplayLayoutRestoreItemState.MoveRequired);
        var missingCount = items.Count(it => it.State == TargetDisplayLayoutRestoreItemState.MissingFromCurrent);
        var blockedCount = items.Count - alreadyCount - moveCount - missingCount;

        var hasBlocking = items.Any(it =>
            it.State != TargetDisplayLayoutRestoreItemState.AlreadyAtTarget
            && it.State != TargetDisplayLayoutRestoreItemState.MoveRequired
            && it.State != TargetDisplayLayoutRestoreItemState.MissingFromCurrent);

        // MoveRequired 被阻断的也要算
        var finalMoveRequiredCount = moveItems.Count(it => it.State == TargetDisplayLayoutRestoreItemState.MoveRequired);

        // 0 个 MoveRequired 且无阻断 → IsNoOp
        var isNoOp = finalMoveRequiredCount == 0 && !hasBlocking && missingCount == 0;

        return new TargetDisplayLayoutRestorePlan(
            targetDeviceName,
            snapshotTargetIcons.Count,
            alreadyCount,
            finalMoveRequiredCount,
            missingCount,
            blockedCount,
            hasBlocking,
            hasCycle,
            isNoOp,
            items,
            dependencies,
            cycles,
            hasBlocking || hasCycle ? null : executionOrder);
    }

    private static PixelRect RectFromPosition(PixelPoint position, PixelPoint spacing) =>
        checked(new PixelRect(position.X, position.Y, position.X + spacing.X, position.Y + spacing.Y));

    private static bool RectsFullyContained(PixelRect inner, PixelRect outer) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top
        && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static bool RectsOverlap(PixelRect a, PixelRect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
