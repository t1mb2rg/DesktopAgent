using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class StableDesktopObservationSourceTests
{
    [Fact]
    public void Settings_NegativeInterval_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopObservationStabilitySettings(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task CaptureStableAsync_FirstCaptureUnavailable_StopsImmediately()
    {
        var source = new ScriptedObservationSource(
            DesktopObservationCaptureResult.Unavailable(
                new DesktopObservationCaptureIssue(
                    DesktopObservationCaptureIssueCode.IncompleteDesktopRead,
                    "incomplete")),
            DesktopObservationCaptureResult.Captured(Observation("a", new PixelPoint(100, 100))));
        var stableSource = new StableDesktopObservationSource(
            source,
            new DesktopObservationStabilitySettings(TimeSpan.Zero));

        var result = await stableSource.CaptureStableAsync();

        Assert.Equal(1, source.CallCount);
        Assert.Equal(StableDesktopObservationStatus.Unavailable, result.Status);
        Assert.Null(result.StableObservation);
        var failure = Assert.Single(result.CaptureFailures);
        Assert.Equal(DesktopObservationSamplePhase.First, failure.Phase);
        Assert.Equal(DesktopObservationCaptureIssueCode.IncompleteDesktopRead, failure.Issue.Code);
        Assert.Empty(result.StabilityIssues);
    }

    [Fact]
    public async Task CaptureStableAsync_SecondCaptureUnavailable_ReportsSecondPhase()
    {
        var source = new ScriptedObservationSource(
            DesktopObservationCaptureResult.Captured(Observation("a", new PixelPoint(100, 100))),
            DesktopObservationCaptureResult.Unavailable(
                new DesktopObservationCaptureIssue(
                    DesktopObservationCaptureIssueCode.TopologyChangedDuringCapture,
                    "topology changed")));
        var stableSource = new StableDesktopObservationSource(
            source,
            new DesktopObservationStabilitySettings(TimeSpan.Zero));

        var result = await stableSource.CaptureStableAsync();

        Assert.Equal(2, source.CallCount);
        Assert.Equal(StableDesktopObservationStatus.Unavailable, result.Status);
        var failure = Assert.Single(result.CaptureFailures);
        Assert.Equal(DesktopObservationSamplePhase.Second, failure.Phase);
        Assert.Equal(DesktopObservationCaptureIssueCode.TopologyChangedDuringCapture, failure.Issue.Code);
    }

    [Fact]
    public async Task CaptureStableAsync_TwoEquivalentCaptures_ReturnsSecondObservation()
    {
        var first = Observation("a", new PixelPoint(100, 100));
        var second = Observation("a", new PixelPoint(100, 100));
        var source = new ScriptedObservationSource(
            DesktopObservationCaptureResult.Captured(first),
            DesktopObservationCaptureResult.Captured(second));
        var stableSource = new StableDesktopObservationSource(
            source,
            new DesktopObservationStabilitySettings(TimeSpan.Zero));

        var result = await stableSource.CaptureStableAsync();

        Assert.Equal(StableDesktopObservationStatus.Stable, result.Status);
        Assert.Same(second, result.StableObservation);
        Assert.Empty(result.CaptureFailures);
        Assert.Empty(result.StabilityIssues);
    }

    [Fact]
    public async Task CaptureStableAsync_TwoCompleteButChangedCaptures_IsUnstable()
    {
        var source = new ScriptedObservationSource(
            DesktopObservationCaptureResult.Captured(Observation("a", new PixelPoint(100, 100))),
            DesktopObservationCaptureResult.Captured(Observation("a", new PixelPoint(300, 400))));
        var stableSource = new StableDesktopObservationSource(
            source,
            new DesktopObservationStabilitySettings(TimeSpan.Zero));

        var result = await stableSource.CaptureStableAsync();

        Assert.Equal(StableDesktopObservationStatus.Unstable, result.Status);
        Assert.Null(result.StableObservation);
        Assert.Empty(result.CaptureFailures);
        var issue = Assert.Single(result.StabilityIssues);
        Assert.Equal(DesktopObservationStabilityIssueCode.IconChanged, issue.Code);
        Assert.Equal("a", issue.StableId);
    }

    private static DesktopObservation Observation(string stableId, PixelPoint position)
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
            stableId,
            stableId + ".txt",
            "c:\\desktop\\" + stableId + ".txt",
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

    private sealed class ScriptedObservationSource(params DesktopObservationCaptureResult[] results)
        : IDesktopObservationSource
    {
        private int _index;
        public int CallCount { get; private set; }

        public Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            var index = Math.Min(_index++, results.Length - 1);
            return Task.FromResult(results[index]);
        }
    }
}
