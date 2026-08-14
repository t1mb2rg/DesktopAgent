using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private readonly SqliteDesktopLayoutSnapshotStore _directApplySnapshotStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly SqliteRecoverySessionStore _directApplyRecoveryStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);

    private DesktopLayoutDirectApplyService? _directApplyService;
    private bool _directApplyInProgress;

    private DesktopLayoutDirectApplyService DirectApplyService =>
        _directApplyService ??= new DesktopLayoutDirectApplyService(
            _directApplySnapshotStore,
            _directApplyRecoveryStore);

    private async void ApplyToDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (_directApplyInProgress)
            return;

        if (_latestPreview is null)
        {
            ApplyToDesktopButton.IsEnabled = false;
            ShowInfo(
                "请先预览",
                "“应用到桌面”只执行你刚刚看过并确认的预览。先点击“预览”读取当前桌面。",
                InfoBarSeverity.Informational);
            return;
        }

        DesktopLayoutEditDraft currentDraft;
        try
        {
            currentDraft = BuildDraftFromEditor();
        }
        catch (Exception ex)
        {
            ShowError("当前布局不可执行", ex.Message);
            return;
        }

        if (!DesktopLayoutDirectApplyPreparationService.HasSameExecutionSemantics(
                currentDraft,
                _latestPreview.FrozenDraft))
        {
            InvalidateDirectApplyEvidence("布局已变化 · 请重新预览");
            ShowInfo(
                "布局在预览后发生了变化",
                "Writer 未启动。请重新预览当前布局，再决定是否应用到桌面。",
                InfoBarSeverity.Warning);
            return;
        }

        DesktopLayoutDirectApplyPreparation preparation;
        try
        {
            preparation = DesktopLayoutDirectApplyPreparationService.CreateFromPreview(_latestPreview);
        }
        catch (Exception ex)
        {
            InvalidateDirectApplyEvidence("预览无法建立执行候选 · 请重新预览");
            ShowError("当前预览不能执行", ex.Message);
            return;
        }

        if (!preparation.IsReady || preparation.Candidate is null)
        {
            var issues = preparation.CandidateBuildResult.Issues.Count == 0
                ? "当前预览不能生成安全执行候选。"
                : string.Join("；", preparation.CandidateBuildResult.Issues.Select(issue => issue.Message));
            InvalidateDirectApplyEvidence("执行候选不可用 · 请重新预览");
            ShowInfo("当前预览不能应用", issues, InfoBarSeverity.Warning);
            return;
        }

        var candidate = preparation.Candidate;
        if (candidate.IsNoOp || candidate.MoveRequiredCount == 0)
        {
            ApplyToDesktopButton.IsEnabled = false;
            ShowInfo("已经在目标位置", "当前预览没有需要移动的桌面图标。", InfoBarSeverity.Success);
            return;
        }

        if (!candidate.IsExecutable)
        {
            ApplyToDesktopButton.IsEnabled = false;
            ShowInfo("当前计划不可执行", "安全执行候选未通过 Core 校验。请重新预览。", InfoBarSeverity.Warning);
            return;
        }

        var stagingText = candidate.UsesTemporaryStaging
            ? "是。计划包含循环，一个或多个图标会先进入临时中转位置。"
            : "否。";

        var confirmContent =
            $"布局：{currentDraft.Name}\n" +
            $"目标显示器：{candidate.TargetDeviceName}\n" +
            $"预计移动：{candidate.MoveRequiredCount} 个图标\n" +
            $"执行步骤：{candidate.ExecutionStepCount}\n" +
            $"使用临时中转：{stagingText}\n\n" +
            "确认后 Tessel 会再次读取当前桌面并重新规划。" +
            "只有 fresh Candidate 与本次预览语义完全一致，且安全闸门再次通过，才会启动 Writer。\n\n" +
            "Writer 前会创建并回读安全快照与恢复检查点；失败时可能自动回滚。";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "应用布局到真实桌面？",
            Content = new TextBlock
            {
                Text = confirmContent,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            },
            PrimaryButtonText = "应用到桌面",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            ShowInfo("已取消", "没有移动任何桌面图标。", InfoBarSeverity.Informational);
            return;
        }

        _directApplyInProgress = true;
        ApplyToDesktopButton.IsEnabled = false;
        LayoutCanvasStatusText.Text = "正在 fresh 验证并准备安全执行…";

        try
        {
            var result = await DirectApplyService.ApplyAsync(preparation);
            InvalidateDirectApplyEvidence("应用流程已结束 · 请重新预览当前桌面");
            PresentDirectApplyResult(result, candidate.MoveRequiredCount);
        }
        catch (OperationCanceledException)
        {
            InvalidateDirectApplyEvidence("应用已取消 · 请重新预览");
            ShowInfo("应用已取消", "Writer 未启动或已由安全执行层收敛。请重新预览。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            InvalidateDirectApplyEvidence("执行状态未知 · 已暂停继续应用");
            ShowError(
                "应用流程发生未预期异常",
                $"{ex.GetType().Name}: {ex.Message}。为安全起见，请先检查恢复状态，不要重复执行。" );
        }
        finally
        {
            _directApplyInProgress = false;
            ApplyToDesktopButton.IsEnabled =
                _latestPreview is not null &&
                _latestPreview.Plan.IsComplete &&
                _latestPreview.Plan.MoveRequiredCount > 0;
        }
    }

    private void PresentDirectApplyResult(
        DesktopLayoutDirectApplyResult result,
        int confirmedMoveCount)
    {
        switch (result.Outcome)
        {
            case DesktopLayoutDirectApplyOutcome.Completed:
                ShowInfo(
                    "布局已应用到桌面",
                    $"本次确认计划预计移动 {confirmedMoveCount} 个图标；安全执行与独立验证已完成。",
                    InfoBarSeverity.Success);
                return;

            case DesktopLayoutDirectApplyOutcome.CompletedNoOp:
                ShowInfo(
                    "桌面已经就位",
                    "进入最终执行层时已无需移动，Writer 没有产生额外位移。",
                    InfoBarSeverity.Success);
                return;

            case DesktopLayoutDirectApplyOutcome.UnresolvedRecoveryBlocked:
                ShowInfo(
                    "检测到未完成恢复",
                    IssueText(result, "上一次桌面操作尚未完成恢复。新的真实写入已被安全阻断。"),
                    InfoBarSeverity.Error);
                return;

            case DesktopLayoutDirectApplyOutcome.PreviewChanged:
            case DesktopLayoutDirectApplyOutcome.AuthorizationTypeChanged:
                ShowInfo(
                    "桌面状态在确认后发生变化",
                    IssueText(result, "Writer 未启动。请重新预览并重新确认。"),
                    InfoBarSeverity.Warning);
                return;

            case DesktopLayoutDirectApplyOutcome.FreshObservationUnavailable:
            case DesktopLayoutDirectApplyOutcome.FreshCandidateRejected:
            case DesktopLayoutDirectApplyOutcome.ScopeRejected:
            case DesktopLayoutDirectApplyOutcome.PreparationNotReady:
                ShowInfo(
                    "安全检查拒绝执行",
                    IssueText(result, "Writer 未启动。请重新预览后再试。"),
                    InfoBarSeverity.Warning);
                return;

            case DesktopLayoutDirectApplyOutcome.ExecutionFailed:
                if (result.RequiresManualRecovery || result.IsPositionUncertain)
                {
                    ShowInfo(
                        "执行未安全收敛，需要处理恢复状态",
                        IssueText(result, "Tessel 已阻断继续写入。不要重复应用布局。"),
                        InfoBarSeverity.Error);
                    return;
                }

                var execution = result.ExecutionResult;
                if (execution?.Status == DesktopAgent.Core.DesktopLayouts.Restore.Execution.TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack)
                {
                    ShowInfo(
                        "应用失败，但已回滚",
                        IssueText(result, "桌面已执行安全回滚。请重新预览。"),
                        InfoBarSeverity.Warning);
                    return;
                }

                ShowInfo(
                    "布局未应用",
                    IssueText(result, "执行层拒绝或失败；请重新预览。"),
                    InfoBarSeverity.Warning);
                return;

            default:
                ShowInfo(
                    "布局未应用",
                    IssueText(result, $"结果：{result.Outcome}。请重新预览。"),
                    InfoBarSeverity.Warning);
                return;
        }
    }

    private void InvalidateDirectApplyEvidence(string status)
    {
        _latestPreview = null;
        _previewItems.Clear();
        ApplyToDesktopButton.IsEnabled = false;
        LayoutPreviewDetails.Visibility = Visibility.Collapsed;
        ResetPreviewSummary();
        LayoutCanvasStatusText.Text = status;
        RenderLayoutCanvas();
    }

    private static string IssueText(DesktopLayoutDirectApplyResult result, string fallback)
        => result.Issues.Count == 0
            ? fallback
            : $"{fallback} {string.Join("；", result.Issues.Take(3))}";
}
