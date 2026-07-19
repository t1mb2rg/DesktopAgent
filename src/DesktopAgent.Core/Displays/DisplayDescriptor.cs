using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.Displays;

/// <summary>
/// 一块显示器在当前 Windows 显示拓扑中的只读描述。
/// DeviceName 是 PoC 阶段标识；正式版将补充更稳定的设备身份信息。
/// </summary>
public sealed record DisplayDescriptor(
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    public double ScaleX => DpiX / 96d;

    public double ScaleY => DpiY / 96d;
}
