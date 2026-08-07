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
}
