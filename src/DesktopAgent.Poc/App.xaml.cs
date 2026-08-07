using System.Windows;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.DesktopLayouts.Templates.Rules;
using DesktopAgent.Poc.Acceptance;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.Displays;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopAgent.Poc;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        AcceptanceFaultInjectionOptions acceptanceFaultOptions;
        try
        {
            acceptanceFaultOptions = AcceptanceFaultInjectionOptions.FromEnvironment();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"M2 acceptance fault injection 配置无效，应用将 fail-closed 退出。\n\n{ex.Message}",
                "DesktopAgent - Fault Injection 配置错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        if (acceptanceFaultOptions.IsEnabled)
        {
            MessageBox.Show(
                "FAULT INJECTION ENABLED\n\n" +
                "这是 M2 实机验收专用 Debug 模式。命中配置的 Exact Writer 调用后，" +
                "DesktopAgent 会在调用方持久化 checkpoint 之前立即终止进程。\n\n" +
                acceptanceFaultOptions.Describe(),
                "DesktopAgent - M2 Acceptance Fault Injection",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
#endif

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IDisplayTopologyReader, NativeDisplayTopologyReader>();
                services.AddSingleton<IDesktopIconReader, DesktopShellIconReader>();
                services.AddSingleton<DesktopShellIconPositionWriter>();
#if DEBUG
                services.AddSingleton<IDesktopIconPositionWriter>(sp =>
                    acceptanceFaultOptions.IsEnabled
                        ? new AcceptanceFaultInjectingDesktopIconPositionWriter(
                            sp.GetRequiredService<DesktopShellIconPositionWriter>(),
                            acceptanceFaultOptions)
                        : sp.GetRequiredService<DesktopShellIconPositionWriter>());
#else
                services.AddSingleton<IDesktopIconPositionWriter>(sp =>
                    sp.GetRequiredService<DesktopShellIconPositionWriter>());
#endif
                services.AddSingleton<StaThreadExecutor>();
                services.AddSingleton<DesktopLayoutSnapshotFactory>();
                services.AddSingleton<IRecoverySessionStore>(
                    new SqliteRecoverySessionStore(
                        DesktopAgentDataPaths.DefaultDatabasePath));
                services.AddSingleton<IDesktopLayoutSnapshotStore>(
                    new SqliteDesktopLayoutSnapshotStore(
                        DesktopAgentDataPaths.DefaultDatabasePath));
                services.AddSingleton<IDesktopLayoutSafetySnapshotFactory>(sp =>
                    new DefaultSafetySnapshotFactory(
                        sp.GetRequiredService<DesktopLayoutSnapshotFactory>()));
                services.AddSingleton<RecoveryOrchestrator>();
                services.AddSingleton<PersistedRecoveryCoordinator>();
                services.AddSingleton<TargetDisplayLayoutExecutionCoordinator>();
                services.AddSingleton<IDesktopLayoutTemplateStore>(
                    new SqliteDesktopLayoutTemplateStore(
                        DesktopAgentDataPaths.DefaultDatabasePath));
                services.AddSingleton<IDesktopLayoutIconRuleStore>(
                    new SqliteDesktopLayoutIconRuleStore(
                        DesktopAgentDataPaths.DefaultDatabasePath));
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _host.StartAsync().GetAwaiter().GetResult();

        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
