namespace DesktopAgent.Core.Displays;

/// <summary>
/// 读取当前任意数量显示器的拓扑信息。
/// 该接口不依赖 WPF 或 WinUI，便于后续替换表现层。
/// </summary>
public interface IDisplayTopologyReader
{
    IReadOnlyList<DisplayDescriptor> GetDisplays();
}
