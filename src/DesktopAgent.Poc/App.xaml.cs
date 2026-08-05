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
                services.AddSingleton<IDesktopIconPositionWriter, DesktopShellIconPositionWriter>();
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
