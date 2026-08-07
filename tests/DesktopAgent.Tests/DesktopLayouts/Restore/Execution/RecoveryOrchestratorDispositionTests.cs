using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class RecoveryOrchestratorDispositionTests
{
    [Theory]
    [InlineData(RecoverySessionLifecycle.Ready)]
    [InlineData(RecoverySessionLifecycle.Completed)]
    [InlineData(RecoverySessionLifecycle.FailedAndRolledBack)]
    public async Task TryResolveAsync_HandlingNone_IsAlreadyResolved(
        RecoverySessionLifecycle lifecycle)
    {
        var orchestrator = new RecoveryOrchestrator(new NeverCalledStore());
        var record = new RecoverySessionRecord { Lifecycle = lifecycle };

        var resolved = await orchestrator.TryResolveAsync(record);

        Assert.True(resolved);
    }

    [Fact]
    public async Task TryResolveAsync_Executing_StillNeedsAutomaticReconciliation()
    {
        var orchestrator = new RecoveryOrchestrator(new NeverCalledStore());
        var record = new RecoverySessionRecord
        {
            Lifecycle = RecoverySessionLifecycle.Executing,
        };

        var resolved = await orchestrator.TryResolveAsync(record);

        Assert.False(resolved);
        Assert.Equal(
            RecoverySessionHandling.AutomaticReconciliation,
            record.GetDisposition().Handling);
    }

    [Theory]
    [InlineData(
        RecoverySessionLifecycle.FailedRollbackIncomplete,
        RecoverySessionSafetyClass.KnownIncomplete)]
    [InlineData(
        RecoverySessionLifecycle.PositionUncertain,
        RecoverySessionSafetyClass.PositionUncertain)]
    public async Task TryResolveAsync_ManualIntervention_RemainsUnresolved(
        RecoverySessionLifecycle lifecycle,
        RecoverySessionSafetyClass expectedSafetyClass)
    {
        var orchestrator = new RecoveryOrchestrator(new NeverCalledStore());
        var record = new RecoverySessionRecord { Lifecycle = lifecycle };

        var resolved = await orchestrator.TryResolveAsync(record);

        Assert.False(resolved);
        var disposition = record.GetDisposition();
        Assert.Equal(RecoverySessionHandling.ManualIntervention, disposition.Handling);
        Assert.Equal(expectedSafetyClass, disposition.SafetyClass);
    }

    [Fact]
    public async Task TryResolveAsync_DoesNotTouchPersistence()
    {
        var store = new NeverCalledStore();
        var orchestrator = new RecoveryOrchestrator(store);
        var record = new RecoverySessionRecord
        {
            Lifecycle = RecoverySessionLifecycle.Completed,
        };

        Assert.True(await orchestrator.TryResolveAsync(record));
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task HasUnresolvedSessionsAsync_ResolvedStoreCandidate_CannotPromoteItself()
    {
        var store = new CandidateStore(
            new RecoverySessionRecord
            {
                Lifecycle = RecoverySessionLifecycle.Completed,
            });
        var orchestrator = new RecoveryOrchestrator(store);

        var hasUnresolved = await orchestrator.HasUnresolvedSessionsAsync();

        Assert.False(hasUnresolved);
        Assert.Equal(1, store.ListCallCount);
    }

    [Fact]
    public async Task GetUnresolvedInfoAsync_ResolvedStoreCandidate_IsFilteredByDisposition()
    {
        var store = new CandidateStore(
            new RecoverySessionRecord
            {
                Lifecycle = RecoverySessionLifecycle.FailedAndRolledBack,
            });
        var orchestrator = new RecoveryOrchestrator(store);

        var (sessions, hasCorruption) = await orchestrator.GetUnresolvedInfoAsync();

        Assert.False(hasCorruption);
        Assert.Empty(sessions);
    }

    [Theory]
    [InlineData(RecoverySessionLifecycle.Executing)]
    [InlineData(RecoverySessionLifecycle.FailedRollbackIncomplete)]
    [InlineData(RecoverySessionLifecycle.PositionUncertain)]
    public async Task GetUnresolvedInfoAsync_AuthoritativeDisposition_KeepsUnresolved(
        RecoverySessionLifecycle lifecycle)
    {
        var record = new RecoverySessionRecord
        {
            Lifecycle = lifecycle,
        };
        var store = new CandidateStore(record);
        var orchestrator = new RecoveryOrchestrator(store);

        var (sessions, hasCorruption) = await orchestrator.GetUnresolvedInfoAsync();

        Assert.False(hasCorruption);
        Assert.Single(sessions);
        Assert.Same(record, sessions[0]);
        Assert.True(sessions[0].GetDisposition().IsUnresolved);
    }

    [Fact]
    public async Task HasUnresolvedSessionsAsync_StoreCorruption_FailsClosed()
    {
        var orchestrator = new RecoveryOrchestrator(
            new ThrowingCandidateStore(new InvalidDataException("corrupt")));

        Assert.True(await orchestrator.HasUnresolvedSessionsAsync());
    }

    [Fact]
    public async Task GetUnresolvedInfoAsync_StoreCorruption_ReportsCorruption()
    {
        var orchestrator = new RecoveryOrchestrator(
            new ThrowingCandidateStore(new InvalidDataException("corrupt")));

        var (sessions, hasCorruption) = await orchestrator.GetUnresolvedInfoAsync();

        Assert.True(hasCorruption);
        Assert.Empty(sessions);
    }

    private sealed class NeverCalledStore : IRecoverySessionStore
    {
        public int CallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => UnexpectedCall();

        public Task SaveAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => UnexpectedCall();

        public Task<RecoverySessionRecord> UpdateAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => UnexpectedCall<RecoverySessionRecord>();

        public Task<RecoverySessionRecord?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => UnexpectedCall<RecoverySessionRecord?>();

        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
            => UnexpectedCall<IReadOnlyList<RecoverySessionRecord>>();

        private Task UnexpectedCall()
        {
            CallCount++;
            return Task.FromException(
                new InvalidOperationException("TryResolveAsync 不应访问持久化层。"));
        }

        private Task<T> UnexpectedCall<T>()
        {
            CallCount++;
            return Task.FromException<T>(
                new InvalidOperationException("TryResolveAsync 不应访问持久化层。"));
        }
    }

    private sealed class CandidateStore : IRecoverySessionStore
    {
        private readonly IReadOnlyList<RecoverySessionRecord> _candidates;

        public CandidateStore(params RecoverySessionRecord[] candidates)
        {
            _candidates = Array.AsReadOnly(candidates);
        }

        public int ListCallCount { get; private set; }

        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCallCount++;
            return Task.FromResult<IReadOnlyList<RecoverySessionRecord>>(
                _candidates.Take(limit).ToArray());
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RecoverySessionRecord> UpdateAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RecoverySessionRecord?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingCandidateStore : IRecoverySessionStore
    {
        private readonly Exception _exception;

        public ThrowingCandidateStore(Exception exception)
        {
            _exception = exception;
        }

        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<RecoverySessionRecord>>(_exception);

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RecoverySessionRecord> UpdateAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RecoverySessionRecord?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
