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
        try
        {
            pendingCount = await RecoveryProductService.GetPendingCountAsync();
        }
        catch (Exception ex)
        {
            ShowError("无法读取恢复状态", $"{ex.GetType().Name}: {ex.Message}");
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
                    "Tessel 会重新读取当前真实桌面，并把它与持久化恢复会话中的 Original / Desired 意图进行协调。\n\n" +
                    "只有当前事实足够明确时，恢复协调器才可能执行补偿写；如果桌面处于第三种或不确定状态，它会停止并继续要求人工处理，不会猜测。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            },
            PrimaryButtonText = "处理恢复状态",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            ShowInfo("已取消", "没有执行恢复写入。", InfoBarSeverity.Informational);
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

    private async void AcceptCurrentDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (_directApplyInProgress)
            return;

        int pendingCount;
        try
        {
            pendingCount = await RecoveryProductService.GetPendingCountAsync();
        }
        catch (Exception ex)
        {
            ShowError("无法读取恢复状态", $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (pendingCount == 0)
        {
            ShowInfo("没有待处理恢复", "当前没有需要放弃的 unresolved 会话。", InfoBarSeverity.Success);
            return;
        }

        if (pendingCount != 1)
        {
            ShowInfo(
                "不能批量接受当前桌面",
                $"检测到 {pendingCount} 笔 unresolved 会话。v1 不会批量放弃恢复证据。",
                InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "接受当前桌面并结束恢复？",
            Content = new TextBlock
            {
                Text =
                    "这个操作不会移动任何桌面图标。\n\n" +
                    "你是在明确告诉 Tessel：当前真实桌面可以作为新的现实基线，不再尝试把这笔会话恢复到 Original 或 Desired。" +
                    "恢复会话、Safety Snapshot、Writer Attempts 和失败证据仍会保留，只把这笔会话标记为“用户已接受当前状态”，从而解除新的布局应用锁。\n\n" +
                    "仅当你确认当前桌面布局可以放弃时使用。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 540,
            },
            PrimaryButtonText = "接受当前桌面",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            ShowInfo("已取消", "恢复锁保持不变，没有执行 Writer。", InfoBarSeverity.Informational);
            return;
        }

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
        AcceptCurrentDesktopButton.IsEnabled = !busy;
        ApplyToDesktopButton.IsEnabled = !busy
            && _latestPreview is not null
            && _latestPreview.Plan.IsComplete
            && _latestPreview.Plan.MoveRequiredCount > 0;

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
                    $"恢复协调器已 fresh 检查，但当前事实不足以安全自动收敛。Tessel 不会猜测写入。若你愿意放弃这笔恢复，可使用“接受当前桌面”。{issueText}",
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
