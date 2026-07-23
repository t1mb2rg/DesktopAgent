using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 桌面图标位置写入接口。
/// 所有方法必须在 STA 线程上调用。
/// </summary>
public interface IDesktopIconPositionWriter
{
    /// <summary>
    /// 读取桌面图标定位能力信息（自动排列状态和间距）。
    /// 不修改桌面，不调用 SelectAndPositionItems。
    /// 必须在 STA 线程调用。
    /// </summary>
    DesktopIconPositioningCapabilities GetCapabilities();

    /// <summary>
    /// 将单个桌面图标移动至指定物理像素坐标。
    /// 自动排列开启时拒绝移动。只支持一次移动一个图标。
    /// 通过 StableId 在当前桌面视图枚举中唯一匹配目标 PIDL。
    /// 必须在 STA 线程调用。
    /// </summary>
    /// <param name="stableId">要移动的图标 StableId（唯一匹配）。</param>
    /// <param name="targetPosition">目标物理像素坐标。</param>
    /// <returns>移动操作结果，包含前后位置和 IsExact 判断。</returns>
    DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition);
}
