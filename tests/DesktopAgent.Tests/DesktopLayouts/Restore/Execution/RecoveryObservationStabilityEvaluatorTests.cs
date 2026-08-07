using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoveryObservationStabilityEvaluatorTests
{
    private const string D1 = @"\\.\DISPLAY1";
    private const string D2 = @"\\.\DISPLAY2";
    private static readonly PixelPoint P0 = new(100, 100);
    private static readonly PixelPoint P1 = new(300, 100);
    private static readonly PixelRect W1 = new(0, 0, 1920, 1080);
    private static readonly PixelRect W2 = new(1920, 0, 3840, 1080);

    [Fact]
    public void IdenticalRelevantFactsAndTopology_AreStable()
    {
        var plan = Plan(Step(0, "a", P0, P1));
        var first = Sample(Displays(), Read(Icon("a", P1, D1)));
        var second = Sample(Displays(), Read(Icon("a", P1, D1)));

        var result = RecoveryObservationStabilityEvaluator.Evaluate(plan, first, second);

        Assert.True(result.IsStable);
        Assert.Equal(RecoveryObservationStabilityStatus.Stable, result.Status);
        Assert.Same(second, result.StableSample);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void RelevantIconMovesBetweenReads_IsUnstable()
    {
        var plan = Plan(Step(0, "a", P0, P1));
        var first = Sample(Displays(), Read(Icon("a", P0, D1)));
        var second = Sample(Displays(), Read(Icon("a", P1, D1)));

        var result = RecoveryObservationStabilityEvaluator.Evaluate(plan, first, second);

        Assert.Equal(RecoveryObservationStabilityStatus.Unstable, result.Status);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryObservationStabilityIssueCode.RelevantIconChanged
                     && issue.StableId == "a");
    }

    [Fact]
    public void UnrelatedIconMovesBetweenReads_DoesNotInvalidateRecoveryObservation()
    {
        var plan = Plan(Step(0, "a", P0, P1));
        var first = Sample(Displays(), Read(
            Icon("a", P1, D1),
            Icon("unrelated", new PixelPoint(700, 100), D1)));
        var second = Sample(Displays(), Read(
            Icon("a", P1, D1),
            Icon("unrelated", new PixelPoint(900, 100), D1)));

        var result = RecoveryObservationStabilityEvaluator.Evaluate(plan, first, second);

        Assert.True(result.IsStable);
    }

    [Fact]
    public void DisplayListOrderChange_IsStillStable()
    {
        var plan = Plan(Step(0, "a", P0, P1));
        var firstDisplays = Displays();
        var secondDisplays = firstDisplays.Reverse().ToArray();

        var result = RecoveryObservationStabilityEvaluator.Evaluate(
            plan,
            Sample(firstDisplays, Read(Icon("a", P1, D1))),
            Sample(secondDisplays, Read(Icon("a", P1, D1))));

        Assert.True(result.IsStable);
    }

    [Fact]
    public void TopologyGeometryChange_IsUnstable()
    {
        var plan = Plan(Step(0, "a", P0, P1));
        var changed = new[]
        {
            new DisplayDescriptor(D1, W1, new PixelRect(0, 0, 1900, 1040), 96, 96, true),
            new DisplayDescriptor(D2, W2, W2, 96, 96, false),
        };

        var result = RecoveryObservationStabilityEvaluator.Evaluate(
            plan,
            Sample(Displays(), Read(Icon("a", P1, D1))),
            Sample(changed, Read(Icon("a", P1, D1))));

        Assert.Equal(RecoveryObservationStabilityStatus.Unstable, result.Status);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryObservationStabilityIssueCode.TopologyChanged);
    }

    [Fact]
    public void MissingTargetDisplay_IsUnavailable()
    {
        var plan = Plan(Step(0, "a", P0, P1));
        var onlySecond = new[]
        {
            new DisplayDescriptor(D2, W2, W2, 96, 96, true),
        };

        var result = RecoveryObservationStabilityEvaluator.Evaluate(
            plan,
            Sample(onlySecond, Read(Icon("a", P1, D2))),
            Sample(onlySecond, Read(Icon("a", P1, D2))));

        Assert.Equal(RecoveryObservationStabilityStatus.Unavailable, result.Status);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryObservationStabilityIssueCode.TargetDisplayMissing);
    }

    [Fact]
    public void IncompleteRead_IsUnavailable()
    {
        var plan = Plan(Step(0, "a", P0, P1));
        var incomplete = new DesktopIconReadResult(
            new[] { Icon("a", P1, D1) },
            1,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);

        var result = RecoveryObservationStabilityEvaluator.Evaluate(
            plan,
            Sample(Displays(), incomplete),
            Sample(Displays(), Read(Icon("a", P1, D1))));

        Assert.Equal(RecoveryObservationStabilityStatus.Unavailable, result.Status);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryObservationStabilityIssueCode.IncompleteDesktopRead);
    }

    [Fact]
    public void MissingRelevantIconInBothReads_CanBeStable_ThenReconcilerDecidesMissing()
    {
        var plan = Plan(Step(0, "a", P0, P1));
        var first = Sample(Displays(), Read());
        var second = Sample(Displays(), Read());

        var stability = RecoveryObservationStabilityEvaluator.Evaluate(plan, first, second);
        var reconciliation = RecoveryReconciler.Reconcile(plan, stability.StableSample!.Desktop);

        Assert.True(stability.IsStable);
        Assert.Equal(RecoveryReconciliationState.NeedsAttention, reconciliation.State);
        Assert.Equal(RecoveryItemRelation.Missing, Assert.Single(reconciliation.Items).Relation);
    }

    [Fact]
    public void EquivalentTopologyComparison_IsOrderInsensitiveButGeometrySensitive()
    {
        var expected = Displays();
        var reversed = expected.Reverse().ToArray();
        var changed = new[]
        {
            new DisplayDescriptor(D1, W1, W1, 120, 120, true),
            new DisplayDescriptor(D2, W2, W2, 96, 96, false),
        };

        Assert.True(RecoveryObservationStabilityEvaluator.AreTopologiesEquivalent(expected, reversed));
        Assert.False(RecoveryObservationStabilityEvaluator.AreTopologiesEquivalent(expected, changed));
    }

    private static RecoveryObservationSample Sample(
        IReadOnlyList<DisplayDescriptor> displays,
        DesktopIconReadResult desktop)
        => new(displays, desktop);

    private static IReadOnlyList<DisplayDescriptor> Displays()
        => new[]
        {
            new DisplayDescriptor(D1, W1, W1, 96, 96, true),
            new DisplayDescriptor(D2, W2, W2, 96, 96, false),
        };

    private static DesktopIconReadResult Read(params DesktopIconDescriptor[] icons)
        => new(icons, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

    private static DesktopIconDescriptor Icon(string stableId, PixelPoint position, string monitor)
        => new(stableId, $"N_{stableId}", null, ItemKind.File, position, monitor, false, true);

    private static TargetDisplayLayoutExecutionStep Step(
        int index,
        string stableId,
        PixelPoint from,
        PixelPoint to)
        => new(
            index,
            stableId,
            $"N_{stableId}",
            from,
            to,
            TargetDisplayLayoutExecutionStepKind.MoveToTarget,
            D1);

    private static TargetDisplayLayoutExecutionPlan Plan(params TargetDisplayLayoutExecutionStep[] steps)
        => new(
            D1,
            true,
            false,
            steps,
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());
}
