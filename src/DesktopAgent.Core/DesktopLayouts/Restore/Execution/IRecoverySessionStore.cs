namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 恢复会话持久化存储的 Core 契约。
/// 不含 Persistence、SQLite、WPF、COM 或真实 Writer 依赖。
/// </summary>
public interface IRecoverySessionStore
{
    /// <summary>持久化初始化（Schema 创建/升级）。幂等。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>保存新的恢复会话记录。入参 record.Revision 必须为 1，否则抛出 ArgumentException。</summary>
    /// <exception cref="ArgumentException">record.Revision != 1。</exception>
    /// <exception cref="InvalidOperationException">SessionId 已存在。</exception>
    /// <exception cref="InvalidDataException">记录未通过 Validate()。</exception>
    Task SaveAsync(RecoverySessionRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// 以乐观并发更新现有记录。
    /// 调用方传入当前 Revision N；Store 内部 CAS 校验后原子更新到 N+1。
    /// 不可变字段（SessionId、SafetySnapshotId、TargetDeviceName、StartedAtUtc、
    /// SourceRestorePlan、ExecutionPlan）与数据库不一致时拒绝更新。
    /// </summary>
    /// <returns>从数据库重读的完整记录（含递增后的 Revision）。</returns>
    /// <exception cref="InvalidOperationException">SessionId 不存在、Revision 不匹配或不可变字段变更。</exception>
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
