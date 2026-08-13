namespace DesktopAgent.Tests.Productization;

public sealed class DesktopZoneEditingProductBoundaryContractTestsFixed
{
    [Fact]
    public void OrdinaryOverlay_RemainsClickThrough()
    {
        var root = FindRepositoryRoot();
        var overlay = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WindowsInterop",
            "DesktopOverlays",
            "DesktopZoneOverlayHost.cs"));

        Assert.Contains("WsExTransparent", overlay, StringComparison.Ordinal);
        Assert.Contains("HtTransparent", overlay, StringComparison.Ordinal);
        Assert.Contains("不拦截鼠标", overlay, StringComparison.Ordinal);
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
