namespace DesktopAgent.WinUI;

public sealed partial class MainWindow
{
    private readonly ProductWorkspaceSession _workspaceSession = new();
    private long _observedWorkspaceRevision;

    private async Task InitializeWorkspaceSessionAsync()
    {
        await _workspaceSession.InitializeAsync();
        _observedWorkspaceRevision = _workspaceSession.Session.Snapshot().Revision;
        _workspaceSession.Changed += WorkspaceSession_Changed;
        _workspaceSession.DesktopEditorRequested += OpenWorkspaceDesktopEditor;
    }

    private void WorkspaceSession_Changed()
    {
        void Refresh()
        {
            if (_exitRequested) return;
            var revision = _workspaceSession.Session.Snapshot().Revision;
            if (revision == _observedWorkspaceRevision) return;
            _observedWorkspaceRevision = revision;
            if (_layoutManagementView?.IsCoordinateOperationInProgress != true)
                _layoutManagementView?.InvalidateDesignRevisionEvidence();
        }
        if (DispatcherQueue.HasThreadAccess) Refresh();
        else DispatcherQueue.TryEnqueue(Refresh);
    }
}
