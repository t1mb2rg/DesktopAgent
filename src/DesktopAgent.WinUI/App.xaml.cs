using DesktopAgent.Core.Productization;
using Microsoft.UI.Xaml;

namespace DesktopAgent.WinUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _mainWindow;

    internal MainWindow? ProductWindow => _mainWindow;

    public App()
    {
        InitializeComponent();
        Program.SecondaryActivationReceived += OnSecondaryActivationReceived;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow
        {
            Title = TesselProductIdentity.DisplayName,
        };
        _mainWindow.Activate();
    }

    private void OnSecondaryActivationReceived(object? sender, EventArgs e)
    {
        var window = _mainWindow;
        if (window is null)
            return;

        _ = window.DispatcherQueue.TryEnqueue(window.ShowFromBackground);
    }
}
