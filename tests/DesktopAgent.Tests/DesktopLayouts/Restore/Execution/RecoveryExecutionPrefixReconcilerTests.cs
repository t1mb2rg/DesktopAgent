using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoveryExecutionPrefixReconcilerTests
{
    private const string Monitor = @"\\.\DISPLAY1";
    private const string OtherMonitor = @"\\.\DISPLAY2";

    private static readonly PixelPoint A0 = new(100, 100);
    private static readonly PixelPoint B0 = new(300, 100);
    private static readonly PixelPoint Staging = new(700, 500);

    [Fact]
    public void Swap_OriginalState_MatchesPrefixZero()
    {
        var (source, plan) = SwapPlan();

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(Live("a", A0), Live("b", B0)));

        Assert.Equal(RecoveryExecutionPrefixState.Original, result.State);
        Assert.Equal(0, result.AppliedStepCount);
        Assert.Null(result.NextRollbackStepIndex);
        Assert.False(result.RequiresAttention);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Swap_AfterEnterStaging_MatchesPrefixOne()
    {
        var (source, plan) = SwapPlan();

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(Live("a", Staging), Live("b", B0)));

        Assert.Equal(RecoveryExecutionPrefixState.Intermediate, result.State);
        Assert.Equal(1, result.AppliedStepCount);
        Assert.Equal(0, result.NextRollbackStepIndex);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Swap_AfterMiddleDirectMove_MatchesPrefixTwo()
    {
        var (source, plan) = SwapPlan();

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(Live("a", Staging), Live("b", A0)));

        Assert.Equal(RecoveryExecutionPrefixState.Intermediate, result.State);
        Assert.Equal(2, result.AppliedStepCount);
        Assert.Equal(1, result.NextRollbackStepIndex);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Swap_FullDesiredState_MatchesCommittedPrefix()
    {
        var (source, plan) = SwapPlan();

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(Live("a", B0), Live("b", A0)));

        Assert.Equal(RecoveryExecutionPrefixState.Committed, result.State);
        Assert.Equal(plan.Steps.Count, result.AppliedStepCount);
        Assert.Equal(plan.Steps.Count - 1, result.NextRollbackStepIndex);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void RollbackCrash_EarlierPrefixIsRecognizedWithoutJournalInput()
    {
        var (source, plan) = SwapPlan();

        // 假设 Forward 已完成全部三步，随后 rollback 已物理撤销最后一步但 checkpoint 尚未落盘。
        // 当前事实应回到 prefix 2；reconciler 不接收任何 Journal progress。
        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(Live("a", Staging), Live("b", A0)));

        Assert.Equal(RecoveryExecutionPrefixState.Intermediate, result.State);
        Assert.Equal(2, result.AppliedStepCount);
        Assert.Equal(1, result.NextRollbackStepIndex);
    }

    [Fact]
    public void ForwardCrash_AdvancedPrefixIsRecognizedWithoutCheckpoint()
    {
        var (source, plan) = SwapPlan();

        // Writer 已把 A 放到 staging，但持久化仍可能声称没有完成任何 step。
        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(Live("a", Staging), Live("b", B0)));

        Assert.Equal(RecoveryExecutionPrefixState.Intermediate, result.State);
        Assert.Equal(1, result.AppliedStepCount);
    }

    [Fact]
    public void ArbitraryThirdPosition_DoesNotMatchAnyPrefix()
    {
        var (source, plan) = SwapPlan();
        var elsewhere = new PixelPoint(900, 700);

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(Live("a", elsewhere), Live("b", B0)));

        Assert.Equal(RecoveryExecutionPrefixState.NeedsAttention, result.State);
        Assert.Null(result.AppliedStepCount);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryExecutionPrefixIssueCode.NoMatchingPrefix);
    }

    [Fact]
    public void MissingRelevantIcon_NeedsAttention()
    {
        var (source, plan) = SwapPlan();

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(Live("a", Staging)));

        Assert.Equal(RecoveryExecutionPrefixState.NeedsAttention, result.State);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryExecutionPrefixIssueCode.MissingObservedStableId
                     && issue.StableId == "b");
    }

    [Fact]
    public void DuplicateObservedStableId_NeedsAttention()
    {
        var (source, plan) = SwapPlan();

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(
                Live("a", Staging),
                Live("a", Staging),
                Live("b", B0)));

        Assert.Equal(RecoveryExecutionPrefixState.NeedsAttention, result.State);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryExecutionPrefixIssueCode.DuplicateObservedStableId
                     && issue.StableId == "a");
    }

    [Fact]
    public void IncompleteRead_NeedsAttentionBeforeUsingPartialFacts()
    {
        var (source, plan) = SwapPlan();
        var read = new DesktopIconReadResult(
            new[] { Live("a", Staging), Live("b", B0) },
            1,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);

        var result = RecoveryExecutionPrefixReconciler.Reconcile(source, plan, read);

        Assert.Equal(RecoveryExecutionPrefixState.NeedsAttention, result.State);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryExecutionPrefixIssueCode.IncompleteDesktopRead);
    }

    [Fact]
    public void SamePixelOnWrongMonitor_DoesNotMatchPrefix()
    {
        var (source, plan) = SwapPlan();

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(
                Live("a", Staging, OtherMonitor),
                Live("b", B0)));

        Assert.Equal(RecoveryExecutionPrefixState.NeedsAttention, result.State);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryExecutionPrefixIssueCode.NoMatchingPrefix);
    }

    [Fact]
    public void UnrelatedIconMovement_IsIgnored()
    {
        var (source, plan) = SwapPlan();

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(
                Live("a", Staging),
                Live("b", A0),
                Live("unrelated", new PixelPoint(1234, 567))));

        Assert.Equal(RecoveryExecutionPrefixState.Intermediate, result.State);
        Assert.Equal(2, result.AppliedStepCount);
    }

    [Fact]
    public void TamperedStepContinuity_FailsClosedBeforeObservationMatching()
    {
        var (source, plan) = SwapPlan();
        var badSteps = plan.Steps.ToArray();
        badSteps[2] = badSteps[2] with { FromPosition = new PixelPoint(999, 999) };
        var tampered = new TargetDisplayLayoutExecutionPlan(
            plan.TargetDeviceName,
            true,
            false,
            badSteps,
            plan.StagingAssignments,
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            tampered,
            Read(Live("a", B0), Live("b", A0)));

        Assert.Equal(RecoveryExecutionPrefixState.NeedsAttention, result.State);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent
                     && issue.StableId == "a");
    }

    [Fact]
    public void TamperedStagingAssignment_FailsClosed()
    {
        var (source, plan) = SwapPlan();
        var wrongAssignment = new TargetDisplayLayoutStagingAssignment(
            "a",
            new PixelPoint(888, 888),
            0,
            2);
        var tampered = new TargetDisplayLayoutExecutionPlan(
            plan.TargetDeviceName,
            true,
            false,
            plan.Steps,
            new[] { wrongAssignment },
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            tampered,
            Read(Live("a", Staging), Live("b", B0)));

        Assert.Equal(RecoveryExecutionPrefixState.NeedsAttention, result.State);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent);
    }

    [Fact]
    public void RepeatedWholeStateAcrossPrefixes_IsAmbiguousAndFailsClosed()
    {
        var item = MoveItem("a", A0, A0);
        var source = SourcePlan(
            requiresStaging: false,
            new[] { item });
        var away = new PixelPoint(500, 500);
        var plan = new TargetDisplayLayoutExecutionPlan(
            Monitor,
            true,
            false,
            new[]
            {
                Step(0, "a", A0, away, TargetDisplayLayoutExecutionStepKind.MoveToTarget),
                Step(1, "a", away, A0, TargetDisplayLayoutExecutionStepKind.MoveToTarget),
            },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var result = RecoveryExecutionPrefixReconciler.Reconcile(
            source,
            plan,
            Read(Live("a", A0)));

        Assert.Equal(RecoveryExecutionPrefixState.NeedsAttention, result.State);
        Assert.Contains(result.Issues,
            issue => issue.Code == RecoveryExecutionPrefixIssueCode.AmbiguousPrefixMatch);
    }

    [Fact]
    public void NoWorkPlan_ReturnsNoWork()
    {
        var source = SourcePlan(false, Array.Empty<TargetDisplayLayoutRestorePlanItem>());
        var plan = new TargetDisplayLayoutExecutionPlan(
            Monitor,
            true,
            true,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        var result = RecoveryExecutionPrefixReconciler.Reconcile(source, plan, Read());

        Assert.Equal(RecoveryExecutionPrefixState.NoWork, result.State);
        Assert.Equal(0, result.AppliedStepCount);
        Assert.Null(result.NextRollbackStepIndex);
        Assert.Empty(result.Issues);
    }

    private static (TargetDisplayLayoutRestorePlan Source, TargetDisplayLayoutExecutionPlan Plan) SwapPlan()
    {
        var source = SourcePlan(
            requiresStaging: true,
            new[]
            {
                MoveItem("a", A0, B0),
                MoveItem("b", B0, A0),
            });

        var steps = new[]
        {
            Step(0, "a", A0, Staging, TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, 0),
            Step(1, "b", B0, A0, TargetDisplayLayoutExecutionStepKind.MoveToTarget),
            Step(2, "a", Staging, B0, TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget),
        };

        var plan = new TargetDisplayLayoutExecutionPlan(
            Monitor,
            true,
            false,
            steps,
            new[] { new TargetDisplayLayoutStagingAssignment("a", Staging, 0, 2) },
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        return (source, plan);
    }

    private static TargetDisplayLayoutRestorePlan SourcePlan(
        bool requiresStaging,
        IReadOnlyList<TargetDisplayLayoutRestorePlanItem> items)
        => new(
            Monitor,
            snapshotTargetIconCount: items.Count,
            alreadyAtTargetCount: 0,
            moveRequiredCount: items.Count,
            missingCount: 0,
            blockedCount: 0,
            hasBlockingErrors: false,
            requiresTemporaryStaging: requiresStaging,
            isNoOp: items.Count == 0,
            items,
            Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            requiresStaging
                ? new[] { new TargetDisplayLayoutRestoreCycle(items.Select(item => item.StableId).ToArray()) }
                : Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            moveExecutionOrder: requiresStaging ? null : items.Select(item => item.StableId).ToArray());

    private static TargetDisplayLayoutRestorePlanItem MoveItem(
        string stableId,
        PixelPoint current,
        PixelPoint target)
        => new(
            stableId,
            stableId.ToUpperInvariant(),
            target,
            current,
            Monitor,
            TargetDisplayLayoutRestoreItemState.MoveRequired,
            isFileSystemItem: true,
            isSystemItem: false);

    private static TargetDisplayLayoutExecutionStep Step(
        int index,
        string stableId,
        PixelPoint from,
        PixelPoint to,
        TargetDisplayLayoutExecutionStepKind kind,
        int? stagingSequence = null)
        => new(
            index,
            stableId,
            stableId.ToUpperInvariant(),
            from,
            to,
            kind,
            Monitor,
            stagingSequence);

    private static DesktopIconReadResult Read(params DesktopIconDescriptor[] icons)
        => new(
            icons,
            0,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);

    private static DesktopIconDescriptor Live(
        string stableId,
        PixelPoint position,
        string monitor = Monitor)
        => new(
            stableId,
            stableId.ToUpperInvariant(),
            null,
            ItemKind.File,
            position,
            monitor,
            false,
            true);
}
