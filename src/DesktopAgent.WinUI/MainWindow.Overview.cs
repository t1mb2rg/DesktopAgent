using Microsoft.UI.Xaml;

namespace DesktopAgent.WinUI;

public sealed partial class MainWindow
{
    private readonly OverviewLightweightReadModelLoader _overviewReadModelLoader = new();
    private long _homeVisibilityCallbackToken;
    private bool _overviewRefreshInFlight;

    private async void HomeView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_homeVisibilityCallbackToken == 0)
        {
            _homeVisibilityCallbackToken = HomeView.RegisterPropertyChangedCallback(
                VisibilityProperty,
                OnHomeVisibilityChanged);
        }

        await RefreshOverviewLightweightAsync();
    }

    private void OnHomeVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (HomeView.Visibility == Visibility.Visible)
            _ = RefreshOverviewLightweightAsync();
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
}
