namespace DesktopAgent.WinUI;

public sealed partial class MainWindow
{
    private bool _zoneEditorActive;

    internal void HideForZoneEditor()
    {
        if (_exitRequested || _zoneEditorActive)
            return;

        _zoneEditorActive = true;
        _residentReleaseTimer.Stop();
        _appWindow.Hide();
    }

    internal void ShowAfterZoneEditor()
    {
        if (_exitRequested)
            return;

        _zoneEditorActive = false;
        _appWindow.Show(activateWindow: true);
        EnsureSelectedSectionHydrated();
        Activate();
    }
}
