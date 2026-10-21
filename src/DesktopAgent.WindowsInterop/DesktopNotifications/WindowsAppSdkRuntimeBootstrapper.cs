using Microsoft.Windows.ApplicationModel.DynamicDependency;
using Microsoft.Windows.ApplicationModel.WindowsAppRuntime;

namespace DesktopAgent.WindowsInterop.DesktopNotifications;

public sealed record WindowsAppSdkRuntimeInitializationResult(
    bool IsInitialized,
    string? DeploymentStatus,
    string? Failure);

/// <summary>
/// Explicit Windows App SDK runtime/deployment bootstrap for the real DesktopAgent process.
/// Keeping this out of module initializers prevents xUnit from starting WinAppSDK merely by
/// loading the PoC assembly as a test dependency.
/// </summary>
public sealed class WindowsAppSdkRuntimeBootstrapper : IDisposable
{
    // Microsoft.WindowsAppSDK 2.3.x encodes Major.Minor as 0x0002_0003.
    // Keep this constant in lockstep with the WindowsInterop package reference.
    private const uint WindowsAppSdkMajorMinor = 0x0002_0003;

    private readonly object _gate = new();
    private bool _bootstrapInitialized;
    private int _disposeStarted;
    private WindowsAppSdkRuntimeInitializationResult _result =
        new(false, null, "Not initialized yet.");

    public WindowsAppSdkRuntimeInitializationResult Result
    {
        get
        {
            lock (_gate)
                return _result;
        }
    }

    public WindowsAppSdkRuntimeInitializationResult Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0,
                this);

            if (_bootstrapInitialized && _result.IsInitialized)
                return _result;

            try
            {
                Bootstrap.Initialize(WindowsAppSdkMajorMinor);
                _bootstrapInitialized = true;

                // AppNotificationManager depends on the Windows App SDK Singleton package.
                // Disable the MSBuild auto-initializer and perform this explicitly so failures
                // are observable and only happen in the real app process.
                var deployment = DeploymentManager.Initialize(
                    new DeploymentInitializeOptions
                    {
                        OnErrorShowUI = false,
                        ForceDeployment = false,
                    });

                if (deployment.Status != DeploymentStatus.Ok)
                {
                    var failure =
                        $"Windows App SDK deployment status is {deployment.Status}. "
                        + $"ExtendedError: {deployment.ExtendedError}";
                    ShutdownBootstrapCore();
                    _result = new WindowsAppSdkRuntimeInitializationResult(
                        false,
                        deployment.Status.ToString(),
                        failure);
                    return _result;
                }

                _result = new WindowsAppSdkRuntimeInitializationResult(
                    true,
                    deployment.Status.ToString(),
                    null);
                return _result;
            }
            catch (Exception ex)
            {
                ShutdownBootstrapCore();
                _result = new WindowsAppSdkRuntimeInitializationResult(
                    false,
                    null,
                    ex.ToString());
                return _result;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            ShutdownBootstrapCore();
            _result = new WindowsAppSdkRuntimeInitializationResult(
                false,
                null,
                "Stopped.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        lock (_gate)
            ShutdownBootstrapCore();
    }

    private void ShutdownBootstrapCore()
    {
        if (!_bootstrapInitialized)
            return;

        _bootstrapInitialized = false;
        try
        {
            Bootstrap.Shutdown();
        }
        catch
        {
            // Shutdown is best-effort during degraded startup/app exit.
        }
    }
}
