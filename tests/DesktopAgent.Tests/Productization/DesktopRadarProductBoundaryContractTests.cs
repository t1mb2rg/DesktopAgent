namespace DesktopAgent.Tests.Productization;

public sealed class DesktopRadarProductBoundaryContractTests
{
    [Fact]
    public void Overview_RadarServiceIsLazyAndRefreshIsEventDriven()
    {
        var root = FindRepositoryRoot();
        var overview = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "MainWindow.Overview.cs"));

        Assert.Contains("private DesktopRadarOverviewService? _desktopRadarOverviewService;", overview, StringComparison.Ordinal);
        Assert.Contains("_desktopRadarOverviewService ??= new DesktopRadarOverviewService()", overview, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private readonly DesktopRadarOverviewService _desktopRadarOverviewService = new();",
            overview,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", overview, StringComparison.Ordinal);
        Assert.Contains("RefreshRadar_Click", overview, StringComparison.Ordinal);
        Assert.Contains("ShouldRefresh", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void RadarPresentationStore_DoesNotReferenceExecutionOrDesktopItemIdentityTypes()
    {
        var root = FindRepositoryRoot();
        var store = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.Persistence",
            "Productization",
            "JsonDesktopRadarSnapshotStore.cs"));

        Assert.Contains("presentation-only", store, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DesktopIconDescriptor", store, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopIconMove", store, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", store, StringComparison.Ordinal);
        Assert.DoesNotContain("WriterCommand", store, StringComparison.Ordinal);
        Assert.DoesNotContain("StableId", store, StringComparison.Ordinal);
        Assert.DoesNotContain("ParsingName", store, StringComparison.Ordinal);
        Assert.DoesNotContain("PixelPosition", store, StringComparison.Ordinal);
    }

    [Fact]
    public void RadarBuilder_IsOneWayCompressionFromObservationIntoPresentationSnapshot()
    {
        var root = FindRepositoryRoot();
        var builder = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.Core",
            "Productization",
            "DesktopRadarSnapshotBuilder.cs"));

        Assert.Contains("DesktopObservation observation", builder, StringComparison.Ordinal);
        Assert.Contains("DesktopRadarSnapshot Build", builder, StringComparison.Ordinal);
        Assert.Contains("BinIcons", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopIconMovePlanner", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("DesktopShellIconPositionWriter", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Recovery", builder.Split("/// </summary>", 2, StringSplitOptions.None).Last(), StringComparison.Ordinal);
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
