using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class PersistedRecoveryCoordinatorAuthorityTests
{
    private const string TD = @"\\.\DISPLAY1";
    private static readonly PixelRect WA = new(0, 0, 1920, 1080);

    [Fact]
    public async Task ListUnresolvedAsync_ResolvedStoreCandidate_IsFilteredAtInteropBoundary()
    {
        var completed = Make(RecoverySessionLifecycle.Completed);
        var executing = Make(RecoverySessionLifecycle.Executing);
        var store = new CandidateStore(completed, executing);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.ListUnresolvedAsync();

        var item = Assert.Single(result);
        Assert.Same(executing, item);
        Assert.Equal(50, store.LastRequestedLimit);
    }

    [Fact]
    public async Task ListUnresolvedAsync_NullCandidate_ThrowsCorruption()
    {
        var coordinator = CreateCoordinator(
            new CandidateStore(new RecoverySessionRecord[] { null! }));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => coordinator.ListUnresolvedAsync());
    }

    [Fact]
    public async Task HasUnresolvedAsync_DoesNotLetResolvedFirstCandidateHideLaterUnresolved()
    {
        var completed = Make(RecoverySessionLifecycle.Completed);
        var executing = Make(RecoverySessionLifecycle.Executing);
        var store = new CandidateStore(completed, executing);
        var coordinator = CreateCoordinator(store);

        var result = await coordinator.HasUnresolvedAsync();

        Assert.True(result);
        Assert.Equal(50, store.LastRequestedLimit);
    }

    [Fact]
    public async Task HasUnresolvedAsync_OnlyResolvedCandidates_ReturnsFalse()
    {
        var coordinator = CreateCoordinator(
            new CandidateStore(
                Make(RecoverySessionLifecycle.Completed),
                Make(RecoverySessionLifecycle.FailedAndRolledBack)));

        Assert.False(await coordinator.HasUnresolvedAsync());
    }

    [Fact]
    public async Task HasUnresolvedAsync_NullCandidate_FailsClosed()
    {
        var coordinator = CreateCoordinator(
            new CandidateStore(new RecoverySessionRecord[] { null! }));

        Assert.True(await coordinator.HasUnresolvedAsync());
    }

    [Fact]
    public async Task HasUnresolvedAsync_StoreCorruption_FailsClosed()
    {
        var coordinator = CreateCoordinator(
            new ThrowingStore(new InvalidDataException("corrupt")));

        Assert.True(await coordinator.HasUnresolvedAsync());
    }

    private static PersistedRecoveryCoordinator CreateCoordinator(IRecoverySessionStore store) =>
        new(
            store,
            new FakeDisplayTopologyReader(TD, WA),
            new FakeDesktopIconReader(Array.Empty<DesktopIconDescriptor>()),
            new NeverCalledWriter(),
            new StaThreadExecutor());

    private static RecoverySessionRecord Make(RecoverySessionLifecycle lifecycle) =>
        new()
        {
            Lifecycle = lifecycle,
        };

    private sealed class CandidateStore : IRecoverySessionStore
    {
        private readonly IReadOnlyList<RecoverySessionRecord> _candidates;

        public CandidateStore(params RecoverySessionRecord[] candidates)
        {
            _candidates = Array.AsReadOnly(candidates);
        }

        public int LastRequestedLimit { get; private set; }

        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequestedLimit = limit;
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

    private sealed class ThrowingStore : IRecoverySessionStore
    {
        private readonly Exception _exception;

        public ThrowingStore(Exception exception)
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

    private sealed class NeverCalledWriter : IDesktopIconPositionWriter
    {
        public DesktopIconPositioningCapabilities GetCapabilities()
            => throw new InvalidOperationException(
                "查询未解决会话时不应读取 Writer capabilities。");

        public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
            => throw new InvalidOperationException(
                "查询未解决会话时不应调用 Writer。 ");
    }
}
