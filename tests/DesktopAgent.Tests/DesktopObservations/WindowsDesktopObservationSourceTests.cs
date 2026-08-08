using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class WindowsDesktopObservationSourceTests
{
    [Fact]
    public async Task CaptureAsync_CompleteFacts_ReturnsCapturedObservation()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080));
        var icon = Icon("c:\\desktop\\a.txt", new PixelPoint(100, 100));
        var capabilities = new DesktopIconPositioningCapabilities(
            false,
            new PixelPoint(93, 122),
            ConnectionMethod.FindWindowSW,
            null);

        using var sta = new StaThreadExecutor();
        var source = new WindowsDesktopObservationSource(
            new FakeTopologyReader(display),
            new FakeIconReader(Read(icon)),
            new FakeCapabilitiesReader(capabilities),
            sta);

        var result = await source.CaptureAsync();

        Assert.True(result.IsCaptured);
        Assert.Empty(result.Issues);
        var observation = Assert.IsType<Core.DesktopObservations.DesktopObservation>(result.Observation);
        Assert.NotEqual(Guid.Empty, observation.ObservationId);
        Assert.Single(observation.Displays);
        Assert.Single(observation.Icons);
        Assert.Equal("\\\\.\\DISPLAY1", observation.Icons[0].MonitorDeviceName);
        Assert.Equal(new PixelPoint(93, 122), observation.PositioningCapabilities.Spacing);
        Assert.False(observation.PositioningCapabilities.IsAutoArrangeEnabled);
    }

    [Fact]
    public void Constructor_DoesNotExposeWriterDependency()
    {
        var parameterTypes = typeof(WindowsDesktopObservationSource)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IDesktopIconPositioningCapabilitiesReader), parameterTypes);
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public async Task CaptureAsync_IncompleteDesktopRead_FailsClosed()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080));
        using var sta = new StaThreadExecutor();
        var source = new WindowsDesktopObservationSource(
            new FakeTopologyReader(display),
            new FakeIconReader(new DesktopIconReadResult(
                Array.Empty<DesktopIconDescriptor>(),
                1,
                ConnectionMethod.FindWindowSW,
                TimeSpan.FromMilliseconds(3),
                null)),
            new FakeCapabilitiesReader(Capabilities()),
            sta);

        var result = await source.CaptureAsync();

        Assert.False(result.IsCaptured);
        Assert.Null(result.Observation);
        Assert.Contains(result.Issues, issue =>
            issue.Code == DesktopObservationCaptureIssueCode.IncompleteDesktopRead);
    }

    [Fact]
    public async Task CaptureAsync_DuplicateStableId_FailsClosed()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080));
        using var sta = new StaThreadExecutor();
        var source = new WindowsDesktopObservationSource(
            new FakeTopologyReader(display),
            new FakeIconReader(Read(
                Icon("same", new PixelPoint(100, 100)),
                Icon("same", new PixelPoint(200, 100)))),
            new FakeCapabilitiesReader(Capabilities()),
            sta);

        var result = await source.CaptureAsync();

        Assert.False(result.IsCaptured);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DesktopObservationCaptureIssueCode.DuplicateStableId, issue.Code);
        Assert.Equal("same", issue.StableId);
    }

    [Fact]
    public async Task CaptureAsync_DuplicateDisplayDeviceName_FailsClosedBeforeShellRead()
    {
        var first = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080));
        var second = Display("\\\\.\\DISPLAY1", new PixelRect(1920, 0, 3840, 1080));
        var iconReader = new FakeIconReader(Read());
        using var sta = new StaThreadExecutor();
        var source = new WindowsDesktopObservationSource(
            new FakeTopologyReader(first, second),
            iconReader,
            new FakeCapabilitiesReader(Capabilities()),
            sta);

        var result = await source.CaptureAsync();

        Assert.False(result.IsCaptured);
        Assert.Equal(0, iconReader.CallCount);
        Assert.Contains(result.Issues, issue =>
            issue.Code == DesktopObservationCaptureIssueCode.DuplicateDisplayDeviceName);
    }

    [Fact]
    public async Task CaptureAsync_IconOutsideCurrentTopology_FailsClosed()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080));
        using var sta = new StaThreadExecutor();
        var source = new WindowsDesktopObservationSource(
            new FakeTopologyReader(display),
            new FakeIconReader(Read(Icon("outside", new PixelPoint(5000, 5000)))),
            new FakeCapabilitiesReader(Capabilities()),
            sta);

        var result = await source.CaptureAsync();

        Assert.False(result.IsCaptured);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DesktopObservationCaptureIssueCode.IconOutsideDisplayTopology, issue.Code);
        Assert.Equal("outside", issue.StableId);
    }

    [Fact]
    public async Task CaptureAsync_NoDisplays_FailsClosedBeforeShellRead()
    {
        var iconReader = new FakeIconReader(Read());
        using var sta = new StaThreadExecutor();
        var source = new WindowsDesktopObservationSource(
            new FakeTopologyReader(),
            iconReader,
            new FakeCapabilitiesReader(Capabilities()),
            sta);

        var result = await source.CaptureAsync();

        Assert.False(result.IsCaptured);
        Assert.Equal(0, iconReader.CallCount);
        Assert.Equal(DesktopObservationCaptureIssueCode.NoDisplays, Assert.Single(result.Issues).Code);
    }

    private static DisplayDescriptor Display(string name, PixelRect bounds) =>
        new(name, bounds, bounds, 96, 96, name.EndsWith("DISPLAY1", StringComparison.Ordinal));

    private static DesktopIconDescriptor Icon(string stableId, PixelPoint position) =>
        new(
            stableId,
            stableId,
            stableId,
            ItemKind.File,
            position,
            null,
            false,
            true);

    private static DesktopIconReadResult Read(params DesktopIconDescriptor[] icons) =>
        new(
            icons,
            0,
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5),
            null);

    private static DesktopIconPositioningCapabilities Capabilities() =>
        new(
            false,
            new PixelPoint(93, 122),
            ConnectionMethod.FindWindowSW,
            null);

    private sealed class FakeTopologyReader(params DisplayDescriptor[] displays) : IDisplayTopologyReader
    {
        public IReadOnlyList<DisplayDescriptor> GetDisplays() => displays;
    }

    private sealed class FakeIconReader(DesktopIconReadResult result) : IDesktopIconReader
    {
        public int CallCount { get; private set; }

        public DesktopIconReadResult ReadDesktopIcons()
        {
            CallCount++;
            return result;
        }
    }

    private sealed class FakeCapabilitiesReader(DesktopIconPositioningCapabilities result)
        : IDesktopIconPositioningCapabilitiesReader
    {
        public DesktopIconPositioningCapabilities GetCapabilities() => result;
    }
}
