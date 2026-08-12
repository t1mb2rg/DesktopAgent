using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Productization;

namespace DesktopAgent.Tests.Productization;

public sealed class DesktopRadarSnapshotTests
{
    [Fact]
    public void Snapshot_DefensivelyCopiesPresentationOnlyDensityData()
    {
        var cells = new List<DesktopRadarDensityCell>
        {
            new(1, 1, DesktopRadarDensityLevel.Low),
            new(2, 2, DesktopRadarDensityLevel.High),
        };
        var displays = new List<DesktopRadarDisplaySnapshot>
        {
            new(
                "DISPLAY-A",
                new PixelRect(-1080, 0, 0, 1920),
                isPrimary: false,
                itemCount: 7,
                gridColumns: 5,
                gridRows: 10,
                cells),
            new(
                "DISPLAY-B",
                new PixelRect(0, 0, 2560, 1440),
                isPrimary: true,
                itemCount: 18,
                gridColumns: 12,
                gridRows: 7,
                new[] { new DesktopRadarDensityCell(4, 3, DesktopRadarDensityLevel.Medium) }),
        };

        var observationId = Guid.NewGuid();
        var layoutId = Guid.NewGuid();
        var snapshot = new DesktopRadarSnapshot(
            observationId,
            new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.FromHours(8)),
            "topology:v1:test",
            layoutId,
            layoutVersion: 4,
            displays);

        cells.Clear();
        displays.Clear();

        Assert.Equal(observationId, snapshot.ObservationId);
        Assert.Equal(TimeSpan.Zero, snapshot.ObservedAtUtc.Offset);
        Assert.Equal(layoutId, snapshot.LayoutTemplateId);
        Assert.Equal(4, snapshot.LayoutVersion);
        Assert.Equal(25, snapshot.TotalItemCount);
        Assert.Equal(2, snapshot.Displays.Count);
        Assert.Equal(2, snapshot.Displays[0].DensityCells.Count);
        Assert.Equal(DesktopRadarDensityLevel.High, snapshot.Displays[0].DensityCells[1].Density);
    }

    [Fact]
    public void DisplaySnapshot_RejectsInvalidOrDuplicateCells()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);

        Assert.Throws<ArgumentException>(() => new DesktopRadarDisplaySnapshot(
            "DISPLAY-A",
            bounds,
            false,
            itemCount: 1,
            gridColumns: 8,
            gridRows: 5,
            new[]
            {
                new DesktopRadarDensityCell(2, 2, DesktopRadarDensityLevel.Low),
                new DesktopRadarDensityCell(2, 2, DesktopRadarDensityLevel.High),
            }));

        Assert.Throws<ArgumentException>(() => new DesktopRadarDisplaySnapshot(
            "DISPLAY-A",
            bounds,
            false,
            itemCount: 1,
            gridColumns: 8,
            gridRows: 5,
            new[] { new DesktopRadarDensityCell(8, 0, DesktopRadarDensityLevel.Low) }));
    }

    [Fact]
    public void PublicContract_DoesNotExposeDesktopItemIdentityOrExecutionPayload()
    {
        var propertyNames = new[]
        {
            typeof(DesktopRadarSnapshot),
            typeof(DesktopRadarDisplaySnapshot),
            typeof(DesktopRadarDensityCell),
        }
        .SelectMany(type => type.GetProperties())
        .Select(property => property.Name)
        .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("StableId", propertyNames);
        Assert.DoesNotContain("DisplayName", propertyNames);
        Assert.DoesNotContain("ParsingName", propertyNames);
        Assert.DoesNotContain("PixelPosition", propertyNames);
        Assert.DoesNotContain("TargetPosition", propertyNames);
        Assert.DoesNotContain("WriterCommand", propertyNames);
        Assert.DoesNotContain("Path", propertyNames);
    }

    [Fact]
    public void Snapshot_RequiresLayoutIdAndVersionToMatchAsOneFreshnessPair()
    {
        var display = new DesktopRadarDisplaySnapshot(
            "DISPLAY-A",
            new PixelRect(0, 0, 1920, 1080),
            true,
            itemCount: 0,
            gridColumns: 8,
            gridRows: 5,
            Array.Empty<DesktopRadarDensityCell>());

        Assert.Throws<ArgumentException>(() => new DesktopRadarSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "topology:v1:test",
            Guid.NewGuid(),
            layoutVersion: null,
            new[] { display }));

        Assert.Throws<ArgumentException>(() => new DesktopRadarSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "topology:v1:test",
            layoutTemplateId: null,
            layoutVersion: 1,
            new[] { display }));
    }
}
