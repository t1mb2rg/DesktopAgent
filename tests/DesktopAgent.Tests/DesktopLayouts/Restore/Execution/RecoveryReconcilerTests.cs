using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoveryReconcilerTests
{
    private const string Monitor = @"\\.\DISPLAY1";
    private static readonly PixelPoint P0 = new(100, 100);
    private static readonly PixelPoint P1 = new(300, 100);
    private static readonly PixelPoint P2 = new(500, 100);

    [Fact]
    public void ObservedAtDesired_IsCommitted_EvenWithoutJournalProgressInput()
    {
        var result = RecoveryReconciler.Reconcile(
            Plan(Step(0, "a", P0, P1)),
            Read(Live("a", P1)));

        Assert.Equal(RecoveryReconciliationState.Committed, result.State);
        Assert.False(result.RequiresAttention);
        Assert.Empty(result.Issues);
        var item = Assert.Single(result.Items);
        Assert.Equal(RecoveryItemRelation.AtDesired, item.Relation);
        Assert.Equal(P1, item.ObservedPosition);
    }

    [Fact]
    public void ObservedAtOriginal_IsRestored_EvenIfRollbackCheckpointCouldLag()
    {
        var result = RecoveryReconciler.Reconcile(
            Plan(Step(0, "a", P0, P1)),
            Read(Live("a", P0)));

        Assert.Equal(RecoveryReconciliationState.Restored, result.State);
        Assert.False(result.RequiresAttention);
        Assert.Empty(result.Issues);
        Assert.Equal(RecoveryItemRelation.AtOriginal, Assert.Single(result.Items).Relation);
    }

    [Fact]
    public void ObservedElsewhere_NeedsAttention_DoesNotGuessHistory()
    {
        var result = RecoveryReconciler.Reconcile(
            Plan(Step(0, "a", P0, P1)),
            Read(Live("a", P2)));

        Assert.Equal(RecoveryReconciliationState.NeedsAttention, result.State);
        Assert.True(result.RequiresAttention);
        Assert.Equal(RecoveryItemRelation.Elsewhere, Assert.Single(result.Items).Relation);
        Assert.Contains(result.Issues,
            i => i.Code == RecoveryReconciliationIssueCode.PositionOutsideKnownEndpoints
                 && i.StableId == "a");
    }

    [Fact]
    public void MissingStableId_NeedsAttention()
    {
        var result = RecoveryReconciler.Reconcile(
            Plan(Step(0, "a", P0, P1)),
            Read());

        Assert.Equal(RecoveryReconciliationState.NeedsAttention, result.State);
        Assert.Equal(RecoveryItemRelation.Missing, Assert.Single(result.Items).Relation);
        Assert.Contains(result.Issues,
            i => i.Code == RecoveryReconciliationIssueCode.MissingObservedStableId);
    }

    [Fact]
    public void DuplicateObservedStableId_NeedsAttention()
    {
        var result = RecoveryReconciler.Reconcile(
            Plan(Step(0, "a", P0, P1)),
            Read(Live("a", P0), Live("a", P1)));

        Assert.Equal(RecoveryReconciliationState.NeedsAttention, result.State);
        Assert.Equal(RecoveryItemRelation.Ambiguous, Assert.Single(result.Items).Relation);
        Assert.Contains(result.Issues,
            i => i.Code == RecoveryReconciliationIssueCode.DuplicateObservedStableId);
    }

    [Fact]
    public void IncompleteDesktopRead_NeedsAttention_WithoutUsingPartialPositions()
    {
        var read = new DesktopIconReadResult(
            new[] { Live("a", P1) },
            1,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);

        var result = RecoveryReconciler.Reconcile(
            Plan(Step(0, "a", P0, P1)), read);

        Assert.Equal(RecoveryReconciliationState.NeedsAttention, result.State);
        var item = Assert.Single(result.Items);
        Assert.Equal(RecoveryItemRelation.ObservationUnavailable, item.Relation);
        Assert.Null(item.ObservedPosition);
        Assert.Contains(result.Issues,
            i => i.Code == RecoveryReconciliationIssueCode.IncompleteDesktopRead);
    }

    [Fact]
    public void DesiredAndOriginalTogether_AreMixedKnown_NotUncertain()
    {
        var result = RecoveryReconciler.Reconcile(
            Plan(
                Step(0, "a", P0, P1),
                Step(1, "b", new PixelPoint(100, 300), new PixelPoint(300, 300))),
            Read(
                Live("a", P1),
                Live("b", new PixelPoint(100, 300))));

        Assert.Equal(RecoveryReconciliationState.MixedKnown, result.State);
        Assert.False(result.RequiresAttention);
        Assert.Empty(result.Issues);
        Assert.Contains(result.Items, i => i.StableId == "a" && i.Relation == RecoveryItemRelation.AtDesired);
        Assert.Contains(result.Items, i => i.StableId == "b" && i.Relation == RecoveryItemRelation.AtOriginal);
    }

    [Fact]
    public void DuplicatePlanStableId_FailsClosed()
    {
        var result = RecoveryReconciler.Reconcile(
            Plan(
                Step(0, "a", P0, P1),
                Step(1, "a", new PixelPoint(100, 300), new PixelPoint(300, 300))),
            Read(Live("a", P1)));

        Assert.Equal(RecoveryReconciliationState.NeedsAttention, result.State);
        Assert.Empty(result.Items);
        Assert.Contains(result.Issues,
            i => i.Code == RecoveryReconciliationIssueCode.DuplicatePlanStableId);
    }

    [Fact]
    public void TemporaryStagingPlan_FailsClosedInR1()
    {
        var staging = new PixelPoint(700, 500);
        var plan = new TargetDisplayLayoutExecutionPlan(
            Monitor,
            isExecutable: true,
            isNoOp: false,
            new[]
            {
                new TargetDisplayLayoutExecutionStep(
                    0, "a", "A", P0, staging,
                    TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging,
                    Monitor,
                    0),
                new TargetDisplayLayoutExecutionStep(
                    1, "a", "A", staging, P1,
                    TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget,
                    Monitor,
                    0),
            },
            new[] { new TargetDisplayLayoutStagingAssignment("a", staging, 0, 1) },
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var result = RecoveryReconciler.Reconcile(plan, Read(Live("a", P1)));

        Assert.Equal(RecoveryReconciliationState.NeedsAttention, result.State);
        Assert.Contains(result.Issues,
            i => i.Code == RecoveryReconciliationIssueCode.TemporaryStagingUnsupported);
    }

    [Fact]
    public void EmptyPlan_IsNoWork()
    {
        var result = RecoveryReconciler.Reconcile(Plan(), Read());

        Assert.Equal(RecoveryReconciliationState.NoWork, result.State);
        Assert.False(result.RequiresAttention);
        Assert.Empty(result.Items);
        Assert.Empty(result.Issues);
    }

    private static TargetDisplayLayoutExecutionStep Step(
        int index, string stableId, PixelPoint from, PixelPoint to)
        => new(
            index,
            stableId,
            stableId.ToUpperInvariant(),
            from,
            to,
            TargetDisplayLayoutExecutionStepKind.MoveToTarget,
            Monitor);

    private static TargetDisplayLayoutExecutionPlan Plan(
        params TargetDisplayLayoutExecutionStep[] steps)
        => new(
            Monitor,
            isExecutable: true,
            isNoOp: steps.Length == 0,
            steps,
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

    private static DesktopIconReadResult Read(params DesktopIconDescriptor[] icons)
        => new(
            icons,
            0,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);

    private static DesktopIconDescriptor Live(string stableId, PixelPoint position)
        => new(
            stableId,
            stableId.ToUpperInvariant(),
            null,
            ItemKind.File,
            position,
            Monitor,
            false,
            true);
}
