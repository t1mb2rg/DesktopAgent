using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.Intelligence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class AiAssistantView : UserControl
{
    private readonly AiDesktopUnderstandingService _understandingService = new();
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _probeCts;
    private string? _currentIntentSummary;
    private AiDesktopUnderstandingProductResult? _latestSuccessfulUnderstanding;

    internal event Action<DesktopUnderstandingLayoutDraftCompiler.CompilationResult>?
        LayoutDraftImportRequested;

    internal bool IsRequestActive => _analysisCts is not null || _probeCts is not null;

    public AiAssistantView()
    {
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

    private async void AnalyzeDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (IsRequestActive)
            return;

        AiStatusBar.Severity = InfoBarSeverity.Informational;
        AiStatusBar.Title = "正在完整理解当前桌面";
        AiStatusBar.Message = "Tessel 会分析本次 fresh 观察到的全部桌面项目。项目较多时可能需要几分钟，你可以随时取消；期间不会调用 Writer。";
        AiStatusBar.IsOpen = true;

        var guidanceText = AiGuidanceTextBox.Text.Trim();
        var guidance = guidanceText.Length == 0 ? null : guidanceText;
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        SetBusy(true);

        try
        {
            var result = await _understandingService.AnalyzeAsync(
                _currentIntentSummary,
                guidance,
                cts.Token);

            if (cts.IsCancellationRequested)
            {
                ShowCancelled();
                return;
            }

            if (result.Succeeded && result.Draft is not null)
            {
                _latestSuccessfulUnderstanding = result;
                RenderDraft(result);
                _currentIntentSummary = result.Draft.IntentSummary;
                AiGuidanceTextBox.Text = string.Empty;
                AiAnalyzeButton.Content = "根据修正重新分析";
                AiStatusBar.Severity = InfoBarSeverity.Success;
                AiStatusBar.Title = "AI 已完成桌面理解";
                AiStatusBar.Message = "结果仍是语义草案，没有调用 Writer，也没有移动任何图标。";
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
            AiStatusBar.Title = "AI 分析没有完成";
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
        => CancelActiveRequest();

    private void SetBusy(bool busy)
    {
        AiProbeButton.IsEnabled = !busy;
        AiAnalyzeButton.IsEnabled = !busy;
        AiImportLayoutButton.IsEnabled = !busy && _latestSuccessfulUnderstanding?.Draft is not null;
        AiGuidanceTextBox.IsEnabled = !busy;
        AiCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AiProgressRing.IsActive = busy;
        AiProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderDraft(AiDesktopUnderstandingProductResult result)
    {
        var draft = result.Draft!;
        AiResultCard.Visibility = Visibility.Visible;
        AiDesktopSummaryText.Text = draft.DesktopSummary;
        AiIntentSummaryText.Text = draft.IntentSummary;
        AiResultMetaText.Text = result.ActiveLayoutName is null
            ? $"Fresh 观察并分析了 {result.ObservedItemCount} 个桌面项目 · 当前未使用布局作为 Zone 上下文"
            : $"Fresh 观察并分析了 {result.ObservedItemCount} 个桌面项目 · Zone 上下文来自“{result.ActiveLayoutName}”";

        AiCategoriesPanel.Children.Clear();
        foreach (var category in draft.Categories)
        {
            var assignmentCount = draft.Assignments.Count(assignment =>
                string.Equals(assignment.CategoryKey, category.Key, StringComparison.Ordinal));
            AiCategoriesPanel.Children.Add(CreateResultItem(
                $"{category.Name} · {assignmentCount} 项",
                category.Description));
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
}
