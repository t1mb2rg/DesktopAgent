using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.WindowsInterop.DesktopLayouts.Workspaces.Execution;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private readonly SqliteDesktopWorkspaceDynamicExecutionJournalStore _workspaceDynamicExecutionJournalStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);

    private void PresentWorkspaceApplyResult(
        DesktopWorkspaceDynamicExecutionRunResult result,
        DesktopWorkspaceFrozenPreview preview)
    {
        switch (result.Status)
        {
            case DesktopWorkspaceExecutionRunStatus.Committed:
                ShowInfo(
                    "布局已应用",
                    $"{preview.MoveCount} 个预计移动项目已经完成 Dynamic Workspace Writer 流程，并通过最终 fresh 独立验证。",
                    InfoBarSeverity.Success);
                return;

            case DesktopWorkspaceExecutionRunStatus.NoOp:
                ShowInfo(
                    "桌面已经就位",
                    "fresh check 后不再需要任何移动，没有启动 Writer。",
                    InfoBarSeverity.Success);
                return;

            case DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite:
                ShowInfo(
                    IsDynamicWorkspaceRecoveryBlock(result.ErrorType)
                        ? "检测到未完成恢复"
                        : "当前精确预览已不能安全应用",
                    $"Writer 未启动。{DynamicWorkspaceExecutionDiagnostic(result)} 请重新生成精确预览后再试。",
                    IsDynamicWorkspaceRecoveryBlock(result.ErrorType)
                        ? InfoBarSeverity.Error
                        : InfoBarSeverity.Warning);
                return;

            case DesktopWorkspaceExecutionRunStatus.NeedsAttention:
                ShowInfo(
                    "Workspace 执行没有安全收敛",
                    $"Tessel 已停止新的 Writer，并保留 Safety Snapshot 与 Dynamic execution journal。{DynamicWorkspaceExecutionDiagnostic(result)} " +
                    "不要重复应用；点击“处理恢复状态”可以确认当前存在 Dynamic Workspace 恢复现场。",
                    InfoBarSeverity.Error);
                return;

            case DesktopWorkspaceExecutionRunStatus.Restored:
                // Dynamic execution 当前不自动 rollback。若未来引入 snapshot convergence recovery，
                // 产品文案必须在对应恢复合同落地后再开放。
                ShowInfo(
                    "Dynamic Workspace 已恢复",
                    "执行层报告已恢复；请重新生成精确预览后再继续。",
                    InfoBarSeverity.Warning);
                return;

            default:
                ShowInfo(
                    "Workspace Apply 未完成",
                    DynamicWorkspaceExecutionDiagnostic(result),
                    InfoBarSeverity.Error);
                return;
        }
    }

    private static bool IsDynamicWorkspaceRecoveryBlock(string? errorType)
        => string.Equals(errorType, "UnresolvedStaticWorkspaceExecution", StringComparison.Ordinal) ||
           string.Equals(errorType, "UnresolvedDynamicWorkspaceExecution", StringComparison.Ordinal) ||
           string.Equals(errorType, "UnresolvedLegacyRecovery", StringComparison.Ordinal);

    private static string DynamicWorkspaceExecutionDiagnostic(
        DesktopWorkspaceDynamicExecutionRunResult result)
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
