using DesktopAgent.Core.Automation;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// 把 Adapter 产出的 Provenance 汇总成策略层需要的单一证据等级。
///
/// 唯一允许的输入是**经过 Adapter 降级过滤后存活**的 Provenance：
/// 在那里，拓扑不一致（stale）或非实测来源的事实已经变成 DemotedFacts，
/// 对应显示器早已退回推断，所以这里出现的 Measured 一定同时满足"实测 + 拓扑匹配"。
/// 不要拿 store 里的原始事实直接调用本方法——策略门禁本身不带拓扑指纹，
/// 只有 Adapter 的降级判定才知道一条实测事实是否仍然属于当前拓扑。
///
/// 汇总语义（fail-closed）：
///   - 没有任何 Provenance → Unknown（例如几何构建失败，或调用方还没接事实）
///   - 全部显示器都是实测 → Measured
///   - 任一显示器是推断 → Inferred（只要还有一块显示器没实测，整次执行就不能按实测授权）
///   - 出现未知来源 → Unknown（比 Inferred 更保守）
/// </summary>
public static class DesktopExplorerGridGeometryEvidence
{
    public static DesktopGeometryEvidence FromProvenance(
        IReadOnlyList<DesktopExplorerGridProvenance>? provenance)
    {
        if (provenance is null || provenance.Count == 0)
            return DesktopGeometryEvidence.Unknown;

        var evidence = DesktopGeometryEvidence.Measured;
        foreach (var entry in provenance)
        {
            switch (DesktopGeometryEvidenceMapping.From(entry.Source))
            {
                case DesktopGeometryEvidence.Measured:
                    continue;
                case DesktopGeometryEvidence.Inferred:
                    evidence = DesktopGeometryEvidence.Inferred;
                    continue;
                default:
                    return DesktopGeometryEvidence.Unknown;
            }
        }

        return evidence;
    }
}
