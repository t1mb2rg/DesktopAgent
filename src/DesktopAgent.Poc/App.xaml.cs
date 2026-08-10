using System.Windows;
using System.Windows.Threading;
using DesktopAgent.Application.DesktopAutomation;
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
using DesktopAgent.WindowsInterop.DesktopAutomation;
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
    private WindowsAppSdkRuntimeBootstrapper? _windowsAppSdkRuntime;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var enableM3DesktopSurfaces = true;
        var enableWindowsAppNotifications = true;
        var m3AutomaticAcceptance = M3AutomaticAcceptanceOptions.Disabled;

#if DEBUG
        AcceptanceFaultInjectionOptions acceptanceFaultOptions;
        try
        {
            acceptanceFaultOptions = AcceptanceFaultInjectionOptions.FromEnvironment();
            m3AutomaticAcceptance = M3AutomaticAcceptanceOptions.FromEnvironment();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Acceptance 配置无效，应用将 fail-closed 退出。\n\n" + ex.Message,
                "DesktopAgent - Acceptance 配置错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        if (acceptanceFaultOptions.IsEnabled && m3AutomaticAcceptance.IsEnabled)
        {
            MessageBox.Show(
                "M2 Writer fault injection 与 M3 unattended automatic acceptance 不能在同一进程同时启用。\n\n"
                + "请清除其中一组 DESKTOPAGENT_* acceptance 环境变量后重新启动。",
                "DesktopAgent - Acceptance 模式冲突",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        if (acceptanceFaultOptions.IsEnabled)
        {
            enableM3DesktopSurfaces = false;
            enableWindowsAppNotifications = false;

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

        if (m3AutomaticAcceptance.IsEnabled)
        {
            var safeFailureDescription = m3AutomaticAcceptance.InjectPreWriterFailure
                ? "\n\nB07-D SAFE FAILURE INJECTION ENABLED\n"
                    + "本次进程会在 Coordinator 已通过 fresh/scope gates 后、创建 safety snapshot 时故意失败。\n"
                    + "该位置早于 recovery checkpoint 与第一笔 Writer；用于验证 Failure → Cooldown → Fuse，桌面 Writer 不应被调用。"
                : string.Empty;

            var answer = MessageBox.Show(
                "M3 AUTOMATIC ACCEPTANCE ARMED\n\n"
                + "本次 Debug 进程允许 unattended REAL desktop writes。\n"
                + "这不是普通 DesktopAgent 运行模式。\n\n"
                + m3AutomaticAcceptance.Describe()
                + safeFailureDescription + "\n\n"
                + "启动后 RequestedMode 仍默认为 Suggestion。"
                + "只有你随后从 tray 明确选择 Automatic，自动执行链才可能进入真实 M2 Coordinator / Writer。\n\n"
                + "确认本次进程用于受控实机验收并继续？",
                "DesktopAgent - M3 UNATTENDED WRITE ACCEPTANCE",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                Shutdown(-1);
                return;
            }
        }
#endif

        WindowsAppSdkRuntimeInitializationResult runtimeInitialization =
            new(false, null, "Windows App Notification disabled for this process.");

        if (enableWindowsAppNotifications)
        {
            _windowsAppSdkRuntime = new WindowsAppSdkRuntimeBootstrapper();
            runtimeInitialization = _windowsAppSdkRuntime.Start();
        }

        var windowsAppSdkReady = runtimeInitialization.IsInitialized;

#if DEBUG
        if (enableWindowsAppNotifications && !windowsAppSdkReady)
        {
            MessageBox.Show(
                "Windows App SDK runtime / Singleton 初始化失败。DesktopAgent 会继续运行；"
                + "Suggestion / Diff 不受影响，notification 会降级到 diagnostics-only surface。\n\n"
                + runtimeInitialization.Failure,
                "DesktopAgent - Windows App SDK unavailable",
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
                    m3AutomaticAcceptance.InjectPreWriterFailure
                        ? new M3AcceptanceFailingSafetySnapshotFactory()
                        : new DefaultSafetySnapshotFactory(
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

                // M3-03 Suggestion：旧 Candidate 只作为建议证据；M3-B 永远通过 fresh rebuilder 重建。
                services.AddSingleton<DesktopLayoutSuggestionLifecycle>();
                services.AddSingleton<IDesktopLayoutSuggestionBuilder, DesktopLayoutSuggestionBuilder>();
                services.AddSingleton<DesktopLayoutSuggestionPipeline>();
                services.AddSingleton<M3SuggestionMonitoringCoordinator>();

                // 普通 PoC 使用 provisional-closed gate；只有 Debug-only M3 acceptance 配置
                // 通过环境变量武装 + 启动确认后，本次进程才使用 ValidatedOpen。
                services.AddSingleton(M3AutomationPoCDefaults.RuntimeSettings);
                services.AddSingleton(m3AutomaticAcceptance);
                services.AddSingleton(sp =>
                    m3AutomaticAcceptance.IsEnabled
                        ? m3AutomaticAcceptance.CreateValidatedRuntimePolicy(
                            sp.GetRequiredService<TimeProvider>())
                        : M3AutomationPoCDefaults.CreateRuntimePolicy(
                            sp.GetRequiredService<TimeProvider>()));
                services.AddSingleton(
                    m3AutomaticAcceptance.IsEnabled
                        ? m3AutomaticAcceptance.RiskSettings
                        : M3AutomationPoCDefaults.RiskSettings);
                services.AddSingleton<DesktopAutomationCandidateRiskPolicy>();
                services.AddSingleton<IDesktopAutomationCandidateRiskPolicy>(sp =>
                    sp.GetRequiredService<DesktopAutomationCandidateRiskPolicy>());
                services.AddSingleton<IDesktopAutomationCandidateRiskEvidencePolicy>(sp =>
                    sp.GetRequiredService<DesktopAutomationCandidateRiskPolicy>());
                services.AddSingleton<DesktopAutomationPreflightDecisionPolicy>();
                services.AddSingleton<
                    IDesktopLayoutFreshCandidateRebuilder,
                    DesktopLayoutFreshCandidateRebuilder>();
                services.AddSingleton<DesktopAutomationFreshExecutionHandoff>();
                services.AddSingleton<DesktopAutomationAutomaticSafetyGate>();
                services.AddSingleton<DesktopAutomationCoordinatorExecutionBridge>();
                services.AddSingleton<DesktopAutomationExecutionOutcomeCoordinator>();
                services.AddSingleton<DesktopAutomationExecutionCycleCoordinator>();
                services.AddSingleton<M3AutomaticExecutionTriggerCoordinator>();

                // M3-04C：runtime 初始化成功时使用真实 Windows App Notification sink；
                // 初始化失败则 diagnostics wrapper 退回纯内存 surface，主程序继续运行。
                services.AddSingleton(
                    new DesktopSuggestionNotificationPolicySettings(TimeSpan.FromSeconds(30)));
                services.AddSingleton<DesktopSuggestionNotificationPolicy>();
                services.AddSingleton<WindowsAppNotificationSink>();
                services.AddSingleton(sp =>
                    windowsAppSdkReady
                        ? new M3DiagnosticsNotificationSink(
                            sp.GetRequiredService<WindowsAppNotificationSink>(),
                            sp.GetRequiredService<TimeProvider>())
                        : new M3DiagnosticsNotificationSink(
                            sp.GetRequiredService<TimeProvider>()));
                services.AddSingleton<IDesktopSuggestionNotificationSink>(sp =>
                    sp.GetRequiredService<M3DiagnosticsNotificationSink>());
                services.AddSingleton<DesktopSuggestionNotificationDispatcher>();
                services.AddSingleton<DesktopSuggestionNotificationScheduler>();
                services.AddSingleton<M3SuggestionNotificationCoordinator>();

                services.AddSingleton<M3DiagnosticsWindow>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<M3TrayController>();
            })
            .Build();

        _host.StartAsync().GetAwaiter().GetResult();

        if (windowsAppSdkReady)
        {
#if DEBUG
            StartWindowsAppNotifications(showFailureDialog: true);
#else
            StartWindowsAppNotifications(showFailureDialog: false);
#endif
        }

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (enableM3DesktopSurfaces)
        {
#if DEBUG
            var diagnosticsWindow = _host.Services.GetRequiredService<M3DiagnosticsWindow>();
            diagnosticsWindow.Owner = mainWindow;
            diagnosticsWindow.Show();
#endif

            // Tray is the persistent lifetime surface. Window X actions become Hide only after
            // each window's existing safety Closing handlers have allowed the close request.
            _host.Services.GetRequiredService<M3TrayController>().Start();
        }
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

        _windowsAppSdkRuntime?.Dispose();
        _windowsAppSdkRuntime = null;

        base.OnExit(e);
    }
}
