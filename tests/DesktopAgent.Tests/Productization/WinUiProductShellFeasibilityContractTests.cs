using System.Xml.Linq;

namespace DesktopAgent.Tests.Productization;

public sealed class WinUiProductShellFeasibilityContractTests
{
    [Fact]
    public void Project_UsesX64UnpackagedStableWinUiShape()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "DesktopAgent.WinUI", "DesktopAgent.WinUI.csproj");
        var document = XDocument.Load(projectPath);

        Assert.Equal("true", Property(document, "UseWinUI"));
        Assert.Equal("None", Property(document, "WindowsPackageType"));
        Assert.Equal("x64", Property(document, "PlatformTarget"));
        Assert.Equal("x64", Property(document, "Platforms"));
        Assert.Contains("DISABLE_XAML_GENERATED_MAIN", Property(document, "DefineConstants"));

        var appSdk = document
            .Descendants("PackageReference")
            .Single(element => string.Equals(
                (string?)element.Attribute("Include"),
                "Microsoft.WindowsAppSDK",
                StringComparison.Ordinal));
        Assert.Equal("2.3.1", (string?)appSdk.Attribute("Version"));

        var references = document
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static value => value is not null)
            .ToArray();

        Assert.Contains(references, value => value!.EndsWith("DesktopAgent.Core\\DesktopAgent.Core.csproj", StringComparison.Ordinal));
        Assert.Contains(references, value => value!.EndsWith("DesktopAgent.Application\\DesktopAgent.Application.csproj", StringComparison.Ordinal));
        Assert.Contains(references, value => value!.EndsWith("DesktopAgent.Persistence\\DesktopAgent.Persistence.csproj", StringComparison.Ordinal));
        Assert.Contains(references, value => value!.EndsWith("DesktopAgent.WindowsInterop\\DesktopAgent.WindowsInterop.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(references, value => value!.Contains("DesktopAgent.Poc", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductShell_ContainsSingleInstanceCloseToTrayAndExplicitExitBoundaries()
    {
        var root = FindRepositoryRoot();
        var productRoot = Path.Combine(root, "src", "DesktopAgent.WinUI");

        var program = File.ReadAllText(Path.Combine(productRoot, "Program.cs"));
        Assert.Contains("AppInstance.FindOrRegisterForKey", program, StringComparison.Ordinal);
        Assert.Contains("RedirectActivationToAsync", program, StringComparison.Ordinal);

        var window = File.ReadAllText(Path.Combine(productRoot, "MainWindow.xaml.cs"));
        Assert.Contains("_appWindow.Closing", window, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", window, StringComparison.Ordinal);
        Assert.Contains("_appWindow.Hide()", window, StringComparison.Ordinal);
        Assert.Contains("ExitApplication", window, StringComparison.Ordinal);

        var tray = File.ReadAllText(Path.Combine(productRoot, "TrayIconHost.cs"));
        Assert.Contains("Shell_NotifyIconW", tray, StringComparison.Ordinal);
        Assert.Contains("NimDelete", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void FrozenUpstreamProjects_DoNotReferenceProductUi()
    {
        var root = FindRepositoryRoot();
        var upstreamProjects = new[]
        {
            "src/DesktopAgent.Core/DesktopAgent.Core.csproj",
            "src/DesktopAgent.Application/DesktopAgent.Application.csproj",
            "src/DesktopAgent.Persistence/DesktopAgent.Persistence.csproj",
            "src/DesktopAgent.WindowsInterop/DesktopAgent.WindowsInterop.csproj",
        };

        foreach (var relativePath in upstreamProjects)
        {
            var text = File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("DesktopAgent.WinUI", text, StringComparison.Ordinal);
        }
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
