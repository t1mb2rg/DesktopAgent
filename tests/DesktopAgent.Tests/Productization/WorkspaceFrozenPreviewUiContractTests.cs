namespace DesktopAgent.Tests.Productization;

public sealed class WorkspaceFrozenPreviewUiContractTests
{
    [Fact]
    public void WorkspaceFrozenPreviewUi_UsesExactPixelRealization_AndNeverEnablesWriterAuthority()
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
        Assert.Contains("MaxWorkspaceFrozenPreviewMovementLines = 32", source, StringComparison.Ordinal);
        Assert.Contains("MaxWorkspaceFrozenPreviewItemDots = 180", source, StringComparison.Ordinal);
        Assert.Contains("ApplyToDesktopButton.IsEnabled = false", source, StringComparison.Ordinal);

        Assert.DoesNotContain("DesktopLogicalGridCell", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopDesiredWorkspacePlan", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopLayoutDirectApplyService", source, StringComparison.Ordinal);
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
