namespace DesktopAgent.Core.Automation;

/// <summary>UI / SQLite 无关的 Pending Queue 持久化边界。</summary>
public interface IDesktopPendingActionStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>按 PendingActionId 新增或完整替换一条待确认动作。</summary>
    Task SaveAsync(
        DesktopPendingAction action,
        CancellationToken cancellationToken = default);

    Task<DesktopPendingAction?> GetAsync(
        Guid pendingActionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// oldest first 读取有限 Pending 工作集。
    /// limit 必须由调用方显式提供，Store 不允许无界返回整个数据库。
    /// </summary>
    Task<IReadOnlyList<DesktopPendingAction>> GetPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid pendingActionId,
        CancellationToken cancellationToken = default);
}
