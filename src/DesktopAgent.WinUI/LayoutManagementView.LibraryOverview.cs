using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>
    /// 从设计库移除当前选中的设计。只归档资产，不删除历史版本，也不移动桌面图标。
    /// </summary>
    private async void DeleteWorkspaceDesign_Click(object sender, RoutedEventArgs e)
    {
        var asset = _workspaceDetailAsset;
        if (asset is null)
        {
            ShowInfo("没有选中设计", "请先在左侧设计库里点一个设计，再执行删除。", InfoBarSeverity.Informational);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除这个设计？",
            Content = new TextBlock
            {
                Text = $"「{asset.Name}」会从设计库移除，之后不再出现在列表里。"
                    + $"\n\n它的历史设计版本仍保留在本地数据中，不会被这条操作销毁；"
                    + $"桌面图标也不会移动，已经应用过的布局不会因此回退。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var archived = await _workspaceBlueprintStore.ArchiveAsync(asset.BlueprintId);
            if (!archived)
            {
                ShowError("这个设计已经不在设计库里", "它可能已被删除，或者列表已经过期。请刷新设计库后再看。");
                await RefreshWorkspaceLibraryAsync(showSuccess: false);
                return;
            }

            _workspaceDetailAsset = null;
            _workspaceDetailRevisions = null;
            await RefreshWorkspaceLibraryAsync(showSuccess: false);
            ShowInfo("设计已从设计库移除", $"「{asset.Name}」不再出现在设计库。历史版本仍保留在本地数据中。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError("删除设计失败", ex.Message);
        }
    }
    private void ReturnToLibrary_Click(object sender, RoutedEventArgs e)
    {
        SetLayoutStage(DesktopLayoutStage.Design);
        ShowLibraryOverview();
    }
    private void OpenPreparationTools_Click(object sender, RoutedEventArgs e)
        => SetLayoutStage(DesktopLayoutStage.Prepare);
}
