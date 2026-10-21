namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// 网格事实的 durable store。
///
/// 这个 Store 只保存"事实 + 证据"，不保存执行授权判定。
/// 判定仍然由 <see cref="ExplorerGridMeasurement.CanAuthorizeExecution"/> 负责，
/// 这样策略层不会因为换了存储实现而改变口径。
///
/// 事实是环境绑定的：读取必须带上当前 topology fingerprint。
/// 拓扑、缩放或任务栏变化后，陈旧事实不再被视为当前事实。
/// </summary>
public interface IExplorerGridMeasurementStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 追加一条不可变观测。观测一旦写入不得修改或覆盖；
    /// 新观测只改变"当前事实"的选择，历史证据必须保留。
    /// </summary>
    Task AppendObservationAsync(
        ExplorerGridMeasurement observation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取该显示器在当前拓扑下的当前事实。
    /// 拓扑不匹配时返回 null：陈旧事实作废，不能继续作为执行依据。
    /// </summary>
    Task<ExplorerGridMeasurement?> GetActiveAsync(
        string deviceName,
        string currentTopologyFingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取该显示器的最新观测，不论是否匹配当前拓扑。
    /// 用于显式降级：调用方需要知道"曾经实测过、但已失效"这一事实本身。
    /// </summary>
    Task<ExplorerGridMeasurement?> GetLatestByDeviceAsync(
        string deviceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取在当前拓扑下仍然有效的全部事实，每块显示器取最新一条。
    /// </summary>
    Task<IReadOnlyList<ExplorerGridMeasurement>> ListActiveAsync(
        string currentTopologyFingerprint,
        CancellationToken cancellationToken = default);
}
