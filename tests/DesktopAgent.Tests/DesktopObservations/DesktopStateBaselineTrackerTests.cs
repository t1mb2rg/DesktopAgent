using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopStateBaselineTrackerTests
{
    [Fact]
    public void AcceptStableObservation_FirstObservationOnlyEstablishesBaseline()
    {
        var tracker = new DesktopStateBaselineTracker();
        var first = Observation(new PixelPoint(100, 100));

        var result = tracker.AcceptStableObservation(first);

        Assert.Equal(DesktopStateBaselineUpdateStatus.BaselineEstablished, result.Status);
        Assert.Same(first, result.CurrentObservation);
        Assert.Null(result.PreviousObservation);
        Assert.Null(result.ChangeSet);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public void AcceptStableObservation_SubsequentObservationComparesThenAdvancesBaseline()
    {
        var tracker = new DesktopStateBaselineTracker();
        var first = Observation(new PixelPoint(100, 100));
        var second = Observation(new PixelPoint(300, 400));
        var third = Observation(new PixelPoint(300, 400));

        tracker.AcceptStableObservation(first);
        var secondResult = tracker.AcceptStableObservation(second);
        var thirdResult = tracker.AcceptStableObservation(third);

        Assert.Equal(DesktopStateBaselineUpdateStatus.Compared, secondResult.Status);
        Assert.Same(first, secondResult.PreviousObservation);
        Assert.True(secondResult.HasChanges);
        var move = Assert.Single(secondResult.ChangeSet!.ItemChanges);
        Assert.True(move.IsMoved);

        Assert.Equal(DesktopStateBaselineUpdateStatus.Compared, thirdResult.Status);
        Assert.Same(second, thirdResult.PreviousObservation);
        Assert.False(thirdResult.HasChanges);
        Assert.Empty(thirdResult.ChangeSet!.ItemChanges);
    }

    [Fact]
    public void Reset_NextStableObservationEstablishesFreshBaseline()
    {
        var tracker = new DesktopStateBaselineTracker();
        tracker.AcceptStableObservation(Observation(new PixelPoint(100, 100)));

        tracker.Reset();
        var afterReset = Observation(new PixelPoint(500, 500));
        var result = tracker.AcceptStableObservation(afterReset);

        Assert.Equal(DesktopStateBaselineUpdateStatus.BaselineEstablished, result.Status);
        Assert.Same(afterReset, result.CurrentObservation);
        Assert.Null(result.ChangeSet);
    }

    private static DesktopObservation Observation(PixelPoint position)
    {
        var display = new DisplayDescriptor(
            "\\\\.\\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var capabilities = new DesktopIconPositioningCapabilities(
            false,
            new PixelPoint(93, 122),
            ConnectionMethod.FindWindowSW,
            null);
        var icon = new DesktopIconDescriptor(
            "a",
            "a.txt",
            "c:\\desktop\\a.txt",
            ItemKind.File,
            position,
            display.DeviceName,
            false,
            true);

        return new DesktopObservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { display },
            capabilities,
            new[] { icon },
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5));
    }
}
