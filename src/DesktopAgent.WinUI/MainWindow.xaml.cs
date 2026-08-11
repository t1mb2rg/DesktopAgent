using System.Collections.ObjectModel;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.Automation;
using DesktopAgent.Core.Productization;
using DesktopAgent.Persistence.Automation;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.Intelligence;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class MainWindow : Window
{
    private const int PendingDisplayLimit = 100;
    private const string DefaultAiSecretReference = "openai-compatible-api-key";

    private readonly AppWindow _appWindow;
    private readonly TrayIconHost _trayIcon;
    private readonly SqliteDesktopPendingActionStore _pendingStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly JsonDesktopProductSettingsStore _settingsStore =
        new(DesktopAgentDataPaths.DefaultSettingsPath);
    private readonly DpapiIntelligenceSecretStore _secretStore =
        new(DesktopAgentDataPaths.DefaultSecretsDirectory);
    private readonly ObservableCollection<PendingActionListItem> _pendingItems = new();

    private DesktopProductSettings _settings = DesktopProductSettings.CreateDefault();
    private bool _pendingStoreInitialized;
    private bool _settingsLoaded;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DesktopAgent";

        PendingList.ItemsSource = _pendingItems;
        LayoutsView.Children.Clear();
        LayoutsView.Children.Add(new LayoutManagementView());
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

    private async void ShellNavigation_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settingsLoaded)
            return;

        _settingsLoaded = true;
        await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        SettingsStatusBar.IsOpen = false;

        try
        {
            await _settingsStore.InitializeAsync();
            _settings = await _settingsStore.GetAsync();
            await ApplySettingsToUiAsync();

            if (!_settings.FirstRunCompleted)
            {
                ShellNavigation.SelectedItem = ShellNavigation.FooterMenuItems[0];
                ShowSection("settings");
            }
        }
        catch (Exception ex)
        {
            ShellNavigation.SelectedItem = ShellNavigation.FooterMenuItems[0];
            ShowSection("settings");
            SettingsStatusBar.Severity = InfoBarSeverity.Error;
            SettingsStatusBar.Title = "无法读取产品设置";
            SettingsStatusBar.Message = ex.Message;
            SettingsStatusBar.IsOpen = true;
        }
    }

    private async Task ApplySettingsToUiAsync()
    {
        AutomationModeCombo.SelectedIndex = (int)_settings.AutomationMode;
        AiEnabledToggle.IsOn = _settings.AiEnabled;
        AiEndpointTextBox.Text = _settings.AiBaseUri?.AbsoluteUri ?? string.Empty;
        AiModelTextBox.Text = _settings.AiModel ?? string.Empty;
        AiApiKeyPasswordBox.Password = string.Empty;
        HomeAutomationModeText.Text = _settings.AutomationMode.ToString();

        FirstRunInfoBar.IsOpen = !_settings.FirstRunCompleted;
        CompleteFirstRunButton.Visibility = _settings.FirstRunCompleted
            ? Visibility.Collapsed
            : Visibility.Visible;

        var hasSecret = _settings.AiSecretReference is not null &&
            await _secretStore.ContainsSecretAsync(_settings.AiSecretReference);
        AiSecretStatusText.Text = hasSecret
            ? "API Key 已由 Windows DPAPI 安全保存。PasswordBox 不会回显现有 Key。"
            : "当前没有已保存的 API Key。本地无认证 Provider 可以保持此状态。";
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
        AiView.Visibility = string.Equals(tag, "ai", StringComparison.Ordinal)
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

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        => await SaveSettingsAsync(markFirstRunCompleted: false);

    private async void CompleteFirstRun_Click(object sender, RoutedEventArgs e)
        => await SaveSettingsAsync(markFirstRunCompleted: true);

    private async Task SaveSettingsAsync(bool markFirstRunCompleted)
    {
        SettingsStatusBar.IsOpen = false;

        try
        {
            // LayoutManagementView 也可以更新 ActiveLayoutTemplateId。
            // 保存设置前重新读取，避免用 MainWindow 的旧快照把跨页偏好覆盖掉。
            _settings = await _settingsStore.GetAsync();

            if (AutomationModeCombo.SelectedIndex is < 0 or > 2)
                throw new InvalidOperationException("请选择有效的 Automation Mode。");

            var mode = (DesktopAutomationMode)AutomationModeCombo.SelectedIndex;
            var endpointText = AiEndpointTextBox.Text.Trim();
            var modelText = AiModelTextBox.Text.Trim();

            Uri? baseUri = null;
            if (endpointText.Length > 0)
            {
                if (!Uri.TryCreate(endpointText, UriKind.Absolute, out baseUri))
                    throw new ArgumentException("AI Base URI 不是有效的绝对 URI。");
            }

            var model = modelText.Length == 0 ? null : modelText;
            var newSecret = AiApiKeyPasswordBox.Password;
            var hasNewSecret = newSecret.Length > 0;

            if (hasNewSecret && baseUri is null)
                throw new ArgumentException("填写 API Key 前请先配置 AI Base URI 与 Model。");

            var secretReference = hasNewSecret
                ? DefaultAiSecretReference
                : _settings.AiSecretReference;

            if (baseUri is null)
                secretReference = null;

            // 先形成并校验完整候选配置，再写 Secret，避免“设置失败但新 Key 已落盘”。
            var candidate = new DesktopProductSettings(
                mode,
                _settings.FirstRunCompleted || markFirstRunCompleted,
                AiEnabledToggle.IsOn,
                baseUri,
                model,
                secretReference,
                _settings.ActiveLayoutTemplateId);

            if (candidate.AiBaseUri is not null)
            {
                _ = new OpenAiCompatibleProviderOptions(
                    candidate.AiBaseUri,
                    candidate.AiModel!,
                    candidate.AiSecretReference);
            }

            if (hasNewSecret)
                await _secretStore.SaveSecretAsync(DefaultAiSecretReference, newSecret);

            await _settingsStore.SaveAsync(candidate);
            _settings = candidate;
            await ApplySettingsToUiAsync();

            SettingsStatusBar.Severity = InfoBarSeverity.Success;
            SettingsStatusBar.Title = markFirstRunCompleted ? "首次设置已完成" : "设置已保存";
            SettingsStatusBar.Message = "保存设置不会发起 AI 请求，也不会触发桌面移动。";
            SettingsStatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            AiApiKeyPasswordBox.Password = string.Empty;
            SettingsStatusBar.Severity = InfoBarSeverity.Error;
            SettingsStatusBar.Title = "设置未保存";
            SettingsStatusBar.Message = ex.Message;
            SettingsStatusBar.IsOpen = true;
        }
    }

    private async void RemoveApiKey_Click(object sender, RoutedEventArgs e)
    {
        SettingsStatusBar.IsOpen = false;

        try
        {
            _settings = await _settingsStore.GetAsync();
            var reference = _settings.AiSecretReference ?? DefaultAiSecretReference;
            var removed = await _secretStore.DeleteSecretAsync(reference);

            if (_settings.AiSecretReference is not null)
            {
                _settings = new DesktopProductSettings(
                    _settings.AutomationMode,
                    _settings.FirstRunCompleted,
                    _settings.AiEnabled,
                    _settings.AiBaseUri,
                    _settings.AiModel,
                    aiSecretReference: null,
                    activeLayoutTemplateId: _settings.ActiveLayoutTemplateId);
                await _settingsStore.SaveAsync(_settings);
            }

            AiApiKeyPasswordBox.Password = string.Empty;
            await ApplySettingsToUiAsync();

            SettingsStatusBar.Severity = InfoBarSeverity.Success;
            SettingsStatusBar.Title = removed ? "API Key 已删除" : "没有已保存的 API Key";
            SettingsStatusBar.Message = "settings.json 只保留非秘密 Provider 配置。";
            SettingsStatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            SettingsStatusBar.Severity = InfoBarSeverity.Error;
            SettingsStatusBar.Title = "无法删除 API Key";
            SettingsStatusBar.Message = ex.Message;
            SettingsStatusBar.IsOpen = true;
        }
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
