using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 纯函数移动准备服务。验证选中图标是否可安全移动并规划相邻空位。
/// 不访问 Shell COM、SQLite、WPF、文件系统、显示器 API。
/// </summary>
public static class DesktopIconMovePreparationService
{
    public static DesktopIconMovePreparation Prepare(
        string selectedStableId,
        DesktopIconReadResult readResult,
        DisplayDescriptor targetDisplay,
        DesktopIconPositioningCapabilities capabilities)
    {
        if (selectedStableId is null)
            throw new ArgumentNullException(nameof(selectedStableId));
        if (string.IsNullOrWhiteSpace(selectedStableId))
            throw new ArgumentException("selectedStableId 不能为空白。", nameof(selectedStableId));

        ArgumentNullException.ThrowIfNull(readResult);
        ArgumentNullException.ThrowIfNull(targetDisplay);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (readResult.FailedItemCount != 0)
            throw new InvalidOperationException(
                $"读取结果不完整（{readResult.FailedItemCount} 个失败），不能准备移动。");

        if (capabilities.IsAutoArrangeEnabled)
            throw new InvalidOperationException(
                "自动排列图标已开启。请先在桌面右键菜单中关闭「自动排列图标」后再操作。");

        // StableId 精确匹配（必须恰好 1 个），记录匹配索引
        DesktopIconDescriptor? selectedIcon = null;
        var matchCount = 0;
        var selectedIndex = -1;
        for (var i = 0; i < readResult.Icons.Count; i++)
        {
            if (string.Equals(readResult.Icons[i].StableId, selectedStableId, StringComparison.Ordinal))
            {
                matchCount++;
                selectedIcon = readResult.Icons[i];
                selectedIndex = i;
            }
        }

        if (matchCount == 0)
            throw new InvalidOperationException(
                $"在读取结果中未找到 StableId \"{selectedStableId}\" 的图标。");
        if (matchCount > 1)
            throw new InvalidOperationException(
                $"StableId \"{selectedStableId}\" 在读取结果中有 {matchCount} 个匹配（预期恰好 1 个）。");

        if (selectedIndex < 0 || selectedIcon is null)
            throw new InvalidOperationException("内部错误：匹配索引无效。");

        // 必须是文件系统项目、非系统项目
        if (!selectedIcon.IsFileSystemItem)
            throw new InvalidOperationException(
                $"选中图标 \"{selectedIcon.DisplayName}\" 不是文件系统项目，不允许移动。");
        if (selectedIcon.IsSystemItem)
            throw new InvalidOperationException(
                $"选中图标 \"{selectedIcon.DisplayName}\" 是系统项目，不允许移动。");

        // 必须有 MonitorDeviceName
        if (selectedIcon.MonitorDeviceName is null)
            throw new InvalidOperationException(
                $"选中图标 \"{selectedIcon.DisplayName}\" 没有显示器归属，无法确定工作区。");

        // 必须属于目标显示器
        if (!string.Equals(selectedIcon.MonitorDeviceName, targetDisplay.DeviceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"选中图标属于 {selectedIcon.MonitorDeviceName}，但当前目标显示器是 {targetDisplay.DeviceName}。");

        // 构建 occupiedPositions：按索引排除选中图标
        var occupiedPositions = new List<PixelPoint>(readResult.Icons.Count - 1);
        for (var i = 0; i < readResult.Icons.Count; i++)
        {
            if (i == selectedIndex) continue;
            occupiedPositions.Add(readResult.Icons[i].PixelPosition);
        }

        var plan = DesktopIconMovePlanner.PlanAdjacentMove(
            selectedIcon.PixelPosition,
            targetDisplay.WorkArea,
            capabilities.Spacing,
            occupiedPositions);

        return new DesktopIconMovePreparation(selectedIcon, plan);
    }
}
