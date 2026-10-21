using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>标题避让以现有 ReservedRegion 合同进入编译/等价检查，不产生新的执行旁路。</summary>
public static class DesktopWorkspaceZonePresentation
{
    public const string LabelRegionPrefix = "tessel-label:";
    public static DesktopWorkspaceDesignDraft WithLabelReservations(DesktopWorkspaceDesignDraft draft, ExplorerWorkspaceGeometry geometry)
    {
        var regions = draft.ReservedRegions.Where(region => !region.RegionKey.StartsWith(LabelRegionPrefix, StringComparison.Ordinal)).ToList();
        foreach (var zone in draft.Zones)
        {
            var display = geometry.Displays.Single(item => item.DisplayRef == zone.DisplayRef);
            var rect = ExplorerGridPresentation.Quantize(zone.Bounds, display);
            regions.Add(new(LabelRegionPrefix + zone.ZoneKey, "分区标题 · " + zone.Name, zone.DisplayRef,
                ExplorerGridPresentation.Relative(ExplorerGridPresentation.LabelCells(rect, display), display)));
        }
        return new(draft.DesignId, draft.Summary, draft.DisplayPolicies, draft.Zones, draft.ZoneRelations,
            draft.CategoryRules, draft.ItemOverrides, regions);
    }
}
