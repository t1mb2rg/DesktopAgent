using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.WindowsInterop.DesktopLayouts.Workspaces.Execution;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private readonly AcceptedAwareDesktopWorkspaceExecutionJournalStore _workspaceExecutionJournalStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);

    private DesktopWorkspaceApplyService? _workspaceApplyService;
    private Guid? _workspaceFrozenPreviewApplyPreviewId;

    private DesktopWorkspaceApplyService WorkspaceApplyService =>
        _workspaceApplyService ??= new DesktopWorkspaceApplyService(
            _directApplySnapshotStore,
            _workspaceExecutionJournalStore,
            _directApplyRecoveryStore);

    internal void EnableWorkspaceFrozenPreviewApply()
    {
        _workspaceFrozenPreviewApplyPreviewId =
            _workspaceFrozenPreview is { MoveCount: > 0 } preview
                ? preview.PreviewId
                : null;
        RefreshApplyButtonFromCurrentEvidence();
    }

    private bool HasWorkspaceFrozenPreviewApplyTicket(DesktopWorkspaceFrozenPreview preview)
        => _workspaceFrozenPreviewApplyPreviewId == preview.PreviewId;

    private void DisableWorkspaceFrozenPreviewApplyEvidence()
    {
        _workspaceFrozenPreviewApplyPreviewId = null;
        RefreshApplyButtonFromCurrentEvidence();
    }

    private void RefreshApplyButtonFromCurrentEvidence()
    {
        if (_workspaceFrozenPreview is not null)
        {
            ApplyToDesktopButton.Content = "应用这个布局";
            ApplyToDesktopButton.IsEnabled =
                !_directApplyInProgress &&
                HasWorkspaceFrozenPreviewApplyTicket(_workspaceFrozenPreview) &&
                _workspaceFrozenPreview.MoveCount > 0;
            return;
        }

        ApplyToDesktopButton.Content = "应用到桌面";
        ApplyToDesktopButton.IsEnabled =
            !_directApplyInProgress &&
            _latestPreview is not null &&
            _latestPreview.Plan.IsComplete &&
            _latestPreview.Plan.MoveRequiredCount > 0;
    }

    private async Task ApplyWorkspaceFrozenPreviewAsync()
    {
        var preview = _workspaceFrozenPreview;
        if (preview is null || !HasWorkspaceFrozenPreviewApplyTicket(preview))
        {
            RefreshApplyButtonFromCurrentEvidence();
            ShowInfo(
                "请重新生成精确预览",
                "Workspace Apply 只接受当前会话里尚未消费的 Frozen Preview。Writer 未启动。",
                InfoBarSeverity.Informational);
            return;
        }

        if (preview.MoveCount == 0)
        {
            _workspaceFrozenPreviewApplyPreviewId = null;
            RefreshApplyButtonFromCurrentEvidence();
            ShowInfo("桌面已经就位", "这份精确预览没有需要移动的项目。", InfoBarSeverity.Success);
            return;
        }

        var designDisplayCount = preview.Displays.Count(static display =>
            display.Policy == DesktopWorkspaceDisplayPolicyKind.Design);
        var preserveDisplayCount = preview.Displays.Count(static display =>
            display.Policy == DesktopWorkspaceDisplayPolicyKind.Preserve);
        var stayCount = preview.KeepCount + preview.Items.Count(static item =>
            item.Disposition == DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation.DesktopWorkspaceCompiledItemDisposition.Place &&
            !item.IsPositionChange);

        var confirmContent =
            $"设计版本：v{preview.Blueprint.Revision}\n" +
            $"显示器：{preview.Displays.Count} 块（参与设计 {designDisplayCount} / 保持现状 {preserveDisplayCount}）\n" +
            $"预计移动：{preview.MoveCount} 个项目\n" +
            $"其中跨屏：{preview.CrossDisplayAssignmentCount} 个\n" +
            $"保持当前位置：{stayCount} 个\n\n" +
            "确认后 Tessel 会重新读取当前桌面，并严格检查显示拓扑、Explorer 几何、项目集合与每个项目的起点。\n" +
            "只要当前桌面与这份 Frozen Preview 有任何变化，本次操作就会停止，不会静默重新设计或重新编译另一份布局。\n\n" +
            "通过 fresh check 后，Tessel 会先保存并回读全桌面安全快照和 Workspace execution journal，之后才允许第一次 Writer。";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "应用这个布局？",
            Content = new TextBlock
            {
                Text = confirmContent,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
            },
            PrimaryButtonText = "确认并应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            ShowInfo("已取消", "Frozen Preview 仍然有效，没有移动任何桌面项目。", InfoBarSeverity.Informational);
            return;
        }

        // Confirmation 只在用户真正点下“确认并应用”后生成，并且一次性消费当前 PreviewId 的执行入口。
        var confirmation = DesktopWorkspaceApplyConfirmation.Create(preview);
        _workspaceFrozenPreviewApplyPreviewId = null;
        _directApplyInProgress = true;
        RefreshApplyButtonFromCurrentEvidence();
        ReconcileRecoveryButton.IsEnabled = false;
        PreviewLayoutButton.IsEnabled = false;
        LayoutCanvasStatusText.Text = "正在检查当前桌面并准备安全执行…";

        try
        {
            var result = await WorkspaceApplyService.ApplyAsync(confirmation);
            LayoutCanvasStatusText.Text = "Workspace Apply 已结束 · 再次执行前需要新的精确预览";
            PresentWorkspaceApplyResult(result, preview);
        }
        catch (OperationCanceledException)
        {
            LayoutCanvasStatusText.Text = "应用在 Writer 前取消 · 请重新生成精确预览";
            ShowInfo(
                "应用已取消",
                "操作在进入不可取消的 Writer boundary 前结束。请重新生成精确预览后再决定是否应用。",
                InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            LayoutCanvasStatusText.Text = "执行入口发生未预期异常 · 新的写入已暂停";
            ShowError(
                "Workspace Apply 发生未预期异常",
                $"{ex.GetType().Name}: {ex.Message}。请先点击“处理恢复状态”检查是否存在未收敛执行现场，不要重复应用。" );
        }
        finally
        {
            _directApplyInProgress = false;
            ReconcileRecoveryButton.IsEnabled = true;
            // Frozen Preview 仍然留作只读审阅，但当前 Apply ticket 已消费。
            // 普通 legacy Preview 不得在这里重新点亮，否则会把两套 Preview 生命周期串线。
            PreviewLayoutButton.IsEnabled = false;
            RefreshApplyButtonFromCurrentEvidence();
        }
    }

    private void PresentWorkspaceApplyResult(
        DesktopWorkspaceExecutionRunResult result,
        DesktopWorkspaceFrozenPreview preview)
    {
        switch (result.Status)
        {
            case DesktopWorkspaceExecutionRunStatus.Committed:
                ShowInfo(
                    "布局已应用",
                    $"{preview.MoveCount} 个预计移动项目已经完成 Workspace Writer 流程，并通过最终 fresh 独立验证。",
                    InfoBarSeverity.Success);
                return;

            case DesktopWorkspaceExecutionRunStatus.NoOp:
                ShowInfo(
                    "桌面已经就位",
                    "fresh check 后不再需要任何移动，没有启动 Writer。",
                    InfoBarSeverity.Success);
                return;

            case DesktopWorkspaceExecutionRunStatus.Restored:
                ShowInfo(
                    "应用没有保留，并已恢复",
                    "执行层只回滚了具有可信 ExactSuccess 证据的步骤，并通过安全快照验证。请重新生成精确预览。",
                    InfoBarSeverity.Warning);
                return;

            case DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite:
                ShowInfo(
                    IsWorkspaceRecoveryBlock(result.ErrorType)
                        ? "检测到未完成恢复"
                        : "当前精确预览已不能安全应用",
                    $"Writer 未启动。{WorkspaceExecutionDiagnostic(result)} 请重新生成精确预览后再试。",
                    IsWorkspaceRecoveryBlock(result.ErrorType)
                        ? InfoBarSeverity.Error
                        : InfoBarSeverity.Warning);
                return;

            case DesktopWorkspaceExecutionRunStatus.NeedsAttention:
                ShowInfo(
                    "Workspace 执行没有安全收敛",
                    $"Tessel 已停止新的 Writer，并保留 Safety Snapshot 与 V9 execution journal。{WorkspaceExecutionDiagnostic(result)} " +
                    "不要重复应用；点击“处理恢复状态”可以确认当前存在 Workspace 恢复现场。",
                    InfoBarSeverity.Error);
                return;

            default:
                ShowInfo(
                    "Workspace Apply 未完成",
                    WorkspaceExecutionDiagnostic(result),
                    InfoBarSeverity.Error);
                return;
        }
    }

    private static bool IsWorkspaceRecoveryBlock(string? errorType)
        => string.Equals(errorType, "UnresolvedWorkspaceExecution", StringComparison.Ordinal) ||
           string.Equals(errorType, "UnresolvedLegacyRecovery", StringComparison.Ordinal);

    private static string WorkspaceExecutionDiagnostic(DesktopWorkspaceExecutionRunResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            parts.Add(result.ErrorMessage.Trim());
        if (result.VerificationIssues.Count > 0)
            parts.AddRange(result.VerificationIssues.Take(3));
        if (result.SessionId.HasValue)
            parts.Add($"执行会话 {result.SessionId.Value:D}");
        return parts.Count == 0 ? "没有额外诊断。" : string.Join("；", parts);
    }
}
