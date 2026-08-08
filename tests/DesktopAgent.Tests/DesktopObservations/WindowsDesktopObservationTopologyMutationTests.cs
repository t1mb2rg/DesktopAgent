using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class WindowsDesktopObservationTopologyMutationTests
{
    [Fact]
    public async Task CaptureAsync_TopologyChangesDuringShellRead_FailsClosed()
    {
        var before = new DisplayDescriptor(
            "\\\\.\\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);
        var after = before with
        {
            WorkArea = new PixelRect(0, 0, 1920, 1000),
            DpiX = 120,
            DpiY = 120,
        };

        using var sta = new StaThreadExecutor();
        var source = new WindowsDesktopObservationSource(
            new ScriptedTopologyReader(new[] { before }, new[] { after }),
            new FixedIconReader(new DesktopIconReadResult(
                new[]
                {
                    new DesktopIconDescriptor(
                        "icon-a",
                        "icon-a",
                        "c:\\desktop\\a.txt",
                        ItemKind.File,
                        new PixelPoint(100, 100),
                        null,
                        false,
                        true),
                },
                0,
                ConnectionMethod.FindWindowSW,
                TimeSpan.FromMilliseconds(5),
                null)),
            new FixedCapabilitiesReader(new DesktopIconPositioningCapabilities(
                false,
                new PixelPoint(93, 122),
                ConnectionMethod.FindWindowSW,
                null)),
            sta);

        var result = await source.CaptureAsync();

        Assert.False(result.IsCaptured);
        Assert.Equal(
            DesktopObservationCaptureIssueCode.TopologyChangedDuringCapture,
            Assert.Single(result.Issues).Code);
    }

    private sealed class ScriptedTopologyReader(params IReadOnlyList<DisplayDescriptor>[] reads)
        : IDisplayTopologyReader
    {
        private int _index;

        public IReadOnlyList<DisplayDescriptor> GetDisplays()
        {
            var index = Interlocked.Increment(ref _index) - 1;
            return reads[Math.Min(index, reads.Length - 1)];
        }
    }

    private sealed class FixedIconReader(DesktopIconReadResult result) : IDesktopIconReader
    {
        public DesktopIconReadResult ReadDesktopIcons() => result;
    }

    private sealed class FixedCapabilitiesReader(DesktopIconPositioningCapabilities result)
        : IDesktopIconPositioningCapabilitiesReader
    {
        public DesktopIconPositioningCapabilities GetCapabilities() => result;
    }
}
