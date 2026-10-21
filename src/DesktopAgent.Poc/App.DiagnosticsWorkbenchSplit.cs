using System;
using System.Windows.Threading;
using DesktopAgent.Poc.Acceptance;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopAgent.Poc;

public partial class App
{
    private bool _diagnosticsWorkbenchSplitScheduled;
    private bool _diagnosticsWorkbenchLifecycleApplied;

    /// <summary>
    /// 由 Diagnostics 主窗口完成首帧渲染后调用。
    /// 生命周期拆分仍排到 ApplicationIdle，避免与 App.OnStartup 后半段的旧 M3 tray composition 竞态。
    /// </summary>
    internal void ScheduleDiagnosticsWorkbenchLifecycle(MainWindow mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);

        if (_diagnosticsWorkbenchSplitScheduled)
            return;

        _diagnosticsWorkbenchSplitScheduled = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => ApplyDiagnosticsWorkbenchLifecycle(mainWindow)));
    }

    private void ApplyDiagnosticsWorkbenchLifecycle(MainWindow mainWindow)
    {
        if (_diagnosticsWorkbenchLifecycleApplied || _host is null)
            return;

        _diagnosticsWorkbenchLifecycleApplied = true;

        var runtimeProfile = DiagnosticsWorkbenchRuntimeProfile.For(IsResidentAcceptanceArmed());
        if (!runtimeProfile.KeepResidentTray)
        {
            var tray = _host.Services.GetRequiredService<M3TrayController>();
            if (tray.IsStarted)
                tray.Dispose();
        }

        if (runtimeProfile.ShutdownOnMainWindowClose)
            mainWindow.Closed += (_, _) => Shutdown();
    }

    private static bool IsResidentAcceptanceArmed()
    {
#if DEBUG
        // App.OnStartup 已先验证 acceptance 环境变量；这里仅复用同一配置判断是否必须
        // 保留旧 M3 resident tray。若环境在运行中被外部改坏，宁可按普通 diagnostics
        // fail-closed 为非 resident，而不是意外保留 unattended 控制面。
        try
        {
            return M3AutomaticAcceptanceOptions.FromEnvironment().IsEnabled;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }
}
