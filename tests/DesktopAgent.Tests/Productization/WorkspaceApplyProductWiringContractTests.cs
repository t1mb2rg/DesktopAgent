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

        Assert.Contains("_workspaceFrozenPreviewApplyPreviewId", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("HasWorkspaceFrozenPreviewApplyTicket", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("preview.PreviewId", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceApplyConfirmation.Create(preview)", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("Title = \"应用这个布局？\"", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonText = \"确认并应用\"", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("不会静默重新设计或重新编译另一份布局", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("_workspaceFrozenPreviewApplyPreviewId = null", workspaceApply, StringComparison.Ordinal);

        Assert.Contains("if (_layoutManagementView.TryLoadAiWorkspaceFrozenPreview(preview))", mainWindow, StringComparison.Ordinal);
        Assert.Contains("EnableWorkspaceFrozenPreviewApply", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceApply_ProductBoundary_KeepsPhysicalWriterOutOfWinUi_AndUsesDynamicCoordinator()
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
        Assert.DoesNotContain("DesktopWorkspaceDynamicExecutionCoordinator", workspaceApply, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", directApply, StringComparison.Ordinal);

        Assert.Contains("DesktopShellIconPositionWriter", service, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceDynamicExecutionCoordinator", service, StringComparison.Ordinal);
        Assert.DoesNotContain("new DesktopWorkspaceExecutionCoordinator", service, StringComparison.Ordinal);
        Assert.Contains("IDesktopWorkspaceDynamicExecutionJournalStore", service, StringComparison.Ordinal);
        Assert.Contains("UnresolvedLegacyRecovery", service, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", service, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossGenerationRecovery_RoutesV9ToInspector_AndDynamicEvidenceFailsClosedBeforeLegacyRecovery()
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

        Assert.Contains("_workspaceDynamicExecutionJournalStore.ListUnresolvedAsync", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("_workspaceExecutionJournalStore.ListUnresolvedAsync", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("if (dynamicWorkspacePending.Count > 0)", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("PresentWorkspaceRecoveryInspectorAsync(workspacePending[0])", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("Dynamic Journal、Safety Snapshot 与逐次 fresh/Writer 证据仍然保留", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("V9 Journal、Safety Snapshot 和 Writer 证据仍然保留", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("新的真实写入继续保持阻断", recoveryUi, StringComparison.Ordinal);
        Assert.Contains("当前版本不会用 V9 或旧单屏恢复器猜测处理 Dynamic 现场", recoveryUi, StringComparison.Ordinal);

        var dynamicBranch = recoveryUi.IndexOf("if (dynamicWorkspacePending.Count > 0)", StringComparison.Ordinal);
        var workspaceBranch = recoveryUi.IndexOf("if (workspacePending.Count > 0)", StringComparison.Ordinal);
        var legacyReconcile = recoveryUi.IndexOf("RecoveryProductService.ReconcileSingleAsync", StringComparison.Ordinal);
        Assert.True(dynamicBranch >= 0 && workspaceBranch > dynamicBranch && legacyReconcile > workspaceBranch);
        Assert.Contains("return;", recoveryUi[dynamicBranch..workspaceBranch], StringComparison.Ordinal);
        Assert.Contains("return;", recoveryUi[workspaceBranch..legacyReconcile], StringComparison.Ordinal);
    }

    [Fact]
    public void FrozenPreviewLifecycle_InvalidatesAuthorityByPreviewIdentityInsteadOfRendererMutation()
    {
        var root = FindRepositoryRoot();
        var workspaceApply = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceApply.cs");
        var replacement = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.LogicalWorkspaceReplacement.cs");
        var previewGate = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.PreviewGate.cs");
        var frozen = Read(root, "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceFrozenPreview.cs");

        Assert.Contains("_workspaceFrozenPreviewApplyPreviewId", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("preview.PreviewId", workspaceApply, StringComparison.Ordinal);
        Assert.Contains("DisableWorkspaceFrozenPreviewApplyEvidence", replacement, StringComparison.Ordinal);
        Assert.Contains("HasWorkspaceFrozenPreviewApplyTicket(_workspaceFrozenPreview)", previewGate, StringComparison.Ordinal);

        Assert.DoesNotContain("_workspaceFrozenPreviewApplyPreviewId", frozen, StringComparison.Ordinal);
        Assert.DoesNotContain("DisableWorkspaceFrozenPreviewApplyEvidence", frozen, StringComparison.Ordinal);

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
