using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopIcons;

public sealed class DisplayAssignmentServiceTests
{
    private static DisplayDescriptor CreateDisplay(
        string deviceName,
        int left, int top, int right, int bottom,
        bool isPrimary = false) =>
        new(
            deviceName,
            new PixelRect(left, top, right, bottom),
            new PixelRect(left, top, right, bottom),
            96, 96,
            isPrimary);

    [Fact]
    public void AssignMonitor_EmptyDisplays_ReturnsNull()
    {
        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(100, 100), []);

        Assert.Null(result);
    }

    [Fact]
    public void AssignMonitor_PointInsidePrimaryDisplay_ReturnsPrimary()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, 0, 2560, 1440, isPrimary: true),
        };

        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(1280, 720), displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName);
    }

    [Fact]
    public void AssignMonitor_PointInsideSecondaryNegativeX_ReturnsCorrectDisplay()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", -1920, 0, 0, 1080, isPrimary: false),
            CreateDisplay(@"\\.\DISPLAY2", 0, 0, 2560, 1440, isPrimary: true),
        };

        // 图标在左侧副屏
        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(-500, 500), displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName);
    }

    [Fact]
    public void AssignMonitor_PointInsideSecondaryNegativeY_ReturnsCorrectDisplay()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, -1440, 2560, 0, isPrimary: false),
            CreateDisplay(@"\\.\DISPLAY2", 0, 0, 2560, 1440, isPrimary: true),
        };

        // 图标在上方副屏
        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(1280, -200), displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName);
    }

    [Fact]
    public void AssignMonitor_ThreeDisplays_MixedCoordinates()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", -3840, 0, -1920, 1080, isPrimary: false),
            CreateDisplay(@"\\.\DISPLAY2", -1920, 0, 0, 1080, isPrimary: false),
            CreateDisplay(@"\\.\DISPLAY3", 0, 0, 2560, 1440, isPrimary: true),
        };

        // 图标在中间副屏
        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(-960, 540), displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY2", result.DeviceName);
    }

    [Fact]
    public void AssignMonitor_PointOnLeftBoundary_StillInside()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, 0, 1920, 1080, isPrimary: true),
        };

        // 在左边界上（包含）
        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(0, 500), displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName);
    }

    [Fact]
    public void AssignMonitor_PointOnTopBoundary_StillInside()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, 0, 1920, 1080, isPrimary: true),
        };

        // 在上边界上（包含）
        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(500, 0), displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName);
    }

    [Fact]
    public void AssignMonitor_PointOutsideAllDisplays_ReturnsNearest()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, 0, 1920, 1080, isPrimary: true),
            CreateDisplay(@"\\.\DISPLAY2", 1920, 0, 3840, 1080, isPrimary: false),
        };

        // 图标在 DISPLAY1 左侧远处（不在任何显示器内）
        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(-1000, 500), displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName); // 最近
    }

    [Fact]
    public void AssignMonitor_PointOutside_NearestIsSecondary()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, 0, 1920, 1080, isPrimary: true),
            CreateDisplay(@"\\.\DISPLAY2", 1920, 0, 3840, 1080, isPrimary: false),
        };

        // 图标在 DISPLAY2 右侧远处
        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(5000, 500), displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY2", result.DeviceName); // 最近
    }

    [Fact]
    public void AssignMonitor_Equidistant_ReturnsStableResult()
    {
        // 两个显示器上下排列，点在正中间 → X 方向距离相同，Y 方向一侧更近
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, 0, 1000, 1000, isPrimary: true),
            CreateDisplay(@"\\.\DISPLAY2", 0, 2000, 1000, 3000, isPrimary: false),
        };

        // 点在两个显示器正中间的 X 轴上
        var result = DisplayAssignmentService.AssignMonitor(
            new PixelPoint(500, 1500), displays);

        Assert.NotNull(result);
        // DISPLAY1 更近（点在下边界下方 500+1 像素 vs DISPLAY2 上边界上方 500 像素）
        Assert.Equal(@"\\.\DISPLAY2", result.DeviceName);
    }

    [Fact]
    public void SquaredDistanceToRect_PointInside_ReturnsZero()
    {
        var rect = new PixelRect(0, 0, 1920, 1080);
        var point = new PixelPoint(960, 540);

        var dist = DisplayAssignmentService.SquaredDistanceToRect(point, rect);

        Assert.Equal(0, dist);
    }

    [Fact]
    public void SquaredDistanceToRect_PointOutsideRight_ReturnsCorrectDistance()
    {
        var rect = new PixelRect(0, 0, 1920, 1080);
        var point = new PixelPoint(2000, 500); // 右边界外 81px

        var dist = DisplayAssignmentService.SquaredDistanceToRect(point, rect);

        Assert.Equal(81L * 81L, dist);
    }

    [Fact]
    public void SquaredDistanceToRect_PointOutsideLeft_ReturnsCorrectDistance()
    {
        var rect = new PixelRect(0, 0, 1920, 1080);
        var point = new PixelPoint(-100, 500); // 左边界外 100px

        var dist = DisplayAssignmentService.SquaredDistanceToRect(point, rect);

        Assert.Equal(100L * 100L, dist);
    }

    [Fact]
    public void SquaredDistanceToRect_PointOutsideTopLeft_ReturnsDiagonalDistance()
    {
        var rect = new PixelRect(0, 0, 1920, 1080);
        var point = new PixelPoint(-100, -200); // 左上角外

        var dist = DisplayAssignmentService.SquaredDistanceToRect(point, rect);

        Assert.Equal(100L * 100L + 200L * 200L, dist);
    }

    [Fact]
    public void SquaredDistanceToRect_NegativeRectOrigin()
    {
        var rect = new PixelRect(-1920, 0, 0, 1080);
        var point = new PixelPoint(-2500, 500); // 左边界外 580px

        var dist = DisplayAssignmentService.SquaredDistanceToRect(point, rect);

        Assert.Equal(580L * 580L, dist);
    }
}
