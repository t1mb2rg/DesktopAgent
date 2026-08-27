namespace DesktopAgent.Tests.Productization;

public sealed class LogicalWorkspacePreviewReadabilityContractTests
{
    [Fact]
    public void LogicalWorkspacePreview_CapsFocusedMovementLinesAndRedrawsRegionOutlines()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.LogicalWorkspacePreview.cs"));

        Assert.Contains("MaxLogicalPreviewMovementLines = 32", source, StringComparison.Ordinal);
        Assert.Contains("selectedDisplayRef", source, StringComparison.Ordinal);
        Assert.Contains("movementLinesDrawn", source, StringComparison.Ordinal);
        Assert.Contains("AddLogicalRegionOutline", source, StringComparison.Ordinal);
        Assert.Contains("const double size = 5d", source, StringComparison.Ordinal);
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
