using System.Text;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private DesktopWorkspaceRecoveryProductService? _workspaceRecoveryProductService;

    private DesktopWorkspaceRecoveryProductService WorkspaceRecoveryProductService =>
        _workspaceRecoveryProductService ??= new DesktopWorkspaceRecoveryProductService(
            _workspaceExecutionJournalStore);

    private async Task PresentWorkspaceRecoveryInspectorAsync(
        DesktopWorkspaceExecutionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var message = BuildWorkspaceRecoveryEvidenceText(journal);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "检测到 Workspace 恢复现场",
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
            },
            PrimaryButtonText = "接受当前桌面并结束会话",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            ShowInfo(
                "Workspace 恢复锁保持不变",
                "没有启动 Writer，也没有修改 execution journal。新的布局应用仍然被阻断。",
                InfoBarSeverity.Informational);
            return;
        }

        SetRecoveryUiBusy(true, "正在记录你对当前 Workspace 现场的明确裁决…");
        try
        {
            var result = await WorkspaceRecoveryProductService.AcceptCurrentStateSingleAsync();
            DisableWorkspaceFrozenPreviewApplyEvidence();
            InvalidateDirectApplyEvidence("Workspace 恢复现场已裁决 · 必须重新理解/预览当前桌面");

            switch (result.Outcome)
            {
                case DesktopWorkspaceRecoveryProductOutcome.AcceptedCurrentState:
                    ShowInfo(
                        "已接受当前桌面",
                        "没有执行任何恢复 Writer。这次 Workspace Apply 仍然保留为 NeedsAttention 历史，" +
                        "Safety Snapshot、Execution Plan 和 Writer Attempts 均未删除；只是你明确接受当前桌面作为新的现实基线，execution lock 已解除。",
                        InfoBarSeverity.Success);
                    return;

                case DesktopWorkspaceRecoveryProductOutcome.NoPendingRecovery:
                    ShowInfo(
                        "恢复现场已经不存在",
                        "没有执行 Writer。请重新读取当前桌面后再生成新的 Workspace Preview。",
                        InfoBarSeverity.Informational);
                    return;

                case DesktopWorkspaceRecoveryProductOutcome.MultiplePendingRecoveries:
                    ShowInfo(
                        "仍有多笔 Workspace 恢复现场",
                        $"当前检测到 {result.PendingCount} 笔 unresolved journal，Tessel 不猜测裁决顺序。",
                        InfoBarSeverity.Error);
                    return;

                default:
                    ShowInfo(
                        "当前 Workspace 现场不能这样结束",
                        result.Message ?? "execution lock 保持不变。",
                        InfoBarSeverity.Error);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            ShowInfo("操作已取消", "没有修改 Workspace execution journal。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowError(
                "无法结束 Workspace 恢复现场",
                $"{ex.GetType().Name}: {ex.Message}。execution lock 保持不变。" );
        }
        finally
        {
            SetRecoveryUiBusy(false, null);
        }
    }

    private static string BuildWorkspaceRecoveryEvidenceText(
        DesktopWorkspaceExecutionJournal journal)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"执行会话：{journal.SessionId:D}");
        builder.AppendLine($"状态：{journal.Lifecycle}");
        builder.AppendLine(
            $"已精确完成：{journal.CompletedForwardStepIndexes.Count} / {journal.ExecutionPlan.Steps.Count} 步");
        builder.AppendLine($"Safety Snapshot：{journal.SafetySnapshotId:D}");

        if (journal.LastFailedStepIndex is int failedStepIndex &&
            failedStepIndex >= 0 &&
            failedStepIndex < journal.ExecutionPlan.Steps.Count)
        {
            var step = journal.ExecutionPlan.Steps[failedStepIndex];
            builder.AppendLine();
            builder.AppendLine($"失败 Step：#{failedStepIndex} · {step.Kind}");
            builder.AppendLine(
                $"冻结 From：{step.FromDisplayRef} ({step.FromPixelPosition.X},{step.FromPixelPosition.Y})");
            builder.AppendLine(
                $"冻结 To：{step.ToDisplayRef} ({step.ToPixelPosition.X},{step.ToPixelPosition.Y})");

            var evidence = journal.ForwardAttempts
                .Where(attempt => attempt.SourceStepIndex == failedStepIndex)
                .OrderByDescending(static attempt => attempt.AttemptIndex)
                .FirstOrDefault();
            if (evidence is not null)
            {
                builder.AppendLine($"Writer 结果：{evidence.Outcome}");
                builder.AppendLine(
                    "Writer Previous：" + FormatOptionalPoint(evidence.ReportedPreviousPixelPosition));
                builder.AppendLine(
                    "Writer Requested：" +
                    $"({evidence.RequestedPixelPosition.X},{evidence.RequestedPixelPosition.Y})");
                builder.AppendLine(
                    "Writer Actual：" + FormatOptionalPoint(evidence.ActualPixelPosition));
            }
        }

        if (!string.IsNullOrWhiteSpace(journal.ForwardFailureType) ||
            !string.IsNullOrWhiteSpace(journal.ForwardFailureMessage))
        {
            builder.AppendLine();
            builder.AppendLine(
                $"失败诊断：{journal.ForwardFailureType ?? "Unknown"} · " +
                $"{journal.ForwardFailureMessage ?? "没有额外消息"}");
        }

        builder.AppendLine();
        builder.AppendLine(
            "“接受当前桌面并结束会话”不会移动任何图标，也不会把这次 Apply 标记为成功或假装已恢复到 Safety Snapshot。" );
        builder.AppendLine(
            "它只记录你的明确裁决：接受现在看到的桌面作为新的现实基线，并解除这笔 execution lock。所有失败证据继续保留。" );
        return builder.ToString().TrimEnd();
    }

    private static string FormatOptionalPoint(DesktopAgent.Core.Geometry.PixelPoint? point)
        => point.HasValue
            ? $"({point.Value.X},{point.Value.Y})"
            : "未知";
}
