using System.Windows;
using System.Windows.Threading;
using DesktopAgent.Application.DesktopNotifications;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Application.DesktopSuggestions;
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
using DesktopAgent.WindowsInterop.DesktopNotifications;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopAgent.Poc;

public partial class App : System.Windows.Application
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
            var behaviorDescription = acceptanceFaultOptions.WillTerminateProcess
                ? "命中配置的 Exact Writer 调用后，DesktopAgent 会在调用方持久化 checkpoint 之前立即终止进程。"
                : "命中配置的 Exact Writer 调用后，DesktopAgent 会在调用方持久化 checkpoint 之前抛出受控异常。应用不会退出；执行链会先 fail-closed，随后返回主界面。若真实写入已经发生，主界面会保持人工恢复锁。";

            MessageBox.Show(
                "FAULT INJECTION ENABLED\n\n" +
                "这是 M2 实机验收 / PoC 调试专用 Debug 模式。\n\n" +
                behaviorDescription + "\n\n" +
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
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IDisplayTopologyReader, NativeDisplayTopologyReader>();
                services.AddSingleton<IDesktopIconReader, DesktopShellIconReader>();
                services.AddSingleton<DesktopShellIconPositionWriter>();
                services.AddSingleton<IDesktopIconPositioningCapabilitiesReader>(sp =>
                    sp.GetRequiredService<DesktopShellIconPositionWriter>());
#if DEBUG
                services.AddSingleton<IDesktopIconPositionWriter>(sp =>
                    acceptanceFaultOptions.IsEnabled
                        ? new AcceptanceFaultInjectingDesktopIconPositionWriter(
                            sp.GetRequiredService<DesktopShellIconPositionWriter>(),
                            acceptanceFaultOptions,
                            scheduleSoftFaultUiReturn: acceptanceFaultOptions.Behavior == AcceptanceFaultInjectionBehavior.SoftError
                                ? ScheduleSoftFaultUiReturn
                                : null)
                        : sp.GetRequiredService<DesktopShellIconPositionWriter>());
#else
                services.AddSingleton<IDesktopIconPositionWriter>(sp =>
                    sp.GetRequiredService<DesktopShellIconPositionWriter>());
#endif
                services.AddSingleton<StaThreadExecutor>();
                services.AddSingleton<IDesktopObservationSource, WindowsDesktopObservationSource>();
                services.AddSingleton(
                    new DesktopObservationStabilitySettings(TimeSpan.FromMilliseconds(500)));
                services.AddSingleton<IStableDesktopObservationSource, StableDesktopObservationSource>();
                services.AddSingleton<DesktopStateBaselineTracker>();
                services.AddSingleton<DesktopObservationDiagnosticsRunner>();
                services.AddSingleton(
                    new DesktopObservationHintBatchSettings(
                        TimeSpan.FromMilliseconds(700),
                        TimeSpan.FromSeconds(3)));
                services.AddSingleton(
                    new PeriodicObservationHintSettings(TimeSpan.FromSeconds(15)));
                services.AddSingleton<IPeriodicObservationHintSource>(sp =>
                    new PeriodicObservationHintSource(
                        sp.GetRequiredService<PeriodicObservationHintSettings>()));
                services.AddSingleton<
                    IM3ReadOnlyMonitoringHintSourceFactory,
                    DesktopFileSystemMonitoringHintSourceFactory>();
                services.AddSingleton<M3ReadOnlyMonitoringSession>();

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

                // M3-03 Suggestion 仍为只读：Candidate 只作为建议证据，不连接 Coordinator / Writer。
                services.AddSingleton<DesktopLayoutSuggestionLifecycle>();
                services.AddSingleton<IDesktopLayoutSuggestionBuilder, DesktopLayoutSuggestionBuilder>();
                services.AddSingleton<DesktopLayoutSuggestionPipeline>();
                services.AddSingleton<M3SuggestionMonitoringCoordinator>();

                // M3-04C：真实 Windows App Notification 作为平台 sink；
                // diagnostics wrapper 只在平台操作成功后镜像 surface，并保留 PoC stale-action 模拟能力。
                services.AddSingleton(
                    new DesktopSuggestionNotificationPolicySettings(TimeSpan.FromSeconds(30)));
                services.AddSingleton<DesktopSuggestionNotificationPolicy>();
                services.AddSingleton<WindowsAppNotificationSink>();
                services.AddSingleton(sp =>
                    new M3DiagnosticsNotificationSink(
                        sp.GetRequiredService<WindowsAppNotificationSink>(),
                        sp.GetRequiredService<TimeProvider>()));
                services.AddSingleton<IDesktopSuggestionNotificationSink>(sp =>
                    sp.GetRequiredService<M3DiagnosticsNotificationSink>());
                services.AddSingleton<DesktopSuggestionNotificationDispatcher>();
                services.AddSingleton<DesktopSuggestionNotificationScheduler>();
                services.AddSingleton<M3SuggestionNotificationCoordinator>();

                services.AddSingleton<M3DiagnosticsWindow>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _host.StartAsync().GetAwaiter().GetResult();

#if DEBUG
        // M2 crash-window acceptance 与 M3 notification 是不同验收场景。
        // 故障注入启用时不注册 Windows App Notification，避免 COM activation / callback 干扰。
        if (!acceptanceFaultOptions.IsEnabled)
        {
            StartWindowsAppNotifications(showFailureDialog: true);
        }
#else
        StartWindowsAppNotifications(showFailureDialog: false);
#endif

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

#if DEBUG
        // Acceptance fault injection 与 M3 diagnostics 使用不同的人工验收场景。
        // 故障注入启用时不自动弹出诊断窗，避免干扰 crash-window / SoftError 验收。
        if (!acceptanceFaultOptions.IsEnabled)
        {
            var diagnosticsWindow = _host.Services.GetRequiredService<M3DiagnosticsWindow>();
            diagnosticsWindow.Owner = mainWindow;
            diagnosticsWindow.Show();
        }
#endif
    }

    private void StartWindowsAppNotifications(bool showFailureDialog)
    {
        var sink = _host!.Services.GetRequiredService<WindowsAppNotificationSink>();
        var registration = sink.Start();
        if (registration.IsRegistered || !showFailureDialog)
            return;

        MessageBox.Show(
            "Windows App Notification 注册失败。DesktopAgent 会继续运行，Suggestion / Diff 不受影响；"
            + "通知层将保持 fail-closed，并在诊断窗口记录 SinkFailed。\n\n"
            + registration.Failure,
            "DesktopAgent - App Notification unavailable",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

#if DEBUG
    private void ScheduleSoftFaultUiReturn(string message)
    {
        // Writer 在专用 STA 线程上触发该回调。只排队，不在 Writer 线程直接碰 WPF。
        // ApplicationIdle 保证现有 Execute_Click 异常路径优先完成：先建立人工恢复锁，再返回主页。
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                Window? activeChild = null;
                foreach (Window window in Windows)
                {
                    if (!ReferenceEquals(window, MainWindow) && window.IsVisible)
                    {
                        activeChild = window;
                        if (window.IsActive)
                            break;
                    }
                }

                var text =
                    "已命中 SoftError 调试故障。\n\n" +
                    "DesktopAgent 没有退出；当前执行已经按异常路径 fail-closed。" +
                    "如果真实写入已经发生，返回主页后会保留人工恢复锁，禁止继续写入。\n\n" +
                    message;

                if (activeChild is not null)
                {
                    MessageBox.Show(
                        activeChild,
                        text,
                        "DesktopAgent - Soft Debug Fault",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    activeChild.Close();
                }
                else if (MainWindow is not null)
                {
                    MessageBox.Show(
                        MainWindow,
                        text,
                        "DesktopAgent - Soft Debug Fault",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(
                        text,
                        "DesktopAgent - Soft Debug Fault",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                if (MainWindow is not null)
                {
                    if (!MainWindow.IsVisible)
                        MainWindow.Show();
                    MainWindow.Activate();
                }
            }));
    }
#endif

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
