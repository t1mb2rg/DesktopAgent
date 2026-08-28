using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>
/// 把本地 DesktopObservation 投影成 Workspace Compiler / Apply 共用的 local reality sidecar。
/// 这里是 MonitorDeviceName → provider-safe DisplayRef 与 movable 事实的唯一产品投影规则之一；
/// 不读取 Semantic Understanding，也不拥有任何布局设计或执行 authority。
/// </summary>
public static class DesktopWorkspaceLocalItemFactProjector
{
    public static IReadOnlyList<DesktopWorkspaceCompilationItemFact> Project(
        DesktopObservation observation,
        ExplorerWorkspaceGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(geometry);

        if (observation.ObservationId != geometry.ObservationId)
            throw new ArgumentException("DesktopObservation 与 Explorer Geometry 必须来自同一次读取。", nameof(geometry));

        var displayByDeviceName = geometry.Displays.ToDictionary(
            static display => display.DeviceName,
            StringComparer.Ordinal);
        var facts = new List<DesktopWorkspaceCompilationItemFact>(observation.Icons.Count);
        foreach (var icon in observation.Icons.OrderBy(static icon => icon.StableId, StringComparer.Ordinal))
        {
            if (icon.MonitorDeviceName is null ||
                !displayByDeviceName.TryGetValue(icon.MonitorDeviceName, out var display))
            {
                throw new InvalidDataException(
                    $"项目 \"{icon.DisplayName}\" 无法映射到当前 Explorer Workspace Geometry。 ");
            }

            facts.Add(new DesktopWorkspaceCompilationItemFact(
                icon.StableId,
                display.DisplayRef,
                icon.PixelPosition,
                IsMovable(icon)));
        }

        if (facts.Select(static fact => fact.StableId)
            .Distinct(StringComparer.Ordinal).Count() != facts.Count)
        {
            throw new InvalidDataException("DesktopObservation 包含重复 StableId，无法建立 Workspace local reality。 ");
        }

        return Array.AsReadOnly(facts.ToArray());
    }

    public static bool IsMovable(DesktopIconDescriptor icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        return !icon.IsSystemItem && icon.ItemKind != ItemKind.SystemItem;
    }
}
