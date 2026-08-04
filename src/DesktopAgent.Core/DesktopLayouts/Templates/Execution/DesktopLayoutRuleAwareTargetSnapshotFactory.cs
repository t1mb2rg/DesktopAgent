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
            PixelPoint targetPos;
            string? mon;

            switch (item.PlacementState)
            {
                case DesktopLayoutRuleAwarePreviewPlacementState.Fixed:
                case DesktopLayoutRuleAwarePreviewPlacementState.Unassigned:
                    // Excluded/Pinned: keep CurrentPosition on target display
                    targetPos = item.CurrentPosition;
                    mon = targetDeviceName;
                    break;

                case DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget:
                case DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired:
                    targetPos = item.TargetPosition!.Value;
                    mon = targetDeviceName;
                    break;

                default:
                    throw new InvalidOperationException($"未处理的 PlacementState: {item.PlacementState}");
            }

            descriptors.Add(new DesktopIconDescriptor(
                icon.StableId,
                icon.DisplayName,
                icon.ParsingName,
                icon.ItemKind,
                targetPos,
                mon,
                icon.IsSystemItem,
                icon.IsFileSystemItem));
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

        // Sort to restore InputIcons order
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
        if (snapshot.Icons.Count != previewPlan.InputIconCount)
            throw new InvalidOperationException(
                $"目标快照图标数量 ({snapshot.Icons.Count}) != PreviewPlan.InputIconCount ({previewPlan.InputIconCount})。");

        var seenSids = new HashSet<string>(StringComparer.Ordinal);
        var targetPositions = new HashSet<PixelPoint>();
        var targetMon = previewPlan.TargetDisplay.DeviceName;

        for (var i = 0; i < snapshot.Icons.Count; i++)
        {
            var si = snapshot.Icons[i];
            var pi = previewPlan.InputIcons[i];

            if (si.Ordinal != i)
                throw new InvalidOperationException($"目标快照 Ordinal[{i}] 不连续。");
            if (!StringComparer.Ordinal.Equals(si.StableId, pi.StableId))
                throw new InvalidOperationException($"目标快照 StableId[{i}] 不匹配。");
            if (!seenSids.Add(si.StableId))
                throw new InvalidOperationException($"目标快照 StableId \"{si.StableId}\" 重复。");

            // 找到对应的 PreviewItem
            DesktopLayoutRuleAwarePreviewItem? item = null;
            foreach (var pItem in previewPlan.Items)
                if (ReferenceEquals(pItem.Icon, pi)) { item = pItem; break; }
            if (item is null)
            {
                // OutOfScope
                if (!string.Equals(si.MonitorDeviceName ?? "", pi.MonitorDeviceName ?? "", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"OOS 图标 MonitorDeviceName 被改写: \"{pi.MonitorDeviceName}\" → \"{si.MonitorDeviceName}\"。");
                if (si.PixelPosition != pi.PixelPosition)
                    throw new InvalidOperationException($"OOS 图标位置被改写: {pi.StableId}。");
                continue;
            }

            // Target display icons
            if (!string.Equals(si.MonitorDeviceName, targetMon, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"目标显示器图标 MonitorDeviceName 不一致: {si.StableId}。");

            if (item.IsAssigned)
            {
                if (si.PixelPosition != item.TargetPosition!.Value)
                    throw new InvalidOperationException($"Assigned 图标位置不等于 TargetPosition: {si.StableId}。");
            }

            if (item.IsAssigned && item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget)
            {
                if (si.PixelPosition != item.CurrentPosition)
                    throw new InvalidOperationException($"AlreadyAtTarget 图标 TargetPosition != Current: {si.StableId}。");
            }

            // 目标显示器内目标坐标唯一
            if (!targetPositions.Add(si.PixelPosition))
                throw new InvalidOperationException(
                    $"目标显示器内目标坐标重复: ({si.PixelPosition.X},{si.PixelPosition.Y})。");
        }
    }
}
