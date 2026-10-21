namespace DesktopAgent.Tests.Productization;

/// <summary>
/// 校准从"工程诊断工具"变成"客户看得见的面板"之后必须保住的边界：
/// 入口在产品壳里，但探测运行器住在共享的 WindowsInterop 层，
/// 而不是让产品去引用诊断工作台工程。
/// </summary>
public sealed class ProductGridCalibrationSurfaceContractTests
{
    [Fact]
    public void ProductLayoutSurface_ExposesCalibrationNextToTheAuthorizationsEntry()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.xaml"));

        Assert.Contains("x:Name=\"GridProbeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("校准桌面网格", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RunGridProbe_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GridDisplayAuthorizationsButton\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CalibrationPanel_RequiresExplicitConsentBeforeAnyPhysicalWrite()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.GridProbe.cs"));

        Assert.Contains("IsPrimaryButtonEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("consent.Checked", source, StringComparison.Ordinal);
        Assert.Contains("开始校准", source, StringComparison.Ordinal);
        Assert.Contains("改变显示器拓扑", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CalibrationPanel_ReusesTheSharedSessionInsteadOfItsOwnReportFormatting()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.GridProbe.cs"));

        Assert.Contains("ExplorerGridProbeSession", source, StringComparison.Ordinal);
        Assert.DoesNotContain("=== 网格事实写入 ===", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CalibrationPanel_KeepsThePhysicalWriterInTheSharedLayer()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.GridProbe.cs"));

        Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", source, StringComparison.Ordinal);
        Assert.Contains("ExplorerGridPhysicalProbeRunnerFactory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CalibrationPanel_RequiresAllDisplaysInsteadOfSupervisedFallback()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.GridProbe.cs"));

        Assert.Contains("完成全部显示器校准后才能进入产品", source, StringComparison.Ordinal);
        Assert.DoesNotContain("只能监督执行", source, StringComparison.Ordinal);
        Assert.Contains("不会用推断顶替", source, StringComparison.Ordinal);
        Assert.Contains("MeasuredDisplayCount", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeRunner_LivesInSharedWindowsInterop_NotInTheDiagnosticsWorkbench()
    {
        var root = FindRepositoryRoot();
        var shared = Path.Combine(root, "src", "DesktopAgent.WindowsInterop", "ExplorerGridPhysicalProbe");

        Assert.True(File.Exists(Path.Combine(shared, "ExplorerGridPhysicalProbeRunner.cs")));
        Assert.True(File.Exists(Path.Combine(shared, "ExplorerGridProbeSession.cs")));
        Assert.True(File.Exists(Path.Combine(shared, "ExplorerGridProbePlan.cs")));
        Assert.False(Directory.Exists(Path.Combine(root, "src", "DesktopAgent.Poc", "ExplorerGridPhysicalProbe")));
    }

    [Fact]
    public void DiagnosticsWorkbench_StillHostsTheProbePage_AndSharesTheSameSession()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.Poc",
            "MainWindow.ExplorerGridPhysicalProbe.cs"));

        Assert.Contains("ExplorerGridProbeSession.RecordFactsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ExplorerGridProbeSession.PersistReport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("=== 网格事实写入 ===", source, StringComparison.Ordinal);
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
