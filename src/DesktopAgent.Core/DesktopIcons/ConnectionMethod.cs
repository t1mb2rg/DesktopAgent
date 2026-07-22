namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 桌面 Shell 连接方式诊断标识。
/// 不依赖 COM、Win32 或 WPF。
/// </summary>
public enum ConnectionMethod
{
    /// <summary>主路径：IShellWindows.FindWindowSW + SWC_DESKTOP。</summary>
    FindWindowSW,

    /// <summary>降级路径：IShellWindows.Item 枚举 + Progman 窗口类名检测。</summary>
    EnumerationFallback,
}
