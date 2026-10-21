using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopObservationUnderstandingRequestFactoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void MissingIntentAndGuidanceBecomeNull(string? text)
    {
        var request = DesktopObservationUnderstandingRequestFactory.Create(Observation(),
            currentIntentSummary: text, latestUserGuidance: text);
        Assert.Null(request.CurrentIntentSummary);
        Assert.Null(request.LatestUserGuidance);
        Assert.Single(request.Items);
    }

    [Fact]
    public void NewSharedSessionCanStartUnderstandingWithoutAnIntent()
    {
        var session = new DesktopWorkspaceDesignSession();
        var request = DesktopObservationUnderstandingRequestFactory.Create(Observation(),
            currentIntentSummary: session.Snapshot().State.Intent.Summary);
        Assert.Null(request.CurrentIntentSummary);
    }

    [Fact]
    public void ExistingIntentIsPreservedAndLengthLimitsRemainEnforced()
    {
        var request = DesktopObservationUnderstandingRequestFactory.Create(Observation(),
            currentIntentSummary: "开发区放右边", latestUserGuidance: "保留中央空白");
        Assert.Equal("开发区放右边", request.CurrentIntentSummary);
        Assert.Equal("保留中央空白", request.LatestUserGuidance);
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopObservationUnderstandingRequestFactory.Create(
            Observation(), currentIntentSummary: new string('x', DesktopFirstRunUnderstandingRequest.MaxIntentSummaryChars + 1)));
    }

    private static DesktopObservation Observation() => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, Array.Empty<DisplayDescriptor>(),
        new DesktopIconPositioningCapabilities(false, new PixelPoint(80, 80), ConnectionMethod.FindWindowSW, null),
        new[] { new DesktopIconDescriptor("item", "说明.txt", null, ItemKind.File,
            new PixelPoint(0, 0), null, false, true) },
        ConnectionMethod.FindWindowSW, TimeSpan.Zero);
}
