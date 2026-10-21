namespace DesktopAgent.Poc;

/// <summary>
/// M5-06 Diagnostics Workbench 的进程生命周期合同。
/// 普通工程诊断是前台工具；只有显式武装的 unattended acceptance
/// 才允许保留历史 M3 resident tray，因为该验收仍依赖 tray 切换 Automatic。
/// </summary>
public sealed record DiagnosticsWorkbenchRuntimeProfile(
    bool KeepResidentTray,
    bool ShutdownOnMainWindowClose)
{
    public static DiagnosticsWorkbenchRuntimeProfile For(bool automaticAcceptanceArmed)
        => automaticAcceptanceArmed
            ? new DiagnosticsWorkbenchRuntimeProfile(
                KeepResidentTray: true,
                ShutdownOnMainWindowClose: false)
            : new DiagnosticsWorkbenchRuntimeProfile(
                KeepResidentTray: false,
                ShutdownOnMainWindowClose: true);
}
