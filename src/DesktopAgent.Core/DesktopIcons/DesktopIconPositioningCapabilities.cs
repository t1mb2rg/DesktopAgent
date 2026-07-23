using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 桌面图标定位能力信息，由 GetCapabilities 返回。
/// 纯数据 record，不包含 COM 对象、PIDL 或 HWND。
/// </summary>
public sealed record DesktopIconPositioningCapabilities
{
    public bool IsAutoArrangeEnabled { get; }
    public PixelPoint Spacing { get; }
    public ConnectionMethod ConnectionMethod { get; }
    public Exception? PrimaryPathException { get; }

    public DesktopIconPositioningCapabilities(
        bool isAutoArrangeEnabled,
        PixelPoint spacing,
        ConnectionMethod connectionMethod,
        Exception? primaryPathException)
    {
        if (spacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing.X 必须大于 0。");
        if (spacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing.Y 必须大于 0。");

        IsAutoArrangeEnabled = isAutoArrangeEnabled;
        Spacing = spacing;
        ConnectionMethod = connectionMethod;
        PrimaryPathException = primaryPathException;
    }
}
