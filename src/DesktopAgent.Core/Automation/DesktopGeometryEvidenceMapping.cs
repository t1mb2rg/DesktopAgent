using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Core.Automation;

/// <summary>
/// 把几何测量来源映射为策略层的证据等级。
///
/// 这里必须用显式 switch，不能做数值转换：两套枚举的数值顺序**不同**
/// （<see cref="ExplorerGridEvidenceSource.Measured"/> = 0 / Inferred = 1，
/// 而 <see cref="DesktopGeometryEvidence.Unknown"/> = 0 / Measured = 1 / Inferred = 2），
/// 因此 <c>(DesktopGeometryEvidence)(int)source</c> 会把 Inferred(1) 静默变成 Measured(1)，
/// 把"推断"升级成"实测"——这正是本类型要挡住的 fail-open。
/// 任何未识别的来源一律映射为 <see cref="DesktopGeometryEvidence.Unknown"/>（fail-closed）。
/// </summary>
public static class DesktopGeometryEvidenceMapping
{
    public static DesktopGeometryEvidence From(ExplorerGridEvidenceSource source) => source switch
    {
        ExplorerGridEvidenceSource.Measured => DesktopGeometryEvidence.Measured,
        ExplorerGridEvidenceSource.Inferred => DesktopGeometryEvidence.Inferred,
        _ => DesktopGeometryEvidence.Unknown,
    };
}
