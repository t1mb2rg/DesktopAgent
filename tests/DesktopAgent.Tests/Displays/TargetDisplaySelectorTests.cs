using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.Displays;

public sealed class TargetDisplaySelectorTests
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
    public void SelectTarget_WithNoDisplays_ReturnsNull()
    {
        var result = TargetDisplaySelector.SelectTarget([]);

        Assert.Null(result);
    }

    [Fact]
    public void SelectTarget_WithSingleDisplay_ReturnsThatDisplay()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, 0, 1920, 1080, isPrimary: true),
        };

        var result = TargetDisplaySelector.SelectTarget(displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName);
    }

    [Fact]
    public void SelectTarget_DefaultsToPrimaryDisplay()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", -1920, 0, 0, 1080, isPrimary: false),
            CreateDisplay(@"\\.\DISPLAY2", 0, 0, 2560, 1440, isPrimary: true),
            CreateDisplay(@"\\.\DISPLAY3", 2560, 0, 4480, 1440, isPrimary: false),
        };

        var result = TargetDisplaySelector.SelectTarget(displays);

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY2", result.DeviceName);
        Assert.True(result.IsPrimary);
    }

    [Fact]
    public void SelectTarget_PreservesPreviousSelection_WhenDeviceStillPresent()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, 0, 1920, 1080, isPrimary: false),
            CreateDisplay(@"\\.\DISPLAY2", 1920, 0, 4480, 1440, isPrimary: true),
        };

        // 用户之前选择了 DISPLAY1（非主屏）
        var result = TargetDisplaySelector.SelectTarget(displays, previousDeviceName: @"\\.\DISPLAY1");

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName);
        Assert.False(result.IsPrimary);
    }

    [Fact]
    public void SelectTarget_FallsBackToPrimary_WhenPreviousDeviceGone()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY2", 0, 0, 2560, 1440, isPrimary: true),
            CreateDisplay(@"\\.\DISPLAY3", 2560, 0, 4480, 1440, isPrimary: false),
        };

        // 用户之前选择的 DISPLAY1 已断开
        var result = TargetDisplaySelector.SelectTarget(displays, previousDeviceName: @"\\.\DISPLAY1");

        Assert.NotNull(result);
        Assert.True(result.IsPrimary);
        Assert.Equal(@"\\.\DISPLAY2", result.DeviceName);
    }

    [Fact]
    public void SelectTarget_FallsBackToFirstDisplay_WhenNoPrimaryAndPreviousGone()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY2", -1920, 0, 0, 1080, isPrimary: false),
            CreateDisplay(@"\\.\DISPLAY3", 0, 0, 1920, 1080, isPrimary: false),
        };

        // 没有主屏，且之前选择已断开
        var result = TargetDisplaySelector.SelectTarget(displays, previousDeviceName: @"\\.\DISPLAY1");

        Assert.NotNull(result);
        // 回退到列表第一块显示器
        Assert.Equal(@"\\.\DISPLAY2", result.DeviceName);
    }

    [Fact]
    public void SelectTarget_PreservesPrevious_WhenPrimaryAlsoExists()
    {
        // 用户明确选择了非主屏的副屏，刷新后应保留
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", -1920, 0, 0, 1080, isPrimary: false),
            CreateDisplay(@"\\.\DISPLAY2", 0, 0, 2560, 1440, isPrimary: true),
        };

        var result = TargetDisplaySelector.SelectTarget(displays, previousDeviceName: @"\\.\DISPLAY1");

        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result.DeviceName);
    }

    [Fact]
    public void SelectTarget_WithNullPreviousDeviceName_DefaultsToPrimary()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", -1920, 0, 0, 1080, isPrimary: false),
            CreateDisplay(@"\\.\DISPLAY2", 0, 0, 2560, 1440, isPrimary: true),
        };

        var result = TargetDisplaySelector.SelectTarget(displays, previousDeviceName: null);

        Assert.NotNull(result);
        Assert.True(result.IsPrimary);
    }

    [Fact]
    public void SelectTarget_WithEmptyPreviousDeviceName_DefaultsToPrimary()
    {
        var displays = new[]
        {
            CreateDisplay(@"\\.\DISPLAY1", 0, 0, 2560, 1440, isPrimary: true),
        };

        var result = TargetDisplaySelector.SelectTarget(displays, previousDeviceName: string.Empty);

        Assert.NotNull(result);
        Assert.True(result.IsPrimary);
    }
}
