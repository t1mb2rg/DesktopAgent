using System.Text;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
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
            AiStatusBar.Title = "还没有可设计的完整桌面语义模型";
            AiStatusBar.Message = "先完成一次 fresh 桌面理解，再生成 Workspace Blueprint 预览。";
            AiStatusBar.IsOpen = true;
            return;
        }

        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        SetBusy(true);
        AiStatusBar.Severity = InfoBarSeverity.Informational;
        AiStatusBar.Title = "正在设计 Workspace Blueprint";
        AiStatusBar.Message =
            "AI 只设计多屏 Workspace 的 Zone、留白、Category→Zone 与少量例外；" +
            "本地 Compiler 再映射到 Explorer 原生槽位，并由独立 Checker 验证。当前 Writer 为 0。";
        AiStatusBar.IsOpen = true;

        try
        {
            var result = await _workspacePlanningService.PlanAsync(
                latest.Draft,
                latest.Observation,
                _conversationState,
                BuildWorkspaceSourceUserIntent(),
                cts.Token);
            if (cts.IsCancellationRequested)
            {
                ShowCancelled();
                return;
            }

            if (!result.Succeeded || result.Preview is null)
            {
                AiStatusBar.Severity = result.Status == AiDesktopWorkspacePlanningProductStatus.AiDisabled
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Error;
                AiStatusBar.Title = "Workspace Blueprint 没有形成可验证预览";
                AiStatusBar.Message = BuildWorkspacePlanningFailureMessage(result);
                AiStatusBar.IsOpen = true;
                return;
            }

            if (WorkspaceFrozenPreviewRequested is null)
                throw new InvalidOperationException("布局工作台当前没有挂载 Workspace Frozen Preview 入口。");

            WorkspaceFrozenPreviewRequested.Invoke(result.Preview);
            AiStatusBar.Severity = result.PersistenceWarning is null
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
            AiStatusBar.Title = result.PersistenceWarning is null
                ? "已生成并保存 Workspace Frozen Preview"
                : "Workspace Frozen Preview 已生成，但 Library 保存失败";
            AiStatusBar.Message =
                $"Blueprint v{result.Preview.Blueprint.Revision} · " +
                $"{result.Preview.Displays.Count} 块显示器 · " +
                $"{result.Preview.Items.Count} 个项目 · " +
                $"移动 {result.Preview.MoveCount} · 跨屏 {result.Preview.CrossDisplayAssignmentCount} · " +
                $"AI 设计尝试 {result.DesignAttempts} 次。" +
                (result.PersistenceWarning is null
                    ? "Blueprint immutable revision history 已写入 Workspace Library。"
                    : result.PersistenceWarning) +
                " 当前仍是 0 Writer。";
            AiStatusBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            ShowCancelled();
        }
        catch (Exception ex)
        {
            AiStatusBar.Severity = InfoBarSeverity.Error;
            AiStatusBar.Title = "无法生成 Workspace Blueprint 预览";
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

    private static string BuildWorkspacePlanningFailureMessage(
        AiDesktopWorkspacePlanningProductResult result)
    {
        var message = result.Message ??
            $"结果：{result.Status}。";
        var attempts = result.DesignAttempts > 0
            ? $" AI 设计尝试 {result.DesignAttempts} 次。"
            : string.Empty;

        if (result.CompileIssues.Count == 0)
        {
            return message + attempts +
                   " 没有生成 Frozen Preview，也没有调用 Writer。";
        }

        var diagnostics = result.CompileIssues
            .Take(3)
            .Select(FormatWorkspaceCompileIssue)
            .ToArray();
        return message + attempts +
               $" 本地 Compiler 最后诊断：{string.Join("；", diagnostics)}。" +
               "没有生成 Frozen Preview，也没有调用 Writer。";
    }

    private static string FormatWorkspaceCompileIssue(DesktopWorkspaceCompileIssue issue)
    {
        var location = FormatWorkspaceCompileIssueLocation(issue.DisplayRef, issue.ZoneKey);
        var detail = issue.Code switch
        {
            DesktopWorkspaceCompileIssueCode.PreserveDisplayConflict =>
                "设计要求与“保持现状”显示器发生冲突",
            DesktopWorkspaceCompileIssueCode.NonMovableItemRequiresExplicitPolicy =>
                "存在不可移动项目，Blueprint 需要明确 Keep / Exclude 策略",
            DesktopWorkspaceCompileIssueCode.MissingCategoryRule =>
                "存在没有 Category→Zone 规则的类别",
            DesktopWorkspaceCompileIssueCode.ZoneQuantizesToEmpty =>
                "分区量化后没有可用 Explorer 原生槽位",
            DesktopWorkspaceCompileIssueCode.ReservedRegionQuantizesToEmpty =>
                "保留区域量化后没有覆盖任何 Explorer 原生槽位",
            DesktopWorkspaceCompileIssueCode.ZoneRelationViolated =>
                "分区之间的设计关系在本机几何上无法保持",
            DesktopWorkspaceCompileIssueCode.ZoneOverlap =>
                "分区在本机几何上发生重叠",
            DesktopWorkspaceCompileIssueCode.ZoneCapacityInsufficient =>
                issue.RequiredSlots.HasValue && issue.AvailableSlots.HasValue
                    ? $"分区容量不足，需要 {issue.RequiredSlots.Value} 个槽位，但只有 {issue.AvailableSlots.Value} 个"
                    : "分区容量不足",
            _ => $"本地 Compiler 拒绝：{issue.Code}",
        };

        return location.Length == 0 ? detail : $"{location}：{detail}";
    }

    private static string FormatWorkspaceCompileIssueLocation(string? displayRef, string? zoneKey)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(displayRef))
            parts.Add(FormatWorkspaceDiagnosticDisplay(displayRef));
        if (!string.IsNullOrWhiteSpace(zoneKey))
            parts.Add($"分区 {zoneKey.Trim()}");
        return string.Join(" / ", parts);
    }

    private static string FormatWorkspaceDiagnosticDisplay(string displayRef)
    {
        var normalized = displayRef.Trim();
        if (string.Equals(normalized, "primary", StringComparison.OrdinalIgnoreCase))
            return "主屏";
        if (normalized.StartsWith("secondary_", StringComparison.OrdinalIgnoreCase))
            return $"副屏 {normalized["secondary_".Length..]}";
        return normalized;
    }

    /// <summary>
    /// legacy exact-cell PlanningSession 兼容路径仍保留到重构完成。
    /// 新 Workspace Blueprint 路径不调用此方法；保留它只是避免在 Phase 7-C 同时删除旧产品合同。
    /// </summary>
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
