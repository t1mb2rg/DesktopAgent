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
        var completed = Make(RecoverySessionLifecycle.Completed);

        var result = RecoverySessionCandidateProjector.Project([completed]);

        Assert.False(result.HasCorruption);
        Assert.Empty(result.Unresolved);
    }

    [Theory]
    [InlineData(RecoverySessionLifecycle.Executing)]
    [InlineData(RecoverySessionLifecycle.FailedRollbackIncomplete)]
    [InlineData(RecoverySessionLifecycle.PositionUncertain)]
    public void UnresolvedDisposition_IsPreserved(
        RecoverySessionLifecycle lifecycle)
    {
        var record = Make(lifecycle);

        var result = RecoverySessionCandidateProjector.Project([record]);

        Assert.False(result.HasCorruption);
        Assert.Single(result.Unresolved);
        Assert.Same(record, result.Unresolved[0]);
    }

    [Fact]
    public void MixedCandidates_PreservesInputOrderOfAuthoritativeUnresolved()
    {
        var completed = Make(RecoverySessionLifecycle.Completed);
        var executing = Make(RecoverySessionLifecycle.Executing);
        var restored = Make(RecoverySessionLifecycle.FailedAndRolledBack);
        var manual = Make(RecoverySessionLifecycle.FailedRollbackIncomplete);

        var result = RecoverySessionCandidateProjector.Project(
            [completed, executing, restored, manual]);

        Assert.False(result.HasCorruption);
        Assert.Equal(2, result.Unresolved.Count);
        Assert.Same(executing, result.Unresolved[0]);
        Assert.Same(manual, result.Unresolved[1]);
    }

    [Fact]
    public void NullCandidate_IsCorruption()
    {
        var result = RecoverySessionCandidateProjector.Project([null!]);

        Assert.True(result.HasCorruption);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void Corruption_DiscardsEarlierProjectedResults()
    {
        var executing = Make(RecoverySessionLifecycle.Executing);

        var result = RecoverySessionCandidateProjector.Project([executing, null!]);

        Assert.True(result.HasCorruption);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void Result_DefensivelyCopiesInputList()
    {
        var executing = Make(RecoverySessionLifecycle.Executing);
        var source = new List<RecoverySessionRecord> { executing };

        var result = RecoverySessionCandidateProjector.Project(source);
        source.Clear();

        Assert.Single(result.Unresolved);
        Assert.Same(executing, result.Unresolved[0]);
    }

    private static RecoverySessionRecord Make(
        RecoverySessionLifecycle lifecycle) =>
        new()
        {
            Lifecycle = lifecycle,
        };
}
