namespace DesktopAgent.Tests.Productization;

public sealed class OverviewLightweightReadModelContractTests
{
    [Fact]
    public void OverviewReadModel_UsesPersistedSummariesWithoutShellOrExecutionDependencies()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "OverviewLightweightReadModel.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("JsonDesktopProductSettingsStore", source, StringComparison.Ordinal);
        Assert.Contains("SqliteDesktopLayoutTemplateStore", source, StringComparison.Ordinal);
        Assert.Contains("SqliteDesktopPendingActionStore", source, StringComparison.Ordinal);
        Assert.Contains("CountAsync", source, StringComparison.Ordinal);

        Assert.DoesNotContain("WindowsDesktopObservationSource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconReader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPosition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopZoneOverlayHost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAiCompatible", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Writer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OverviewRefresh_IsEventDrivenAndDoesNotPoll()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "MainWindow.Overview.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("HomeView_Loaded", source, StringComparison.Ordinal);
        Assert.Contains("RegisterPropertyChangedCallback", source, StringComparison.Ordinal);
        Assert.Contains("Visibility.Visible", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", source, StringComparison.Ordinal);
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
