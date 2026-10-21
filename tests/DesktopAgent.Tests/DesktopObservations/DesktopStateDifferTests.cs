using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopStateDifferTests
{
    [Fact]
    public void Compare_SameFactsIgnoringOrderAndDiagnostics_IsEmpty()
    {
        var d1 = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var d2 = Display("\\\\.\\DISPLAY2", new PixelRect(-1280, 0, 0, 1024), false);
        var a = Icon("a", new PixelPoint(100, 100), d1.DeviceName);
        var b = Icon("b", new PixelPoint(-900, 100), d2.DeviceName);

        var previous = Observation(
            new[] { d1, d2 },
            Capabilities(false, new PixelPoint(93, 122), ConnectionMethod.FindWindowSW),
            new[] { a, b },
            ConnectionMethod.FindWindowSW);
        var current = Observation(
            new[] { d2, d1 },
            Capabilities(false, new PixelPoint(93, 122), ConnectionMethod.EnumerationFallback),
            new[] { b, a },
            ConnectionMethod.EnumerationFallback);

        var diff = DesktopStateDiffer.Compare(previous, current);

        Assert.False(diff.HasChanges);
        Assert.Null(diff.TopologyChange);
        Assert.Null(diff.PositioningCapabilitiesChange);
        Assert.Empty(diff.ItemChanges);
        Assert.Equal(previous.ObservationId, diff.PreviousObservationId);
        Assert.Equal(current.ObservationId, diff.CurrentObservationId);
    }

    [Fact]
    public void Compare_AddedAndRemoved_UsesOrdinalStableIdsAndDeterministicOrder()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var previous = Observation(
            new[] { display },
            Capabilities(),
            new[]
            {
                Icon("B", new PixelPoint(100, 100), display.DeviceName),
                Icon("keep", new PixelPoint(200, 100), display.DeviceName),
            });
        var current = Observation(
            new[] { display },
            Capabilities(),
            new[]
            {
                Icon("keep", new PixelPoint(200, 100), display.DeviceName),
                Icon("b", new PixelPoint(300, 100), display.DeviceName),
            });

        var diff = DesktopStateDiffer.Compare(previous, current);

        Assert.True(diff.HasChanges);
        Assert.Collection(
            diff.ItemChanges,
            item =>
            {
                Assert.Equal("B", item.StableId);
                Assert.Equal(DesktopItemStateChangeKind.Removed, item.Kind);
                Assert.NotNull(item.Previous);
                Assert.Null(item.Current);
            },
            item =>
            {
                Assert.Equal("b", item.StableId);
                Assert.Equal(DesktopItemStateChangeKind.Added, item.Kind);
                Assert.Null(item.Previous);
                Assert.NotNull(item.Current);
            });
    }

    [Fact]
    public void Compare_PositionChanged_ProducesMovedChangedItem()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var before = Icon("a", new PixelPoint(100, 100), display.DeviceName);
        var after = before with { PixelPosition = new PixelPoint(300, 400) };

        var diff = DesktopStateDiffer.Compare(
            Observation(new[] { display }, Capabilities(), new[] { before }),
            Observation(new[] { display }, Capabilities(), new[] { after }));

        var change = Assert.Single(diff.ItemChanges);
        Assert.Equal(DesktopItemStateChangeKind.Changed, change.Kind);
        Assert.Equal(DesktopItemFactChange.Position, change.FactChanges);
        Assert.True(change.IsMoved);
        Assert.Equal(before, change.Previous);
        Assert.Equal(after, change.Current);
    }

    [Fact]
    public void Compare_MonitorAssignmentChanged_IsMovedEvenWithoutPositionChange()
    {
        var d1 = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var d2 = Display("\\\\.\\DISPLAY2", new PixelRect(1920, 0, 3840, 1080), false);
        var before = Icon("a", new PixelPoint(100, 100), d1.DeviceName);
        var after = before with { MonitorDeviceName = d2.DeviceName };

        var diff = DesktopStateDiffer.Compare(
            Observation(new[] { d1, d2 }, Capabilities(), new[] { before }),
            Observation(new[] { d1, d2 }, Capabilities(), new[] { after }));

        var change = Assert.Single(diff.ItemChanges);
        Assert.Equal(DesktopItemFactChange.MonitorAssignment, change.FactChanges);
        Assert.True(change.IsMoved);
    }

    [Fact]
    public void Compare_MetadataChanged_ReportsExactFactFlags()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var before = Icon("a", new PixelPoint(100, 100), display.DeviceName);
        var after = before with
        {
            DisplayName = "renamed",
            ParsingName = "c:\\desktop\\renamed.lnk",
            ItemKind = ItemKind.Shortcut,
            IsSystemItem = true,
            IsFileSystemItem = false,
        };

        var diff = DesktopStateDiffer.Compare(
            Observation(new[] { display }, Capabilities(), new[] { before }),
            Observation(new[] { display }, Capabilities(), new[] { after }));

        var change = Assert.Single(diff.ItemChanges);
        var expected =
            DesktopItemFactChange.DisplayName
            | DesktopItemFactChange.ParsingName
            | DesktopItemFactChange.ItemKind
            | DesktopItemFactChange.SystemItemFlag
            | DesktopItemFactChange.FileSystemItemFlag;
        Assert.Equal(expected, change.FactChanges);
        Assert.False(change.IsMoved);
    }

    [Fact]
    public void Compare_TopologyAndCapabilitiesChanged_AreStructuralChanges()
    {
        var beforeDisplay = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var afterDisplay = beforeDisplay with { DpiX = 120, DpiY = 120 };
        var icon = Icon("a", new PixelPoint(100, 100), beforeDisplay.DeviceName);

        var diff = DesktopStateDiffer.Compare(
            Observation(
                new[] { beforeDisplay },
                Capabilities(false, new PixelPoint(93, 122)),
                new[] { icon }),
            Observation(
                new[] { afterDisplay },
                Capabilities(true, new PixelPoint(100, 130)),
                new[] { icon }));

        Assert.True(diff.HasChanges);
        Assert.NotNull(diff.TopologyChange);
        var capabilities = Assert.IsType<DesktopPositioningCapabilitiesChange>(
            diff.PositioningCapabilitiesChange);
        Assert.True(capabilities.AutoArrangeChanged);
        Assert.True(capabilities.IconSpacingChanged);
        Assert.Empty(diff.ItemChanges);
    }

    private static DesktopObservation Observation(
        IReadOnlyList<DisplayDescriptor> displays,
        DesktopIconPositioningCapabilities capabilities,
        IReadOnlyList<DesktopIconDescriptor> icons,
        ConnectionMethod readMethod = ConnectionMethod.FindWindowSW) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            displays,
            capabilities,
            icons,
            readMethod,
            TimeSpan.FromMilliseconds(5));

    private static DisplayDescriptor Display(string name, PixelRect bounds, bool primary) =>
        new(name, bounds, bounds, 96, 96, primary);

    private static DesktopIconDescriptor Icon(
        string stableId,
        PixelPoint position,
        string monitorDeviceName) =>
        new(
            stableId,
            stableId + ".txt",
            "c:\\desktop\\" + stableId + ".txt",
            ItemKind.File,
            position,
            monitorDeviceName,
            false,
            true);

    private static DesktopIconPositioningCapabilities Capabilities(
        bool autoArrange = false,
        PixelPoint? spacing = null,
        ConnectionMethod method = ConnectionMethod.FindWindowSW) =>
        new(
            autoArrange,
            spacing ?? new PixelPoint(93, 122),
            method,
            null);
}
