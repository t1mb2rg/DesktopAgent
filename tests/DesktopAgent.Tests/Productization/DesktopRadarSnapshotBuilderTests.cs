using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Productization;

namespace DesktopAgent.Tests.Productization;

public sealed class DesktopRadarSnapshotBuilderTests
{
    [Fact]
    public void Build_AggregatesFreshObservationIntoLowResolutionPresentationData()
    {
        var display = new DisplayDescriptor(
            "DISPLAY-A",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var observation = CreateObservation(
            new[] { display },
            new[]
            {
                CreateIcon("item-a", new PixelPoint(100, 100), "DISPLAY-A"),
                CreateIcon("item-b", new PixelPoint(130, 120), "DISPLAY-A"),
                CreateIcon("item-c", new PixelPoint(1500, 800), "DISPLAY-A"),
                CreateIcon("item-d", new PixelPoint(1700, 900), monitor: null),
            });

        var snapshot = new DesktopRadarSnapshotBuilder().Build(observation);
        var radarDisplay = Assert.Single(snapshot.Displays);

        Assert.Equal(observation.ObservationId, snapshot.ObservationId);
        Assert.Equal(4, snapshot.TotalItemCount);
        Assert.Equal("DISPLAY-A", radarDisplay.DisplayKey);
        Assert.Equal(display.Bounds, radarDisplay.Bounds);
        Assert.Equal(4, radarDisplay.ItemCount);
        Assert.NotEmpty(radarDisplay.DensityCells);
        Assert.All(radarDisplay.DensityCells, cell =>
        {
            Assert.InRange(cell.Column, 0, radarDisplay.GridColumns - 1);
            Assert.InRange(cell.Row, 0, radarDisplay.GridRows - 1);
        });
    }

    [Fact]
    public void Build_SelectsGridIndependentlyForLandscapeAndPortraitDisplays()
    {
        var landscape = new DisplayDescriptor(
            "LANDSCAPE",
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(0, 0, 2560, 1400),
            96,
            96,
            true);
        var portrait = new DisplayDescriptor(
            "PORTRAIT",
            new PixelRect(-1080, 0, 0, 1920),
            new PixelRect(-1080, 0, 0, 1880),
            96,
            96,
            false);
        var icons = Enumerable.Range(0, 24)
            .Select(index => index < 14
                ? CreateIcon(
                    $"land-{index}",
                    new PixelPoint(80 + ((index % 7) * 300), 80 + ((index / 7) * 420)),
                    "LANDSCAPE")
                : CreateIcon(
                    $"port-{index}",
                    new PixelPoint(-1000 + (((index - 14) % 3) * 300), 100 + (((index - 14) / 3) * 420)),
                    "PORTRAIT"))
            .ToArray();

        var snapshot = new DesktopRadarSnapshotBuilder().Build(
            CreateObservation(new[] { landscape, portrait }, icons));
        var landscapeRadar = snapshot.Displays.Single(display => display.DisplayKey == "LANDSCAPE");
        var portraitRadar = snapshot.Displays.Single(display => display.DisplayKey == "PORTRAIT");

        Assert.True(landscapeRadar.GridColumns > landscapeRadar.GridRows);
        Assert.True(portraitRadar.GridRows > portraitRadar.GridColumns);
        Assert.Equal(14, landscapeRadar.ItemCount);
        Assert.Equal(10, portraitRadar.ItemCount);
    }

    [Fact]
    public void Build_ReusesPreviousGridForSmallDesktopChanges()
    {
        var display = new DisplayDescriptor(
            "DISPLAY-A",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var originalIcons = Enumerable.Range(0, 18)
            .Select(index => CreateIcon(
                $"item-{index}",
                new PixelPoint(70 + ((index % 6) * 280), 80 + ((index / 6) * 300)),
                "DISPLAY-A"))
            .ToArray();
        var builder = new DesktopRadarSnapshotBuilder();
        var first = builder.Build(CreateObservation(new[] { display }, originalIcons));

        var changedIcons = originalIcons
            .Append(CreateIcon("item-new", new PixelPoint(1750, 950), "DISPLAY-A"))
            .ToArray();
        var second = builder.Build(
            CreateObservation(new[] { display }, changedIcons),
            previousSnapshot: first);

        Assert.Equal(first.Displays[0].GridColumns, second.Displays[0].GridColumns);
        Assert.Equal(first.Displays[0].GridRows, second.Displays[0].GridRows);
    }

    [Fact]
    public void Build_TopologyFingerprintIsIndependentOfObservationDisplayOrder()
    {
        var left = new DisplayDescriptor(
            "LEFT",
            new PixelRect(-1080, 0, 0, 1920),
            new PixelRect(-1080, 0, 0, 1880),
            96,
            96,
            false);
        var main = new DisplayDescriptor(
            "MAIN",
            new PixelRect(0, 200, 2560, 1640),
            new PixelRect(0, 200, 2560, 1600),
            96,
            96,
            true);
        var builder = new DesktopRadarSnapshotBuilder();

        var first = builder.Build(CreateObservation(new[] { left, main }, Array.Empty<DesktopIconDescriptor>()));
        var second = builder.Build(CreateObservation(new[] { main, left }, Array.Empty<DesktopIconDescriptor>()));

        Assert.Equal(first.DisplayTopologyFingerprint, second.DisplayTopologyFingerprint);
    }

    private static DesktopObservation CreateObservation(
        IReadOnlyList<DisplayDescriptor> displays,
        IReadOnlyList<DesktopIconDescriptor> icons)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            displays,
            new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled: false,
                new PixelPoint(75, 75),
                ConnectionMethod.FindWindowSW,
                primaryPathException: null),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(10));

    private static DesktopIconDescriptor CreateIcon(
        string id,
        PixelPoint position,
        string? monitor)
        => new(
            id,
            $"{id}.txt",
            $"C:\\Desktop\\{id}.txt",
            ItemKind.File,
            position,
            monitor,
            IsSystemItem: false,
            IsFileSystemItem: true);
}
