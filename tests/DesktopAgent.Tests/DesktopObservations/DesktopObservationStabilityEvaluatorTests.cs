using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopObservationStabilityEvaluatorTests
{
    [Fact]
    public void Evaluate_SameSemanticFactsIgnoringOrderAndDiagnostics_IsStableAndUsesSecond()
    {
        var d1 = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var d2 = Display("\\\\.\\DISPLAY2", new PixelRect(-1280, 0, 0, 1024), false);
        var a = Icon("a", new PixelPoint(100, 100), d1.DeviceName);
        var b = Icon("b", new PixelPoint(-900, 100), d2.DeviceName);

        var first = Observation(
            new[] { d1, d2 },
            Capabilities(false, new PixelPoint(93, 122), ConnectionMethod.FindWindowSW),
            new[] { a, b },
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5));
        var second = Observation(
            new[] { d2, d1 },
            Capabilities(false, new PixelPoint(93, 122), ConnectionMethod.EnumerationFallback),
            new[] { b, a },
            ConnectionMethod.EnumerationFallback,
            TimeSpan.FromMilliseconds(99));

        var result = DesktopObservationStabilityEvaluator.Evaluate(first, second);

        Assert.True(result.IsStable);
        Assert.Same(second, result.StableObservation);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Evaluate_TopologyFactChanged_IsUnstable()
    {
        var before = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var after = before with { DpiX = 120, DpiY = 120 };
        var icon = Icon("a", new PixelPoint(100, 100), before.DeviceName);

        var result = DesktopObservationStabilityEvaluator.Evaluate(
            Observation(new[] { before }, Capabilities(), new[] { icon }),
            Observation(new[] { after }, Capabilities(), new[] { icon }));

        Assert.False(result.IsStable);
        Assert.Contains(result.Issues, issue =>
            issue.Code == DesktopObservationStabilityIssueCode.TopologyChanged);
    }

    [Fact]
    public void Evaluate_AutoArrangeAndSpacingChanged_ReportsBoth()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var icon = Icon("a", new PixelPoint(100, 100), display.DeviceName);

        var result = DesktopObservationStabilityEvaluator.Evaluate(
            Observation(
                new[] { display },
                Capabilities(false, new PixelPoint(93, 122)),
                new[] { icon }),
            Observation(
                new[] { display },
                Capabilities(true, new PixelPoint(100, 130)),
                new[] { icon }));

        Assert.False(result.IsStable);
        Assert.Contains(result.Issues, issue =>
            issue.Code == DesktopObservationStabilityIssueCode.AutoArrangeChanged);
        Assert.Contains(result.Issues, issue =>
            issue.Code == DesktopObservationStabilityIssueCode.IconSpacingChanged);
    }

    [Fact]
    public void Evaluate_IconSetChanged_ReportsAddedAndRemovedByOrdinalStableId()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var first = Observation(
            new[] { display },
            Capabilities(),
            new[]
            {
                Icon("A", new PixelPoint(100, 100), display.DeviceName),
                Icon("B", new PixelPoint(200, 100), display.DeviceName),
            });
        var second = Observation(
            new[] { display },
            Capabilities(),
            new[]
            {
                Icon("A", new PixelPoint(100, 100), display.DeviceName),
                Icon("b", new PixelPoint(200, 100), display.DeviceName),
            });

        var result = DesktopObservationStabilityEvaluator.Evaluate(first, second);

        Assert.False(result.IsStable);
        Assert.Contains(result.Issues, issue =>
            issue.Code == DesktopObservationStabilityIssueCode.IconRemoved && issue.StableId == "B");
        Assert.Contains(result.Issues, issue =>
            issue.Code == DesktopObservationStabilityIssueCode.IconAdded && issue.StableId == "b");
    }

    [Fact]
    public void Evaluate_SameStableIdButSemanticFactChanged_IsUnstable()
    {
        var display = Display("\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true);
        var before = Icon("a", new PixelPoint(100, 100), display.DeviceName);
        var after = before with
        {
            DisplayName = "renamed.txt",
            PixelPosition = new PixelPoint(300, 400),
        };

        var result = DesktopObservationStabilityEvaluator.Evaluate(
            Observation(new[] { display }, Capabilities(), new[] { before }),
            Observation(new[] { display }, Capabilities(), new[] { after }));

        Assert.False(result.IsStable);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DesktopObservationStabilityIssueCode.IconChanged, issue.Code);
        Assert.Equal("a", issue.StableId);
    }

    private static DesktopObservation Observation(
        IReadOnlyList<DisplayDescriptor> displays,
        DesktopIconPositioningCapabilities capabilities,
        IReadOnlyList<DesktopIconDescriptor> icons,
        ConnectionMethod readMethod = ConnectionMethod.FindWindowSW,
        TimeSpan? readTime = null) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            displays,
            capabilities,
            icons,
            readMethod,
            readTime ?? TimeSpan.FromMilliseconds(5));

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
