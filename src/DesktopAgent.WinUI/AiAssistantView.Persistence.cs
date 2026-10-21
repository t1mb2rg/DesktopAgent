using DesktopAgent.Core.Intelligence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class AiAssistantView
{
    private bool _persistenceHydrated;
    private AiPersistedUnderstandingProductState? _persistedUnderstandingState;

    private async void AiAssistantView_Loaded(object sender, RoutedEventArgs e)
    {
        await _workspaceSession.InitializeAsync();
        AttachWorkspaceSession();
        await _workspaceSession.EnsureUnderstandingAsync();
        if (_latestSuccessfulUnderstanding is { } cached)
        {
            RenderDraft(cached);
            RenderConversationState();
            AiAnalyzeButton.Content = "发送";
            return;
        }
        if (_persistenceHydrated || IsRequestActive)
            return;

        _persistenceHydrated = true;
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        SetBusy(true);
        try
        {
            var state = await _understandingService.RestoreLatestAsync(cts.Token);
            if (cts.IsCancellationRequested)
                return;

            _persistedUnderstandingState = state;
            RenderPersistedUnderstanding(state);
        }
        catch (OperationCanceledException)
        {
            _persistenceHydrated = false;
            // View lifetime / app shutdown cancellation is not a persistence failure.
        }
        catch (Exception ex)
        {
            AiStatusBar.Severity = InfoBarSeverity.Warning;
            AiStatusBar.Title = "无法恢复上一次桌面理解";
            AiStatusBar.Message = ex.Message;
            AiStatusBar.IsOpen = true;
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, cts))
                _analysisCts = null;
            cts.Dispose();
            SetBusy(false);
        }
    }

    private void RenderPersistedUnderstanding(AiPersistedUnderstandingProductState state)
    {
        if (state.Status == AiPersistedUnderstandingRestoreStatus.None)
            return;

        if (state.Status == AiPersistedUnderstandingRestoreStatus.PersistenceFailure)
        {
            AiStatusBar.Severity = InfoBarSeverity.Warning;
            AiStatusBar.Title = "已保存的桌面理解无法安全恢复";
            AiStatusBar.Message = state.Message ?? "持久化数据未通过完整性检查。";
            AiStatusBar.IsOpen = true;
            return;
        }

        var snapshot = state.Snapshot;
        if (snapshot is null)
            return;

        var semantic = snapshot.Understanding;
        AiResultCard.Visibility = Visibility.Collapsed;
        AiDesktopSummaryText.Text = semantic.DesktopSummary;
        AiResultMetaText.Text =
            $"已保存理解 · {snapshot.SourceItemCount} 个项目 · {snapshot.CreatedAtUtc.ToLocalTime():g}";
        AiIntentSummaryText.Text =
            "桌面理解与设计草稿独立保存；精确预览与执行确认不会跨重启恢复。";

        AiCategoriesPanel.Children.Clear();
        foreach (var category in semantic.Categories)
        {
            var count = semantic.Assignments.Count(assignment =>
                string.Equals(assignment.CategoryKey, category.Key, StringComparison.Ordinal));
            AiCategoriesPanel.Children.Add(CreateResultItem(
                $"{category.Name} · {count} 项",
                category.Description));
        }

        AiZonesPanel.Children.Clear();
        AiZonesPanel.Children.Add(CreateSecondaryText(
            "持久化 Understanding 不包含 Zone 或显示器设计；这些内容属于 Workspace Blueprint。"));

        AiQuestionsPanel.Children.Clear();
        if (semantic.Questions.Count == 0)
        {
            AiQuestionsPanel.Children.Add(CreateSecondaryText("上一次理解没有遗留待确认问题。"));
        }
        else
        {
            foreach (var question in semantic.Questions)
                AiQuestionsPanel.Children.Add(CreateSecondaryText($"• {question.Text}"));
        }

        AiImportLayoutButton.IsEnabled = false;
        // Persisted pure semantics do not create a legacy Draft. Keep exactly one explicit
        // full re-understanding action instead of surfacing two equivalent buttons.
        AiRefreshUnderstandingButton.Visibility = Visibility.Collapsed;
        AiAnalyzeButton.Content = "重新理解桌面";

        if (state.Status == AiPersistedUnderstandingRestoreStatus.DesktopUnavailable)
        {
            AiStatusBar.Severity = InfoBarSeverity.Informational;
            AiStatusBar.Title = "已恢复上一次桌面理解，但尚未检查变化";
            AiStatusBar.Message = state.Message ??
                "当前无法建立 fresh DesktopObservation，因此不会把这份历史理解标记为 Current。";
            AiStatusBar.IsOpen = true;
            return;
        }

        var freshness = state.Freshness;
        if (freshness is null)
            return;

        switch (freshness.Freshness)
        {
            case DesktopSemanticUnderstandingFreshness.Current:
                AiStatusBar.Severity = InfoBarSeverity.Success;
                AiStatusBar.Title = "已恢复上一次桌面理解 · 当前";
                AiStatusBar.Message =
                    $"已用 fresh 桌面事实检查 {freshness.CurrentItemCount} 个项目；语义来源没有变化。";
                break;

            case DesktopSemanticUnderstandingFreshness.PossiblyStale:
                AiStatusBar.Severity = InfoBarSeverity.Informational;
                AiStatusBar.Title = "已恢复上一次桌面理解 · 可能已过期";
                AiStatusBar.Message =
                    "桌面语义来源发生了变化，但大多数已分类项目仍然存在。" +
                    $"当前 {freshness.CurrentItemCount} 项；建议检查变化后再继续设计。";
                break;

            case DesktopSemanticUnderstandingFreshness.ClearlyStale:
                AiStatusBar.Severity = InfoBarSeverity.Warning;
                AiStatusBar.Title = "已恢复上一次桌面理解 · 明显已过期";
                AiStatusBar.Message =
                    $"当前桌面与保存理解差异较大，已有 {freshness.MissingKnownAssignmentCount} 个分类对象不再出现。" +
                    "重新理解前不会把这份历史结果当作当前事实。";
                break;

            default:
                throw new InvalidOperationException($"未知 Semantic Understanding freshness：{freshness.Freshness}。");
        }

        AiStatusBar.IsOpen = true;
    }
}
