namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 只读窄接口：列出「已经成功整理完」的恢复会话，供「撤销上次整理」取回撤销依据。
///
/// 为什么单独开一个接口：<see cref="IRecoverySessionStore"/> 的既有成员是「未解决现场」查询，
/// 生产与测试里有十几处实现，往里加成员会一次性打断所有实现；而撤销只需要一个窄只读查询。
/// 本接口不授予任何写入权，也不解释生命周期——解释权仍在 Core 的
/// <see cref="RecoverySessionDisposition"/> 与撤销前置判定里。
/// </summary>
public interface ICommittedRecoverySessionQuery
{
    /// <summary>
    /// 按「越新越靠前」返回已完成的<strong>整理</strong>会话
    /// （<see cref="RecoverySessionLifecycle.Completed"/> 且
    /// <see cref="RecoverySessionOrigin.Organize"/>）。
    ///
    /// 撤销自己写下的那一笔（<see cref="RecoverySessionOrigin.CheckpointRestore"/>）
    /// 必须被排除，否则「撤销 → 再撤销」会把桌面重新整理一遍。
    /// 实现只做候选筛选，不判断「能不能撤销」。
    /// </summary>
    Task<IReadOnlyList<RecoverySessionRecord>> ListCommittedAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
