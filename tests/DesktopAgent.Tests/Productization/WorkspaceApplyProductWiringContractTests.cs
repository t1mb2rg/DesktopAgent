namespace DesktopAgent.Tests.Productization;

public sealed class WorkspaceApplyProductWiringContractTests
{
    [Fact]
    public void FrozenPreview_ProductRoute_UsesExplicitOneShotConfirmationWithoutLegacyDownConversion()
    {
        var root = FindRepositoryRoot();
        var directApply = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.DirectApply.cs");
        var workspaceApply = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceApply.cs");
        var mainWindow = Read(root, "src", "DesktopAgent.WinUI", "MainWindow.AiAssistant.cs");

        Assert.Contains("if (_workspaceFrozenPreview is not null)", directApply, StringComparison.Ordinal);
        Assert.Contains("ApplyWorkspaceFrozenPreviewAsync", directApply, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopLayoutDirectApplyPreparationService.CreateFromPreview(_workspaceFrozenPreview", directApply, StringComparison.Ordinal);

        Assert.Contains("_workspaceFrozenPreviewApplyAvailable", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceApplyConfirmation.Create(preview)", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("Title = \"应用这个布局？\"", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonText = \"确认并应用\"", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("不会静默重新设计或重新编译另一份布局", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("_workspaceFrozenPreviewApplyAvailable = false", workspaceApply, StringComparison.Ordinal);

        Assert.Contains("if (_layoutManagementView.TryLoadAiWorkspaceFrozenPreview(preview))", mainWindow, StringComparison.Ordinal);
        Assert.Contains("EnableWorkspaceFrozenPreviewApply", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceApply_ProductBoundary_KeepsPhysicalWriterOutOfWinUi()
    {
        var root = FindRepositoryRoot();
        var workspaceApply = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceApply.cs");
        var directApply = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.DirectApply.cs");
        var service = Read(
            root,
            "src",
            "DesktopAgent.WindowsInterop",
            "DesktopLayouts",
            "Workspaces",
            "Execution",
            "DesktopWorkspaceApplyService.cs");

        Assert.DoesNotContain("DesktopShellIconPositionWriter", workspaceApply, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", workspaceApply, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopWorkspaceExecutionCoordinator", workspaceApply, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", directApply, StringComparison.Ordinal);

        Assert.Contains("DesktopShellIconPositionWriter", service, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceExecutionCoordinator", service, StringComparison.Ordinal);
        Assert.Contains("UnresolvedLegacyRecovery", service, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", service, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossGenerationRecovery_InterlocksBothApplyFamiliesAndNeverFakesLegacyRecovery()
    {
        var root = FindRepositoryRoot();
        var legacyApply = Read(
            root,
            "src",
            "DesktopAgent.WindowsInterop",
            "DesktopLayouts",
            "Restore",
            "Execution",
            "DesktopLayoutDirectApplyService.cs");
        var recoveryUi = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.Recovery.cs");

        Assert.Contains("_workspaceExecutionJournalStore.ListUnresolvedAsync", legacyApply, StringComparison.Ordinal);
        Assert.Contains("UnresolvedRecoveryBlocked", legacyApply, StringComparison.Ordinal);

        Assert.Contains("_workspaceExecutionJournalStore.ListUnresolvedAsync", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("检测到 Workspace 恢复现场", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("不会把 Workspace 第三状态伪装成旧单屏恢复", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("新的真实写入继续保持阻断", recoveryUi, StringComparison.Ordinal);
    }

    [Fact]
    public void FrozenPreviewLifecycle_DestroysApplyAuthorityWhenPreviewIsClearedOrReplaced()
    {
        var root = FindRepositoryRoot();
        var frozen = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceFrozenPreview.cs");
        var replacement = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.LogicalWorkspaceReplacement.cs");
        var previewGate = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.PreviewGate.cs");

        Assert.Contains("_workspaceFrozenPreviewApplyAvailable = false", frozen, StringComparison.Ordinal);
        Assert.Contains("RefreshApplyButtonFromCurrentEvidence", frozen, StringComparison.Ordinal);
        Assert.Contains("DisableWorkspaceFrozenPreviewApplyEvidence", replacement, StringComparison.Ordinal);

        Assert.Contains("if (_workspaceFrozenPreview is not null)", previewGate, StringComparison.Ordinal);
        Assert.Contains("RenderWorkspaceFrozenPreviewCanvas", previewGate, StringComparison.Ordinal);
        Assert.Contains("不会重新读取桌面", previewGate, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopAgent.sln")))
                return current.FullName;
        }

        throw new Xunit.Sdk.XunitException("无法从测试输出目录定位 DesktopAgent.sln。");
    }
}
