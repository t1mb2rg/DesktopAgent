using System.Collections.ObjectModel;
using DesktopAgent.Persistence.Automation;
using DesktopAgent.Persistence.DesktopLayouts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class MainWindow : Window
{
    private const int PendingDisplayLimit = 100;

    private readonly AppWindow _appWindow;
    private readonly TrayIconHost _trayIcon;
    private readonly SqliteDesktopPendingActionStore _pendingStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly ObservableCollection<PendingActionListItem> _pendingItems = new();

    private bool _pendingStoreInitialized;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DesktopAgent";

        PendingList.ItemsSource = _pendingItems;
        ShellNavigation.SelectedItem = ShellNavigation.MenuItems[0];

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Closing += OnAppWindowClosing;

        _trayIcon = new TrayIconHost(hwnd, ShowFromBackground);
    }

    internal void ShowFromBackground()
    {
        if (_exitRequested)
            return;

        _appWindow.Show(activateWindow: true);
        Activate();
    }

    private async void NavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString() ?? "home";
        ShowSection(tag);

        if (string.Equals(tag, "pending", StringComparison.Ordinal))
            await RefreshPendingAsync();
    }

    private void ShowSection(string tag)
    {
        HomeView.Visibility = string.Equals(tag, "home", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        PendingView.Visibility = string.Equals(tag, "pending", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LayoutsView.Visibility = string.Equals(tag, "layouts", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsView.Visibility = string.Equals(tag, "settings", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void RefreshPending_Click(object sender, RoutedEventArgs e)
        => await RefreshPendingAsync();

    private async Task RefreshPendingAsync()
    {
        PendingStatusBar.IsOpen = false;
        PendingSummaryText.Text = "正在读取 Pending Queue…";

        try
        {
            if (!_pendingStoreInitialized)
            {
                await _pendingStore.InitializeAsync();
                _pendingStoreInitialized = true;
            }

            var actions = await _pendingStore.GetPendingAsync(PendingDisplayLimit);

            _pendingItems.Clear();
            foreach (var action in actions)
                _pendingItems.Add(PendingActionListItem.From(action));

            var hasItems = _pendingItems.Count > 0;
            PendingList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            PendingEmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            PendingSummaryText.Text = hasItems
                ? $"当前显示 {_pendingItems.Count} 个待确认项目，按进入队列时间从早到晚排列。"
                : "Pending Queue 目前为空。";

            if (_pendingItems.Count == PendingDisplayLimit)
            {
                PendingStatusBar.Severity = InfoBarSeverity.Informational;
                PendingStatusBar.Title = "仅显示前 100 项";
                PendingStatusBar.Message = "Pending Store 保持有界读取；后续产品页会增加分页与批量处理。";
                PendingStatusBar.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            _pendingItems.Clear();
            PendingList.Visibility = Visibility.Collapsed;
            PendingEmptyState.Visibility = Visibility.Collapsed;
            PendingSummaryText.Text = "Pending Queue 读取失败。";
            PendingStatusBar.Severity = InfoBarSeverity.Error;
            PendingStatusBar.Title = "无法读取待确认队列";
            PendingStatusBar.Message = ex.Message;
            PendingStatusBar.IsOpen = true;
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
        => _appWindow.Hide();

    private void Exit_Click(object sender, RoutedEventArgs e)
        => ExitApplication();

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
            return;

        args.Cancel = true;
        _appWindow.Hide();
    }

    private void ExitApplication()
    {
        if (_exitRequested)
            return;

        _exitRequested = true;
        _trayIcon.Dispose();
        _appWindow.Closing -= OnAppWindowClosing;
        Close();
    }
}
