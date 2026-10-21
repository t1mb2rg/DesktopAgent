using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopObservationDiagnosticsRunnerTests
{
    [Fact]
    public async Task RunOnceAsync_FirstStableObservation_EstablishesBaseline()
    {
        var observation = Observation(new PixelPoint(100, 100));
        var runner = Runner(Stable(observation));

        var result = await runner.RunOnceAsync();

        Assert.Equal(DesktopObservationDiagnosticsStatus.BaselineEstablished, result.Status);
        Assert.NotNull(result.BaselineUpdate);
        Assert.Equal(
            DesktopStateBaselineUpdateStatus.BaselineEstablished,
            result.BaselineUpdate.Status);
        Assert.Same(observation, result.BaselineUpdate.CurrentObservation);
        Assert.Null(result.ChangeSet);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task RunOnceAsync_EquivalentStableObservation_ReturnsNoChanges()
    {
        var runner = Runner(
            Stable(Observation(new PixelPoint(100, 100))),
            Stable(Observation(new PixelPoint(100, 100))));

        await runner.RunOnceAsync();
        var result = await runner.RunOnceAsync();

        Assert.Equal(DesktopObservationDiagnosticsStatus.NoChanges, result.Status);
        Assert.NotNull(result.ChangeSet);
        Assert.False(result.ChangeSet.HasChanges);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task RunOnceAsync_ChangedStableObservation_ReturnsChangesDetected()
    {
        var runner = Runner(
            Stable(Observation(new PixelPoint(100, 100))),
            Stable(Observation(new PixelPoint(320, 240))));

        await runner.RunOnceAsync();
        var result = await runner.RunOnceAsync();

        Assert.Equal(DesktopObservationDiagnosticsStatus.ChangesDetected, result.Status);
        Assert.True(result.HasChanges);
        var changeSet = Assert.IsType<DesktopStateChangeSet>(result.ChangeSet);
        var item = Assert.Single(changeSet.ItemChanges);
        Assert.Equal(DesktopItemStateChangeKind.Changed, item.Kind);
        Assert.True(item.FactChanges.HasFlag(DesktopItemFactChange.Position));
        Assert.True(item.IsMoved);
    }

    [Fact]
    public async Task RunOnceAsync_Unstable_DoesNotAdvanceBaseline()
    {
        var initial = Observation(new PixelPoint(100, 100));
        var changed = Observation(new PixelPoint(500, 400));
        var runner = Runner(
            Stable(initial),
            Unstable(),
            Stable(changed));

        await runner.RunOnceAsync();
        var unstable = await runner.RunOnceAsync();
        var after = await runner.RunOnceAsync();

        Assert.Equal(DesktopObservationDiagnosticsStatus.Unstable, unstable.Status);
        Assert.Null(unstable.BaselineUpdate);
        Assert.Equal(DesktopObservationDiagnosticsStatus.ChangesDetected, after.Status);
        Assert.Same(initial, after.BaselineUpdate!.PreviousObservation);
        Assert.Same(changed, after.BaselineUpdate.CurrentObservation);
    }

    [Fact]
    public async Task RunOnceAsync_Unavailable_DoesNotAdvanceBaseline()
    {
        var initial = Observation(new PixelPoint(100, 100));
        var changed = Observation(new PixelPoint(700, 500));
        var runner = Runner(
            Stable(initial),
            Unavailable(),
            Stable(changed));

        await runner.RunOnceAsync();
        var unavailable = await runner.RunOnceAsync();
        var after = await runner.RunOnceAsync();

        Assert.Equal(DesktopObservationDiagnosticsStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.BaselineUpdate);
        Assert.Equal(DesktopObservationDiagnosticsStatus.ChangesDetected, after.Status);
        Assert.Same(initial, after.BaselineUpdate!.PreviousObservation);
    }

    [Fact]
    public async Task ResetBaseline_NextStableObservation_EstablishesBaselineAgain()
    {
        var runner = Runner(
            Stable(Observation(new PixelPoint(100, 100))),
            Stable(Observation(new PixelPoint(200, 200))));

        await runner.RunOnceAsync();
        runner.ResetBaseline();
        var result = await runner.RunOnceAsync();

        Assert.Equal(DesktopObservationDiagnosticsStatus.BaselineEstablished, result.Status);
        Assert.Null(result.ChangeSet);
    }

    private static DesktopObservationDiagnosticsRunner Runner(
        params StableDesktopObservationResult[] results) =>
        new(
            new ScriptedStableObservationSource(results),
            new DesktopStateBaselineTracker());

    private static StableDesktopObservationResult Stable(DesktopObservation observation) =>
        new(
            StableDesktopObservationStatus.Stable,
            observation,
            Array.Empty<DesktopObservationCaptureFailure>(),
            Array.Empty<DesktopObservationStabilityIssue>());

    private static StableDesktopObservationResult Unstable() =>
        new(
            StableDesktopObservationStatus.Unstable,
            null,
            Array.Empty<DesktopObservationCaptureFailure>(),
            new[]
            {
                new DesktopObservationStabilityIssue(
                    DesktopObservationStabilityIssueCode.IconChanged,
                    "desktop is still changing",
                    "a"),
            });

    private static StableDesktopObservationResult Unavailable() =>
        new(
            StableDesktopObservationStatus.Unavailable,
            null,
            new[]
            {
                new DesktopObservationCaptureFailure(
                    DesktopObservationSamplePhase.First,
                    new DesktopObservationCaptureIssue(
                        DesktopObservationCaptureIssueCode.IncompleteDesktopRead,
                        "incomplete")),
            },
            Array.Empty<DesktopObservationStabilityIssue>());

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

    private sealed class ScriptedStableObservationSource(
        params StableDesktopObservationResult[] results)
        : IStableDesktopObservationSource
    {
        private int _index;

        public Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var index = Math.Min(_index++, results.Length - 1);
            return Task.FromResult(results[index]);
        }
    }
}
