using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 只读读取当前 Explorer 桌面视图中的图标项目与物理像素坐标。
/// </summary>
public interface IDesktopIconReader
{
    /// <summary>
    /// 读取桌面图标列表及诊断信息。
    /// 必须从 STA 线程调用。
    /// 单个项目读取失败时记录错误并继续读取其余项目。
    /// </summary>
    /// <returns>
    /// 包含图标列表、连接方式、单项失败数和读取耗时的结果。
    /// 无法连接桌面视图时抛出异常。
    /// </returns>
    DesktopIconReadResult ReadDesktopIcons();
}
