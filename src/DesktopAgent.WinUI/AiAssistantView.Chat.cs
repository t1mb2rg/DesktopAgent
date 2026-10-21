using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class AiAssistantView
{
    private readonly AiWorkspaceChatService _chatService = new();
    private readonly List<WorkspaceChatAction> _pendingChatActions = [];
    private long _pendingChatActionRevision;
    private long _renderedChatSequence = -1;
    private bool _followChatTail = true;
    internal event Action? ModelSettingsRequested;

    private void ChatModelSettings_Click(object sender, RoutedEventArgs e) => ModelSettingsRequested?.Invoke();

    private void DismissChatAction_Click(object sender, RoutedEventArgs e)
    {
        _pendingChatActions.Clear();
        AiChatActionCard.Visibility = Visibility.Collapsed;
    }

    private void ChatScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => _followChatTail = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 80;

    private void ChatReply_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_followChatTail)
            DispatcherQueue.TryEnqueue(() => ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, disableAnimation: true));
    }

    private async Task SendWorkspaceChatAsync(string guidance)
    {
        RememberWorkspaceUserIntent(guidance);
        using var cts = new CancellationTokenSource();
        _analysisCts = cts;
        AiGuidanceTextBox.Text = string.Empty;
        AiStreamingReplyText.Text = string.Empty;
        AiStreamingReplyText.Visibility = Visibility.Collapsed;
        AiStreamingMarkdownHost.Content = null;
        AiStreamingMarkdownHost.Visibility = Visibility.Visible;
        AiStatusBar.IsOpen = false;
        SetBusy(true);
        try
        {
            var latest = await _workspaceSession.EnsureUnderstandingAsync().WaitAsync(cts.Token);
            if (latest.Draft is null || latest.Observation is null)
                throw new InvalidOperationException(_workspaceSession.UnderstandingMessage ?? "桌面理解尚未就绪。");
            var snapshot = _workspaceSession.Session.Snapshot();
            var context = DesktopLayoutConversationContextBuilder.Build(latest.Draft, latest.Observation, _conversationState, guidance);
            var lastStreamingRender = Environment.TickCount64;
            var progress = new Progress<string>(chunk =>
            {
                if (ReferenceEquals(_analysisCts, cts) && !cts.IsCancellationRequested)
                {
                    AiStreamingReplyText.Text += chunk;
                    // Bound native control rebuilding during token streams.
                    if (AiStreamingMarkdownHost.Content is null || Environment.TickCount64 - lastStreamingRender >= 100)
                    {
                        AiStreamingMarkdownHost.Content = ChatMarkdownRenderer.Render(AiStreamingReplyText.Text);
                        lastStreamingRender = Environment.TickCount64;
                    }
                }
            });
            var reply = await _chatService.ReplyAsync(context, snapshot, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var edit = _workspaceSession.Session.TryApply(snapshot.Revision, reply.Commands);
            if (edit.Status is DesktopWorkspaceEditStatus.Invalid or DesktopWorkspaceEditStatus.Stale)
                throw new InvalidOperationException(edit.Message ?? "设计已变化，本次回复未覆盖当前设计。");
            var text = WorkspaceChatReplyText.ForCommittedReply(reply);
            _workspaceSession.Session.AppendMessage(DesktopWorkspaceMessageRole.Assistant, text);
            if (reply.Actions.Count > 0)
            {
                _pendingChatActions.Clear();
                _pendingChatActions.AddRange(reply.Actions);
                _pendingChatActionRevision = _workspaceSession.Session.Snapshot().Revision;
            }
            AiActionStateText.Text = reply.Actions.Count == 0
                ? "本轮没有生成操作卡片。需要保存或预览时，可直接点击下方按钮；尚未执行任何操作。"
                : $"本轮已生成 {reply.Actions.Count} 个操作建议，点击相应按钮后才会执行。";
            RenderChatActions();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            AiStatusBar.Title = "回复已停止";
            AiStatusBar.Message = "未完成的工具动作没有修改设计。";
            AiStatusBar.Severity = InfoBarSeverity.Informational;
            AiStatusBar.IsOpen = true;
            AiGuidanceTextBox.Text = guidance;
            _workspaceSession.Session.AppendMessage(DesktopWorkspaceMessageRole.LocalResult, "这轮回复已停止，未完成的动作没有修改设计。");
        }
        catch (Exception ex)
        {
            AiStatusBar.Title = "这轮对话没有完成";
            AiStatusBar.Message = ex is OperationCanceledException ? "模型回复超时，请重试。已有设计没有被覆盖。" : ex.Message;
            AiStatusBar.Severity = InfoBarSeverity.Warning;
            AiStatusBar.IsOpen = true;
            AiGuidanceTextBox.Text = guidance;
            _workspaceSession.Session.AppendMessage(DesktopWorkspaceMessageRole.LocalResult, "这轮对话未完成，已有设计已保留。可以重试。");
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, cts)) _analysisCts = null;
            AiStreamingReplyText.Visibility = Visibility.Collapsed;
            AiStreamingMarkdownHost.Visibility = Visibility.Collapsed;
            AiStreamingMarkdownHost.Content = null;
            SetBusy(false);
            RenderSessionState();
        }
    }

    private void RenderChatHistory()
    {
        var snapshot = _workspaceSession.Session.Snapshot();
        if (_pendingChatActions.Count > 0 && snapshot.Revision != _pendingChatActionRevision)
        {
            _pendingChatActions.Clear();
            RenderChatActions();
        }
        if (_renderedChatSequence == snapshot.Sequence) return;
        _renderedChatSequence = snapshot.Sequence;
        AiChatMessages.Children.Clear();
        foreach (var message in snapshot.Messages)
        {
            var content = new StackPanel { Spacing = 6, MaxWidth = 820 };
            var isUser = message.Role == DesktopWorkspaceMessageRole.User;
            content.Children.Add(new TextBlock
            {
                Text = message.Role == DesktopWorkspaceMessageRole.User ? "你" :
                    message.Role == DesktopWorkspaceMessageRole.LocalResult ? "操作结果" : "Tessel",
                FontSize = 11, Opacity = 0.5,
            });
            content.Children.Add(message.Role == DesktopWorkspaceMessageRole.Assistant
                ? ChatMarkdownRenderer.Render(message.Text)
                : new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 15 });
            AiChatMessages.Children.Add(new Border
            {
                Child = content,
                Padding = new Thickness(16, 13, 16, 13),
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                BorderThickness = new Thickness(1),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(22, 128, 128, 128)),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(
                    isUser ? (byte)42 : (byte)16, 128, 128, 128)),
            });
        }
        AiWelcomePanel.Visibility = snapshot.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderChatTopology();
    }
}
