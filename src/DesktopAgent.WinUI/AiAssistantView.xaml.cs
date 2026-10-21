using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace DesktopAgent.WinUI;

public sealed partial class AiAssistantView : UserControl
{
    private readonly AiDesktopUnderstandingService _understandingService = new();
    private readonly AiDesktopLayoutConversationService _layoutConversationService = new();
    private readonly AiDesktopLogicalWorkspacePlanningService _logicalWorkspacePlanningService = new();
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _probeCts;
    private AiDesktopUnderstandingProductResult? _latestSuccessfulUnderstanding
    {
        get => _workspaceSession.LatestUnderstanding;
        set => _workspaceSession.LatestUnderstanding = value;
    }
    private DesktopLayoutConversationState _conversationState =>
        DesktopWorkspaceConversationIntentAdapter.ToLegacy(_workspaceSession.Session.Snapshot().State.Intent);

    internal event Action<DesktopUnderstandingLayoutDraftCompiler.CompilationResult>?
        LayoutDraftImportRequested;

    internal event Action<DesktopLogicalWorkspacePlanningSession>?
        LogicalWorkspacePreviewRequested;

    internal bool IsRequestActive => _analysisCts is not null || _probeCts is not null;

    public AiAssistantView() : this(new ProductWorkspaceSession()) { }

    internal AiAssistantView(ProductWorkspaceSession workspaceSession)
    {
        _workspaceSession = workspaceSession;
        InitializeComponent();
        Unloaded += AiAssistantView_Unloaded;
    }

    internal void CancelActiveRequest()
    {
        _analysisCts?.Cancel();
        _probeCts?.Cancel();
    }

    private async void TestAiConnection_Click(object sender, RoutedEventArgs e)
    {
        if (IsRequestActive)
            return;

        AiStatusBar.IsOpen = false;
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        SetBusy(true);

        try
        {
            var result = await _understandingService.ProbeAsync(cts.Token);
            if (cts.IsCancellationRequested)
            {
                ShowCancelled();
                return;
            }

            if (result.Succeeded)
            {
                AiStatusBar.Severity = InfoBarSeverity.Success;
                AiStatusBar.Title = "AI 连接正常";
                AiStatusBar.Message = string.IsNullOrWhiteSpace(result.Category)
                    ? "Provider 已返回有效结构化结果。连接测试不会读取真实桌面，也不会调用 Writer。"
                    : $"Provider 已返回有效结构化结果（探针分类：{result.Category}）。连接测试不会读取真实桌面，也不会调用 Writer。";
                AiStatusBar.IsOpen = true;
                return;
            }

            AiStatusBar.Severity = result.Status == IntelligenceProviderStatus.Cancelled
                ? InfoBarSeverity.Informational
                : InfoBarSeverity.Error;
            AiStatusBar.Title = "AI 连接测试失败";
            AiStatusBar.Message = FormatProviderFailure(
                result.Status,
                result.HttpStatusCode,
                result.Message);
            AiStatusBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            ShowCancelled();
        }
        catch (Exception ex)
        {
            AiStatusBar.Severity = InfoBarSeverity.Error;
            AiStatusBar.Title = "AI 连接测试没有完成";
            AiStatusBar.Message = ex.Message;
            AiStatusBar.IsOpen = true;
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts))
                _probeCts = null;
            cts.Dispose();
            SetBusy(false);
        }
    }

    private void AiGuidanceTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.Enter)
            return;

        // Shift+Enter 保留为换行；输入法正在组合时也不拦截，让 IME 自行上屏。
        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
            return;

        e.Handled = true;
        AnalyzeDesktop_Click(AiAnalyzeButton, new RoutedEventArgs());
    }

    // TextBox 内部会先行消费 Enter，所以真正的拦截必须走隧道的 PreviewKeyDown。
    private void AiGuidanceTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        => AiGuidanceTextBox_KeyDown(sender, e);

    private async void AnalyzeDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (IsRequestActive)
            return;

        var guidanceText = AiGuidanceTextBox.Text.Trim();
        if (guidanceText.Length == 0)
        {
            AiStatusBar.Severity = InfoBarSeverity.Informational;
            AiStatusBar.Title = "写一句你想怎么调整";
            AiStatusBar.Message = "桌面语义模型已经建立。现在只需要描述布局修正，例如“大多数放副屏，主屏中间留空看壁纸”。";
            AiStatusBar.IsOpen = true;
            return;
        }

        await SendWorkspaceChatAsync(guidanceText);
    }

    private async void RefreshUnderstanding_Click(object sender, RoutedEventArgs e)
    {
        if (IsRequestActive)
            return;

        await _workspaceSession.EnsureUnderstandingAsync(force: true);
        RenderSessionState();
    }

    private async Task RunFullUnderstandingAsync(string? guidance)
    {
        AiStatusBar.Severity = InfoBarSeverity.Informational;
        AiStatusBar.Title = _latestSuccessfulUnderstanding is null
            ? "正在建立桌面语义模型"
            : "正在重新理解当前桌面";
        AiStatusBar.Message = "这一步会 fresh 读取并分析全部桌面项目，可能需要几分钟；不会调用 Writer。完成后，后续布局修正会恢复为轻量 Delta 对话。";
        AiStatusBar.IsOpen = true;

        var previousSummary = _conversationState.IntentSummary.Length > 0
            ? _conversationState.IntentSummary
            : _latestSuccessfulUnderstanding?.Draft?.IntentSummary;
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        SetBusy(true);

        try
        {
            var result = await _understandingService.AnalyzeAsync(
                previousSummary,
                guidance,
                cts.Token);

            if (cts.IsCancellationRequested)
            {
                ShowCancelled();
                return;
            }

            if (result.Succeeded && result.Draft is not null && result.Observation is not null)
            {
                _latestSuccessfulUnderstanding = result;
                _persistedUnderstandingState = null;
                RememberWorkspaceUserIntent(guidance);
                _workspaceSession.Session.AppendMessage(DesktopWorkspaceMessageRole.Assistant, result.Draft.DesktopSummary);
                RenderDraft(result);
                AiGuidanceTextBox.Text = string.Empty;
                AiAnalyzeButton.Content = "应用这条修正";
                AiRefreshUnderstandingButton.Visibility = Visibility.Visible;

                if (string.IsNullOrWhiteSpace(result.PersistenceWarning))
                {
                    AiStatusBar.Severity = InfoBarSeverity.Success;
                    AiStatusBar.Title = "桌面语义模型已建立";
                    AiStatusBar.Message = $"已完整理解 {result.ObservedItemCount} 个项目，并保存为长期语义记忆。之后的对话不会重新跑这批项目，除非你明确点击“重新理解桌面”。";
                }
                else
                {
                    AiStatusBar.Severity = InfoBarSeverity.Warning;
                    AiStatusBar.Title = "桌面语义模型已建立，但长期记忆未保存";
                    AiStatusBar.Message = $"已完整理解 {result.ObservedItemCount} 个项目，本次会话可以继续使用；{result.PersistenceWarning}";
                }

                AiStatusBar.IsOpen = true;
                return;
            }

            ShowFailure(result);
        }
        catch (OperationCanceledException)
        {
            ShowCancelled();
        }
        catch (Exception ex)
        {
            AiStatusBar.Severity = InfoBarSeverity.Error;
            AiStatusBar.Title = "AI 完整理解没有完成";
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

    private async Task RunLayoutConversationAsync(string guidance)
    {
        var latest = _latestSuccessfulUnderstanding;
        if (latest?.Draft is null || latest.Observation is null)
            throw new InvalidOperationException("轻量布局对话需要先建立完整桌面语义模型。");

        AiStatusBar.Severity = InfoBarSeverity.Informational;
        AiStatusBar.Title = "正在应用布局修正";
        AiStatusBar.Message = "这轮不会重新读取或重新分类全部桌面，只让 AI 对已有语义模型返回一个 Layout Delta。";
        AiStatusBar.IsOpen = true;

        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        SetBusy(true);

        try
        {
            var baseRevision = _workspaceSession.Session.Snapshot().Revision;
            var request = DesktopLayoutConversationContextBuilder.Build(
                latest.Draft,
                latest.Observation,
                _conversationState,
                guidance);
            var result = await _layoutConversationService.PlanAsync(request, cts.Token);

            if (cts.IsCancellationRequested)
            {
                ShowCancelled();
                return;
            }

            if (!result.Succeeded || result.Delta is null)
            {
                AiStatusBar.Severity = result.Status == IntelligenceProviderStatus.Cancelled
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Error;
                AiStatusBar.Title = "这条布局修正没有完成";
                AiStatusBar.Message = FormatProviderFailure(
                    result.Status,
                    result.HttpStatusCode,
                    result.Message);
                AiStatusBar.IsOpen = true;
                return;
            }

            var edit = _workspaceSession.Session.TryApply(baseRevision,
                [new ReplaceWorkspaceIntent(DesktopWorkspaceConversationIntentAdapter.FromLegacy(_conversationState.Apply(result.Delta)))]);
            if (!edit.Applied)
            {
                AiStatusBar.Title = "设计已变化";
                AiStatusBar.Message = edit.Message ?? "本次修正没有覆盖当前设计，请按最新设计重新提出要求。";
                AiStatusBar.IsOpen = true;
                return;
            }
            RememberWorkspaceUserIntent(guidance);
            _workspaceSession.Session.AppendMessage(DesktopWorkspaceMessageRole.Assistant, BuildDeltaSummary(result.Delta));
            RenderConversationState();
            AiGuidanceTextBox.Text = string.Empty;
            AiStatusBar.Severity = InfoBarSeverity.Success;
            AiStatusBar.Title = "布局修正已记住";
            AiStatusBar.Message = BuildDeltaSummary(result.Delta);
            AiStatusBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            ShowCancelled();
        }
        catch (Exception ex)
        {
            AiStatusBar.Severity = InfoBarSeverity.Error;
            AiStatusBar.Title = "无法应用这条布局修正";
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

    private void ImportLayoutDraft_Click(object sender, RoutedEventArgs e)
    {
        if (IsRequestActive)
            return;

        var understanding = _latestSuccessfulUnderstanding?.Draft;
        if (understanding is null)
        {
            AiStatusBar.Severity = InfoBarSeverity.Warning;
            AiStatusBar.Title = "还没有可导入的 AI 草案";
            AiStatusBar.Message = "先完成一次桌面理解，再导入布局工作台。";
            AiStatusBar.IsOpen = true;
            return;
        }

        try
        {
            var compiled = DesktopUnderstandingLayoutDraftCompiler.Compile(understanding);
            if (LayoutDraftImportRequested is null)
                throw new InvalidOperationException("布局工作台当前没有挂载 AI 草案导入入口。");

            LayoutDraftImportRequested.Invoke(compiled);
        }
        catch (Exception ex)
        {
            AiStatusBar.Severity = InfoBarSeverity.Error;
            AiStatusBar.Title = "无法导入布局草案";
            AiStatusBar.Message = ex.Message;
            AiStatusBar.IsOpen = true;
        }
    }

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e)
        => CancelActiveRequest();

    private void AiAssistantView_Unloaded(object sender, RoutedEventArgs e)
    {
        _sharedDesignCanvas?.CancelDrag();
        CancelActiveRequest();
        _workspaceSession.Changed -= WorkspaceSession_Changed;
        _sessionSubscribed = false;
    }

    private void SetBusy(bool busy)
    {
        AiDirectSaveButton.IsEnabled = !busy;
        AiDirectPreviewButton.IsEnabled = !busy;
        foreach (var row in AiChatActionsPanel.Children.OfType<StackPanel>())
            foreach (var button in row.Children.OfType<Button>()) button.IsEnabled = !busy;
        AiProbeButton.IsEnabled = !busy;
        AiRefreshUnderstandingButton.IsEnabled = !busy;
        AiAnalyzeButton.IsEnabled = !busy;
        AiImportLayoutButton.IsEnabled = !busy && _latestSuccessfulUnderstanding?.Draft is not null;
        AiGuidanceTextBox.IsEnabled = !busy;
        AiCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AiProgressRing.IsActive = busy;
        AiProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AiAnalyzeButton.Content = "发送";
    }

    private void RenderDraft(AiDesktopUnderstandingProductResult result)
    {
        var draft = result.Draft!;
        AiResultCard.Visibility = Visibility.Collapsed;
        AiDesktopSummaryText.Text = draft.DesktopSummary;
        AiResultMetaText.Text = result.ActiveLayoutName is null
            ? $"桌面模型 · {result.ObservedItemCount} 个项目 · 当前未使用布局作为 Zone 上下文"
            : $"桌面模型 · {result.ObservedItemCount} 个项目 · Zone 上下文来自“{result.ActiveLayoutName}”";
        RenderConversationState();

        AiQuestionsPanel.Children.Clear();
        if (draft.Questions.Count == 0)
        {
            AiQuestionsPanel.Children.Add(CreateSecondaryText("暂时没有需要你确认的问题。"));
        }
        else
        {
            foreach (var question in draft.Questions)
                AiQuestionsPanel.Children.Add(CreateSecondaryText($"• {question.Text}"));
        }
    }

    private void RenderConversationState()
    {
        var draft = _latestSuccessfulUnderstanding?.Draft;
        if (draft is null)
            return;

        AiIntentSummaryText.Text = _conversationState.IntentSummary.Length == 0
            ? draft.IntentSummary
            : _conversationState.IntentSummary;

        AiCategoriesPanel.Children.Clear();
        foreach (var category in draft.Categories)
        {
            var assignmentCount = draft.Assignments.Count(assignment =>
                string.Equals(assignment.CategoryKey, category.Key, StringComparison.Ordinal));
            var targetText = _conversationState.CategoryDisplayTargets.TryGetValue(category.Key, out var displayRef)
                ? $" → {FormatDisplayRef(displayRef)}"
                : string.Empty;
            AiCategoriesPanel.Children.Add(CreateResultItem(
                $"{category.Name} · {assignmentCount} 项{targetText}",
                category.Description));
        }

        if (_conversationState.DisplayLoadPreferences.Count > 0)
        {
            var densityText = string.Join(
                " · ",
                _conversationState.DisplayLoadPreferences
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{FormatDisplayRef(pair.Key)} {FormatLoad(pair.Value)}"));
            AiCategoriesPanel.Children.Add(CreateSecondaryText($"屏幕负载偏好：{densityText}"));
        }

        AiZonesPanel.Children.Clear();
        if (draft.Zones.Count == 0)
        {
            AiZonesPanel.Children.Add(CreateSecondaryText("AI 暂未建议新的逻辑区域。"));
        }
        else
        {
            foreach (var zone in draft.Zones)
            {
                var assignmentCount = draft.Assignments.Count(assignment =>
                    string.Equals(assignment.ZoneKey, zone.Key, StringComparison.Ordinal));
                var reuseText = zone.ExistingZoneId.HasValue ? " · 复用现有 Zone" : string.Empty;
                AiZonesPanel.Children.Add(CreateResultItem(
                    $"{zone.Name} · {FormatPlacement(zone.PlacementHint)} / {FormatSize(zone.SizeHint)}",
                    $"{assignmentCount} 个项目建议进入此区域{reuseText}"));
            }
        }

        foreach (var reserved in _conversationState.ReservedRegions)
        {
            AiZonesPanel.Children.Add(CreateResultItem(
                $"保留空白 · {FormatDisplayRef(reserved.DisplayRef)} · {FormatPlacement(reserved.PlacementHint)} / {FormatSize(reserved.SizeHint)}",
                reserved.Purpose));
        }
    }

    private void RememberWorkspaceUserIntent(string? guidance)
    {
        if (string.IsNullOrWhiteSpace(guidance))
            return;

        var trimmed = guidance.Trim();
        var messages = _workspaceSession.Session.Snapshot().Messages;
        if (messages.LastOrDefault(message => message.Role == DesktopWorkspaceMessageRole.User)?.Text == trimmed)
        {
            return;
        }

        _workspaceSession.Session.AppendMessage(DesktopWorkspaceMessageRole.User, trimmed);
    }

    private string? BuildWorkspaceSourceUserIntent()
    {
        var messages = _workspaceSession.Session.Snapshot().Messages
            .Where(message => message.Role == DesktopWorkspaceMessageRole.User).Select(message => message.Text).ToArray();
        return messages.Length == 0 ? null : string.Join(Environment.NewLine, messages);
    }

    private static string BuildDeltaSummary(DesktopLayoutConversationDelta delta)
    {
        var changes = delta.CategoryDisplayTargets.Count
            + delta.ItemDisplayTargets.Count
            + delta.DisplayLoadPreferences.Count
            + delta.UpsertReservedRegions.Count
            + delta.RemoveCategoryDisplayTargetKeys.Count
            + delta.RemoveItemDisplayTargetStableIds.Count
            + delta.RemoveDisplayLoadPreferenceRefs.Count
            + delta.RemoveReservedRegionKeys.Count;
        return changes == 0
            ? "AI 更新了当前布局意图摘要，但没有要求改变屏幕分配或空间约束。没有读取桌面，也没有调用 Writer。"
            : $"已记录 {changes} 项布局变化。没有重新扫描全部桌面，也没有调用 Writer；导入工作台后仍需预览和确认。";
    }

    private void ShowFailure(AiDesktopUnderstandingProductResult result)
    {
        switch (result.Status)
        {
            case AiDesktopUnderstandingProductStatus.AiDisabled:
                AiStatusBar.Severity = InfoBarSeverity.Warning;
                AiStatusBar.Title = "AI 尚未启用";
                AiStatusBar.Message = result.Message ?? "请先在设置中配置并启用 AI Provider。";
                break;

            case AiDesktopUnderstandingProductStatus.DesktopUnavailable:
                AiStatusBar.Severity = InfoBarSeverity.Error;
                AiStatusBar.Title = "无法建立 fresh 桌面观察";
                AiStatusBar.Message = result.Message ?? "当前桌面暂时不可读取。";
                break;

            case AiDesktopUnderstandingProductStatus.ProviderFailure:
                AiStatusBar.Severity = result.ProviderStatus == IntelligenceProviderStatus.Cancelled
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Error;
                AiStatusBar.Title = "AI Provider 没有返回可用结果";
                AiStatusBar.Message = result.ProviderStatus == IntelligenceProviderStatus.Timeout
                    ? "完整桌面理解超过 5 分钟仍未完成，已安全停止。可以稍后重试或检查 Provider 服务状态。"
                    : FormatProviderFailure(
                        result.ProviderStatus,
                        result.HttpStatusCode,
                        result.Message);
                break;

            default:
                AiStatusBar.Severity = InfoBarSeverity.Error;
                AiStatusBar.Title = "AI 分析没有完成";
                AiStatusBar.Message = "发生了未识别的产品接入错误。";
                break;
        }

        AiStatusBar.IsOpen = true;
    }

    private void ShowCancelled()
    {
        AiStatusBar.Severity = InfoBarSeverity.Informational;
        AiStatusBar.Title = "已取消 AI 请求";
        AiStatusBar.Message = "当前请求已停止，没有产生桌面副作用。";
        AiStatusBar.IsOpen = true;
    }

    private static StackPanel CreateResultItem(string title, string detail)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Opacity = 0.58,
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static TextBlock CreateSecondaryText(string text)
        => new()
        {
            Text = text,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        };

    private static string FormatProviderFailure(
        IntelligenceProviderStatus? status,
        int? httpStatusCode,
        string? diagnosticMessage = null)
    {
        string summary;
        if (httpStatusCode is 401 or 403)
        {
            summary = $"Provider 拒绝了认证（HTTP {httpStatusCode}）。请检查设置中的 API Key。";
        }
        else if (httpStatusCode.HasValue)
        {
            summary = $"Provider 请求失败（HTTP {httpStatusCode}，{status?.ToString() ?? "Unknown"}）。请检查 Base URI、Model 与 Provider 状态。";
        }
        else
        {
            summary = status switch
            {
                IntelligenceProviderStatus.NotConfigured => "Provider 配置或 Secret 不完整，请检查 AI 设置。",
                IntelligenceProviderStatus.Timeout => "Provider 请求超时，可以稍后重试或检查网络与服务状态。",
                IntelligenceProviderStatus.Cancelled => "当前请求已取消。",
                IntelligenceProviderStatus.InvalidResponse => "Provider 返回了不符合 Tessel 语义合同的结果，已安全拒绝。",
                IntelligenceProviderStatus.Unavailable => "Provider 当前不可用，请检查网络、Base URI 与服务状态。",
                _ => "Provider 当前没有返回可用结果。",
            };
        }

        if (status != IntelligenceProviderStatus.InvalidResponse ||
            string.IsNullOrWhiteSpace(diagnosticMessage))
        {
            return summary;
        }

        return $"{summary}\n技术诊断：{diagnosticMessage}";
    }

    private static string FormatPlacement(DesktopZonePlacementHint hint)
        => hint switch
        {
            DesktopZonePlacementHint.Left => "左侧",
            DesktopZonePlacementHint.Right => "右侧",
            DesktopZonePlacementHint.Top => "上方",
            DesktopZonePlacementHint.Bottom => "下方",
            DesktopZonePlacementHint.Center => "中央",
            DesktopZonePlacementHint.TopLeft => "左上",
            DesktopZonePlacementHint.TopRight => "右上",
            DesktopZonePlacementHint.BottomLeft => "左下",
            DesktopZonePlacementHint.BottomRight => "右下",
            _ => "位置未指定",
        };

    private static string FormatSize(DesktopZoneSizeHint hint)
        => hint switch
        {
            DesktopZoneSizeHint.Compact => "紧凑",
            DesktopZoneSizeHint.Medium => "中等",
            DesktopZoneSizeHint.Large => "较大",
            _ => "弹性",
        };

    private static string FormatDisplayRef(string displayRef)
        => displayRef switch
        {
            "primary" => "主屏",
            _ when displayRef.StartsWith("secondary_", StringComparison.Ordinal) =>
                $"副屏 {displayRef["secondary_".Length..]}",
            _ => displayRef,
        };

    private static string FormatLoad(DesktopDisplayLoadPreference load)
        => load switch
        {
            DesktopDisplayLoadPreference.Low => "低负载",
            DesktopDisplayLoadPreference.Medium => "中负载",
            DesktopDisplayLoadPreference.High => "高负载",
            _ => "未指定",
        };
}
