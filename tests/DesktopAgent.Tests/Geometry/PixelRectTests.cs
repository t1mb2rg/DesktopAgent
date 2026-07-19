using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.Geometry;

public sealed class PixelRectTests
{
    [Fact]
    public void Contains_SupportsNegativeVirtualDesktopCoordinates()
    {
        var secondaryDisplay = new PixelRect(-1920, 0, 0, 1080);

        Assert.True(secondaryDisplay.Contains(new PixelPoint(-100, 500)));
        Assert.False(secondaryDisplay.Contains(new PixelPoint(100, 500)));
    }

    [Fact]
    public void Contains_SupportsNegativeY_DisplayAbovePrimary()
    {
        // 副屏位于主屏上方：Y 坐标为负
        var aboveDisplay = new PixelRect(0, -1440, 2560, 0);

        Assert.True(aboveDisplay.Contains(new PixelPoint(1280, -200)));
        Assert.False(aboveDisplay.Contains(new PixelPoint(1280, 200)));
    }

    [Fact]
    public void Contains_Boundaries_ExclusiveRightAndBottom()
    {
        var rect = new PixelRect(0, 0, 1920, 1080);

        Assert.True(rect.Contains(new PixelPoint(0, 0)));
        Assert.True(rect.Contains(new PixelPoint(1919, 1079)));
        Assert.False(rect.Contains(new PixelPoint(1920, 0)));
        Assert.False(rect.Contains(new PixelPoint(0, 1080)));
    }

    [Fact]
    public void Contains_NegativeRectBoundaries()
    {
        var rect = new PixelRect(-3840, -2160, -1920, -1080);

        Assert.True(rect.Contains(new PixelPoint(-3840, -2160)));
        Assert.True(rect.Contains(new PixelPoint(-1921, -1081)));
        Assert.False(rect.Contains(new PixelPoint(-1920, -1080)));
        Assert.False(rect.Contains(new PixelPoint(-4000, -2000)));
    }

    [Fact]
    public void Center_ReturnsExpectedPoint()
    {
        var rect = new PixelRect(0, 0, 2560, 1440);

        Assert.Equal(new PixelPoint(1280, 720), rect.Center);
    }

    [Fact]
    public void Center_HandlesNegativeCoordinates()
    {
        var rect = new PixelRect(-1920, 0, 0, 1080);

        Assert.Equal(new PixelPoint(-960, 540), rect.Center);
    }

    [Fact]
    public void Center_HandlesOddDimensions()
    {
        // Width=1921, Height=1081 → center rounds toward zero (integer division)
        var rect = new PixelRect(0, 0, 1921, 1081);

        Assert.Equal(new PixelPoint(960, 540), rect.Center);
    }

    [Fact]
    public void WidthAndHeight_CalculateCorrectly()
    {
        var rect = new PixelRect(-3840, -2160, -1920, -1080);

        Assert.Equal(1920, rect.Width);
        Assert.Equal(1080, rect.Height);
    }

    [Fact]
    public void WidthAndHeight_WithMixedSignCoordinates()
    {
        // 跨主屏边界的显示器：从负坐标到正坐标
        var rect = new PixelRect(-1920, -600, 2560, 1440);

        Assert.Equal(4480, rect.Width);
        Assert.Equal(2040, rect.Height);
    }

    [Fact]
    public void DifferentResolutionDisplays_HaveCorrectBounds()
    {
        // 模拟：主屏 4K @ 200%, 副屏 1080p 位于左侧
        var primary4k = new PixelRect(0, 0, 3840, 2160);
        var secondary1080p = new PixelRect(-1920, 560, 0, 1640);

        Assert.Equal(3840, primary4k.Width);
        Assert.Equal(2160, primary4k.Height);
        Assert.Equal(1920, secondary1080p.Width);
        Assert.Equal(1080, secondary1080p.Height);

        // 副屏不与主屏顶端对齐
        Assert.Equal(new PixelPoint(-960, 1100), secondary1080p.Center);
    }
}
