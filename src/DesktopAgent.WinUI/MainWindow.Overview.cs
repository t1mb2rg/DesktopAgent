using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DesktopAgent.WinUI;

public sealed partial class MainWindow
{
    private readonly OverviewLightweightReadModelLoader _overviewReadModelLoader = new();
    private readonly DesktopRadarOverviewService _desktopRadarOverviewService = new();
    private DesktopRadarOverviewState? _desktopRadarState;
    private long _homeVisibilityCallbackToken;
    private bool _overviewRefreshInFlight;
    private bool _radarPresentationLoadInFlight;
    private bool _radarRefreshInFlight;

    private async void HomeView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_homeVisibilityCallbackToken == 0)
        {
            _homeVisibilityCallbackToken = HomeView.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty,
                OnHomeVisibilityChanged);
        }

        await RefreshOverviewLightweightAsync();
        await ReloadRadarPresentationAsync();
    }

    private void OnHomeVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (HomeView.Visibility == Visibility.Visible)
            _ = RefreshHomeForVisibleAsync();
    }

    private async Task RefreshHomeForVisibleAsync()
    {
        await RefreshOverviewLightweightAsync();
        await ReloadRadarPresentationAsync();
    }

    private async Task RefreshOverviewLightweightAsync()
    {
        if (_overviewRefreshInFlight || _exitRequested)
            return;

        _overviewRefreshInFlight = true;
        try
        {
            var model = await _overviewReadModelLoader.LoadAsync();
            HomeAutomationModeText.Text = model.AutomationMode.ToString();
            HomeCurrentLayoutText.Text = model.ActiveLayoutTitle;
            HomeCurrentLayoutDetailText.Text = model.ActiveLayoutDetail;
            HomePendingCountText.Text = model.PendingTitle;
            HomePendingDetailText.Text = model.PendingDetail;
        }
        catch
        {
            HomeCurrentLayoutText.Text = "暂时不可用";
            HomeCurrentLayoutDetailText.Text = "概览没有读取到当前布局摘要。";
            HomePendingCountText.Text = "—";
            HomePendingDetailText.Text = "概览没有读取到待确认数量。";
        }
        finally
        {
            _overviewRefreshInFlight = false;
        }
    }

    private async Task ReloadRadarPresentationAsync()
    {
        if (_radarPresentationLoadInFlight || _exitRequested)
            return;

        _radarPresentationLoadInFlight = true;
        try
        {
            _desktopRadarState = await _desktopRadarOverviewService.LoadCachedAsync();
            ApplyRadarState(_desktopRadarState);

            if (_desktopRadarOverviewService.ShouldRefresh(_desktopRadarState.Snapshot))
                _ = RefreshRadarAsync();
        }
        catch (Exception ex)
        {
            HomeRadarFreshnessText.Text = "需要刷新";
            HomeRadarStatusText.Text = $"桌面地图暂时不可用：{ex.Message}";
        }
        finally
        {
            _radarPresentationLoadInFlight = false;
        }
    }

    private async void RefreshRadar_Click(object sender, RoutedEventArgs e)
        => await RefreshRadarAsync();

    private async Task RefreshRadarAsync()
    {
        if (_radarRefreshInFlight || _exitRequested)
            return;

        _radarRefreshInFlight = true;
        HomeRadarRefreshButton.IsEnabled = false;
        HomeRadarStatusText.Text = "正在读取当前桌面…";
        try
        {
            _desktopRadarState = await _desktopRadarOverviewService.RefreshAsync(
                _desktopRadarState?.Snapshot);
            ApplyRadarState(_desktopRadarState);
        }
        catch (Exception ex)
        {
            HomeRadarFreshnessText.Text = "需要刷新";
            HomeRadarStatusText.Text = $"刷新失败：{ex.Message}";
        }
        finally
        {
            HomeRadarRefreshButton.IsEnabled = true;
            _radarRefreshInFlight = false;
        }
    }

    private void ApplyRadarState(DesktopRadarOverviewState state)
    {
        HomeRadarView.SetState(state);
        HomeRadarFreshnessText.Text = state.FreshnessText;
        HomeRadarStatusText.Text = state.StatusMessage
            ?? "点击“打开布局”查看和调整空间。";

        var snapshot = state.Snapshot;
        if (snapshot is null)
        {
            HomeRadarSummaryText.Text = "等待桌面观察";
            return;
        }

        var itemText = $"{snapshot.TotalItemCount} 个项目";
        var displayText = snapshot.Displays.Count > 1
            ? $" · {snapshot.Displays.Count} 块屏幕"
            : string.Empty;
        var layoutText = state.ActiveLayout is null
            ? " · 还没有设置布局"
            : $" · {state.ActiveLayout.Zones.Count} 个分区";
        HomeRadarSummaryText.Text = itemText + displayText + layoutText;
    }

    private void OpenCurrentLayout_Click(object sender, RoutedEventArgs e)
        => NavigateToSection("layouts");

    private void CurrentLayoutCard_Tapped(object sender, TappedRoutedEventArgs e)
        => NavigateToSection("layouts");

    private void AutomationCard_Tapped(object sender, TappedRoutedEventArgs e)
        => NavigateToSection("settings");

    private void PendingCard_Tapped(object sender, TappedRoutedEventArgs e)
        => NavigateToSection("pending");

    private void NavigateToSection(string tag)
    {
        var target = ShellNavigation.MenuItems
            .Concat(ShellNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                tag,
                StringComparison.Ordinal));
        if (target is null)
            return;

        ShellNavigation.SelectedItem = target;
        ShowSection(tag);
    }
}
