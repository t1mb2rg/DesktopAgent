namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 恢复会话持久化存储的 Core 契约。
/// 不含 Persistence、SQLite、WPF、COM 或真实 Writer 依赖。
/// </summary>
public interface IRecoverySessionStore
{
    /// <summary>持久化初始化（Schema 创建/升级）。幂等。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>保存新的恢复会话记录。Revision 必须为 1。</summary>
    /// <exception cref="InvalidOperationException">SessionId 已存在。</exception>
    /// <exception cref="InvalidDataException">记录未通过 Validate()。</exception>
    Task SaveAsync(RecoverySessionRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// 以乐观并发更新现有记录。
    /// 仅在 record.Revision 与数据库当前 Revision 匹配时成功。
    /// 调用方应先 Get、修改、WithNextRevision()、再 Update。
    /// </summary>
    /// <returns>更新后的实际记录（包含递增后的 Revision）。</returns>
    /// <exception cref="InvalidOperationException">SessionId 不存在或 Revision 不匹配。</exception>
    /// <exception cref="InvalidDataException">更新后的记录未通过 Validate()。</exception>
    Task<RecoverySessionRecord> UpdateAsync(RecoverySessionRecord record, CancellationToken cancellationToken = default);

    /// <summary>读取单个恢复记录（含完整 Plans、Attempts、Verification）。</summary>
    /// <returns>不存在时返回 null。</returns>
    Task<RecoverySessionRecord?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询所有未解决的恢复记录。
    /// 未解决定义：RequiresManualRecovery=true 或 IsPositionUncertain=true
    /// 或 Lifecycle=Executing（崩溃残留）。
    /// 按 StartedAtUtc 降序排列。
    /// </summary>
    Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
        int limit = 50, CancellationToken cancellationToken = default);
}
