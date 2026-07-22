using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 单个桌面图标的相邻空位移动规划结果。
/// </summary>
public sealed record DesktopIconMovePlan(
    /// <summary>图标的原始位置。</summary>
    PixelPoint OriginalPosition,

    /// <summary>规划的目标位置。</summary>
    PixelPoint TargetPosition,

    /// <summary>移动方向描述（Right / Left / Down / Up）。</summary>
    string Direction);
