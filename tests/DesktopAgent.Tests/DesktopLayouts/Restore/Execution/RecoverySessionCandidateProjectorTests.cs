using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoverySessionCandidateProjectorTests
{
    [Fact]
    public void EmptyCandidates_IsCleanAndEmpty()
    {
        var result = RecoverySessionCandidateProjector.Project(
            Array.Empty<RecoverySessionRecord>());

        Assert.False(result.HasCorruption);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void ResolvedCandidate_IsFilteredOut()
    {
        var completed = Make(
            RecoverySessionLifecycle.Completed,
            requiresManualRecovery: false,
            isPositionUncertain: false);

        var result = RecoverySessionCandidateProjector.Project([completed]);

        Assert.False(result.HasCorruption);
        Assert.Empty(result.Unresolved);
    }

    [Theory]
    [InlineData(RecoverySessionLifecycle.Executing, false, false)]
    [InlineData(RecoverySessionLifecycle.FailedRollbackIncomplete, true, false)]
    [InlineData(RecoverySessionLifecycle.PositionUncertain, true, true)]
    public void UnresolvedDisposition_IsPreserved(
        RecoverySessionLifecycle lifecycle,
        bool requiresManualRecovery,
        bool isPositionUncertain)
    {
        var record = Make(lifecycle, requiresManualRecovery, isPositionUncertain);

        var result = RecoverySessionCandidateProjector.Project([record]);

        Assert.False(result.HasCorruption);
        Assert.Single(result.Unresolved);
        Assert.Same(record, result.Unresolved[0]);
    }

    [Fact]
    public void MixedCandidates_PreservesInputOrderOfAuthoritativeUnresolved()
    {
        var completed = Make(RecoverySessionLifecycle.Completed, false, false);
        var executing = Make(RecoverySessionLifecycle.Executing, false, false);
        var restored = Make(RecoverySessionLifecycle.FailedAndRolledBack, false, false);
        var manual = Make(RecoverySessionLifecycle.FailedRollbackIncomplete, true, false);

        var result = RecoverySessionCandidateProjector.Project(
            [completed, executing, restored, manual]);

        Assert.False(result.HasCorruption);
        Assert.Equal(2, result.Unresolved.Count);
        Assert.Same(executing, result.Unresolved[0]);
        Assert.Same(manual, result.Unresolved[1]);
    }

    [Theory]
    [InlineData(RecoverySessionLifecycle.Completed, true, false)]
    [InlineData(RecoverySessionLifecycle.Executing, true, false)]
    [InlineData(RecoverySessionLifecycle.FailedRollbackIncomplete, false, false)]
    [InlineData(RecoverySessionLifecycle.PositionUncertain, true, false)]
    public void CompatibilityDrift_IsCorruption_NotAnotherBusinessState(
        RecoverySessionLifecycle lifecycle,
        bool requiresManualRecovery,
        bool isPositionUncertain)
    {
        var drifted = Make(lifecycle, requiresManualRecovery, isPositionUncertain);

        var result = RecoverySessionCandidateProjector.Project([drifted]);

        Assert.True(result.HasCorruption);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void Corruption_DiscardsEarlierProjectedResults()
    {
        var executing = Make(RecoverySessionLifecycle.Executing, false, false);
        var drifted = Make(RecoverySessionLifecycle.Completed, true, false);

        var result = RecoverySessionCandidateProjector.Project([executing, drifted]);

        Assert.True(result.HasCorruption);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void Result_DefensivelyCopiesInputList()
    {
        var executing = Make(RecoverySessionLifecycle.Executing, false, false);
        var source = new List<RecoverySessionRecord> { executing };

        var result = RecoverySessionCandidateProjector.Project(source);
        source.Clear();

        Assert.Single(result.Unresolved);
        Assert.Same(executing, result.Unresolved[0]);
    }

    private static RecoverySessionRecord Make(
        RecoverySessionLifecycle lifecycle,
        bool requiresManualRecovery,
        bool isPositionUncertain) =>
        new()
        {
            Lifecycle = lifecycle,
            RequiresManualRecovery = requiresManualRecovery,
            IsPositionUncertain = isPositionUncertain,
        };
}
