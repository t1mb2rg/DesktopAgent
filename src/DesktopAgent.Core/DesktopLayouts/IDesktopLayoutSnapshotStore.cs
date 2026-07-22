namespace DesktopAgent.Core.DesktopLayouts;

/// <summary>
/// 桌面布局快照持久化存储。
/// </summary>
public interface IDesktopLayoutSnapshotStore
{
    /// <summary>初始化数据库（幂等）。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>保存快照。重复 SnapshotId 时拒绝。</summary>
    Task SaveAsync(DesktopLayoutSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>读取完整快照。</summary>
    Task<DesktopLayoutSnapshot?> GetAsync(Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>列出快照摘要，按 CreatedAtUtc 降序。</summary>
    Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(
        int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>删除快照。返回是否实际删除。</summary>
    Task<bool> DeleteAsync(Guid snapshotId, CancellationToken cancellationToken = default);
}
