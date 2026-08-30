namespace DesktopAgent.Tests.Productization;

public sealed class WorkspaceApplyRecoveryInterlockContractTests
{
    [Fact]
    public void WorkspaceApply_ProductBoundaryOwnsRealWriter_AndWinUiDoesNot()
    {
        var root = FindRepositoryRoot();
        var service = Read(root,
            "src", "DesktopAgent.WindowsInterop", "DesktopLayouts", "Workspaces", "Execution",
            "DesktopWorkspaceApplyService.cs");
        var coordinator = Read(root,
            "src", "DesktopAgent.WindowsInterop", "DesktopLayouts", "Workspaces", "Execution",
            "DesktopWorkspaceDynamicExecutionCoordinator.cs");
        var ui = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceApply.cs");

        Assert.Contains("new DesktopShellIconPositionWriter()", service, StringComparison.Ordinal);
        Assert.Contains("new WindowsDesktopObservationSource", service, StringComparison.Ordinal);
        Assert.Contains("new DesktopWorkspaceDynamicExecutionCoordinator", service, StringComparison.Ordinal);
        Assert.Contains("coordinator.ExecuteAsync", service, StringComparison.Ordinal);

        Assert.Contains("Safety Snapshot", ui, StringComparison.Ordinal);
        Assert.Contains("WorkspaceApplyService.ApplyAsync(confirmation)", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveDesktopIcon", ui, StringComparison.Ordinal);

        Assert.Contains("_staticJournalStore.ListUnresolvedAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("_dynamicJournalStore.ListUnresolvedAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("UnresolvedStaticWorkspaceExecution", coordinator, StringComparison.Ordinal);
        Assert.Contains("UnresolvedDynamicWorkspaceExecution", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceAndLegacyApply_MutuallyBlockUnresolvedRecoveryAcrossAllGenerations()
    {
        var root = FindRepositoryRoot();
        var workspace = Read(root,
            "src", "DesktopAgent.WindowsInterop", "DesktopLayouts", "Workspaces", "Execution",
            "DesktopWorkspaceApplyService.cs");
        var legacy = Read(root,
            "src", "DesktopAgent.WindowsInterop", "DesktopLayouts", "Restore", "Execution",
            "DesktopLayoutDirectApplyService.cs");
        var legacyUi = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.DirectApply.cs");

        Assert.Contains("_legacyRecoveryStore.ListUnresolvedAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("UnresolvedLegacyRecovery", workspace, StringComparison.Ordinal);
        Assert.Contains("IDesktopWorkspaceExecutionJournalStore", workspace, StringComparison.Ordinal);
        Assert.Contains("IDesktopWorkspaceDynamicExecutionJournalStore", workspace, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", workspace, StringComparison.Ordinal);

        Assert.Contains("IDesktopWorkspaceExecutionJournalStore", legacy, StringComparison.Ordinal);
        Assert.Contains("_workspaceExecutionJournalStore.ListUnresolvedAsync", legacy, StringComparison.Ordinal);
        Assert.Contains("未解决 V9 Workspace execution journal", legacy, StringComparison.Ordinal);
        Assert.Contains("IDesktopWorkspaceDynamicExecutionJournalStore", legacy, StringComparison.Ordinal);
        Assert.Contains("_dynamicWorkspaceExecutionJournalStore.ListUnresolvedAsync", legacy, StringComparison.Ordinal);
        Assert.Contains("未解决 Dynamic Workspace execution journal", legacy, StringComparison.Ordinal);
        Assert.Contains("DesktopLayoutDirectApplyOutcome.UnresolvedRecoveryBlocked", legacy, StringComparison.Ordinal);

        Assert.Contains("_workspaceDynamicExecutionJournalStore", legacyUi, StringComparison.Ordinal);
        var directApplyCtor = legacyUi.IndexOf("new DesktopLayoutDirectApplyService", StringComparison.Ordinal);
        var dynamicStoreArg = legacyUi.IndexOf("_workspaceDynamicExecutionJournalStore", directApplyCtor, StringComparison.Ordinal);
        Assert.True(directApplyCtor >= 0 && dynamicStoreArg > directApplyCtor);
    }

    [Fact]
    public void RecoveryUi_RecognizesDynamicThenV9BeforeLegacyRecovery_AndNeverCrossFeedsContracts()
    {
        var root = FindRepositoryRoot();
        var recovery = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.Recovery.cs");

        var dynamicRead = recovery.IndexOf(
            "dynamicWorkspacePending = await _workspaceDynamicExecutionJournalStore.ListUnresolvedAsync()",
            StringComparison.Ordinal);
        var workspaceRead = recovery.IndexOf(
            "workspacePending = await _workspaceExecutionJournalStore.ListUnresolvedAsync()",
            StringComparison.Ordinal);
        var legacyRead = recovery.IndexOf(
            "pendingCount = await RecoveryProductService.GetPendingCountAsync()",
            StringComparison.Ordinal);
        var dynamicGate = recovery.IndexOf(
            "if (dynamicWorkspacePending.Count > 0)",
            StringComparison.Ordinal);
        var workspaceGate = recovery.IndexOf(
            "if (workspacePending.Count > 0)",
            StringComparison.Ordinal);
        var inspectorRoute = recovery.IndexOf(
            "PresentWorkspaceRecoveryInspectorAsync(workspacePending[0])",
            StringComparison.Ordinal);
        var legacyZeroGate = recovery.IndexOf(
            "if (pendingCount == 0)",
            StringComparison.Ordinal);
        var legacyReconcile = recovery.IndexOf(
            "RecoveryProductService.ReconcileSingleAsync()",
            StringComparison.Ordinal);

        Assert.True(dynamicRead >= 0);
        Assert.True(workspaceRead > dynamicRead);
        Assert.True(legacyRead > workspaceRead);
        Assert.True(dynamicGate > legacyRead);
        Assert.True(workspaceGate > dynamicGate);
        Assert.True(inspectorRoute > workspaceGate);
        Assert.True(legacyZeroGate > workspaceGate);
        Assert.True(legacyReconcile > workspaceGate);

        Assert.Contains("Dynamic Journal、Safety Snapshot 与逐次 fresh/Writer 证据仍然保留", recovery, StringComparison.Ordinal);
        Assert.Contains("当前版本不会用 V9 或旧单屏恢复器猜测处理 Dynamic 现场", recovery, StringComparison.Ordinal);
        Assert.Contains("V9 Journal、Safety Snapshot 和 Writer 证据仍然保留", recovery, StringComparison.Ordinal);
        Assert.Contains("新的真实写入继续保持阻断", recovery, StringComparison.Ordinal);
        Assert.Contains("DisableWorkspaceFrozenPreviewApplyEvidence();", recovery, StringComparison.Ordinal);

        var returnAfterDynamic = recovery.IndexOf("return;", dynamicGate, StringComparison.Ordinal);
        var returnAfterInspector = recovery.IndexOf("return;", inspectorRoute, StringComparison.Ordinal);
        Assert.True(returnAfterDynamic > dynamicGate && returnAfterDynamic < workspaceGate);
        Assert.True(returnAfterInspector > inspectorRoute);
        Assert.True(legacyZeroGate > returnAfterInspector);
        Assert.True(legacyReconcile > returnAfterInspector);
    }

    [Fact]
    public void WorkspaceApply_ConsumesPreviewBoundConfirmationBeforeEnteringNonCancelableWriterBoundary()
    {
        var root = FindRepositoryRoot();
        var ui = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceApply.cs");
        var service = Read(root,
            "src", "DesktopAgent.WindowsInterop", "DesktopLayouts", "Workspaces", "Execution",
            "DesktopWorkspaceApplyService.cs");

        Assert.Contains("_workspaceFrozenPreviewApplyPreviewId", ui, StringComparison.Ordinal);
        Assert.Contains("preview.PreviewId", ui, StringComparison.Ordinal);
        Assert.Contains("HasWorkspaceFrozenPreviewApplyTicket", ui, StringComparison.Ordinal);

        var confirmation = ui.IndexOf("DesktopWorkspaceApplyConfirmation.Create(preview)", StringComparison.Ordinal);
        var consumeTicket = ui.IndexOf("_workspaceFrozenPreviewApplyPreviewId = null", confirmation, StringComparison.Ordinal);
        var applyCall = ui.IndexOf("WorkspaceApplyService.ApplyAsync(confirmation)", StringComparison.Ordinal);

        Assert.True(confirmation >= 0);
        Assert.True(consumeTicket > confirmation);
        Assert.True(applyCall > consumeTicket);

        Assert.Contains("cancellationToken.ThrowIfCancellationRequested();", service, StringComparison.Ordinal);
        Assert.Contains("_executeAsync(confirmation, CancellationToken.None)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceApply_AfterTicketConsumption_KeepsFrozenPreviewInReadOnlyReviewMode()
    {
        var root = FindRepositoryRoot();
        var ui = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceApply.cs");

        Assert.Contains("PreviewLayoutButton.IsEnabled = false", ui, StringComparison.Ordinal);
        Assert.Contains("Frozen Preview 仍然留作只读审阅", ui, StringComparison.Ordinal);
        Assert.Contains("普通 legacy Preview 不得在这里重新点亮", ui, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts)
    {
        var allParts = new string[parts.Length + 1];
        allParts[0] = root;
        Array.Copy(parts, 0, allParts, 1, parts.Length);
        return File.ReadAllText(Path.Combine(allParts));
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
