using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopWorkspacePlanningObservationGateTests
{
    [Fact]
    public void SameSemanticItemsWithChangedPositions_UsesFreshObservation()
    {
        var original = Observation([Icon("a", "A", new(50, 50), @"\\.\DISPLAY1")]);
        var moved = Observation([Icon("a", "A", new(400, 600), @"\\.\DISPLAY2")]);

        Assert.NotEqual(original.ObservationId, moved.ObservationId);
        Assert.True(DesktopWorkspacePlanningObservationGate.Evaluate(original, moved).CanReuseUnderstanding);
    }

    [Fact]
    public void NewIcon_RequiresNewUnderstanding()
    {
        var original = Observation([Icon("a", "A", new(50, 50), @"\\.\DISPLAY1")]);
        var changed = Observation([
            Icon("a", "A", new(50, 50), @"\\.\DISPLAY1"),
            Icon("b", "B", new(70, 90), @"\\.\DISPLAY1"),
        ]);

        var result = DesktopWorkspacePlanningObservationGate.Evaluate(original, changed);

        Assert.False(result.CanReuseUnderstanding);
        Assert.Contains("重新理解桌面", result.BlockReason, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayTopologyChanged_RequiresNewUnderstanding()
    {
        var original = Observation([Icon("a", "A", new(50, 50), @"\\.\DISPLAY1")]);
        var changed = Observation(original.Icons,
            displays: [new DisplayDescriptor(@"\\.\DISPLAY1", new(0, 0, 1600, 900),
                new(0, 0, 1600, 860), 96, 96, true)]);

        Assert.False(DesktopWorkspacePlanningObservationGate.Evaluate(original, changed).CanReuseUnderstanding);
    }

    [Fact]
    public void GridSpacingChanged_RequiresRecalibration()
    {
        var original = Observation([Icon("a", "A", new(50, 50), @"\\.\DISPLAY1")]);
        var changed = Observation(original.Icons, spacing: new PixelPoint(84, 80));

        var result = DesktopWorkspacePlanningObservationGate.Evaluate(original, changed);

        Assert.False(result.CanReuseUnderstanding);
        Assert.Contains("校准网格", result.BlockReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductPreviewEntry_UsesFreshObservationAndNeverFallsBackToOldPositions()
    {
        var root = FindRepositoryRoot();
        var ui = File.ReadAllText(Path.Combine(root,
            "src", "DesktopAgent.WinUI", "AiAssistantView.MultiDisplayImport.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var capture = ui.IndexOf("CaptureObservationAsync(cts.Token)", StringComparison.Ordinal);
        var gate = ui.IndexOf("DesktopWorkspacePlanningObservationGate.Evaluate", StringComparison.Ordinal);
        var plan = ui.IndexOf("_workspacePlanningService.PlanAsync(", StringComparison.Ordinal);

        Assert.True(capture >= 0 && gate > capture && plan > gate);
        Assert.Contains("latest.Observation,\n                capture.Observation", ui, StringComparison.Ordinal);
        Assert.Contains("latest.Draft,\n                capture.Observation", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("latest.Draft,\n                latest.Observation", ui, StringComparison.Ordinal);
    }

    private static DesktopObservation Observation(
        IReadOnlyList<DesktopIconDescriptor> icons,
        IReadOnlyList<DisplayDescriptor>? displays = null,
        PixelPoint? spacing = null)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            displays ??
            [
                new DisplayDescriptor(@"\\.\DISPLAY1", new(0, 0, 1920, 1080),
                    new(0, 0, 1920, 1040), 96, 96, true),
                new DisplayDescriptor(@"\\.\DISPLAY2", new(1920, 0, 1920, 1080),
                    new(1920, 0, 1920, 1040), 96, 96, false),
            ],
            new DesktopIconPositioningCapabilities(false,
                spacing ?? new PixelPoint(80, 80), ConnectionMethod.FindWindowSW, null),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);

    private static DesktopIconDescriptor Icon(
        string id, string name, PixelPoint position, string display)
        => new(id, name, $@"C:\Desktop\{id}", ItemKind.File,
            position, display, false, true);

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DesktopAgent.sln")))
                return dir.FullName;
        }

        throw new Xunit.Sdk.XunitException("无法定位 DesktopAgent.sln。");
    }
}
