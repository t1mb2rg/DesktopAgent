using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private WorkspaceDesignCanvas? _libraryDesignCanvas;
    private bool _sharedDesignSubscribed;
    private bool _sharedDesignActionBusy;
    private readonly DesktopWorkspaceDraftProductService _sharedDraftService = new();

    private void AttachSharedDesign()
    {
        _libraryDesignCanvas ??= new WorkspaceDesignCanvas(_designSession, SharedDesignCanvas, SharedDesignSummaryText);
        if (!_sharedDesignSubscribed)
        {
            _designSession.Changed += SharedDesignChanged;
            _sharedDesignSubscribed = true;
        }
        _libraryDesignCanvas.RenderChatTopology();
    }

    private void SharedDesignChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        _libraryDesignCanvas?.RenderChatTopology();
        _libraryOverviewCanvas?.RenderChatTopology();
    });

    private void DetachSharedDesign()
    {
        _libraryDesignCanvas?.CancelDrag();
        _designSession.Changed -= SharedDesignChanged;
        _sharedDesignSubscribed = false;
    }

    private void OpenSharedDesign_Click(object sender, RoutedEventArgs e)
    {
        SetLayoutStage(DesktopAgent.Application.DesktopLayouts.Workspaces.DesktopLayoutStage.Design);
        LibraryOverviewSurface.Visibility = Visibility.Collapsed;
        WorkspaceDetailSurface.Visibility = Visibility.Collapsed;
        LayoutWorkbenchSurface.Visibility = Visibility.Collapsed;
        SharedDesignSurface.Visibility = Visibility.Visible;
        AttachSharedDesign();
    }

    private void CloseSharedDesign_Click(object sender, RoutedEventArgs e)
    {
        _libraryDesignCanvas?.CancelDrag();
        SharedDesignSurface.Visibility = Visibility.Collapsed;
        SetLayoutStage(DesktopAgent.Application.DesktopLayouts.Workspaces.DesktopLayoutStage.Design);
    }

    private void UndoSharedDesign_Click(object sender, RoutedEventArgs e)
        => _designSession.Session.Undo(_designSession.Session.Snapshot().Revision);
    private void RedoSharedDesign_Click(object sender, RoutedEventArgs e)
        => _designSession.Session.Redo(_designSession.Session.Snapshot().Revision);

    private async void SaveSharedDesign_Click(object sender, RoutedEventArgs e)
    {
        if (_sharedDesignActionBusy) return;
        SetSharedDesignBusy(true);
        var snapshot = _designSession.Session.Snapshot();
        try
        {
            var revision = await _sharedDraftService.SaveAsync(snapshot.State.Draft, null, null);
            await RefreshWorkspaceLibraryAsync(false);
            ShowInfo("设计已保存", $"已保存所选草稿快照为设计版本 v{revision}，桌面图标没有移动。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError("未保存设计版本", ex.Message); }
        finally { SetSharedDesignBusy(false); }
    }

    private async void PreviewSharedDesign_Click(object sender, RoutedEventArgs e)
    {
        if (_sharedDesignActionBusy || IsCoordinateOperationInProgress) return;
        SetSharedDesignBusy(true);
        var snapshot = _designSession.Session.Snapshot();
        try
        {
            var preview = await _sharedDraftService.PreviewAsync(snapshot.State.Draft, CancellationToken.None);
            if (_designSession.Session.Snapshot().Revision != snapshot.Revision)
                throw new InvalidOperationException("草稿已变化，请重新生成预览。");
            PrepareWorkspaceFrozenPreviewReplacement();
            if (!TryLoadAiWorkspaceFrozenPreview(preview)) return;
            EnableWorkspaceFrozenPreviewApply();
            SharedDesignSurface.Visibility = Visibility.Collapsed;
            SetLayoutStage(DesktopAgent.Application.DesktopLayouts.Workspaces.DesktopLayoutStage.Execute);
        }
        catch (Exception ex) { ShowError("无法生成精确预览", ex.Message); }
        finally { SetSharedDesignBusy(false); }
    }

    private void OpenWorkspaceDesktopEditor_Click(object sender, RoutedEventArgs e) => _designSession.RequestDesktopEditor();

    private void SetSharedDesignBusy(bool busy)
    {
        _sharedDesignActionBusy = busy;
        SaveSharedDesignButton.IsEnabled = !busy;
        PreviewSharedDesignButton.IsEnabled = !busy;
    }
}
