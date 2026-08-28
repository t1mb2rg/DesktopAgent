using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopSemanticUnderstandingFreshnessTests
{
    [Fact]
    public void Fingerprint_IgnoresPositionAndMonitorChanges()
    {
        var first = MakeObservation([
            Icon("a", "A", new PixelPoint(10, 20), @"\\.\DISPLAY1"),
            Icon("b", "B", new PixelPoint(30, 40), @"\\.\DISPLAY1"),
        ]);
        var moved = MakeObservation([
            Icon("a", "A", new PixelPoint(900, 700), @"\\.\DISPLAY2"),
            Icon("b", "B", new PixelPoint(-500, 100), @"\\.\DISPLAY2"),
        ]);

        Assert.Equal(
            DesktopSemanticUnderstandingSourceFingerprint.Create(first),
            DesktopSemanticUnderstandingSourceFingerprint.Create(moved));
    }

    [Fact]
    public void Fingerprint_ChangesWhenSemanticIdentityFactsChange()
    {
        var first = MakeObservation([Icon("a", "A", new PixelPoint(10, 20), @"\\.\DISPLAY1")]);
        var renamed = MakeObservation([Icon("a", "A renamed", new PixelPoint(10, 20), @"\\.\DISPLAY1")]);

        Assert.NotEqual(
            DesktopSemanticUnderstandingSourceFingerprint.Create(first),
            DesktopSemanticUnderstandingSourceFingerprint.Create(renamed));
    }

    [Fact]
    public void Evaluate_ExactSemanticSource_IsCurrent()
    {
        var observation = MakeObservation([
            Icon("a", "A", new PixelPoint(10, 20), @"\\.\DISPLAY1"),
            Icon("b", "B", new PixelPoint(30, 40), @"\\.\DISPLAY1"),
        ]);
        var snapshot = MakeSnapshot(observation, ["a", "b"]);
        var moved = MakeObservation([
            Icon("a", "A", new PixelPoint(500, 600), @"\\.\DISPLAY2"),
            Icon("b", "B", new PixelPoint(700, 800), @"\\.\DISPLAY2"),
        ]);

        var result = DesktopSemanticUnderstandingFreshnessEvaluator.Evaluate(snapshot, moved);

        Assert.Equal(DesktopSemanticUnderstandingFreshness.Current, result.Freshness);
        Assert.Equal(0, result.MissingKnownAssignmentCount);
    }

    [Fact]
    public void Evaluate_SmallAddition_IsPossiblyStale()
    {
        var original = MakeObservation(
            Enumerable.Range(1, 10)
                .Select(index => Icon($"item-{index}", $"Item {index}", new PixelPoint(index, index), @"\\.\DISPLAY1"))
                .ToArray());
        var snapshot = MakeSnapshot(original, original.Icons.Select(static icon => icon.StableId).ToArray());
        var current = MakeObservation(original.Icons
            .Concat([Icon("item-11", "Item 11", new PixelPoint(11, 11), @"\\.\DISPLAY1")])
            .ToArray());

        var result = DesktopSemanticUnderstandingFreshnessEvaluator.Evaluate(snapshot, current);

        Assert.Equal(DesktopSemanticUnderstandingFreshness.PossiblyStale, result.Freshness);
    }

    [Fact]
    public void Evaluate_LargeRemoval_IsClearlyStale()
    {
        var original = MakeObservation(
            Enumerable.Range(1, 10)
                .Select(index => Icon($"item-{index}", $"Item {index}", new PixelPoint(index, index), @"\\.\DISPLAY1"))
                .ToArray());
        var snapshot = MakeSnapshot(original, original.Icons.Select(static icon => icon.StableId).ToArray());
        var current = MakeObservation(original.Icons.Take(6).ToArray());

        var result = DesktopSemanticUnderstandingFreshnessEvaluator.Evaluate(snapshot, current);

        Assert.Equal(DesktopSemanticUnderstandingFreshness.ClearlyStale, result.Freshness);
        Assert.Equal(4, result.MissingKnownAssignmentCount);
    }

    private static DesktopSemanticUnderstandingSnapshot MakeSnapshot(
        DesktopObservation observation,
        IReadOnlyList<string> stableIds)
    {
        var understanding = new DesktopSemanticUnderstanding(
            "summary",
            [new DesktopSemanticCategory("cat", "Category", "description")],
            stableIds.Select(id => new DesktopSemanticAssignment(id, "cat", 0.9, "reason")),
            []);

        return new DesktopSemanticUnderstandingSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            observation.ObservationId,
            DesktopSemanticUnderstandingSourceFingerprint.Create(observation),
            observation.Icons.Count,
            understanding);
    }

    private static DesktopObservation MakeObservation(IReadOnlyList<DesktopIconDescriptor> icons)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            [
                new DisplayDescriptor(
                    @"\\.\DISPLAY1",
                    new PixelRect(0, 0, 1920, 1080),
                    new PixelRect(0, 0, 1920, 1040),
                    96,
                    96,
                    true),
                new DisplayDescriptor(
                    @"\\.\DISPLAY2",
                    new PixelRect(1920, 0, 1920, 1080),
                    new PixelRect(1920, 0, 1920, 1040),
                    96,
                    96,
                    false),
            ],
            new DesktopIconPositioningCapabilities(
                false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                null),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);

    private static DesktopIconDescriptor Icon(
        string stableId,
        string displayName,
        PixelPoint position,
        string monitor)
        => new(
            stableId,
            displayName,
            parsingName: $@"C:\Desktop\{stableId}",
            ItemKind.File,
            position,
            monitor,
            isSystemItem: false,
            isFileSystemItem: true);
}
