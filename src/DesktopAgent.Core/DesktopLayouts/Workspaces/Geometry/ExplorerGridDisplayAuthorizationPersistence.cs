namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// 站定授权的 durable store。
///
/// 与网格事实一样是追加式的：一条授权写入后不修改，"当前授权"由
/// device_name + topology_fingerprint 下最新一条记录选出。
/// 撤销是追加一条 Revoked，不是删除历史。
///
/// 读取必须带上当前 topology fingerprint——理由和事实 store 相同：
/// 授权也是环境绑定的，拓扑变化后旧授权不再被视为当前授权。
/// </summary>
public interface IExplorerGridDisplayAuthorizationStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 追加一条不可变记录（授权或撤销）。已写入的记录不得修改或覆盖。
    /// </summary>
    Task AppendAsync(
        ExplorerGridDisplayAuthorization authorization,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取该显示器在当前拓扑下的当前授权。
    ///
    /// 返回 null 表示"这台显示器在当前拓扑下没有授权记录"——包括
    /// 只有绑定别的拓扑的记录这一情形。调用方不得把 null 当成"默认允许"。
    /// 返回 Revoked 记录表示用户明确撤销过，同样不构成授权。
    /// </summary>
    Task<ExplorerGridDisplayAuthorization?> GetActiveAsync(
        string deviceName,
        string currentTopologyFingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取该显示器的最新记录，不论拓扑是否匹配。
    /// 用于显式降级：调用方需要能告诉用户"曾经授权过，但已随拓扑变化失效"。
    /// </summary>
    Task<ExplorerGridDisplayAuthorization?> GetLatestByDeviceAsync(
        string deviceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取当前拓扑下仍然有效的全部记录，每块显示器取最新一条。
    /// </summary>
    Task<IReadOnlyList<ExplorerGridDisplayAuthorization>> ListActiveAsync(
        string currentTopologyFingerprint,
        CancellationToken cancellationToken = default);
}
