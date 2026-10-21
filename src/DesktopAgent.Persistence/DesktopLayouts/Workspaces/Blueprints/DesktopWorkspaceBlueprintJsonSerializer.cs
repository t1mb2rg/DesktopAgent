using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Blueprints;

/// <summary>
/// DesktopWorkspaceBlueprint 的持久化 JSON v1。
/// 使用显式 DTO 重建领域对象，避免 durable payload 偶然绑定 Core 的反射/构造器行为。
/// </summary>
internal static class DesktopWorkspaceBlueprintJsonSerializer
{
    internal const int CurrentPayloadFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static string Serialize(DesktopWorkspaceBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);

        var payload = new BlueprintPayload
        {
            BlueprintId = blueprint.BlueprintId,
            Revision = blueprint.Revision,
            ParentRevision = blueprint.ParentRevision,
            SchemaVersion = blueprint.SchemaVersion,
            DesignSummary = blueprint.DesignSummary,
            DisplayPolicies = blueprint.DisplayPolicies
                .Select(static item => new DisplayPolicyPayload
                {
                    DisplayRef = item.DisplayRef,
                    Policy = (int)item.Policy,
                })
                .ToList(),
            Zones = blueprint.Zones
                .Select(static item => new ZonePayload
                {
                    ZoneKey = item.ZoneKey,
                    Name = item.Name,
                    DisplayRef = item.DisplayRef,
                    Bounds = RelativeRectPayload.From(item.Bounds),
                    Anchor = (int)item.Anchor,
                    PackingOrder = (int)item.PackingOrder,
                })
                .ToList(),
            ZoneRelations = blueprint.ZoneRelations
                .Select(static item => new ZoneRelationPayload
                {
                    ZoneKey = item.ZoneKey,
                    Relation = (int)item.Relation,
                    OtherZoneKey = item.OtherZoneKey,
                })
                .ToList(),
            CategoryRules = blueprint.CategoryRules
                .Select(static item => new CategoryRulePayload
                {
                    CategoryKey = item.CategoryKey,
                    ZoneKey = item.ZoneKey,
                })
                .ToList(),
            ItemOverrides = blueprint.ItemOverrides
                .Select(static item => new ItemOverridePayload
                {
                    ItemRef = item.ItemRef,
                    Kind = (int)item.Kind,
                    ZoneKey = item.ZoneKey,
                    Anchor = item.Anchor.HasValue ? (int)item.Anchor.Value : null,
                })
                .ToList(),
            ReservedRegions = blueprint.ReservedRegions
                .Select(static item => new ReservedRegionPayload
                {
                    RegionKey = item.RegionKey,
                    Purpose = item.Purpose,
                    DisplayRef = item.DisplayRef,
                    Bounds = RelativeRectPayload.From(item.Bounds),
                })
                .ToList(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    internal static DesktopWorkspaceBlueprint Deserialize(string json, int payloadFormatVersion)
    {
        if (payloadFormatVersion != CurrentPayloadFormatVersion)
        {
            throw new InvalidDataException(
                $"不支持的 DesktopWorkspaceBlueprint payload_format_version={payloadFormatVersion}；" +
                $"当前仅支持 {CurrentPayloadFormatVersion}。");
        }
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("DesktopWorkspaceBlueprint payload_json 不能为空。");

        BlueprintPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BlueprintPayload>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("DesktopWorkspaceBlueprint payload_json 不是有效 JSON。", ex);
        }

        if (payload is null)
            throw new InvalidDataException("DesktopWorkspaceBlueprint payload_json 反序列化返回 null。");
        if (payload.DisplayPolicies is null)
            throw new InvalidDataException("DesktopWorkspaceBlueprint payload 缺少 display_policies。");
        if (payload.Zones is null)
            throw new InvalidDataException("DesktopWorkspaceBlueprint payload 缺少 zones。");
        if (payload.ZoneRelations is null)
            throw new InvalidDataException("DesktopWorkspaceBlueprint payload 缺少 zone_relations。");
        if (payload.CategoryRules is null)
            throw new InvalidDataException("DesktopWorkspaceBlueprint payload 缺少 category_rules。");
        if (payload.ItemOverrides is null)
            throw new InvalidDataException("DesktopWorkspaceBlueprint payload 缺少 item_overrides。");
        if (payload.ReservedRegions is null)
            throw new InvalidDataException("DesktopWorkspaceBlueprint payload 缺少 reserved_regions。");

        try
        {
            var displayPolicies = payload.DisplayPolicies
                .Select(static item => item is null
                    ? throw new InvalidDataException("display_policies 不能包含 null。")
                    : new DesktopWorkspaceBlueprintDisplayPolicy(
                        item.DisplayRef ?? string.Empty,
                        ParseEnum<DesktopWorkspaceDisplayPolicyKind>(item.Policy, "display policy")))
                .ToArray();

            var zones = payload.Zones
                .Select(static item => item is null
                    ? throw new InvalidDataException("zones 不能包含 null。")
                    : new DesktopWorkspaceBlueprintZone(
                        item.ZoneKey ?? string.Empty,
                        item.Name ?? string.Empty,
                        item.DisplayRef ?? string.Empty,
                        RestoreRect(item.Bounds, "zone bounds"),
                        ParseEnum<DesktopWorkspaceAnchorKind>(item.Anchor, "zone anchor"),
                        ParseEnum<DesktopWorkspacePackingOrder>(item.PackingOrder, "zone packing_order")))
                .ToArray();

            var relations = payload.ZoneRelations
                .Select(static item => item is null
                    ? throw new InvalidDataException("zone_relations 不能包含 null。")
                    : new DesktopWorkspaceBlueprintZoneRelation(
                        item.ZoneKey ?? string.Empty,
                        ParseEnum<DesktopWorkspaceZoneRelationKind>(item.Relation, "zone relation"),
                        item.OtherZoneKey ?? string.Empty))
                .ToArray();

            var categoryRules = payload.CategoryRules
                .Select(static item => item is null
                    ? throw new InvalidDataException("category_rules 不能包含 null。")
                    : new DesktopWorkspaceBlueprintCategoryRule(
                        item.CategoryKey ?? string.Empty,
                        item.ZoneKey ?? string.Empty))
                .ToArray();

            var itemOverrides = payload.ItemOverrides
                .Select(static item => item is null
                    ? throw new InvalidDataException("item_overrides 不能包含 null。")
                    : new DesktopWorkspaceBlueprintItemOverride(
                        item.ItemRef ?? string.Empty,
                        ParseEnum<DesktopWorkspaceItemOverrideKind>(item.Kind, "item override kind"),
                        item.ZoneKey,
                        item.Anchor.HasValue
                            ? ParseEnum<DesktopWorkspaceAnchorKind>(item.Anchor.Value, "item override anchor")
                            : null))
                .ToArray();

            var reservedRegions = payload.ReservedRegions
                .Select(static item => item is null
                    ? throw new InvalidDataException("reserved_regions 不能包含 null。")
                    : new DesktopWorkspaceBlueprintReservedRegion(
                        item.RegionKey ?? string.Empty,
                        item.Purpose ?? string.Empty,
                        item.DisplayRef ?? string.Empty,
                        RestoreRect(item.Bounds, "reserved region bounds")))
                .ToArray();

            return new DesktopWorkspaceBlueprint(
                payload.BlueprintId ?? Guid.Empty,
                payload.Revision ?? 0,
                payload.DesignSummary ?? string.Empty,
                displayPolicies,
                zones,
                relations,
                categoryRules,
                itemOverrides,
                reservedRegions,
                payload.ParentRevision,
                payload.SchemaVersion ?? 0);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"DesktopWorkspaceBlueprint payload 未通过领域校验：{ex.Message}", ex);
        }
    }

    private static TEnum ParseEnum<TEnum>(int? value, string field)
        where TEnum : struct, Enum
    {
        if (!value.HasValue || !Enum.IsDefined(typeof(TEnum), value.Value))
            throw new InvalidDataException($"DesktopWorkspaceBlueprint {field} 值无效：{value?.ToString() ?? "null"}。");
        return (TEnum)Enum.ToObject(typeof(TEnum), value.Value);
    }

    private static DesktopWorkspaceRelativeRect RestoreRect(RelativeRectPayload? payload, string field)
    {
        if (payload is null)
            throw new InvalidDataException($"DesktopWorkspaceBlueprint {field} 不能为空。");

        try
        {
            return new DesktopWorkspaceRelativeRect(
                payload.Left ?? double.NaN,
                payload.Top ?? double.NaN,
                payload.Width ?? double.NaN,
                payload.Height ?? double.NaN);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"DesktopWorkspaceBlueprint {field} 未通过领域校验：{ex.Message}", ex);
        }
    }

    private sealed class BlueprintPayload
    {
        [JsonPropertyName("blueprint_id")]
        public Guid? BlueprintId { get; init; }

        [JsonPropertyName("revision")]
        public int? Revision { get; init; }

        [JsonPropertyName("parent_revision")]
        public int? ParentRevision { get; init; }

        [JsonPropertyName("schema_version")]
        public int? SchemaVersion { get; init; }

        [JsonPropertyName("design_summary")]
        public string? DesignSummary { get; init; }

        [JsonPropertyName("display_policies")]
        public List<DisplayPolicyPayload?>? DisplayPolicies { get; init; }

        [JsonPropertyName("zones")]
        public List<ZonePayload?>? Zones { get; init; }

        [JsonPropertyName("zone_relations")]
        public List<ZoneRelationPayload?>? ZoneRelations { get; init; }

        [JsonPropertyName("category_rules")]
        public List<CategoryRulePayload?>? CategoryRules { get; init; }

        [JsonPropertyName("item_overrides")]
        public List<ItemOverridePayload?>? ItemOverrides { get; init; }

        [JsonPropertyName("reserved_regions")]
        public List<ReservedRegionPayload?>? ReservedRegions { get; init; }
    }

    private sealed class DisplayPolicyPayload
    {
        [JsonPropertyName("display_ref")]
        public string? DisplayRef { get; init; }

        [JsonPropertyName("policy")]
        public int? Policy { get; init; }
    }

    private sealed class ZonePayload
    {
        [JsonPropertyName("zone_key")]
        public string? ZoneKey { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("display_ref")]
        public string? DisplayRef { get; init; }

        [JsonPropertyName("bounds")]
        public RelativeRectPayload? Bounds { get; init; }

        [JsonPropertyName("anchor")]
        public int? Anchor { get; init; }

        [JsonPropertyName("packing_order")]
        public int? PackingOrder { get; init; }
    }

    private sealed class RelativeRectPayload
    {
        [JsonPropertyName("left")]
        public double? Left { get; init; }

        [JsonPropertyName("top")]
        public double? Top { get; init; }

        [JsonPropertyName("width")]
        public double? Width { get; init; }

        [JsonPropertyName("height")]
        public double? Height { get; init; }

        public static RelativeRectPayload From(DesktopWorkspaceRelativeRect value) => new()
        {
            Left = value.Left,
            Top = value.Top,
            Width = value.Width,
            Height = value.Height,
        };
    }

    private sealed class ZoneRelationPayload
    {
        [JsonPropertyName("zone_key")]
        public string? ZoneKey { get; init; }

        [JsonPropertyName("relation")]
        public int? Relation { get; init; }

        [JsonPropertyName("other_zone_key")]
        public string? OtherZoneKey { get; init; }
    }

    private sealed class CategoryRulePayload
    {
        [JsonPropertyName("category_key")]
        public string? CategoryKey { get; init; }

        [JsonPropertyName("zone_key")]
        public string? ZoneKey { get; init; }
    }

    private sealed class ItemOverridePayload
    {
        [JsonPropertyName("item_ref")]
        public string? ItemRef { get; init; }

        [JsonPropertyName("kind")]
        public int? Kind { get; init; }

        [JsonPropertyName("zone_key")]
        public string? ZoneKey { get; init; }

        [JsonPropertyName("anchor")]
        public int? Anchor { get; init; }
    }

    private sealed class ReservedRegionPayload
    {
        [JsonPropertyName("region_key")]
        public string? RegionKey { get; init; }

        [JsonPropertyName("purpose")]
        public string? Purpose { get; init; }

        [JsonPropertyName("display_ref")]
        public string? DisplayRef { get; init; }

        [JsonPropertyName("bounds")]
        public RelativeRectPayload? Bounds { get; init; }
    }
}
