namespace DesktopAgent.Tests.Productization;

public sealed class WorkspaceFrozenPreviewUiContractTests
{
    [Fact]
    public void WorkspaceFrozenPreviewUi_UsesExactPixelRealization_WithoutOwningWriterAuthority()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.WorkspaceFrozenPreview.cs"));

        Assert.Contains("DesktopWorkspaceFrozenPreview", source, StringComparison.Ordinal);
        Assert.Contains("SourcePixelPosition", source, StringComparison.Ordinal);
        Assert.Contains("TargetPixelPosition", source, StringComparison.Ordinal);
        Assert.Contains("zone.RealizedBounds", source, StringComparison.Ordinal);
        Assert.Contains("region.RealizedBounds", source, StringComparison.Ordinal);
        Assert.Contains("MaxWorkspaceFrozenPreviewItemDots = 180", source, StringComparison.Ordinal);

        // Preview renderer 本身仍然以 0 Writer authority 加载。产品层成功接收后才单独开放一次 Apply ticket。
        Assert.Contains("ApplyToDesktopButton.IsEnabled = false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopLayoutDirectApplyService", source, StringComparison.Ordinal);

        Assert.DoesNotContain("DesktopLogicalGridCell", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopDesiredWorkspacePlan", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceFrozenPreviewUi_ApplyTicketOpensOnlyAfterSuccessfulProductLoad_AndIsOneShot()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "MainWindow.AiAssistant.cs"));
        var apply = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.WorkspaceApply.cs"));
        var directApply = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.DirectApply.cs"));

        Assert.Contains("if (_layoutManagementView.TryLoadAiWorkspaceFrozenPreview(preview))", mainWindow, StringComparison.Ordinal);
        Assert.Contains("EnableWorkspaceFrozenPreviewApply();", mainWindow, StringComparison.Ordinal);

        Assert.Contains("_workspaceFrozenPreviewApplyAvailable", apply, StringComparison.Ordinal);
        Assert.Contains("ApplyToDesktopButton.Content = \"应用这个布局\"", apply, StringComparison.Ordinal);
        Assert.Contains("DesktopWorkspaceApplyConfirmation.Create(preview)", apply, StringComparison.Ordinal);
        Assert.Contains("_workspaceFrozenPreviewApplyAvailable = false", apply, StringComparison.Ordinal);
        Assert.Contains("WorkspaceApplyService.ApplyAsync(confirmation)", apply, StringComparison.Ordinal);
        Assert.Contains("确认并应用", apply, StringComparison.Ordinal);
        Assert.Contains("不会静默重新设计或重新编译另一份布局", apply, StringComparison.Ordinal);

        // 共享按钮只做路由，Workspace 不得重新投影回旧单屏 Direct Apply。
        Assert.Contains("if (_workspaceFrozenPreview is not null)", directApply, StringComparison.Ordinal);
        Assert.Contains("ApplyWorkspaceFrozenPreviewAsync", directApply, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceFrozenPreviewUi_SeparatesTopologyFlowFromFocusedExactPlacements()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.WorkspaceFrozenPreview.cs"));

        Assert.Contains("整体拓扑", source, StringComparison.Ordinal);
        Assert.Contains("_activeWorkspaceFrozenPreviewDisplayIndex < 0", source, StringComparison.Ordinal);
        Assert.Contains("RenderWorkspaceFrozenPreviewTopologySummary", source, StringComparison.Ordinal);
        Assert.Contains("RenderWorkspaceFrozenPreviewAggregateFlows", source, StringComparison.Ordinal);
        Assert.Contains("WorkspaceFrozenAggregateFlow", source, StringComparison.Ordinal);
        Assert.Contains("MaxWorkspaceFrozenPreviewAggregateFlows = 8", source, StringComparison.Ordinal);
        Assert.Contains("ShouldRenderFocusedWorkspacePreviewItem", source, StringComparison.Ordinal);
        Assert.Contains("item.TargetPixelPosition.HasValue", source, StringComparison.Ordinal);
        Assert.Contains("点击聚焦这块显示器", source, StringComparison.Ordinal);

        // 整体拓扑不再默认绘制逐项目迁移线；跨屏变化按 display→display 聚合。
        Assert.DoesNotContain("MaxWorkspaceFrozenPreviewMovementLines", source, StringComparison.Ordinal);
        Assert.DoesNotContain("X1 = X(item.SourcePixelPosition.X)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceFrozenPreviewUi_ProductCopyIsLocalizedAndHidesRequestLocalRefs()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.WorkspaceFrozenPreview.cs"));

        Assert.Contains("AI 工作区精确预览已冻结", source, StringComparison.Ordinal);
        Assert.Contains("设计版本 v", source, StringComparison.Ordinal);
        Assert.Contains("实现证书已通过", source, StringComparison.Ordinal);
        Assert.Contains("主屏", source, StringComparison.Ordinal);
        Assert.Contains("副屏 ", source, StringComparison.Ordinal);
        Assert.Contains("保留区域", source, StringComparison.Ordinal);
        Assert.Contains("FormatWorkspacePreviewCategory", source, StringComparison.Ordinal);
        Assert.Contains("LocalizeWorkspacePreviewLabel", source, StringComparison.Ordinal);

        Assert.DoesNotContain("item.ItemRef", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceFrozenPreviewUi_IsReadOnlyAndRetainedAcrossDeepResident()
    {
        var root = FindRepositoryRoot();
        var selection = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.Selection.cs"));
        var lifecycle = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.Lifecycle.cs"));

        Assert.Contains("_workspaceFrozenPreview is not null", selection, StringComparison.Ordinal);
        Assert.Contains("isWorkspaceFrozenPreview", selection, StringComparison.Ordinal);
        Assert.Contains("SaveLayoutButton.IsEnabled = isEditing && !isAnyFrozenWorkspacePreview", selection, StringComparison.Ordinal);
        Assert.Contains("OverlayToggleButton.IsEnabled = !isAnyFrozenWorkspacePreview", selection, StringComparison.Ordinal);
        Assert.Contains("PreviewLayoutButton.IsEnabled = !isAnyFrozenWorkspacePreview", selection, StringComparison.Ordinal);
        Assert.Contains("_workspaceFrozenPreview is null", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceFrozenPreviewUi_CoexistsWithLegacyLogicalPreviewWithoutRewritingIt()
    {
        var root = FindRepositoryRoot();
        var legacy = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.LogicalWorkspacePreview.cs"));
        var workspace = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.WorkspaceFrozenPreview.cs"));

        Assert.Contains("DesktopLogicalWorkspacePlanningSession", legacy, StringComparison.Ordinal);
        Assert.Contains("DesktopLogicalGridCell", legacy, StringComparison.Ordinal);
        Assert.Contains("ClearAiLogicalWorkspacePreview", workspace, StringComparison.Ordinal);
        Assert.Contains("ClearAiWorkspaceFrozenPreview", workspace, StringComparison.Ordinal);
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
