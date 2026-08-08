using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 只读读取 Explorer 桌面图标定位能力。
/// M3-A 等只读后台路径应依赖此接口，而不是获得 IDesktopIconPositionWriter。
/// 所有方法必须在 STA 线程上调用。
/// </summary>
public interface IDesktopIconPositioningCapabilitiesReader
{
    DesktopIconPositioningCapabilities GetCapabilities();
}
