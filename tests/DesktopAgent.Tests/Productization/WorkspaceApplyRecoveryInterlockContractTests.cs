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
            "DesktopWorkspaceExecutionCoordinator.cs");
        var ui = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.WorkspaceApply.cs");

        Assert.Contains("new DesktopShellIconPositionWriter()", service, StringComparison.Ordinal);
        Assert.Contains("new WindowsDesktopObservationSource", service, StringComparison.Ordinal);
        Assert.Contains("new DesktopWorkspaceExecutionCoordinator", service, StringComparison.Ordinal);
        Assert.Contains("coordinator.ExecuteAsync", service, StringComparison.Ordinal);

        Assert.Contains("Safety Snapshot", ui, StringComparison.Ordinal);
        Assert.Contains("WorkspaceApplyService.ApplyAsync(confirmation)", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveDesktopIcon", ui, StringComparison.Ordinal);

        // Coordinator 自己仍在 fresh-check 后检查 unresolved V9 journal，Writer 之前 fail-closed。
        Assert.Contains("_journalStore.ListUnresolvedAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("UnresolvedWorkspaceExecution", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceAndLegacyApply_MutuallyBlockUnresolvedRecoveryAcrossGenerations()
    {
        var root = FindRepositoryRoot();
        var workspace = Read(root,
            "src", "DesktopAgent.WindowsInterop", "DesktopLayouts", "Workspaces", "Execution",
            "DesktopWorkspaceApplyService.cs");
        var legacy = Read(root,
            "src", "DesktopAgent.WindowsInterop", "DesktopLayouts", "Restore", "Execution",
            "DesktopLayoutDirectApplyService.cs");

        // 新 Workspace 不得越过旧 Recovery 锁。
        Assert.Contains("_legacyRecoveryStore.ListUnresolvedAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("UnresolvedLegacyRecovery", workspace, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", workspace, StringComparison.Ordinal);

        // 旧 Direct Apply 也不得越过新 V9 Workspace journal 锁。
        Assert.Contains("IDesktopWorkspaceExecutionJournalStore", legacy, StringComparison.Ordinal);
        Assert.Contains("_workspaceExecutionJournalStore.ListUnresolvedAsync", legacy, StringComparison.Ordinal);
        Assert.Contains("未解决 Workspace execution journal", legacy, StringComparison.Ordinal);
        Assert.Contains("DesktopLayoutDirectApplyOutcome.UnresolvedRecoveryBlocked", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryUi_RecognizesWorkspaceJournalBeforeLegacyRecovery_WithoutPretendingItCanReconcileIt()
    {
        var root = FindRepositoryRoot();
        var recovery = Read(root,
            "src", "DesktopAgent.WinUI", "LayoutManagementView.Recovery.cs");

        var workspaceRead = recovery.IndexOf(
            "workspacePending = await _workspaceExecutionJournalStore.ListUnresolvedAsync()",
            StringComparison.Ordinal);
        var legacyRead = recovery.IndexOf(
            "pendingCount = await RecoveryProductService.GetPendingCountAsync()",
            StringComparison.Ordinal);
        var workspaceGate = recovery.IndexOf(
            "if (workspacePending.Count > 0)",
            StringComparison.Ordinal);
        var legacyZeroGate = recovery.IndexOf(
            "if (pendingCount == 0)",
            StringComparison.Ordinal);

        Assert.True(workspaceRead >= 0);
        Assert.True(legacyRead >= 0);
        Assert.True(workspaceGate > workspaceRead);
        Assert.True(workspaceGate > legacyRead);
        Assert.True(legacyZeroGate > workspaceGate);

        Assert.Contains("检测到 Workspace 恢复现场", recovery, StringComparison.Ordinal);
        Assert.Contains("当前版本不会把 Workspace 第三状态伪装成旧单屏恢复", recovery, StringComparison.Ordinal);
        Assert.Contains("DisableWorkspaceFrozenPreviewApplyEvidence();", recovery, StringComparison.Ordinal);

        // Workspace 分支在 legacy Reconcile 之前直接 return，当前 slice 只识别和阻断，不假装有恢复 authority。
        var returnAfterWorkspaceGate = recovery.IndexOf("return;", workspaceGate, StringComparison.Ordinal);
        var legacyReconcile = recovery.IndexOf("RecoveryProductService.ReconcileSingleAsync()", StringComparison.Ordinal);
        Assert.True(returnAfterWorkspaceGate > workspaceGate);
        Assert.True(legacyReconcile > returnAfterWorkspaceGate);
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
