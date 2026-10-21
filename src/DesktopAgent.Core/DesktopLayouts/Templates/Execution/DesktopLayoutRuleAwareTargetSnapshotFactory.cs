using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Execution;

public static class DesktopLayoutRuleAwareTargetSnapshotFactory
{
    public static DesktopLayoutSnapshot Create(
        DesktopLayoutRuleAwarePreviewPlan previewPlan,
        DesktopIconReadResult currentReadResult,
        DesktopLayoutSnapshotFactory snapshotFactory)
    {
        if (previewPlan is null) throw new ArgumentNullException(nameof(previewPlan));
        if (currentReadResult is null) throw new ArgumentNullException(nameof(currentReadResult));
        if (snapshotFactory is null) throw new ArgumentNullException(nameof(snapshotFactory));
        if (!previewPlan.IsComplete)
            throw new ArgumentException("PreviewPlan 不完整，不能创建目标快照。");

        // Unassigned 拒绝
        if (previewPlan.DefaultUnassignedCount > 0 || previewPlan.OverrideUnassignedCount > 0)
            throw new ArgumentException("PreviewPlan 包含 Unassigned 图标，不能创建目标快照。");

        var targetDeviceName = previewPlan.TargetDisplay.DeviceName;
        var descriptors = new List<DesktopIconDescriptor>(previewPlan.InputIconCount);

        foreach (var item in previewPlan.Items)
        {
            var icon = item.Icon;

            switch (item.PlacementState)
            {
                case DesktopLayoutRuleAwarePreviewPlacementState.Fixed:
                    // Exclude / Pin 是“明确不参与执行”的当前事实，不是恢复目标。
                    // 它们仍保留在 currentReadResult 中，Restore Planner 会把它们当作真实占位者
                    // 参与碰撞 / 阻断判断，但 Writer 永远不会把它们当成待恢复项目。
                    continue;

                case DesktopLayoutRuleAwarePreviewPlacementState.Unassigned:
                    throw new InvalidOperationException(
                        $"Unassigned 图标 \"{icon.StableId}\" 不能出现在目标快照中。");

                case DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget:
                case DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired:
                    descriptors.Add(new DesktopIconDescriptor(
                        icon.StableId,
                        icon.DisplayName,
                        icon.ParsingName,
                        icon.ItemKind,
                        item.TargetPosition!.Value,
                        targetDeviceName,
                        icon.IsSystemItem,
                        icon.IsFileSystemItem));
                    break;

                default:
                    throw new InvalidOperationException($"未处理的 PlacementState: {item.PlacementState}");
            }
        }

        // OutOfScope: keep current position and monitor
        foreach (var oos in previewPlan.OutOfScopeIcons)
        {
            descriptors.Add(new DesktopIconDescriptor(
                oos.StableId,
                oos.DisplayName,
                oos.ParsingName,
                oos.ItemKind,
                oos.PixelPosition,
                oos.MonitorDeviceName,
                oos.IsSystemItem,
                oos.IsFileSystemItem));
        }

        // Sort to preserve InputIcons relative order after Fixed items are removed.
        var itemBySid = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < previewPlan.InputIcons.Count; i++)
            itemBySid[previewPlan.InputIcons[i].StableId] = i;

        descriptors.Sort((a, b) =>
        {
            if (!itemBySid.TryGetValue(a.StableId, out var ai))
                throw new InvalidOperationException(
                    $"目标快照 StableId \"{a.StableId}\" 不在 PreviewPlan.InputIcons 中。");
            if (!itemBySid.TryGetValue(b.StableId, out var bi))
                throw new InvalidOperationException(
                    $"目标快照 StableId \"{b.StableId}\" 不在 PreviewPlan.InputIcons 中。");
            return ai.CompareTo(bi);
        });

        var snapshot = snapshotFactory.Create(
            descriptors, targetDeviceName, currentReadResult.ConnectionMethod);

        // 严格验证
        VerifySnapshot(snapshot, previewPlan);

        return snapshot;
    }

    private static void VerifySnapshot(
        DesktopLayoutSnapshot snapshot,
        DesktopLayoutRuleAwarePreviewPlan previewPlan)
    {
        var fixedStableIds = previewPlan.Items
            .Where(item => item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.Fixed)
            .Select(item => item.Icon.StableId)
            .ToHashSet(StringComparer.Ordinal);

        var expectedInputIcons = previewPlan.InputIcons
            .Where(icon => !fixedStableIds.Contains(icon.StableId))
            .ToList();

        if (snapshot.Icons.Count != expectedInputIcons.Count)
            throw new InvalidOperationException(
                $"目标快照图标数量 ({snapshot.Icons.Count}) != 可执行目标 + OOS 数量 ({expectedInputIcons.Count})。"
                + $" Fixed={fixedStableIds.Count}。"
            );

        var previewItemsBySid = previewPlan.Items
            .ToDictionary(item => item.Icon.StableId, StringComparer.Ordinal);
        var seenSids = new HashSet<string>(StringComparer.Ordinal);
        var targetPositions = new HashSet<PixelPoint>();
        var targetMon = previewPlan.TargetDisplay.DeviceName;

        for (var i = 0; i < snapshot.Icons.Count; i++)
        {
            var si = snapshot.Icons[i];
            var pi = expectedInputIcons[i];

            if (si.Ordinal != i)
                throw new InvalidOperationException($"目标快照 Ordinal[{i}] 不连续。");
            if (!StringComparer.Ordinal.Equals(si.StableId, pi.StableId))
                throw new InvalidOperationException($"目标快照 StableId[{i}] 不匹配。");
            if (!seenSids.Add(si.StableId))
                throw new InvalidOperationException($"目标快照 StableId \"{si.StableId}\" 重复。");
            if (fixedStableIds.Contains(si.StableId))
                throw new InvalidOperationException($"Fixed 图标不允许进入执行目标快照: {si.StableId}。");

            if (!previewItemsBySid.TryGetValue(si.StableId, out var item))
            {
                // OutOfScope
                if (!string.Equals(si.MonitorDeviceName ?? "", pi.MonitorDeviceName ?? "", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"OOS 图标 MonitorDeviceName 被改写: \"{pi.MonitorDeviceName}\" → \"{si.MonitorDeviceName}\"。");
                if (si.PixelPosition != pi.PixelPosition)
                    throw new InvalidOperationException($"OOS 图标位置被改写: {pi.StableId}。");
                continue;
            }

            if (!item.IsAssigned || !item.TargetPosition.HasValue)
                throw new InvalidOperationException($"非 Assigned 图标意外进入执行目标快照: {si.StableId}。");

            // Target display managed icons
            if (!string.Equals(si.MonitorDeviceName, targetMon, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"目标显示器图标 MonitorDeviceName 不一致: {si.StableId}。");

            if (si.PixelPosition != item.TargetPosition.Value)
                throw new InvalidOperationException($"Assigned 图标位置不等于 TargetPosition: {si.StableId}。");

            if (item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget
                && si.PixelPosition != item.CurrentPosition)
            {
                throw new InvalidOperationException($"AlreadyAtTarget 图标 TargetPosition != Current: {si.StableId}。");
            }

            // 目标显示器内执行目标坐标唯一
            if (!targetPositions.Add(si.PixelPosition))
                throw new InvalidOperationException(
                    $"目标显示器内目标坐标重复: ({si.PixelPosition.X},{si.PixelPosition.Y})。");
        }
    }
}
