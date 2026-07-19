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
    public void Center_ReturnsExpectedPoint()
    {
        var rect = new PixelRect(0, 0, 2560, 1440);

        Assert.Equal(new PixelPoint(1280, 720), rect.Center);
    }
}
