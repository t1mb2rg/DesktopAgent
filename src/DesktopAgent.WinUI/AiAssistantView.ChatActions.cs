using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class AiAssistantView
{
    private readonly DesktopWorkspaceDraftProductService _draftProductService = new();

    private void RenderChatActions()
    {
        AiChatActionsPanel.Children.Clear();
        foreach (var action in _pendingChatActions)
        {
            var button = new Button { Content = action.Kind == WorkspaceChatActionKind.SaveLayout ? "保存布局" : "检查精确预览", Tag = action };
            button.Click += ChatAction_Click;
            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(new TextBlock { Text = action.Title, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(button);
            AiChatActionsPanel.Children.Add(row);
        }
        AiChatActionCard.Visibility = _pendingChatActions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChatAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkspaceChatAction action } && _pendingChatActions.Contains(action))
            await ExecuteChatActionAsync(action, _pendingChatActionRevision);
    }

    private async void SaveCurrentDesign_Click(object sender, RoutedEventArgs e)
        => await ExecuteChatActionAsync(new(WorkspaceChatActionKind.SaveLayout, "保存当前设计", null), _workspaceSession.Session.Snapshot().Revision);

    private async void PreviewCurrentDesign_Click(object sender, RoutedEventArgs e)
        => await ExecuteChatActionAsync(new(WorkspaceChatActionKind.PreviewDesign, "检查当前设计", null), _workspaceSession.Session.Snapshot().Revision);

    private async Task ExecuteChatActionAsync(WorkspaceChatAction action, long revision)
    {
        if (IsRequestActive) return;
        var snapshot = _workspaceSession.Session.Snapshot();
        if (snapshot.Revision != revision)
        {
            _pendingChatActions.Clear();
            RenderChatActions();
            return;
        }
        using var cts = new CancellationTokenSource();
        _analysisCts = cts;
        SetBusy(true);
        try
        {
            string message;
            if (action.Kind == WorkspaceChatActionKind.SaveLayout)
            {
                var savedRevision = await _draftProductService.SaveAsync(snapshot.State.Draft, action.LayoutName, BuildWorkspaceSourceUserIntent());
                message = $"已将这份设计保存为布局 v{savedRevision}，可以在设计库找到。桌面图标没有移动。";
            }
            else
            {
                var preview = await _draftProductService.PreviewAsync(snapshot.State.Draft, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (_workspaceSession.Session.Snapshot().Revision != snapshot.Revision)
                    throw new InvalidOperationException("设计在检查期间发生变化，旧预览未替换当前设计。");
                if (WorkspaceFrozenPreviewRequested is null) throw new InvalidOperationException("精确预览窗口当前不可用。");
                WorkspaceFrozenPreviewRequested.Invoke(preview);
                message = "已打开当前设计的精确预览，尚未移动图标。请检查后再确认应用。";
            }
            _workspaceSession.Session.AppendMessage(DesktopWorkspaceMessageRole.LocalResult, message);
            _pendingChatActions.RemoveAll(item => item.Kind == action.Kind);
            RenderChatActions();
        }
        catch (Exception ex)
        {
            AiStatusBar.Title = "操作没有完成";
            AiStatusBar.Message = ex is OperationCanceledException ? "预览检查已取消，未移动图标。" : ex.Message;
            AiStatusBar.Severity = InfoBarSeverity.Warning;
            AiStatusBar.IsOpen = true;
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, cts)) _analysisCts = null;
            SetBusy(false);
            RenderSessionState();
        }
    }
}
