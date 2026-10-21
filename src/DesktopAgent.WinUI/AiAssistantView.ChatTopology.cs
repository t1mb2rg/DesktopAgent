namespace DesktopAgent.WinUI;
public sealed partial class AiAssistantView
{
    private void OpenWorkspaceDesktopEditor_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => _workspaceSession.RequestDesktopEditor();

    private WorkspaceDesignCanvas? _sharedDesignCanvas;
    private void RenderChatTopology()
    {
        _sharedDesignCanvas ??= new WorkspaceDesignCanvas(_workspaceSession, ChatTopologyCanvas, AiTopologySummaryText);
        _sharedDesignCanvas.RenderChatTopology();
    }
}
