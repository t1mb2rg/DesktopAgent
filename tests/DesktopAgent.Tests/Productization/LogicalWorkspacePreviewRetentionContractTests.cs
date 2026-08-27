namespace DesktopAgent.Tests.Productization;

public sealed class LogicalWorkspacePreviewRetentionContractTests
{
    [Fact]
    public void DeepResident_DoesNotReleaseLayoutViewWhileFrozenLogicalPreviewExists()
    {
        var root = FindRepositoryRoot();
        var lifecycle = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.Lifecycle.cs"));

        Assert.Contains("_logicalWorkspaceSession is null", lifecycle, StringComparison.Ordinal);
        Assert.Contains("CanReleaseForDeepResident", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void NewSuccessfulPlanningSession_PreparesReadOnlyPreviewReplacementBeforeLoading()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "MainWindow.AiAssistant.cs"));
        var replacement = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.LogicalWorkspaceReplacement.cs"));

        var prepare = mainWindow.IndexOf(
            "PrepareLogicalWorkspacePreviewReplacement();",
            StringComparison.Ordinal);
        var load = mainWindow.IndexOf(
            "TryLoadAiLogicalWorkspacePreview(session);",
            StringComparison.Ordinal);

        Assert.True(prepare >= 0);
        Assert.True(load > prepare);
        Assert.Contains(
            "if (_logicalWorkspaceSession is not null && _draft is null)",
            replacement,
            StringComparison.Ordinal);
        Assert.Contains("_isLayoutEditing = false;", replacement, StringComparison.Ordinal);
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
