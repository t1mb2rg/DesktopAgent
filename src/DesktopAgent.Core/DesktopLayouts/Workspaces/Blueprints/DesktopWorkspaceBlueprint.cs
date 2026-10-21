namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

/// <summary>
/// Blueprint 的归一化相对矩形。坐标范围固定为 [0,1]，只表达设计空间，
/// 不代表 Explorer Cell、物理像素或任何 Windows 原生几何。
/// </summary>
public readonly record struct DesktopWorkspaceRelativeRect
{
    public double Left { get; }
    public double Top { get; }
    public double Width { get; }
    public double Height { get; }

    public DesktopWorkspaceRelativeRect(double left, double top, double width, double height)
    {
        RequireFinite(left, nameof(left));
        RequireFinite(top, nameof(top));
        RequireFinite(width, nameof(width));
        RequireFinite(height, nameof(height));

        if (left is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(left), "Left 必须位于 [0,1]。");
        if (top is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(top), "Top 必须位于 [0,1]。");
        if (width is <= 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(width), "Width 必须位于 (0,1]。");
        if (height is <= 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(height), "Height 必须位于 (0,1]。");
        if (left + width > 1d)
            throw new ArgumentOutOfRangeException(nameof(width), "Left + Width 不能超过 1。");
        if (top + height > 1d)
            throw new ArgumentOutOfRangeException(nameof(height), "Top + Height 不能超过 1。");

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private static void RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "值必须是有限数。 ");
    }
}

/// <summary>Zone 内项目的设计级排列起点。Compiler 只负责忠实量化，不得改写该语义。</summary>
public enum DesktopWorkspaceAnchorKind
{
    Unspecified = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomLeft = 3,
    BottomRight = 4,
    Center = 5,
}

/// <summary>Zone 内项目的设计级排列方向。</summary>
public enum DesktopWorkspacePackingOrder
{
    RowMajor = 0,
    ColumnMajor = 1,
}

/// <summary>Blueprint 中 Zone 之间的硬空间关系。</summary>
public enum DesktopWorkspaceZoneRelationKind
{
    LeftOf = 0,
    RightOf = 1,
    Above = 2,
    Below = 3,
}

/// <summary>
/// 显示器在 Blueprint 中的参与策略。Preserve 表示该显示器当前内容必须保持不变，
/// 不能产生迁入或迁出；Design 表示允许 Blueprint 对其进行设计。
/// </summary>
public enum DesktopWorkspaceDisplayPolicyKind
{
    Design = 0,
    Preserve = 1,
}

/// <summary>少量 Item Override 的设计动作。</summary>
public enum DesktopWorkspaceItemOverrideKind
{
    PlaceInZone = 0,
    Keep = 1,
    Exclude = 2,
}

public sealed record DesktopWorkspaceBlueprintDisplayPolicy
{
    public string DisplayRef { get; }
    public DesktopWorkspaceDisplayPolicyKind Policy { get; }

    public DesktopWorkspaceBlueprintDisplayPolicy(
        string displayRef,
        DesktopWorkspaceDisplayPolicyKind policy)
    {
        DisplayRef = RequireText(displayRef, nameof(displayRef));
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy));
        Policy = policy;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

public sealed record DesktopWorkspaceBlueprintZone
{
    public string ZoneKey { get; }
    public string Name { get; }
    public string DisplayRef { get; }
    public DesktopWorkspaceRelativeRect Bounds { get; }
    public DesktopWorkspaceAnchorKind Anchor { get; }
    public DesktopWorkspacePackingOrder PackingOrder { get; }

    public DesktopWorkspaceBlueprintZone(
        string zoneKey,
        string name,
        string displayRef,
        DesktopWorkspaceRelativeRect bounds,
        DesktopWorkspaceAnchorKind anchor = DesktopWorkspaceAnchorKind.TopLeft,
        DesktopWorkspacePackingOrder packingOrder = DesktopWorkspacePackingOrder.RowMajor)
    {
        ZoneKey = RequireText(zoneKey, nameof(zoneKey));
        Name = RequireText(name, nameof(name));
        DisplayRef = RequireText(displayRef, nameof(displayRef));
        if (!Enum.IsDefined(anchor))
            throw new ArgumentOutOfRangeException(nameof(anchor));
        if (!Enum.IsDefined(packingOrder))
            throw new ArgumentOutOfRangeException(nameof(packingOrder));

        Bounds = bounds;
        Anchor = anchor;
        PackingOrder = packingOrder;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

public sealed record DesktopWorkspaceBlueprintZoneRelation
{
    public string ZoneKey { get; }
    public DesktopWorkspaceZoneRelationKind Relation { get; }
    public string OtherZoneKey { get; }

    public DesktopWorkspaceBlueprintZoneRelation(
        string zoneKey,
        DesktopWorkspaceZoneRelationKind relation,
        string otherZoneKey)
    {
        ZoneKey = RequireText(zoneKey, nameof(zoneKey));
        OtherZoneKey = RequireText(otherZoneKey, nameof(otherZoneKey));
        if (!Enum.IsDefined(relation))
            throw new ArgumentOutOfRangeException(nameof(relation));
        if (string.Equals(ZoneKey, OtherZoneKey, StringComparison.Ordinal))
            throw new ArgumentException("Zone 不能与自身建立空间关系。", nameof(otherZoneKey));
        Relation = relation;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

/// <summary>默认布局规则：一个语义 Category 归属于一个 Blueprint Zone。</summary>
public sealed record DesktopWorkspaceBlueprintCategoryRule
{
    public string CategoryKey { get; }
    public string ZoneKey { get; }

    public DesktopWorkspaceBlueprintCategoryRule(string categoryKey, string zoneKey)
    {
        CategoryKey = RequireText(categoryKey, nameof(categoryKey));
        ZoneKey = RequireText(zoneKey, nameof(zoneKey));
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

/// <summary>
/// 少量例外项目的 Blueprint 级覆盖。ItemRef 是 Workspace Design 请求内的 Provider-safe 引用，
/// 不等于 StableId，也不包含文件路径、Windows DeviceName 或 Writer identity。
/// </summary>
public sealed record DesktopWorkspaceBlueprintItemOverride
{
    public string ItemRef { get; }
    public DesktopWorkspaceItemOverrideKind Kind { get; }
    public string? ZoneKey { get; }
    public DesktopWorkspaceAnchorKind? Anchor { get; }

    public DesktopWorkspaceBlueprintItemOverride(
        string itemRef,
        DesktopWorkspaceItemOverrideKind kind,
        string? zoneKey = null,
        DesktopWorkspaceAnchorKind? anchor = null)
    {
        ItemRef = RequireText(itemRef, nameof(itemRef));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (anchor.HasValue && !Enum.IsDefined(anchor.Value))
            throw new ArgumentOutOfRangeException(nameof(anchor));

        if (kind == DesktopWorkspaceItemOverrideKind.PlaceInZone)
        {
            if (string.IsNullOrWhiteSpace(zoneKey))
                throw new ArgumentException("PlaceInZone 必须提供 ZoneKey。", nameof(zoneKey));
        }
        else if (zoneKey is not null || anchor.HasValue)
        {
            throw new ArgumentException("Keep / Exclude 不能携带 ZoneKey 或 Anchor。");
        }

        Kind = kind;
        ZoneKey = zoneKey?.Trim();
        Anchor = anchor;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

public sealed record DesktopWorkspaceBlueprintReservedRegion
{
    public string RegionKey { get; }
    public string Purpose { get; }
    public string DisplayRef { get; }
    public DesktopWorkspaceRelativeRect Bounds { get; }

    public DesktopWorkspaceBlueprintReservedRegion(
        string regionKey,
        string purpose,
        string displayRef,
        DesktopWorkspaceRelativeRect bounds)
    {
        RegionKey = RequireText(regionKey, nameof(regionKey));
        Purpose = RequireText(purpose, nameof(purpose));
        DisplayRef = RequireText(displayRef, nameof(displayRef));
        Bounds = bounds;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

/// <summary>
/// ADR-0005 的长期 Workspace 设计资产。
///
/// Blueprint 只表达用户可感知的设计语义：多屏策略、Zone、相对几何、Zone 关系、
/// Category→Zone、少量 Item Override、Reserved Region 与 anchor。
///
/// 它故意不绑定 Observation、Explorer Grid、Cell、Pixel、StableId、执行顺序、Candidate、
/// Recovery Session 或 Writer authority。相同 revision 构造后不可变；设计变化必须产生新 revision。
/// </summary>
public sealed record DesktopWorkspaceBlueprint
{
    public const int CurrentSchemaVersion = 1;

    private readonly IReadOnlyList<DesktopWorkspaceBlueprintDisplayPolicy> _displayPolicies;
    private readonly IReadOnlyList<DesktopWorkspaceBlueprintZone> _zones;
    private readonly IReadOnlyList<DesktopWorkspaceBlueprintZoneRelation> _zoneRelations;
    private readonly IReadOnlyList<DesktopWorkspaceBlueprintCategoryRule> _categoryRules;
    private readonly IReadOnlyList<DesktopWorkspaceBlueprintItemOverride> _itemOverrides;
    private readonly IReadOnlyList<DesktopWorkspaceBlueprintReservedRegion> _reservedRegions;

    public Guid BlueprintId { get; }
    public int Revision { get; }
    public int? ParentRevision { get; }
    public int SchemaVersion { get; }
    public string DesignSummary { get; }
    public IReadOnlyList<DesktopWorkspaceBlueprintDisplayPolicy> DisplayPolicies => _displayPolicies;
    public IReadOnlyList<DesktopWorkspaceBlueprintZone> Zones => _zones;
    public IReadOnlyList<DesktopWorkspaceBlueprintZoneRelation> ZoneRelations => _zoneRelations;
    public IReadOnlyList<DesktopWorkspaceBlueprintCategoryRule> CategoryRules => _categoryRules;
    public IReadOnlyList<DesktopWorkspaceBlueprintItemOverride> ItemOverrides => _itemOverrides;
    public IReadOnlyList<DesktopWorkspaceBlueprintReservedRegion> ReservedRegions => _reservedRegions;

    public DesktopWorkspaceBlueprint(
        Guid blueprintId,
        int revision,
        string designSummary,
        IEnumerable<DesktopWorkspaceBlueprintDisplayPolicy>? displayPolicies = null,
        IEnumerable<DesktopWorkspaceBlueprintZone>? zones = null,
        IEnumerable<DesktopWorkspaceBlueprintZoneRelation>? zoneRelations = null,
        IEnumerable<DesktopWorkspaceBlueprintCategoryRule>? categoryRules = null,
        IEnumerable<DesktopWorkspaceBlueprintItemOverride>? itemOverrides = null,
        IEnumerable<DesktopWorkspaceBlueprintReservedRegion>? reservedRegions = null,
        int? parentRevision = null,
        int schemaVersion = CurrentSchemaVersion)
    {
        if (blueprintId == Guid.Empty)
            throw new ArgumentException("BlueprintId 不能为 Guid.Empty。", nameof(blueprintId));
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (parentRevision.HasValue && (parentRevision.Value <= 0 || parentRevision.Value >= revision))
            throw new ArgumentOutOfRangeException(nameof(parentRevision), "ParentRevision 必须大于 0 且小于当前 Revision。");
        if (schemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (string.IsNullOrWhiteSpace(designSummary))
            throw new ArgumentException("DesignSummary 不能为空。", nameof(designSummary));

        var displayPolicyArray = displayPolicies?.ToArray() ?? [];
        var zoneArray = zones?.ToArray() ?? [];
        var relationArray = zoneRelations?.ToArray() ?? [];
        var categoryRuleArray = categoryRules?.ToArray() ?? [];
        var itemOverrideArray = itemOverrides?.ToArray() ?? [];
        var reservedArray = reservedRegions?.ToArray() ?? [];

        RejectNullElements(displayPolicyArray, nameof(displayPolicies));
        RejectNullElements(zoneArray, nameof(zones));
        RejectNullElements(relationArray, nameof(zoneRelations));
        RejectNullElements(categoryRuleArray, nameof(categoryRules));
        RejectNullElements(itemOverrideArray, nameof(itemOverrides));
        RejectNullElements(reservedArray, nameof(reservedRegions));

        EnsureUnique(displayPolicyArray.Select(static item => item.DisplayRef), nameof(displayPolicies));
        EnsureUnique(zoneArray.Select(static item => item.ZoneKey), nameof(zones));
        EnsureUnique(categoryRuleArray.Select(static item => item.CategoryKey), nameof(categoryRules));
        EnsureUnique(itemOverrideArray.Select(static item => item.ItemRef), nameof(itemOverrides));
        EnsureUnique(reservedArray.Select(static item => item.RegionKey), nameof(reservedRegions));

        var zoneByKey = zoneArray.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
        var displayPolicyByRef = displayPolicyArray.ToDictionary(
            static policy => policy.DisplayRef,
            StringComparer.Ordinal);

        foreach (var zone in zoneArray)
        {
            if (displayPolicyByRef.TryGetValue(zone.DisplayRef, out var policy) &&
                policy.Policy == DesktopWorkspaceDisplayPolicyKind.Preserve)
            {
                throw new ArgumentException(
                    $"Preserve 显示器 {zone.DisplayRef} 不能包含 Blueprint Zone。",
                    nameof(zones));
            }
        }

        var relationKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in relationArray)
        {
            if (!zoneByKey.TryGetValue(relation.ZoneKey, out var zone))
                throw new ArgumentException($"ZoneRelation 引用了未知 ZoneKey \"{relation.ZoneKey}\"。", nameof(zoneRelations));
            if (!zoneByKey.TryGetValue(relation.OtherZoneKey, out var otherZone))
                throw new ArgumentException($"ZoneRelation 引用了未知 OtherZoneKey \"{relation.OtherZoneKey}\"。", nameof(zoneRelations));
            if (!string.Equals(zone.DisplayRef, otherZone.DisplayRef, StringComparison.Ordinal))
                throw new ArgumentException("ZoneRelation 只能连接同一显示器上的 Zone。", nameof(zoneRelations));

            var relationKey = $"{relation.ZoneKey}\u001f{(int)relation.Relation}\u001f{relation.OtherZoneKey}";
            if (!relationKeys.Add(relationKey))
                throw new ArgumentException("ZoneRelations 不能包含重复关系。", nameof(zoneRelations));
        }

        foreach (var rule in categoryRuleArray)
        {
            if (!zoneByKey.ContainsKey(rule.ZoneKey))
                throw new ArgumentException($"CategoryRule 引用了未知 ZoneKey \"{rule.ZoneKey}\"。", nameof(categoryRules));
        }

        foreach (var itemOverride in itemOverrideArray)
        {
            if (itemOverride.Kind == DesktopWorkspaceItemOverrideKind.PlaceInZone &&
                !zoneByKey.ContainsKey(itemOverride.ZoneKey!))
            {
                throw new ArgumentException(
                    $"ItemOverride 引用了未知 ZoneKey \"{itemOverride.ZoneKey}\"。",
                    nameof(itemOverrides));
            }
        }

        foreach (var reserved in reservedArray)
        {
            if (displayPolicyByRef.TryGetValue(reserved.DisplayRef, out var policy) &&
                policy.Policy == DesktopWorkspaceDisplayPolicyKind.Preserve)
            {
                throw new ArgumentException(
                    $"Preserve 显示器 {reserved.DisplayRef} 不应额外声明 ReservedRegion。",
                    nameof(reservedRegions));
            }
        }

        BlueprintId = blueprintId;
        Revision = revision;
        ParentRevision = parentRevision;
        SchemaVersion = schemaVersion;
        DesignSummary = designSummary.Trim();
        _displayPolicies = Array.AsReadOnly(displayPolicyArray);
        _zones = Array.AsReadOnly(zoneArray);
        _zoneRelations = Array.AsReadOnly(relationArray);
        _categoryRules = Array.AsReadOnly(categoryRuleArray);
        _itemOverrides = Array.AsReadOnly(itemOverrideArray);
        _reservedRegions = Array.AsReadOnly(reservedArray);
    }

    private static void RejectNullElements<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        if (values.Any(static value => value is null))
            throw new ArgumentException("集合不能包含 null。", parameterName);
    }

    private static void EnsureUnique(IEnumerable<string> values, string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
                throw new ArgumentException($"集合包含重复键：{value}。", parameterName);
        }
    }
}
