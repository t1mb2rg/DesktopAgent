using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using Microsoft.UI.Xaml;
namespace DesktopAgent.WinUI;
public sealed partial class LayoutManagementView
{
    private WorkspaceDesignCanvas? _libraryOverviewCanvas;
    private void ShowLibraryOverview()
    {
        WorkspaceDetailSurface.Visibility = Visibility.Collapsed;
        LayoutWorkbenchSurface.Visibility = Visibility.Collapsed;
        SharedDesignSurface.Visibility = Visibility.Collapsed;
        LibraryOverviewSurface.Visibility = Visibility.Visible;
    }
    private void RenderLibraryDesign(DesktopWorkspaceBlueprintRevisionRecord record)
    {
        _libraryOverviewCanvas ??= new WorkspaceDesignCanvas(_designSession, LibraryDesignCanvas, LibraryDesignSummaryText) { IsReadOnly = true };
        _libraryOverviewCanvas.DisplayDraft = DesktopWorkspaceDesignDraft.FromBlueprint(record.Blueprint);
        LibraryDesignTitleText.Text = $"{_workspaceDetailAsset?.Name} · v{record.Blueprint.Revision}";
        _libraryOverviewCanvas.RenderChatTopology();
    }
    private void ShowLibraryDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceDetailAsset is null) return;
        LibraryOverviewSurface.Visibility = Visibility.Collapsed;
        WorkspaceDetailSurface.Visibility = Visibility.Visible;
    }
    private void ReturnToLibrary_Click(object sender, RoutedEventArgs e)
    {
        SetLayoutStage(DesktopLayoutStage.Design);
        ShowLibraryOverview();
    }
    private void OpenPreparationTools_Click(object sender, RoutedEventArgs e)
        => SetLayoutStage(DesktopLayoutStage.Prepare);
}
