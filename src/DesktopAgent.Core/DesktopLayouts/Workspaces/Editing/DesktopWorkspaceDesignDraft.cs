using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

/// <summary>讨论中的设计可以不完整；只有显式转换为 Blueprint 时才检查跨对象约束。</summary>
public sealed record DesktopWorkspaceDesignDraft
{
    public Guid DesignId { get; }
    public string Summary { get; }
    public IReadOnlyList<DesktopWorkspaceBlueprintDisplayPolicy> DisplayPolicies { get; }
    public IReadOnlyList<DesktopWorkspaceBlueprintZone> Zones { get; }
    public IReadOnlyList<DesktopWorkspaceBlueprintZoneRelation> ZoneRelations { get; }
    public IReadOnlyList<DesktopWorkspaceBlueprintCategoryRule> CategoryRules { get; }
    public IReadOnlyList<DesktopWorkspaceBlueprintItemOverride> ItemOverrides { get; }
    public IReadOnlyList<DesktopWorkspaceBlueprintReservedRegion> ReservedRegions { get; }

    public DesktopWorkspaceDesignDraft(Guid designId, string? summary = null,
        IEnumerable<DesktopWorkspaceBlueprintDisplayPolicy>? displayPolicies = null,
        IEnumerable<DesktopWorkspaceBlueprintZone>? zones = null,
        IEnumerable<DesktopWorkspaceBlueprintZoneRelation>? zoneRelations = null,
        IEnumerable<DesktopWorkspaceBlueprintCategoryRule>? categoryRules = null,
        IEnumerable<DesktopWorkspaceBlueprintItemOverride>? itemOverrides = null,
        IEnumerable<DesktopWorkspaceBlueprintReservedRegion>? reservedRegions = null)
    {
        if (designId == Guid.Empty) throw new ArgumentException("DesignId 不能为空。", nameof(designId));
        DesignId = designId;
        Summary = summary?.Trim() ?? string.Empty;
        DisplayPolicies = Copy(displayPolicies);
        Zones = Copy(zones);
        ZoneRelations = Copy(zoneRelations);
        CategoryRules = Copy(categoryRules);
        ItemOverrides = Copy(itemOverrides);
        ReservedRegions = Copy(reservedRegions);
        Unique(DisplayPolicies.Select(item => item.DisplayRef));
        Unique(Zones.Select(item => item.ZoneKey));
        Unique(CategoryRules.Select(item => item.CategoryKey));
        Unique(ItemOverrides.Select(item => item.ItemRef));
        Unique(ReservedRegions.Select(item => item.RegionKey));
        foreach (var bounds in Zones.Select(zone => zone.Bounds).Concat(ReservedRegions.Select(region => region.Bounds)))
            _ = new DesktopWorkspaceRelativeRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    public DesktopWorkspaceBlueprint ToBlueprint(int revision = 1, int? parentRevision = null)
    {
        if (Zones.Count == 0) throw new InvalidOperationException("设计还没有分区，请先完善设计。");
        return new(DesignId, revision, Summary, DisplayPolicies, Zones, ZoneRelations,
            CategoryRules, ItemOverrides, ReservedRegions, parentRevision);
    }

    public static DesktopWorkspaceDesignDraft FromBlueprint(DesktopWorkspaceBlueprint blueprint)
        => new(blueprint.BlueprintId, blueprint.DesignSummary, blueprint.DisplayPolicies, blueprint.Zones,
            blueprint.ZoneRelations, blueprint.CategoryRules, blueprint.ItemOverrides, blueprint.ReservedRegions);

    public DesktopWorkspaceDesignDraft With(string? summary = null,
        IEnumerable<DesktopWorkspaceBlueprintZone>? zones = null,
        IEnumerable<DesktopWorkspaceBlueprintCategoryRule>? categoryRules = null,
        IEnumerable<DesktopWorkspaceBlueprintDisplayPolicy>? displayPolicies = null)
        => new(DesignId, summary ?? Summary, displayPolicies ?? DisplayPolicies, zones ?? Zones, ZoneRelations,
            categoryRules ?? CategoryRules, ItemOverrides, ReservedRegions);

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T>? values) where T : class
    {
        var array = values?.ToArray() ?? [];
        if (array.Any(item => item is null)) throw new ArgumentException("设计集合不能包含空值。");
        return Array.AsReadOnly(array);
    }

    private static void Unique(IEnumerable<string> keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
            if (!seen.Add(key)) throw new ArgumentException($"设计包含重复键：{key}");
    }
}
