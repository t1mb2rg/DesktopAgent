using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class AiAssistantView
{
    internal event Action<DesktopUnderstandingMultiDisplayWorkspaceCompiler.WorkspaceCompilationResult>?
        LayoutWorkspaceImportRequested;

    private void ImportMultiDisplayLayoutDraft_Click(object sender, RoutedEventArgs e)
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

        try
        {
            var workspace = DesktopUnderstandingMultiDisplayWorkspaceCompiler.Compile(
                latest.Draft,
                latest.Observation);

            if (workspace.Displays.Count == 0)
                throw new InvalidOperationException("当前 AI Assignment 没有任何项目能够安全归属到已观察的显示器。");
            if (workspace.UnscopedAssignmentCount != 0)
                throw new InvalidOperationException(
                    $"有 {workspace.UnscopedAssignmentCount} 个 AI Assignment 无法从本次 fresh 观察确定显示器归属。为避免猜测，已拒绝导入。" );
            if (LayoutWorkspaceImportRequested is null)
                throw new InvalidOperationException("布局工作台当前没有挂载多显示器 AI 草案导入入口。");

            LayoutWorkspaceImportRequested.Invoke(workspace);
        }
        catch (Exception ex)
        {
            AiStatusBar.Severity = InfoBarSeverity.Error;
            AiStatusBar.Title = "无法导入多显示器布局草案";
            AiStatusBar.Message = ex.Message;
            AiStatusBar.IsOpen = true;
        }
    }
}
