using System.Xml.Linq;
using DesktopAgent.Poc;

namespace DesktopAgent.Tests.Productization;

public sealed class DiagnosticsWorkbenchSplitContractTests
{
    [Fact]
    public void DiagnosticsProject_HasDedicatedProcessIdentity_WithoutRenamingHistoricalNamespace()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "DesktopAgent.Poc", "DesktopAgent.Poc.csproj");
        var document = XDocument.Load(projectPath);

        Assert.Equal("DesktopAgent.Poc", Property(document, "RootNamespace"));
        Assert.Equal("DesktopAgent.Diagnostics", Property(document, "AssemblyName"));
        Assert.Equal("DesktopAgent Diagnostics Workbench", Property(document, "Product"));

        var solution = File.ReadAllText(Path.Combine(root, "DesktopAgent.sln"));
        Assert.Contains(
            "= \"DesktopAgent.Diagnostics\", \"src\\DesktopAgent.Poc\\DesktopAgent.Poc.csproj\"",
            solution,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsWindow_AppliesWorkbenchIdentityFromItsOwnFirstRenderedFrame()
    {
        var root = FindRepositoryRoot();
        var identityPath = Path.Combine(
            root,
            "src",
            "DesktopAgent.Poc",
            "MainWindow.DiagnosticsWorkbenchIdentity.cs");
        var source = File.ReadAllText(identityPath);

        Assert.Contains("override void OnContentRendered", source, StringComparison.Ordinal);
        Assert.Contains("DesktopAgent Diagnostics Workbench", source, StringComparison.Ordinal);
        Assert.Contains("工程诊断工作台", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleDiagnosticsWorkbenchLifecycle", source, StringComparison.Ordinal);

        var appSplit = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.Poc",
            "App.DiagnosticsWorkbenchSplit.cs"));
        Assert.DoesNotContain("OnActivated", appSplit, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.ApplicationIdle", appSplit, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsInterop_PreservesInternalExecutionGateForDiagnosticsOnlyByFriendAssembly()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(
            root,
            "src",
            "DesktopAgent.WindowsInterop",
            "DesktopAgent.WindowsInterop.csproj");
        var document = XDocument.Load(projectPath);

        var friends = document
            .Descendants("InternalsVisibleTo")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static value => value is not null)
            .ToArray();

        Assert.Contains("DesktopAgent.Diagnostics", friends);
        Assert.Contains("DesktopAgent.Tests", friends);
    }

    [Fact]
    public void NormalDiagnostics_IsForegroundTool_NotSecondResidentProductTray()
    {
        var profile = DiagnosticsWorkbenchRuntimeProfile.For(automaticAcceptanceArmed: false);

        Assert.False(profile.KeepResidentTray);
        Assert.True(profile.ShutdownOnMainWindowClose);
    }

    [Fact]
    public void ExplicitAutomaticAcceptance_MayKeepHistoricalResidentTray()
    {
        var profile = DiagnosticsWorkbenchRuntimeProfile.For(automaticAcceptanceArmed: true);

        Assert.True(profile.KeepResidentTray);
        Assert.False(profile.ShutdownOnMainWindowClose);
    }

    [Fact]
    public void ProductWinUi_DoesNotReferenceDiagnosticsWorkbenchProject()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "DesktopAgent.WinUI", "DesktopAgent.WinUI.csproj");
        var document = XDocument.Load(projectPath);

        var references = document
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static value => value is not null)
            .ToArray();

        Assert.DoesNotContain(
            references,
            value => value!.Contains("DesktopAgent.Poc", StringComparison.Ordinal)
                || value.Contains("DesktopAgent.Diagnostics", StringComparison.Ordinal));
    }

    private static string Property(XDocument document, string name)
        => document
            .Descendants(name)
            .Select(static element => element.Value.Trim())
            .FirstOrDefault()
            ?? throw new Xunit.Sdk.XunitException($"缺少项目属性 {name}。");

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
