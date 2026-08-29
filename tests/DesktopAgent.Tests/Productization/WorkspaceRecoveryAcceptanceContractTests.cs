namespace DesktopAgent.Tests.Productization;

public sealed class WorkspaceRecoveryAcceptanceContractTests
{
    [Fact]
    public void AcceptedCurrentState_PreservesNeedsAttentionHistory_AndFiltersOnlyTheExecutionLock()
    {
        var root = FindRepositoryRoot();
        var marker = Read(root,
            "src", "DesktopAgent.Core", "DesktopLayouts", "Workspaces", "Execution",
            "DesktopWorkspaceExecutionResolutionMarkers.cs");
        var wrapper = Read(root,
            "src", "DesktopAgent.Persistence", "DesktopLayouts", "Workspaces", "Execution",
            "AcceptedAwareDesktopWorkspaceExecutionJournalStore.cs");
        var service = Read(root,
            "src", "DesktopAgent.WinUI", "DesktopWorkspaceRecoveryProductService.cs");

        Assert.Contains("UserAcceptedCurrentState", marker, StringComparison.Ordinal);
        Assert.Contains("Lifecycle == DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention", marker, StringComparison.Ordinal);
        Assert.Contains("IsUserAcceptedCurrentState", wrapper, StringComparison.Ordinal);

        Assert.Contains("RecoveryFailureType = DesktopWorkspaceExecutionResolutionMarkers.UserAcceptedCurrentState", service, StringComparison.Ordinal);
        Assert.Contains("未宣称 Workspace Apply 成功", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Committed", service, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceRecoveryInspector_ShowsWriterEvidence_AndAcceptanceHasZeroWriterAuthority()
    {
        var root = FindRepositoryRoot();
        var recovery = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.Recovery.cs");
        var inspector = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceRecovery.cs");
        var apply = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceApply.cs");
        var directApply = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.DirectApply.cs");

        Assert.Contains("PresentWorkspaceRecoveryInspectorAsync(workspacePending[0])", recovery, StringComparison.Ordinal);
        Assert.Contains("ExpectedPreviousPixelPosition", inspector, StringComparison.Ordinal);
        Assert.Contains("ReportedPreviousPixelPosition", inspector, StringComparison.Ordinal);
        Assert.Contains("RequestedPixelPosition", inspector, StringComparison.Ordinal);
        Assert.Contains("ActualPixelPosition", inspector, StringComparison.Ordinal);
        Assert.Contains("接受当前桌面并结束会话", inspector, StringComparison.Ordinal);
        Assert.Contains("不会移动任何图标", inspector, StringComparison.Ordinal);

        Assert.DoesNotContain("DesktopShellIconPositionWriter", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveDesktopIcon", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", Read(root,
            "src", "DesktopAgent.WinUI", "DesktopWorkspaceRecoveryProductService.cs"), StringComparison.Ordinal);

        Assert.Contains("AcceptedAwareDesktopWorkspaceExecutionJournalStore", apply, StringComparison.Ordinal);
        Assert.Contains("_workspaceExecutionJournalStore", directApply, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return File.ReadAllText(Path.Combine(all));
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopAgent.sln")))
                return current.FullName;
        }

        throw new Xunit.Sdk.XunitException("无法从测试输出目录定位 DesktopAgent.sln。\n");
    }
}
