using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class TargetDisplayLayoutExecutionCoordinatorStagingForwardTests
{
    private static readonly string TD = @"\\.\DISPLAY1";
    private static readonly PixelPoint Spacing = new(80, 80);
    private static readonly PixelRect WorkArea = new(0, 0, 240, 80);

    private static DisplayDescriptor MakeDisplay() =>
        new(TD, WorkArea, WorkArea, 96, 96, true);

    private static DesktopIconPositioningCapabilities MakeCapabilities() =>
        new(false, Spacing, ConnectionMethod.FindWindowSW, null);

    private static DesktopIconSnapshotItem Snap(int ordinal, string stableId, PixelPoint position) =>
        new(ordinal, stableId, $"N_{stableId}", null, ItemKind.File, position, TD, false, true);

    private static DesktopIconDescriptor Live(string stableId, PixelPoint position) =>
        new(stableId, $"N_{stableId}", null, ItemKind.File, position, TD, false, true);

    private static TargetDisplayLayoutRestorePlan MakeSwapRestorePlan(
        IReadOnlyList<DesktopIconDescriptor> liveIcons)
    {
        var snapshot = new DesktopLayoutSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            TD,
            ConnectionMethod.FindWindowSW,
            1,
            [
                Snap(0, "a", new PixelPoint(80, 0)),
                Snap(1, "b", new PixelPoint(0, 0)),
            ]);

        var read = new DesktopIconReadResult(
            liveIcons,
            0,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);

        return TargetDisplayLayoutRestorePlanner.CreatePlan(
            snapshot,
            read,
            [MakeDisplay()],
            MakeCapabilities(),
            TD);
    }

    private static readonly Func<
        TargetDisplayLayoutExecutionPlan,
        TargetDisplayLayoutRecoverySession,
        CancellationToken,
        Task<TargetDisplayLayoutExecutionVerificationResult>> VerifyPassed =
        (_, _, _) => Task.FromResult(
            TargetDisplayLayoutExecutionVerificationResult.ForwardPassed(DateTimeOffset.UtcNow));

    private static TargetDisplayLayoutExecutionCoordinator MakeCoordinator(
        IReadOnlyList<DesktopIconDescriptor> liveIcons,
        ScriptedDesktopIconPositionWriter writer) =>
        new(
            new FakeDisplayTopologyReader(TD, WorkArea),
            new FakeDesktopIconReader(liveIcons),
            writer,
            new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            new InMemoryDesktopLayoutSnapshotStore(),
            TargetDisplayLayoutExecutionPlanner.CreatePlan,
            VerifyPassed,
            VerifyPassed);

    [Fact]
    public async Task PlannerGeneratedSwap_StagingGate_ExecutesWholeForwardPlan()
    {
        var liveIcons = new[]
        {
            Live("a", new PixelPoint(0, 0)),
            Live("b", new PixelPoint(80, 0)),
        };
        var restorePlan = MakeSwapRestorePlan(liveIcons);

        Assert.True(restorePlan.RequiresTemporaryStaging);
        Assert.NotEmpty(restorePlan.Cycles);

        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new PixelPoint(0, 0), new PixelPoint(160, 0));
        writer.ScriptExactResult("b", new PixelPoint(80, 0), new PixelPoint(0, 0));
        writer.ScriptExactResult("a", new PixelPoint(160, 0), new PixelPoint(80, 0));

        var coordinator = MakeCoordinator(liveIcons, writer);
        var result = await coordinator.ExecuteWithScopeGateAsync(
            restorePlan,
            TargetDisplayLayoutStagingExecutionScopeGate.Evaluate);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.Status);
        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.Equal(3, result.ForwardAttempts.Count);
        Assert.Empty(result.RollbackAttempts);
        Assert.Equal(3, writer.AttemptCount);

        Assert.NotNull(result.Session);
        Assert.True(result.Session!.ExecutionPlan.UsesTemporaryStaging);
        Assert.Single(result.Session.ExecutionPlan.StagingAssignments);
        Assert.Equal(new[] { 0, 1, 2 }, result.Session.CompletedForwardStepIndexes);

        Assert.Collection(
            writer.AttemptHistory,
            attempt =>
            {
                Assert.Equal("a", attempt.StableId);
                Assert.Equal(new PixelPoint(160, 0), attempt.TargetPosition);
            },
            attempt =>
            {
                Assert.Equal("b", attempt.StableId);
                Assert.Equal(new PixelPoint(0, 0), attempt.TargetPosition);
            },
            attempt =>
            {
                Assert.Equal("a", attempt.StableId);
                Assert.Equal(new PixelPoint(80, 0), attempt.TargetPosition);
            });
    }

    [Fact]
    public async Task PlannerGeneratedSwap_MiddleStepInexact_RollsBackThroughStagingToOriginal()
    {
        var liveIcons = new[]
        {
            Live("a", new PixelPoint(0, 0)),
            Live("b", new PixelPoint(80, 0)),
        };
        var restorePlan = MakeSwapRestorePlan(liveIcons);

        var writer = new ScriptedDesktopIconPositionWriter();
        writer.ScriptExactResult("a", new PixelPoint(0, 0), new PixelPoint(160, 0));
        writer.ScriptInexactResult(
            "b",
            new PixelPoint(80, 0),
            new PixelPoint(0, 0),
            new PixelPoint(10, 0));
        writer.ScriptExactResult("b", new PixelPoint(10, 0), new PixelPoint(80, 0));
        writer.ScriptExactResult("a", new PixelPoint(160, 0), new PixelPoint(0, 0));

        var coordinator = MakeCoordinator(liveIcons, writer);
        var result = await coordinator.ExecuteWithScopeGateAsync(
            restorePlan,
            TargetDisplayLayoutStagingExecutionScopeGate.Evaluate);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack, result.Status);
        Assert.False(result.RequiresManualRecovery);
        Assert.False(result.IsPositionUncertain);
        Assert.Equal(2, result.ForwardAttempts.Count);
        Assert.Equal(2, result.RollbackAttempts.Count);
        Assert.Equal(4, writer.AttemptCount);

        Assert.Equal(1, result.RollbackAttempts[0].SourceStepIndex);
        Assert.Equal(0, result.RollbackAttempts[1].SourceStepIndex);
        Assert.All(result.RollbackAttempts, attempt =>
            Assert.Equal(TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess, attempt.Outcome));

        Assert.NotNull(result.Session);
        Assert.Single(result.Session!.CompletedForwardStepIndexes);
        Assert.Contains(0, result.Session.CompletedForwardStepIndexes);
        Assert.Equal(new[] { 1, 0 }, result.Session.CompletedRollbackSourceStepIndexes);

        Assert.Collection(
            writer.AttemptHistory,
            attempt => Assert.Equal(new PixelPoint(160, 0), attempt.TargetPosition),
            attempt => Assert.Equal(new PixelPoint(0, 0), attempt.TargetPosition),
            attempt => Assert.Equal(new PixelPoint(80, 0), attempt.TargetPosition),
            attempt => Assert.Equal(new PixelPoint(0, 0), attempt.TargetPosition));
    }
}
