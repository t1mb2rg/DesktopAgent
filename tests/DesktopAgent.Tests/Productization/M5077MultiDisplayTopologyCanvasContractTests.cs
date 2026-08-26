namespace DesktopAgent.Tests.Productization;

public sealed class M5077MultiDisplayTopologyCanvasContractTests
{
    [Fact]
    public void LayoutWorkbench_MultiDisplayTopology_ReusesVirtualDesktopGeometryWithoutWriterAuthority()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "DesktopAgent.WinUI", "LayoutManagementView.TopologyCanvas.cs"));

        Assert.Contains("virtualLeft", source, StringComparison.Ordinal);
        Assert.Contains("virtualTop", source, StringComparison.Ordinal);
        Assert.Contains("item.Display.Bounds.Left", source, StringComparison.Ordinal);
        Assert.Contains("item.Display.Bounds.Top", source, StringComparison.Ordinal);
        Assert.Contains("var bounds = display.Bounds;", source, StringComparison.Ordinal);
        Assert.Contains("X(bounds.Left)", source, StringComparison.Ordinal);
        Assert.Contains("Y(bounds.Top)", source, StringComparison.Ordinal);
        Assert.Contains("state.Draft.Zones", source, StringComparison.Ordinal);
        Assert.Contains("AiWorkspaceDisplayCombo.SelectedIndex", source, StringComparison.Ordinal);
        Assert.Contains("RenderActivePreviewAcrossTopology", source, StringComparison.Ordinal);
        Assert.Contains("var current = item.CurrentPosition;", source, StringComparison.Ordinal);
        Assert.Contains("X1 = currentX", source, StringComparison.Ordinal);
        Assert.Contains("Y1 = currentY", source, StringComparison.Ordinal);
        Assert.Contains("X2 = targetX", source, StringComparison.Ordinal);
        Assert.Contains("Y2 = targetY", source, StringComparison.Ordinal);
        Assert.Contains("item.Icon.MonitorDeviceName == _latestPreview.Plan.TargetDisplay.DeviceName", source, StringComparison.Ordinal);

        Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetDisplayLayoutExecutionCoordinator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveDesktopIcon", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutWorkbench_TopologyIsOnlyEnabledForCompositeWorkspace()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "DesktopAgent.WinUI", "LayoutManagementView.TopologyCanvas.cs"));

        Assert.Contains("!IsAiMultiDisplayWorkspaceActive", source, StringComparison.Ordinal);
        Assert.Contains("_aiWorkspaceDrafts.Count <= 1", source, StringComparison.Ordinal);
        Assert.Contains("HasTopologyMarker", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopAgent.sln")))
                return current.FullName;
        }

        throw new InvalidOperationException("找不到 DesktopAgent.sln。");
    }
}
