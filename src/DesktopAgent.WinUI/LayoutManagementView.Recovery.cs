using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private DesktopLayoutRecoveryProductService? _recoveryProductService;

    private DesktopLayoutRecoveryProductService RecoveryProductService =>
        _recoveryProductService ??= new DesktopLayoutRecoveryProductService(
            _directApplyRecoveryStore);

    private async void ReconcileRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (_directApplyInProgress)
            return;

        int pendingCount;
        IReadOnlyList<DesktopAgent.Core.DesktopLayouts.Workspaces.Execution.DesktopWorkspaceExecutionJournal> workspacePending;
        try
        {
            await _workspaceExecutionJournalStore.InitializeAsync();
            workspacePending = await _workspaceExecutionJournalStore.ListUnresolvedAsync();
            pendingCount = await RecoveryProductService.GetPendingCountAsync();
        }
        catch (Exception ex)
        {
            ShowError("无法读取恢复状态", $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        // V9 Workspace journal 与旧单屏 RecoverySession 是两套不同恢复合同。
        // 当前入口必须先识别 Workspace 现场，绝不能因为 legacy pending=0 就谎报“没有恢复”。
        if (workspacePending.Count > 0)
        {
            DisableWorkspaceFrozenPreviewApplyEvidence();
            ApplyToDesktopButton.IsEnabled = false;

            if (workspacePending.Count == 1 && pendingCount == 0)
            {
                await PresentWorkspaceRecoveryInspectorAsync(workspacePending[0]);
                return;
            }

            var legacySuffix = pendingCount > 0
                ? $" 同时还有 {pendingCount} 笔旧布局 unresolved 恢复会话。"
                : string.Empty;
            ShowInfo(
                "存在多套待处理恢复",
                $"检测到 {workspacePending.Count} 笔 unresolved Workspace execution journal。" +
                "V9 Journal、Safety Snapshot 和 Writer 证据仍然保留，新的真实写入继续保持阻断。" +
                legacySuffix +
                " 当前版本不会猜测多套恢复现场的处理顺序。",
                InfoBarSeverity.Error);
            return;
        }

        if (pendingCount == 0)
        {
            ShowInfo(
                "没有待处理恢复",
                "当前没有 unresolved 桌面执行会话。",
                InfoBarSeverity.Success);
            return;
        }

        if (pendingCount != 1)
        {
            ShowInfo(
                "存在多笔待处理恢复",
                $"检测到 {pendingCount} 笔 unresolved 会话。v1 不猜测恢复顺序，新的真实写入继续保持阻断。",
                InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "处理桌面恢复状态？",
            Content = new TextBlock
            {
                Text =
                    "“处理恢复状态”会重新读取当前真实桌面，并让 Recovery 根据 Original / Desired 意图安全协调；当前事实不足时仍会停止，不猜测。\n\n" +
                    "“接受当前桌面”不会移动任何图标。它表示你明确放弃这笔会话继续恢复到 Original / Desired，把现在看到的真实桌面作为新的现实基线。" +
                    "Safety Snapshot、Writer Attempts 和失败证据仍会保留，只解除这笔 unresolved 锁。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
            },
            PrimaryButtonText = "处理恢复状态",
            SecondaryButtonText = "接受当前桌面",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
        {
            ShowInfo("已取消", "没有执行恢复写入，恢复锁保持不变。", InfoBarSeverity.Informational);
            return;
        }

        // 旧 Recovery 可能产生 Writer 或改变用户认可的现实基线，因此任何 Workspace Frozen Preview
        // 都必须失去一次性 Apply authority。画面可以继续留作参考，但不能再执行。
        DisableWorkspaceFrozenPreviewApplyEvidence();

        if (choice == ContentDialogResult.Secondary)
        {
            await AcceptCurrentDesktopAsync();
            return;
        }

        SetRecoveryUiBusy(true, "正在 fresh 观察并协调恢复状态…");

        try
        {
            var result = await RecoveryProductService.ReconcileSingleAsync();
            InvalidateDirectApplyEvidence("恢复检查已完成 · 请重新预览当前桌面");
            PresentRecoveryResult(result);
        }
        catch (OperationCanceledException)
        {
            ShowInfo("恢复检查已取消", "请重新读取恢复状态后再决定下一步。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowError(
                "恢复流程发生未预期异常",
                $"{ex.GetType().Name}: {ex.Message}。新的布局应用继续保持阻断。" );
        }
        finally
        {
            SetRecoveryUiBusy(false, null);
        }
    }

    private async Task AcceptCurrentDesktopAsync()
    {
        SetRecoveryUiBusy(true, "正在记录用户对当前桌面的明确裁决…");

        try
        {
            var result = await RecoveryProductService.AcceptCurrentStateSingleAsync();
            InvalidateDirectApplyEvidence("恢复裁决已更新 · 请重新预览当前桌面");
            PresentRecoveryResult(result);
        }
        catch (OperationCanceledException)
        {
            ShowInfo("操作已取消", "恢复锁保持不变。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowError(
                "无法接受当前桌面",
                $"{ex.GetType().Name}: {ex.Message}。恢复锁保持不变。" );
        }
        finally
        {
            SetRecoveryUiBusy(false, null);
        }
    }

    private void SetRecoveryUiBusy(bool busy, string? status)
    {
        _directApplyInProgress = busy;
        ReconcileRecoveryButton.IsEnabled = !busy;
        RefreshApplyButtonFromCurrentEvidence();

        if (!string.IsNullOrWhiteSpace(status))
            LayoutCanvasStatusText.Text = status;
    }

    private void PresentRecoveryResult(DesktopLayoutRecoveryProductResult result)
    {
        var issueText = result.Issues.Count == 0
            ? string.Empty
            : $" {string.Join("；", result.Issues.Take(3))}";

        switch (result.Outcome)
        {
            case DesktopLayoutRecoveryProductOutcome.NoPendingRecovery:
                ShowInfo("恢复状态已清空", "当前没有 unresolved 会话。", InfoBarSeverity.Success);
                return;

            case DesktopLayoutRecoveryProductOutcome.MultiplePendingRecoveries:
                ShowInfo(
                    "仍有多笔待处理恢复",
                    $"当前还有 {result.PendingCount} 笔 unresolved 会话。v1 已停止自动处理。{issueText}",
                    InfoBarSeverity.Error);
                return;

            case DesktopLayoutRecoveryProductOutcome.Reconciled:
                ShowInfo(
                    "恢复状态已收敛",
                    $"恢复会话已经基于当前真实桌面安全收敛。请重新预览布局后再继续。{issueText}",
                    InfoBarSeverity.Success);
                return;

            case DesktopLayoutRecoveryProductOutcome.AcceptedCurrentState:
                ShowInfo(
                    "已接受当前桌面",
                    $"这笔恢复会话已由用户明确结束，未执行恢复 Writer。当前桌面现在可作为新的现实基线；请重新预览后再应用布局。{issueText}",
                    InfoBarSeverity.Success);
                return;

            case DesktopLayoutRecoveryProductOutcome.StillNeedsAttention:
                ShowInfo(
                    "当前桌面仍需要人工确认",
                    $"恢复协调器已 fresh 检查，但当前事实不足以安全自动收敛。Tessel 不会猜测写入。若你愿意放弃这笔恢复，再点击“处理恢复状态”并选择“接受当前桌面”。{issueText}",
                    InfoBarSeverity.Error);
                return;

            default:
                ShowInfo(
                    "恢复状态未能处理",
                    $"新的布局应用继续保持阻断。{issueText}",
                    InfoBarSeverity.Error);
                return;
        }
    }
}
