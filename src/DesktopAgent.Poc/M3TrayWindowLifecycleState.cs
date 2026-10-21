namespace DesktopAgent.Poc;

/// <summary>
/// Tracks the irreversible WPF MainWindow close boundary for the tray lifetime surface.
/// A Window instance cannot be shown again after Closed has fired, so exit rollback is only
/// legal while the MainWindow is still alive.
/// </summary>
internal sealed class M3TrayWindowLifecycleState
{
    public bool IsExitRequested { get; private set; }

    public bool IsMainWindowClosed { get; private set; }

    public bool CanOpenFromTray => !IsExitRequested && !IsMainWindowClosed;

    public bool CanShowMainWindowForExit => IsExitRequested && !IsMainWindowClosed;

    public bool TryBeginExit()
    {
        if (IsExitRequested || IsMainWindowClosed)
            return false;

        IsExitRequested = true;
        return true;
    }

    public void CancelExit()
    {
        // Window.Close is an irreversible WPF commit point. Once Closed has fired,
        // a failed later shutdown step must never resurrect tray-open eligibility.
        if (IsMainWindowClosed)
            return;

        IsExitRequested = false;
    }

    public void MarkMainWindowClosed()
    {
        IsMainWindowClosed = true;
    }
}
