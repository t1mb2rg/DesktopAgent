using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 桌面图标位置写入接口。
/// Writer 同时具备只读能力查询，但 M3-A 等只读路径只应依赖
/// IDesktopIconPositioningCapabilitiesReader，避免获得真实写入口。
/// 所有方法必须在 STA 线程上调用。
/// </summary>
public interface IDesktopIconPositionWriter : IDesktopIconPositioningCapabilitiesReader
{
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
