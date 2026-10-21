using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class AiAssistantView
{
    private readonly ProductWorkspaceSession _workspaceSession;
    private bool _sessionSubscribed;
    private bool _designPaneOpen;
    private bool _designPaneAutoOpened;

    private void ToggleDesignPane_Click(object sender, RoutedEventArgs e)
    {
        _designPaneOpen = !_designPaneOpen;
        UpdateDesignPaneLayout();
    }

    private void ChatWorkspace_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDesignPaneLayout();

    private void UpdateDesignPaneLayout()
    {
        if (DesignPane is null || DesignPaneColumn is null) return;
        var wide = ChatWorkspaceGrid.ActualWidth >= 1000;
        DesignPane.Visibility = _designPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        DesignPaneColumn.Width = _designPaneOpen && wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(DesignPane, wide ? 1 : 0);
        Grid.SetRowSpan(DesignPane, wide ? 3 : 1);
        DesignPane.Height = wide ? double.NaN : 300;
        AiToggleDesignButton.Content = _designPaneOpen ? "收起设计" : "当前设计";
    }

    private async void RetryStartupUnderstanding_Click(object sender, RoutedEventArgs e)
        => await _workspaceSession.EnsureUnderstandingAsync();

    private void AttachWorkspaceSession()
    {
        if (!_sessionSubscribed)
        {
            _workspaceSession.Changed += WorkspaceSession_Changed;
            _sessionSubscribed = true;
        }
        RenderSessionState();
    }

    private void WorkspaceSession_Changed()
        => DispatcherQueue.TryEnqueue(RenderSessionState);

    private void RenderSessionState()
    {
        var snapshot = _workspaceSession.Session.Snapshot();
        AiSessionStateText.Text = _workspaceSession.PersistenceMessage ??
            $"设计修订 {snapshot.Revision} · {snapshot.State.Draft.Zones.Count} 个分区 · " +
            (_workspaceSession.UnderstandingMessage ?? "自动保存草稿");
        AiUndoDesignButton.IsEnabled = snapshot.UndoHistory.Count > 0;
        AiRedoDesignButton.IsEnabled = snapshot.RedoHistory.Count > 0;
        AiDirectDesignActions.Visibility = snapshot.State.Draft.Zones.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var understandingReady = _workspaceSession.UnderstandingStatus == AiDesktopUnderstandingProductStatus.Success;
        var understandingBusy = _workspaceSession.UnderstandingInProgress;
        AiStartupStatusText.Text = _workspaceSession.UnderstandingMessage ?? "正在准备桌面理解…";
        AiStartupProgress.Visibility = understandingBusy ? Visibility.Visible : Visibility.Collapsed;
        AiStartupProgress.IsActive = understandingBusy;
        AiStartupSettingsButton.Visibility = !understandingBusy && !understandingReady ? Visibility.Visible : Visibility.Collapsed;
        AiStartupRetryButton.Visibility = !understandingBusy && !understandingReady &&
            _workspaceSession.UnderstandingStatus != AiDesktopUnderstandingProductStatus.AiDisabled ? Visibility.Visible : Visibility.Collapsed;
        if (understandingReady && !_designPaneAutoOpened)
        {
            _designPaneAutoOpened = true;
            _designPaneOpen = true;
        }
        UpdateDesignPaneLayout();
        RenderChatHistory();
        RenderChatTopology();
    }

    private void UndoDesign_Click(object sender, RoutedEventArgs e) => TraverseDesignHistory(undo: true);
    private void RedoDesign_Click(object sender, RoutedEventArgs e) => TraverseDesignHistory(undo: false);

    private void TraverseDesignHistory(bool undo)
    {
        var session = _workspaceSession.Session;
        var revision = session.Snapshot().Revision;
        var result = undo ? session.Undo(revision) : session.Redo(revision);
        if (!result.Applied) return;
        session.AppendMessage(DesktopWorkspaceMessageRole.LocalResult, undo ? "已撤销上一次设计修改。" : "已重做设计修改。");
        if (_latestSuccessfulUnderstanding is not null) RenderConversationState();
        AiStatusBar.Title = undo ? "已撤销设计修改" : "已重做设计修改";
        AiStatusBar.Message = "当前设计已更新，旧精确预览需要重新生成。";
        AiStatusBar.Severity = InfoBarSeverity.Informational;
        AiStatusBar.IsOpen = true;
    }
}
