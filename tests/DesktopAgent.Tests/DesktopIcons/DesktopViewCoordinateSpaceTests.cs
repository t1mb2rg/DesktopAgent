using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopIcons;

/// <summary>
/// 桌面图标视图的坐标空间换算。
///
/// 真机实测（2026-10-14，三屏机器）：桌面列表视图窗口 (-1080,-210,2560,2520)，
/// 客户区原点在屏幕坐标 (-1080,-210)。IFolderView 的 GetItemPosition /
/// SelectAndPositionItems 都用这个视图坐标，产品其余部分用屏幕坐标。
/// </summary>
public sealed class DesktopViewCoordinateSpaceTests
{
    [Fact]
    public void Identity_KeepsPointsUnchanged()
    {
        var space = DesktopViewCoordinateSpace.Identity;

        Assert.Equal(new PixelPoint(14, 2), space.ToScreen(new PixelPoint(14, 2)));
        Assert.Equal(new PixelPoint(14, 2), space.ToView(new PixelPoint(14, 2)));
    }

    [Fact]
    public void ViewOriginOnRealMachine_ShiftsBothWays()
    {
        var space = new DesktopViewCoordinateSpace(new PixelPoint(-1080, -210));

        // 主屏左上角那排图标的视图坐标是 (1218,212)，换算成屏幕坐标应为 (138,2)。
        Assert.Equal(new PixelPoint(138, 2), space.ToScreen(new PixelPoint(1218, 212)));
        Assert.Equal(new PixelPoint(1218, 212), space.ToView(new PixelPoint(138, 2)));

        // 竖屏（原点 -1080,-210）自己的图标：视图 (14,2) 就是屏幕 (-1066,-208)。
        Assert.Equal(new PixelPoint(-1066, -208), space.ToScreen(new PixelPoint(14, 2)));
    }

    [Fact]
    public void RoundTrip_IsLossless()
    {
        var space = new DesktopViewCoordinateSpace(new PixelPoint(-1080, -210));
        var points = new[]
        {
            new PixelPoint(0, 0),
            new PixelPoint(-1066, -208),
            new PixelPoint(138, 2),
            new PixelPoint(2559, 1439),
            new PixelPoint(2228, 2519),
        };

        foreach (var point in points)
            Assert.Equal(point, space.ToView(space.ToScreen(point)));
    }

    [Fact]
    public void ReadAndWritePaths_BothGoThroughTheConversion()
    {
        // 这个 bug 的成因是"读取侧把视图坐标当屏幕坐标用"；写入侧的坑反过来（要把屏幕坐标
        // 换成视图坐标）。两侧都必须走同一个换算点，所以这里钉死源码形状。
        var desktopIcons = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DesktopAgent.WindowsInterop",
            "DesktopIcons");

        var reader = File.ReadAllText(Path.Combine(desktopIcons, "DesktopShellIconReader.cs"));
        var writer = File.ReadAllText(Path.Combine(desktopIcons, "DesktopShellIconPositionWriter.cs"));

        Assert.Contains("coordinateSpace.ToScreen(new PixelPoint(pt.X, pt.Y))", reader, StringComparison.Ordinal);
        Assert.Contains("ctx.SetViewCoordinateSpace(DesktopViewCoordinateSpace.ForWindow(", reader, StringComparison.Ordinal);

        Assert.Contains("var viewTarget = coordinateSpace.ToView(targetPosition);", writer, StringComparison.Ordinal);
        Assert.Contains("new System.Drawing.Point(viewTarget.X, viewTarget.Y)", writer, StringComparison.Ordinal);
        Assert.Contains("coordinateSpace.ToScreen(new PixelPoint(prevPt.X, prevPt.Y))", writer, StringComparison.Ordinal);
        Assert.Contains("coordinateSpace.ToScreen(new PixelPoint(actualPt.X, actualPt.Y))", writer, StringComparison.Ordinal);

        // 原来的写法（原样透传）不许回来。
        Assert.DoesNotContain(
            "new System.Drawing.Point(targetPosition.X, targetPosition.Y)",
            writer,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "DesktopAgent.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
