using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DesktopAgent.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly TrayIconHost _trayIcon;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DesktopAgent";

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
