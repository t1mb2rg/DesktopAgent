using Microsoft.UI.Xaml.Controls;
namespace DesktopAgent.WinUI;
public sealed partial class MainWindow
{
    private WorkspaceDesktopEditorWindow? _workspaceDesktopEditor;
    private bool _openingWorkspaceDesktopEditor;
    private async void OpenWorkspaceDesktopEditor()
    {
        if (_exitRequested || _zoneEditorActive || _openingWorkspaceDesktopEditor ||
            _layoutManagementView?.IsCoordinateOperationInProgress == true) return;
        _openingWorkspaceDesktopEditor = true;
        try
        {
            await _workspaceSession.RefreshDesignGeometryAsync();
            if (_exitRequested) return;
            _workspaceDesktopEditor = new WorkspaceDesktopEditorWindow(_workspaceSession);
            _workspaceDesktopEditor.Closed += (_, _) => { _workspaceDesktopEditor = null; ShowAfterZoneEditor(); };
            _layoutManagementView?.HideDesktopOverlaysForEditing();
            HideForZoneEditor();
            _workspaceDesktopEditor.Activate();
        }
        catch (Exception ex)
        {
            _workspaceDesktopEditor?.Close();
            _workspaceDesktopEditor = null;
            ShowAfterZoneEditor();
            var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "无法打开桌面编辑", Content = ex.Message, CloseButtonText = "知道了" };
            await dialog.ShowAsync();
        }
        finally { _openingWorkspaceDesktopEditor = false; }
    }
}
