using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// 产品侧读取网格实测事实的入口。
///
/// 这条路径只负责"把与当前拓扑绑定的实测事实取出来交给 Adapter"，
/// 不做任何授权判定：能不能执行仍然由策略层决定，几何构建仍然由 Adapter 决定。
///
/// 事实是环境绑定的，因此读取必须带当前拓扑身份；身份不可计算时返回 null，
/// 表示"没有可用事实"，与空集合同义——Adapter 在两种情况下都会回退到图标位置推断，
/// 并把这来源写进 Provenance。
/// </summary>
public sealed class DesktopExplorerGridFactReader
{
    private readonly IExplorerGridMeasurementStore _store;

    public DesktopExplorerGridFactReader(IExplorerGridMeasurementStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// 读取当前拓扑下仍然有效的实测事实（每块显示器取最新一条）。
    /// 拓扑身份无法计算时返回 null，此时不访问 store。
    /// </summary>
    public async Task<IReadOnlyList<ExplorerGridMeasurement>?> TryReadActiveAsync(
        DesktopObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        // 与 Adapter 校验事实时使用同一个推导口径，否则会"存得进、读不出"。
        var topologyIdentity =
            DesktopExplorerWorkspaceGeometryAdapter.ComputeTopologyIdentity(observation);
        if (topologyIdentity is null)
            return null;

        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store
            .ListActiveAsync(topologyIdentity, cancellationToken)
            .ConfigureAwait(false);
    }
}
