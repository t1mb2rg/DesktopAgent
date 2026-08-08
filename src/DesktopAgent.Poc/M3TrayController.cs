using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Poc;

/// <summary>
/// M3-04D tray presentation controller。
/// 只负责窗口可见性、只读 monitoring 启停和当前状态展示。
/// 不持有 Candidate / Writer / ExecutionCoordinator。
/// </summary>
public sealed class M3TrayController : IDisposable
{
    private const int TrayTextLimit = 63;

    private readonly Dispatcher _dispatcher;
    private readonly MainWindow _mainWindow;
    private readonly M3DiagnosticsWindow _diagnosticsWindow;
    private readonly M3SuggestionMonitoringCoordinator _suggestionCoordinator;
    private readonly M3SuggestionNotificationCoordinator _notificationCoordinator;

    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ContextMenuStrip? _menu;
    private System.Windows.Forms.ToolStripMenuItem? _monitoringStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _suggestionStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _notificationStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _toggleMonitoringItem;
    private DispatcherTimer? _statusTimer;

    private bool _started;
    private bool _exitRequested;
    private bool _monitoringOperation;
    private int _disposeStarted;

    public bool IsStarted => _started;

    public M3TrayController(
        MainWindow mainWindow,
        M3DiagnosticsWindow diagnosticsWindow,
        M3SuggestionMonitoringCoordinator suggestionCoordinator,
        M3SuggestionNotificationCoordinator notificationCoordinator)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _diagnosticsWindow = diagnosticsWindow ?? throw new ArgumentNullException(nameof(diagnosticsWindow));
        _suggestionCoordinator = suggestionCoordinator
            ?? throw new ArgumentNullException(nameof(suggestionCoordinator));
        _notificationCoordinator = notificationCoordinator
            ?? throw new ArgumentNullException(nameof(notificationCoordinator));
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
        _diagnosticsWindow.Closing += OnDiagnosticsWindowClosing;

        var openMainItem = new System.Windows.Forms.ToolStripMenuItem("打开 DesktopAgent");
        openMainItem.Click += (_, _) => RunOnDispatcher(OpenMainWindow);

        var openDiagnosticsItem = new System.Windows.Forms.ToolStripMenuItem("打开 M3 诊断");
        openDiagnosticsItem.Click += (_, _) => RunOnDispatcher(OpenDiagnosticsWindow);

        _monitoringStatusItem = new System.Windows.Forms.ToolStripMenuItem
        {
            Enabled = false,
        };
        _suggestionStatusItem = new System.Windows.Forms.ToolStripMenuItem
        {
            Enabled = false,
        };
        _notificationStatusItem = new System.Windows.Forms.ToolStripMenuItem
        {
            Enabled = false,
        };

        _toggleMonitoringItem = new System.Windows.Forms.ToolStripMenuItem();
        _toggleMonitoringItem.Click += OnToggleMonitoringClick;

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出 DesktopAgent");
        exitItem.Click += OnExitClick;

        _menu = new System.Windows.Forms.ContextMenuStrip();
        _menu.Items.Add(openMainItem);
        _menu.Items.Add(openDiagnosticsItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(_monitoringStatusItem);
        _menu.Items.Add(_suggestionStatusItem);
        _menu.Items.Add(_notificationStatusItem);
        _menu.Items.Add(_toggleMonitoringItem);
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
        _notifyIcon.DoubleClick += (_, _) => RunOnDispatcher(OpenMainWindow);

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
        // 如果它已经因为正在执行 / 人工恢复等原因阻止关闭，这里绝不覆盖。
        if (_exitRequested || e.Cancel)
            return;

        e.Cancel = true;
        _mainWindow.Hide();
        RefreshStatus();
    }

    private void OnDiagnosticsWindowClosing(object? sender, CancelEventArgs e)
    {
        _ = sender;
        if (_exitRequested || e.Cancel)
            return;

        // 只隐藏仪表盘，不触发 03D OnClosed，因此后台 monitoring 与 lifecycle 继续存在。
        e.Cancel = true;
        _diagnosticsWindow.Hide();
        RefreshStatus();
    }

    private async void OnToggleMonitoringClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_monitoringOperation || Volatile.Read(ref _disposeStarted) != 0)
            return;

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

    private async void OnExitClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_exitRequested || Volatile.Read(ref _disposeStarted) != 0)
            return;

        _exitRequested = true;
        RefreshStatus();

        try
        {
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
            // 若它取消关闭，说明仍有操作/人工恢复状态需要用户处理，不能强退。
            OpenMainWindow();
            _mainWindow.Close();
            if (_mainWindow.IsLoaded)
            {
                _exitRequested = false;
                RefreshStatus();
                return;
            }

            if (_notifyIcon is not null)
                _notifyIcon.Visible = false;

            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _exitRequested = false;
            RefreshStatus();
            ShowTrayWarning("退出 DesktopAgent 失败", ex.Message);
        }
    }

    private void OpenMainWindow()
    {
        if (!_mainWindow.IsVisible)
            _mainWindow.Show();

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
    }

    private void OpenDiagnosticsWindow()
    {
        // Diagnostics 是 MainWindow 的 owned window；先恢复 owner，避免 owner 隐藏时诊断窗也被压回去。
        OpenMainWindow();

        if (!_diagnosticsWindow.IsVisible)
            _diagnosticsWindow.Show();

        if (_diagnosticsWindow.WindowState == WindowState.Minimized)
            _diagnosticsWindow.WindowState = WindowState.Normal;

        _diagnosticsWindow.Activate();
    }

    private void RefreshStatus()
    {
        if (!_started || _notifyIcon is null)
            return;

        var monitoring = _suggestionCoordinator.IsMonitoring;
        var suggestion = _suggestionCoordinator.SuggestionSnapshot;
        var notification = _notificationCoordinator.SurfaceSnapshot.CurrentNotification;

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

        if (_toggleMonitoringItem is not null)
        {
            _toggleMonitoringItem.Text = monitoring
                ? "暂停后台监听"
                : "恢复后台监听";
            _toggleMonitoringItem.Enabled = !_monitoringOperation && !_exitRequested;
        }

        var suggestionLabel = suggestion.State switch
        {
            DesktopLayoutSuggestionPresentationState.Active => "Active",
            DesktopLayoutSuggestionPresentationState.Suspended => "Suspended",
            _ => "None",
        };
        var text = $"DesktopAgent | 监控:{(monitoring ? "开" : "停")} | 建议:{suggestionLabel}";
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

        _mainWindow.Closing -= OnMainWindowClosing;
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
