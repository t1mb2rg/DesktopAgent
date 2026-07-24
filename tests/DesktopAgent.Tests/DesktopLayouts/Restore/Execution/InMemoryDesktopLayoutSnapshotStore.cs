using DesktopAgent.Core.DesktopLayouts;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class InMemoryDesktopLayoutSnapshotStore : IDesktopLayoutSnapshotStore
{
    private readonly Dictionary<Guid, DesktopLayoutSnapshot> _snapshots = new();

    /// <summary>设为 true 时下一次 SaveAsync 将抛出 SaveException。</summary>
    public bool ShouldThrowOnSave { get; set; }
    public Exception? SaveException { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(DesktopLayoutSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (ShouldThrowOnSave)
            throw SaveException ?? new InvalidOperationException("模拟保存失败。");
        if (!_snapshots.TryAdd(snapshot.SnapshotId, snapshot))
            throw new InvalidOperationException($"重复 SnapshotId：{snapshot.SnapshotId}。");
        return Task.CompletedTask;
    }

    public Task<DesktopLayoutSnapshot?> GetAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        _snapshots.TryGetValue(snapshotId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(
        int limit = 50, CancellationToken cancellationToken = default)
    {
        var summaries = _snapshots.Values
            .OrderByDescending(s => s.CreatedAtUtc)
            .Take(limit)
            .Select(s => new DesktopLayoutSnapshotSummary(
                s.SnapshotId, s.CreatedAtUtc, s.TargetMonitorDeviceName,
                s.ConnectionMethod, s.FormatVersion, s.Icons.Count))
            .ToList();
        return Task.FromResult<IReadOnlyList<DesktopLayoutSnapshotSummary>>(summaries);
    }

    public Task<bool> DeleteAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_snapshots.Remove(snapshotId));
    }
}
