using DesktopAgent.Core.Productization;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace DesktopAgent.WinUI;

public static class Program
{
    internal static event EventHandler? SecondaryActivationReceived;

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey(TesselProductIdentity.InstanceKey);
        if (!instance.IsCurrent)
        {
            instance.RedirectActivationToAsync(activation).AsTask().GetAwaiter().GetResult();
            return 0;
        }

        instance.Activated += OnActivated;

        Microsoft.UI.Xaml.Application.Start(initializationCallbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        return 0;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
        => SecondaryActivationReceived?.Invoke(null, EventArgs.Empty);
}
