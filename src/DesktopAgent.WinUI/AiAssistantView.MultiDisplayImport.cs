using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class AiAssistantView
{
    private async void ImportMultiDisplayLayoutDraft_Click(object sender, RoutedEventArgs e)
    {
        if (IsRequestActive)
            return;

        var latest = _latestSuccessfulUnderstanding;
        if (latest?.Draft is null || latest.Observation is null)
        {
            AiStatusBar.Severity = InfoBarSeverity.Warning;
            AiStatusBar.Title = "还没有可导入的完整 AI 草案";
            AiStatusBar.Message = "先完成一次 fresh 桌面理解，再导入多显示器布局工作区。";
            AiStatusBar.IsOpen = true;
            return;
        }

        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        SetBusy(true);
        AiStatusBar.Severity = InfoBarSeverity.Informational;
        AiStatusBar.Title = "正在生成精确逻辑终态";
        AiStatusBar.Message =
            "会 fresh 重读一次桌面，并让 AI 在只读逻辑网格上给出每个项目的最终格位。" +
            "本地严格校验通过后才会进入预览；Writer 不会启动。";
        AiStatusBar.IsOpen = true;

        try
        {
            var result = await _logicalWorkspacePlanningService.PlanAsync(
                BuildLogicalWorkspacePlanningGuidance(latest.Draft),
                cts.Token);
            if (cts.IsCancellationRequested)
            {
                ShowCancelled();
                return;
            }
            if (!result.Succeeded || result.Session is null)
            {
                AiStatusBar.Severity = result.Status == AiDesktopLogicalWorkspacePlanningProductStatus.AiDisabled
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Error;
                AiStatusBar.Title = "精确逻辑终态没有通过安全边界";
                AiStatusBar.Message = result.Message ??
                    $"结果：{result.Status}。没有生成 Preview，也没有调用 Writer。";
                AiStatusBar.IsOpen = true;
                return;
            }
            if (LogicalWorkspacePreviewRequested is null)
                throw new InvalidOperationException("布局工作台当前没有挂载逻辑工作区预览入口。");

            LogicalWorkspacePreviewRequested.Invoke(result.Session);
            AiStatusBar.Severity = InfoBarSeverity.Success;
            AiStatusBar.Title = "已生成只读逻辑工作区预览";
            AiStatusBar.Message =
                $"{result.Session.FrozenPreview.Displays.Count} 块显示器、" +
                $"{result.Session.FrozenPreview.Items.Count} 个项目、" +
                $"{result.Session.FrozenPreview.CrossDisplayAssignmentCount} 个跨屏终点。" +
                "当前仍是 0 Writer。";
            AiStatusBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            ShowCancelled();
        }
        catch (Exception ex)
        {
            AiStatusBar.Severity = InfoBarSeverity.Error;
            AiStatusBar.Title = "无法导入多显示器布局草案";
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

    private string BuildLogicalWorkspacePlanningGuidance(
        DesktopAgent.Core.Intelligence.DesktopUnderstandingDraft understanding)
    {
        var builder = new StringBuilder();
        builder.AppendLine(_conversationState.IntentSummary.Length > 0
            ? _conversationState.IntentSummary
            : understanding.IntentSummary);

        foreach (var target in _conversationState.CategoryDisplayTargets
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"类别 {target.Key} 的目标屏为 {target.Value}。");
        }
        foreach (var target in _conversationState.ItemDisplayTargets
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"项目 {target.Key} 的目标屏为 {target.Value}。");
        }
        foreach (var load in _conversationState.DisplayLoadPreferences
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"显示器 {load.Key} 的负载偏好为 {load.Value}。");
        }
        foreach (var reserved in _conversationState.ReservedRegions
                     .OrderBy(static region => region.Key, StringComparer.Ordinal))
        {
            builder.AppendLine(
                $"显示器 {reserved.DisplayRef} 保留区域 {reserved.Key}：" +
                $"{reserved.PlacementHint} / {reserved.SizeHint}，用途：{reserved.Purpose}。");
        }

        return builder.ToString().Trim();
    }
}
