using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopOverlays;

namespace DesktopAgent.Tests.DesktopOverlays;

public sealed class DesktopZoneOverlayGeometryTests
{
    [Fact]
    public void MapZoneToPixelRect_UsesDisplayWorkAreaAndNormalizedBounds()
    {
        var display = new DisplayDescriptor(
            "\\\\.\\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var zone = new DesktopLayoutZone(
            Guid.NewGuid(),
            "右侧",
            new NormalizedRect(5000, 0, 10000, 10000),
            1,
            ZonePlacementFlow.ColumnMajor,
            null);

        var result = DesktopZoneOverlayHost.MapZoneToPixelRect(display, zone);

        Assert.Equal(new PixelRect(960, 0, 1920, 1040), result);
    }

    [Fact]
    public void MapZoneToPixelRect_PreservesNegativeVirtualDesktopCoordinates()
    {
        var display = new DisplayDescriptor(
            "\\\\.\\DISPLAY2",
            new PixelRect(-1200, 0, 0, 1920),
            new PixelRect(-1200, 0, 0, 1880),
            144,
            144,
            false);
        var zone = new DesktopLayoutZone(
            Guid.NewGuid(),
            "左上",
            new NormalizedRect(0, 0, 5000, 5000),
            0,
            ZonePlacementFlow.RowMajor,
            null);

        var result = DesktopZoneOverlayHost.MapZoneToPixelRect(display, zone);

        Assert.Equal(new PixelRect(-1200, 0, -600, 940), result);
    }
}
