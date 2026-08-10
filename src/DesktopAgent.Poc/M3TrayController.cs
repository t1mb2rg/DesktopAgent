using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.WindowsInterop.DesktopAutomation;

namespace DesktopAgent.Poc;

/// <summary>
/// M3 tray presentation controller。
/// 负责窗口可见性、monitoring、automatic mode/status 与显式退出。
/// 不直接持有 Candidate / Writer / ExecutionCoordinator。
/// </summary>
public sealed class M3TrayController : IDisposable
{
    private const int TrayTextLimit = 63;

    private readonly Dispatcher _dispatcher;
    private readonly MainWindow _mainWindow;
    private readonly M3DiagnosticsWindow _diagnosticsWindow;
    private readonly M3SuggestionMonitoringCoordinator _suggestionCoordinator;
    private readonly M3SuggestionNotificationCoordinator _notificationCoordinator;
    private readonly DesktopAutomationRuntimePolicy _automationRuntime;
    private readonly M3AutomaticExecutionTriggerCoordinator _automaticTrigger;
    private readonly M3TrayWindowLifecycleState _windowLifecycle = new();

    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ContextMenuStrip? _menu;
    private System.Windows.Forms.ToolStripMenuItem? _monitoringStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _suggestionStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _notificationStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _automationStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _toggleMonitoringItem;
    private System.Windows.Forms.ToolStripMenuItem? _manualModeItem;
    private System.Windows.Forms.ToolStripMenuItem? _suggestionModeItem;
    private System.Windows.Forms.ToolStripMenuItem? _automaticModeItem;
    private System.Windows.Forms.ToolStripMenuItem? _toggleAutomationPauseItem;
    private System.Windows.Forms.ToolStripMenuItem? _resetFuseItem;
    private DispatcherTimer? _statusTimer;

    private bool _started;
    private bool _monitoringOperation;
    private int _disposeStarted;

    public bool IsStarted => _started;

    public M3TrayController(
        MainWindow mainWindow,
        M3DiagnosticsWindow diagnosticsWindow,
        M3SuggestionMonitoringCoordinator suggestionCoordinator,
        M3SuggestionNotificationCoordinator notificationCoordinator,
        DesktopAutomationRuntimePolicy automationRuntime,
        M3AutomaticExecutionTriggerCoordinator automaticTrigger)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _diagnosticsWindow = diagnosticsWindow ?? throw new ArgumentNullException(nameof(diagnosticsWindow));
        _suggestionCoordinator = suggestionCoordinator
            ?? throw new ArgumentNullException(nameof(suggestionCoordinator));
        _notificationCoordinator = notificationCoordinator
            ?? throw new ArgumentNullException(nameof(notificationCoordinator));
        _automationRuntime = automationRuntime ?? throw new ArgumentNullException(nameof(automationRuntime));
        _automaticTrigger = automaticTrigger ?? throw new ArgumentNullException(nameof(automaticTrigger));
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "M3TrayController.Start 必须在 WPF UI Dispatcher 线程调用。");
        }

        if (_started)
            return;

        if (_diagnosticsWindow.Owner is null)
            _diagnosticsWindow.Owner = _mainWindow;

        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.Closed += OnMainWindowClosed;
        _diagnosticsWindow.Closing += OnDiagnosticsWindowClosing;

        var openMainItem = new System.Windows.Forms.ToolStripMenuItem("打开 DesktopAgent");
        openMainItem.Click += (_, _) => RunOnDispatcher(TryOpenMainWindowFromTray);

        var openDiagnosticsItem = new System.Windows.Forms.ToolStripMenuItem("打开 M3 诊断");
        openDiagnosticsItem.Click += (_, _) => RunOnDispatcher(OpenDiagnosticsWindow);

        _monitoringStatusItem = new System.Windows.Forms.ToolStripMenuItem { Enabled = false };
        _suggestionStatusItem = new System.Windows.Forms.ToolStripMenuItem { Enabled = false };
        _notificationStatusItem = new System.Windows.Forms.ToolStripMenuItem { Enabled = false };
        _automationStatusItem = new System.Windows.Forms.ToolStripMenuItem { Enabled = false };

        _toggleMonitoringItem = new System.Windows.Forms.ToolStripMenuItem();
        _toggleMonitoringItem.Click += OnToggleMonitoringClick;

        var automationModeMenu = new System.Windows.Forms.ToolStripMenuItem("自动整理模式");
        _manualModeItem = new System.Windows.Forms.ToolStripMenuItem("Manual 手动");
        _suggestionModeItem = new System.Windows.Forms.ToolStripMenuItem("Suggestion 建议");
        _automaticModeItem = new System.Windows.Forms.ToolStripMenuItem("Automatic 自动");
        _manualModeItem.Click += (_, _) => SetAutomationMode(DesktopAutomationMode.Manual);
        _suggestionModeItem.Click += (_, _) => SetAutomationMode(DesktopAutomationMode.Suggestion);
        _automaticModeItem.Click += (_, _) => SetAutomationMode(DesktopAutomationMode.Automatic);
        automationModeMenu.DropDownItems.Add(_manualModeItem);
        automationModeMenu.DropDownItems.Add(_suggestionModeItem);
        automationModeMenu.DropDownItems.Add(_automaticModeItem);

        _toggleAutomationPauseItem = new System.Windows.Forms.ToolStripMenuItem();
        _toggleAutomationPauseItem.Click += OnToggleAutomationPauseClick;

        _resetFuseItem = new System.Windows.Forms.ToolStripMenuItem("重置自动整理熔断");
        _resetFuseItem.Click += OnResetFuseClick;

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出 DesktopAgent");
        exitItem.Click += OnExitClick;

        _menu = new System.Windows.Forms.ContextMenuStrip();
        _menu.Items.Add(openMainItem);
        _menu.Items.Add(openDiagnosticsItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(_monitoringStatusItem);
        _menu.Items.Add(_suggestionStatusItem);
        _menu.Items.Add(_notificationStatusItem);
        _menu.Items.Add(_automationStatusItem);
        _menu.Items.Add(_toggleMonitoringItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(automationModeMenu);
        _menu.Items.Add(_toggleAutomationPauseItem);
        _menu.Items.Add(_resetFuseItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += (_, _) => RunOnDispatcher(RefreshStatus);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "DesktopAgent",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => RunOnDispatcher(TryOpenMainWindowFromTray);

        _automaticTrigger.CycleCompleted += OnAutomaticCycleCompleted;
        _automaticTrigger.TriggerFaulted += OnAutomaticTriggerFaulted;
        _automaticTrigger.Start();

        _statusTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => RefreshStatus(),
            _dispatcher);
        _statusTimer.Start();

        _started = true;
        RefreshStatus();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        if (!_dispatcher.CheckAccess())
        {
            try
            {
                _dispatcher.Invoke(DisposeOnDispatcher);
            }
            catch
            {
            }
            return;
        }

        DisposeOnDispatcher();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        _ = sender;

        // MainWindow 自己的 M2 安全 OnClosing 先执行。
        if (_windowLifecycle.IsExitRequested || e.Cancel)
            return;

        e.Cancel = true;
        _mainWindow.Hide();
        RefreshStatus();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;

        // WPF Window.Close 是不可逆边界：Closed 后同一实例永远不能再 Show。
        _windowLifecycle.MarkMainWindowClosed();

        if (_notifyIcon is not null)
            _notifyIcon.Visible = false;

        // 正常 tray Exit 会在 OnExitClick 中继续 Shutdown；若出现任何其他真实 Close，
        // 也把它视为终止事件，避免留下持有已关闭 MainWindow 的 zombie tray。
        if (!_windowLifecycle.IsExitRequested)
            System.Windows.Application.Current?.Shutdown();
    }

    private void OnDiagnosticsWindowClosing(object? sender, CancelEventArgs e)
    {
        _ = sender;
        if (_windowLifecycle.IsExitRequested || e.Cancel)
            return;

        // 只隐藏仪表盘，不停止 monitoring / automation trigger。
        e.Cancel = true;
        _diagnosticsWindow.Hide();
        RefreshStatus();
    }

    private async void OnToggleMonitoringClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_monitoringOperation
            || _windowLifecycle.IsExitRequested
            || _windowLifecycle.IsMainWindowClosed
            || Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        _monitoringOperation = true;
        RefreshStatus();
        try
        {
            if (_suggestionCoordinator.IsMonitoring)
                await _suggestionCoordinator.StopMonitoringAsync();
            else
                await _suggestionCoordinator.StartMonitoringAsync();
        }
        catch (Exception ex)
        {
            ShowTrayWarning("后台监听切换失败", ex.Message);
        }
        finally
        {
            _monitoringOperation = false;
            RefreshStatus();
        }
    }

    private void SetAutomationMode(DesktopAutomationMode mode)
    {
        RunOnDispatcher(() =>
        {
            if (_windowLifecycle.IsExitRequested || _windowLifecycle.IsMainWindowClosed)
                return;

            try
            {
                _automationRuntime.SetRequestedMode(mode);
                if (mode == DesktopAutomationMode.Automatic)
                    _automaticTrigger.RequestCurrent();
                RefreshStatus();
            }
            catch (Exception ex)
            {
                ShowTrayWarning("自动整理模式切换失败", ex.Message);
            }
        });
    }

    private void OnToggleAutomationPauseClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RunOnDispatcher(() =>
        {
            if (_windowLifecycle.IsExitRequested || _windowLifecycle.IsMainWindowClosed)
                return;

            try
            {
                if (_automationRuntime.Snapshot.IsPaused)
                {
                    var resumed = _automationRuntime.Resume();
                    if (resumed.RequestedMode == DesktopAutomationMode.Automatic)
                        _automaticTrigger.RequestCurrent();
                }
                else
                {
                    _automationRuntime.Pause("用户从 tray 暂停 automatic execution。");
                }
                RefreshStatus();
            }
            catch (Exception ex)
            {
                ShowTrayWarning("自动整理暂停/恢复失败", ex.Message);
            }
        });
    }

    private void OnResetFuseClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RunOnDispatcher(() =>
        {
            if (_windowLifecycle.IsExitRequested || _windowLifecycle.IsMainWindowClosed)
                return;

            if (!_automationRuntime.Snapshot.IsFuseTripped)
                return;

            var answer = MessageBox.Show(
                _mainWindow,
                "这会清除自动整理连续失败熔断。\n如果失败原因尚未处理，Automatic 仍可能再次失败。\n\n继续重置？",
                "重置自动整理熔断",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                var reset = _automationRuntime.ResetFuse();
                if (reset.RequestedMode == DesktopAutomationMode.Automatic)
                    _automaticTrigger.RequestCurrent();
                RefreshStatus();
            }
            catch (Exception ex)
            {
                ShowTrayWarning("重置自动整理熔断失败", ex.Message);
            }
        });
    }

    private async void OnExitClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (Volatile.Read(ref _disposeStarted) != 0 || !_windowLifecycle.TryBeginExit())
            return;

        RefreshStatus();

        try
        {
            // 先停止接收新的 automatic trigger，并等待正在运行的 cycle/M2 transaction 收口。
            await _automaticTrigger.StopAsync();

            if (_suggestionCoordinator.IsMonitoring)
            {
                try
                {
                    await _suggestionCoordinator.StopMonitoringAsync();
                }
                catch (Exception ex)
                {
                    ShowTrayWarning("停止后台监听时发生异常", ex.Message);
                }
            }

            // 让 MainWindow 现有 M2 Closing 安全检查真正运行。
            // ExitRequested 阶段禁止普通 tray Open，但 exit 自己仍可把尚未 Closed 的窗口拉回前台。
            if (!EnsureMainWindowVisibleForExit())
            {
                CompleteTerminalShutdown();
                return;
            }

            _mainWindow.Close();

            // Closed 事件是唯一可靠的不可逆提交点。若没触发，说明 M2 Closing gate 取消了关闭，
            // 此时才允许回滚 exit request 并恢复 tray 运行。
            if (!_windowLifecycle.IsMainWindowClosed)
            {
                _windowLifecycle.CancelExit();
                _automaticTrigger.Start();
                RefreshStatus();
                return;
            }

            CompleteTerminalShutdown();
        }
        catch (Exception ex)
        {
            // 一旦 MainWindow 已 Closed，就绝不能把 exit 状态回滚成可从 tray 重新 Show。
            if (_windowLifecycle.IsMainWindowClosed)
            {
                CompleteTerminalShutdown();
                return;
            }

            _windowLifecycle.CancelExit();
            try
            {
                _automaticTrigger.Start();
            }
            catch
            {
            }
            RefreshStatus();
            ShowTrayWarning("退出 DesktopAgent 失败", ex.Message);
        }
    }

    private void OnAutomaticCycleCompleted(DesktopAutomationExecutionCycleResult result)
    {
        RunOnDispatcher(() =>
        {
            RefreshStatus();

            if (result.Status == DesktopAutomationExecutionCycleStatus.FaultedAndPaused)
            {
                ShowTrayWarning(
                    "自动整理已安全暂停",
                    result.Fault ?? "Automatic cycle 发生未知异常。请查看诊断。");
                return;
            }

            if (result.Outcome?.Status == DesktopAutomationExecutionOutcomeFeedbackStatus.FailureRecorded)
            {
                var coordinatorStatus = result.Outcome.BridgeResult.CoordinatorRun?.Status.ToString()
                    ?? "Unknown";
                ShowTrayWarning(
                    "自动整理本次未完成",
                    $"Coordinator={coordinatorStatus}。已进入 cooldown；连续失败可能触发 fuse。");
            }
        });
    }

    private void OnAutomaticTriggerFaulted(string fault) =>
        RunOnDispatcher(() => ShowTrayWarning("Automatic trigger 异常", fault));

    private void TryOpenMainWindowFromTray()
    {
        if (!_windowLifecycle.CanOpenFromTray)
            return;

        ShowAndActivateMainWindow();
    }

    private bool EnsureMainWindowVisibleForExit()
    {
        if (!_windowLifecycle.CanShowMainWindowForExit)
            return false;

        ShowAndActivateMainWindow();
        return true;
    }

    private void ShowAndActivateMainWindow()
    {
        if (!_mainWindow.IsVisible)
            _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
    }

    private void OpenDiagnosticsWindow()
    {
        if (!_windowLifecycle.CanOpenFromTray)
            return;

        ShowAndActivateMainWindow();

        if (!_diagnosticsWindow.IsVisible)
            _diagnosticsWindow.Show();

        if (_diagnosticsWindow.WindowState == WindowState.Minimized)
            _diagnosticsWindow.WindowState = WindowState.Normal;

        _diagnosticsWindow.Activate();
    }

    private void CompleteTerminalShutdown()
    {
        if (_notifyIcon is not null)
            _notifyIcon.Visible = false;

        System.Windows.Application.Current?.Shutdown();
    }

    private void RefreshStatus()
    {
        if (!_started || _notifyIcon is null)
            return;

        var monitoring = _suggestionCoordinator.IsMonitoring;
        var suggestion = _suggestionCoordinator.SuggestionSnapshot;
        var notification = _notificationCoordinator.SurfaceSnapshot.CurrentNotification;
        var automation = _automationRuntime.Snapshot;

        if (_monitoringStatusItem is not null)
        {
            _monitoringStatusItem.Text = monitoring
                ? "监控：运行中"
                : "监控：已暂停";
        }

        if (_suggestionStatusItem is not null)
        {
            _suggestionStatusItem.Text = suggestion.State switch
            {
                DesktopLayoutSuggestionPresentationState.Active => "建议：Active",
                DesktopLayoutSuggestionPresentationState.Suspended =>
                    $"建议：Suspended ({suggestion.SuspensionReason})",
                _ => "建议：无",
            };
        }

        if (_notificationStatusItem is not null)
        {
            _notificationStatusItem.Text = notification is null
                ? "通知：无当前通知"
                : "通知：已有当前 Suggestion 通知";
        }

        if (_automationStatusItem is not null)
        {
            var blocker = automation.IsFuseTripped
                ? " | Fuse"
                : automation.IsPaused
                    ? " | Paused"
                    : automation.IsCooldownActive
                        ? " | Cooldown"
                        : !automation.UnattendedGate.IsOpen
                            ? $" | Gate:{automation.UnattendedGate.Reason}"
                            : string.Empty;
            _automationStatusItem.Text =
                $"自动化：请求 {automation.RequestedMode} / 实际 {automation.EffectiveMode}{blocker}";
        }

        if (_toggleMonitoringItem is not null)
        {
            _toggleMonitoringItem.Text = monitoring
                ? "暂停后台监听"
                : "恢复后台监听";
            _toggleMonitoringItem.Enabled = !_monitoringOperation
                && !_windowLifecycle.IsExitRequested
                && !_windowLifecycle.IsMainWindowClosed;
        }

        if (_manualModeItem is not null)
            _manualModeItem.Checked = automation.RequestedMode == DesktopAutomationMode.Manual;
        if (_suggestionModeItem is not null)
            _suggestionModeItem.Checked = automation.RequestedMode == DesktopAutomationMode.Suggestion;
        if (_automaticModeItem is not null)
            _automaticModeItem.Checked = automation.RequestedMode == DesktopAutomationMode.Automatic;

        if (_toggleAutomationPauseItem is not null)
        {
            _toggleAutomationPauseItem.Text = automation.IsPaused
                ? "恢复自动整理"
                : "暂停自动整理";
            _toggleAutomationPauseItem.Enabled = !_windowLifecycle.IsExitRequested
                && !_windowLifecycle.IsMainWindowClosed;
        }

        if (_resetFuseItem is not null)
        {
            _resetFuseItem.Enabled = automation.IsFuseTripped
                && !_windowLifecycle.IsExitRequested
                && !_windowLifecycle.IsMainWindowClosed;
        }

        var suggestionLabel = suggestion.State switch
        {
            DesktopLayoutSuggestionPresentationState.Active => "Active",
            DesktopLayoutSuggestionPresentationState.Suspended => "Suspended",
            _ => "None",
        };
        var text =
            $"DesktopAgent | 监控:{(monitoring ? "开" : "停")} | 自动:{automation.EffectiveMode} | 建议:{suggestionLabel}";
        _notifyIcon.Text = text.Length <= TrayTextLimit
            ? text
            : text[..TrayTextLimit];
    }

    private void ShowTrayWarning(string title, string message)
    {
        if (_notifyIcon is null)
            return;

        try
        {
            _notifyIcon.ShowBalloonTip(
                4000,
                title,
                message,
                System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch
        {
        }
    }

    private void RunOnDispatcher(Action action)
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            return;

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }

    private void DisposeOnDispatcher()
    {
        _statusTimer?.Stop();
        _statusTimer = null;

        _automaticTrigger.CycleCompleted -= OnAutomaticCycleCompleted;
        _automaticTrigger.TriggerFaulted -= OnAutomaticTriggerFaulted;
        try
        {
            _automaticTrigger.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _mainWindow.Closing -= OnMainWindowClosing;
        _mainWindow.Closed -= OnMainWindowClosed;
        _diagnosticsWindow.Closing -= OnDiagnosticsWindowClosing;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _menu?.Dispose();
        _menu = null;
        _started = false;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);
}
