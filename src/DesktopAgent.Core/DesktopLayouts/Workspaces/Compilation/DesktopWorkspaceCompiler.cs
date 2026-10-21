using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;

public enum DesktopWorkspaceCompiledItemDisposition
{
    Place = 0,
    Keep = 1,
    Exclude = 2,
}

public enum DesktopWorkspaceCompileIssueCode
{
    AutoArrangeEnabled = 0,
    DisplayPolicyMismatch = 1,
    UnknownDisplay = 2,
    UnknownCategory = 3,
    MissingLocalItemFact = 4,
    MissingSemanticAssignment = 5,
    UnknownItemRef = 6,
    DuplicateItemRefBinding = 7,
    PreserveDisplayConflict = 8,
    NonMovableItemRequiresExplicitPolicy = 9,
    MissingCategoryRule = 10,
    ZoneQuantizesToEmpty = 11,
    ReservedRegionQuantizesToEmpty = 12,
    ZoneRelationViolated = 13,
    ZoneOverlap = 14,
    ZoneCapacityInsufficient = 15,
}

public sealed record DesktopWorkspaceCompileIssue(
    DesktopWorkspaceCompileIssueCode Code,
    string Message,
    string? DisplayRef = null,
    string? ZoneKey = null,
    string? StableId = null,
    int? RequiredSlots = null,
    int? AvailableSlots = null);

/// <summary>
/// Compiler 所需的本地现实 sidecar。它只告诉施工器项目现在在哪块显示器、真实像素位置和是否可移动，
/// 不要求当前项目先属于某个 Explorer slot / CurrentCell。
/// </summary>
public sealed record DesktopWorkspaceCompilationItemFact
{
    public string StableId { get; }
    public string CurrentDisplayRef { get; }
    public PixelPoint PixelPosition { get; }
    public bool IsMovable { get; }

    public DesktopWorkspaceCompilationItemFact(
        string stableId,
        string currentDisplayRef,
        PixelPoint pixelPosition,
        bool isMovable)
    {
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(currentDisplayRef))
            throw new ArgumentException("CurrentDisplayRef 不能为空。", nameof(currentDisplayRef));

        StableId = stableId;
        CurrentDisplayRef = currentDisplayRef.Trim();
        PixelPosition = pixelPosition;
        IsMovable = isMovable;
    }
}

/// <summary>
/// Provider-safe ItemRef 与本地 StableId 的 authority binding。该映射只留在本地，不进入 Blueprint。
/// </summary>
public sealed record DesktopWorkspaceCompilationItemRefBinding
{
    public string ItemRef { get; }
    public string StableId { get; }

    public DesktopWorkspaceCompilationItemRefBinding(string itemRef, string stableId)
    {
        if (string.IsNullOrWhiteSpace(itemRef))
            throw new ArgumentException("ItemRef 不能为空。", nameof(itemRef));
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));

        ItemRef = itemRef.Trim();
        StableId = stableId;
    }
}

public readonly record struct DesktopWorkspaceNativeRect(
    int FirstColumn,
    int FirstRow,
    int ColumnCount,
    int RowCount)
{
    public int ExclusiveColumn => checked(FirstColumn + ColumnCount);
    public int ExclusiveRow => checked(FirstRow + RowCount);
}

public sealed record CompiledWorkspaceZone
{
    public string ZoneKey { get; }
    public string Name { get; }
    public string DisplayRef { get; }
    public DesktopWorkspaceRelativeRect RequestedBounds { get; }
    public DesktopWorkspaceRelativeRect RealizedBounds { get; }
    public DesktopWorkspaceNativeRect NativeRect { get; }
    public DesktopWorkspaceAnchorKind Anchor { get; }
    public DesktopWorkspacePackingOrder PackingOrder { get; }
    public int AvailableSlotCount { get; }

    public CompiledWorkspaceZone(
        string zoneKey,
        string name,
        string displayRef,
        DesktopWorkspaceRelativeRect requestedBounds,
        DesktopWorkspaceRelativeRect realizedBounds,
        DesktopWorkspaceNativeRect nativeRect,
        DesktopWorkspaceAnchorKind anchor,
        DesktopWorkspacePackingOrder packingOrder,
        int availableSlotCount)
    {
        ZoneKey = RequireText(zoneKey, nameof(zoneKey));
        Name = RequireText(name, nameof(name));
        DisplayRef = RequireText(displayRef, nameof(displayRef));
        if (nativeRect.FirstColumn < 0 || nativeRect.FirstRow < 0 ||
            nativeRect.ColumnCount <= 0 || nativeRect.RowCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(nativeRect));
        if (availableSlotCount < 0)
            throw new ArgumentOutOfRangeException(nameof(availableSlotCount));

        RequestedBounds = requestedBounds;
        RealizedBounds = realizedBounds;
        NativeRect = nativeRect;
        Anchor = anchor;
        PackingOrder = packingOrder;
        AvailableSlotCount = availableSlotCount;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

public sealed record CompiledWorkspaceReservedRegion
{
    public string RegionKey { get; }
    public string Purpose { get; }
    public string DisplayRef { get; }
    public DesktopWorkspaceRelativeRect RequestedBounds { get; }
    public DesktopWorkspaceRelativeRect RealizedBounds { get; }
    public DesktopWorkspaceNativeRect NativeRect { get; }

    public CompiledWorkspaceReservedRegion(
        string regionKey,
        string purpose,
        string displayRef,
        DesktopWorkspaceRelativeRect requestedBounds,
        DesktopWorkspaceRelativeRect realizedBounds,
        DesktopWorkspaceNativeRect nativeRect)
    {
        RegionKey = RequireText(regionKey, nameof(regionKey));
        Purpose = RequireText(purpose, nameof(purpose));
        DisplayRef = RequireText(displayRef, nameof(displayRef));
        if (nativeRect.FirstColumn < 0 || nativeRect.FirstRow < 0 ||
            nativeRect.ColumnCount <= 0 || nativeRect.RowCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(nativeRect));

        RequestedBounds = requestedBounds;
        RealizedBounds = realizedBounds;
        NativeRect = nativeRect;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

public sealed record CompiledWorkspaceItem
{
    public string StableId { get; }
    public string CategoryKey { get; }
    public string SourceDisplayRef { get; }
    public DesktopWorkspaceCompiledItemDisposition Disposition { get; }
    public string? ZoneKey { get; }
    public string? TargetDisplayRef { get; }
    public string? TargetSlotId { get; }
    public PixelPoint? TargetPixelPosition { get; }

    public CompiledWorkspaceItem(
        string stableId,
        string categoryKey,
        string sourceDisplayRef,
        DesktopWorkspaceCompiledItemDisposition disposition,
        string? zoneKey = null,
        string? targetDisplayRef = null,
        string? targetSlotId = null,
        PixelPoint? targetPixelPosition = null)
    {
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        CategoryKey = RequireText(categoryKey, nameof(categoryKey));
        SourceDisplayRef = RequireText(sourceDisplayRef, nameof(sourceDisplayRef));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition));

        var isPlacement = disposition == DesktopWorkspaceCompiledItemDisposition.Place;
        if (isPlacement !=
            (zoneKey is not null && targetDisplayRef is not null && targetSlotId is not null && targetPixelPosition.HasValue))
        {
            throw new ArgumentException("Place 必须完整绑定 Zone / Display / Slot / Pixel；Keep / Exclude 不得携带目标。 ");
        }

        StableId = stableId;
        Disposition = disposition;
        ZoneKey = zoneKey;
        TargetDisplayRef = targetDisplayRef;
        TargetSlotId = targetSlotId;
        TargetPixelPosition = targetPixelPosition;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

/// <summary>
/// 确定性 Compiler 的精确施工结果。PlanFingerprint 是内容寻址身份，相同输入必须得到相同结果。
/// </summary>
public sealed record CompiledWorkspacePlan
{
    private readonly IReadOnlyList<CompiledWorkspaceZone> _zones;
    private readonly IReadOnlyList<CompiledWorkspaceReservedRegion> _reservedRegions;
    private readonly IReadOnlyList<CompiledWorkspaceItem> _items;

    public Guid BlueprintId { get; }
    public int BlueprintRevision { get; }
    public Guid ObservationId { get; }
    public string GeometryFingerprint { get; }
    public string PlanFingerprint { get; }
    public IReadOnlyList<CompiledWorkspaceZone> Zones => _zones;
    public IReadOnlyList<CompiledWorkspaceReservedRegion> ReservedRegions => _reservedRegions;
    public IReadOnlyList<CompiledWorkspaceItem> Items => _items;

    public CompiledWorkspacePlan(
        Guid blueprintId,
        int blueprintRevision,
        Guid observationId,
        string geometryFingerprint,
        string planFingerprint,
        IEnumerable<CompiledWorkspaceZone> zones,
        IEnumerable<CompiledWorkspaceReservedRegion> reservedRegions,
        IEnumerable<CompiledWorkspaceItem> items)
    {
        if (blueprintId == Guid.Empty)
            throw new ArgumentException("BlueprintId 不能为 Guid.Empty。", nameof(blueprintId));
        if (blueprintRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(blueprintRevision));
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为 Guid.Empty。", nameof(observationId));
        if (string.IsNullOrWhiteSpace(geometryFingerprint))
            throw new ArgumentException("GeometryFingerprint 不能为空。", nameof(geometryFingerprint));
        if (string.IsNullOrWhiteSpace(planFingerprint))
            throw new ArgumentException("PlanFingerprint 不能为空。", nameof(planFingerprint));

        var zoneArray = zones?.ToArray() ?? throw new ArgumentNullException(nameof(zones));
        var reservedArray = reservedRegions?.ToArray() ?? throw new ArgumentNullException(nameof(reservedRegions));
        var itemArray = items?.ToArray() ?? throw new ArgumentNullException(nameof(items));
        RejectNullElements(zoneArray, nameof(zones));
        RejectNullElements(reservedArray, nameof(reservedRegions));
        RejectNullElements(itemArray, nameof(items));

        BlueprintId = blueprintId;
        BlueprintRevision = blueprintRevision;
        ObservationId = observationId;
        GeometryFingerprint = geometryFingerprint.Trim();
        PlanFingerprint = planFingerprint.Trim();
        _zones = Array.AsReadOnly(zoneArray);
        _reservedRegions = Array.AsReadOnly(reservedArray);
        _items = Array.AsReadOnly(itemArray);
    }

    private static void RejectNullElements<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        if (values.Any(static value => value is null))
            throw new ArgumentException("集合不能包含 null。", parameterName);
    }
}

public sealed class DesktopWorkspaceCompileResult
{
    public CompiledWorkspacePlan? Plan { get; }
    public IReadOnlyList<DesktopWorkspaceCompileIssue> Issues { get; }
    public bool Succeeded => Plan is not null && Issues.Count == 0;

    private DesktopWorkspaceCompileResult(
        CompiledWorkspacePlan? plan,
        IEnumerable<DesktopWorkspaceCompileIssue> issues)
    {
        var issueArray = issues?.ToArray() ?? throw new ArgumentNullException(nameof(issues));
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("Issues 不能包含 null。", nameof(issues));
        if ((plan is null) == (issueArray.Length == 0))
            throw new ArgumentException("CompileResult 必须是完整 Success 或完整 Failure。 ");

        Plan = plan;
        Issues = Array.AsReadOnly(issueArray);
    }

    public static DesktopWorkspaceCompileResult Success(CompiledWorkspacePlan plan)
        => new(plan ?? throw new ArgumentNullException(nameof(plan)), []);

    public static DesktopWorkspaceCompileResult Failure(IEnumerable<DesktopWorkspaceCompileIssue> issues)
    {
        var issueArray = issues?.ToArray() ?? throw new ArgumentNullException(nameof(issues));
        if (issueArray.Length == 0)
            throw new ArgumentException("Failure 至少需要一个 issue。", nameof(issues));
        return new(null, issueArray);
    }
}

/// <summary>
/// ADR-0005 Workspace Compiler。
///
/// 它只拥有 realization authority：
/// - relative geometry → nearest Explorer-native boundaries；
/// - Category→Zone 展开；
/// - deterministic ordering / packing；
/// - fixed / stationary obstacle avoidance；
/// - Blueprint anchor realization。
///
/// 它没有 classify / redesign / policy authority。任何无法忠实落地的情况都返回结构化 FAIL。
/// </summary>
public static class DesktopWorkspaceCompiler
{
    public static DesktopWorkspaceCompileResult Compile(
        DesktopWorkspaceBlueprint blueprint,
        DesktopSemanticUnderstanding semanticUnderstanding,
        ExplorerWorkspaceGeometry geometry,
        IEnumerable<DesktopWorkspaceCompilationItemFact> itemFacts,
        IEnumerable<DesktopWorkspaceCompilationItemRefBinding>? itemRefBindings = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(semanticUnderstanding);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(itemFacts);

        var issues = new List<DesktopWorkspaceCompileIssue>();
        if (geometry.IsAutoArrangeEnabled)
        {
            issues.Add(new(
                DesktopWorkspaceCompileIssueCode.AutoArrangeEnabled,
                "Explorer Auto Arrange 已启用，无法保证 exact native-slot realization。"));
            return DesktopWorkspaceCompileResult.Failure(issues);
        }

        var factArray = itemFacts.ToArray();
        if (factArray.Any(static item => item is null))
            throw new ArgumentException("ItemFacts 不能包含 null。", nameof(itemFacts));
        var duplicateFactIds = factArray.GroupBy(static item => item.StableId, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicateFactIds.Length > 0)
            throw new ArgumentException("ItemFacts 不能包含重复 StableId。", nameof(itemFacts));

        var bindingArray = itemRefBindings?.ToArray() ?? [];
        if (bindingArray.Any(static item => item is null))
            throw new ArgumentException("ItemRefBindings 不能包含 null。", nameof(itemRefBindings));
        if (bindingArray.Select(static item => item.ItemRef).Distinct(StringComparer.Ordinal).Count() != bindingArray.Length ||
            bindingArray.Select(static item => item.StableId).Distinct(StringComparer.Ordinal).Count() != bindingArray.Length)
        {
            issues.Add(new(
                DesktopWorkspaceCompileIssueCode.DuplicateItemRefBinding,
                "ItemRefBindings 必须是一对一 authority mapping。"));
            return DesktopWorkspaceCompileResult.Failure(issues);
        }

        var geometryDisplays = geometry.Displays.ToDictionary(static display => display.DisplayRef, StringComparer.Ordinal);
        var policies = blueprint.DisplayPolicies.ToDictionary(static policy => policy.DisplayRef, StringComparer.Ordinal);
        if (policies.Count != geometryDisplays.Count ||
            policies.Keys.Any(displayRef => !geometryDisplays.ContainsKey(displayRef)) ||
            geometryDisplays.Keys.Any(displayRef => !policies.ContainsKey(displayRef)))
        {
            issues.Add(new(
                DesktopWorkspaceCompileIssueCode.DisplayPolicyMismatch,
                "Blueprint 必须且只能为当前 Explorer Geometry 的每块显示器声明 Design / Preserve。"));
            return DesktopWorkspaceCompileResult.Failure(issues);
        }

        var factsById = factArray.ToDictionary(static item => item.StableId, StringComparer.Ordinal);
        foreach (var fact in factArray)
        {
            if (!geometryDisplays.ContainsKey(fact.CurrentDisplayRef))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.UnknownDisplay,
                    $"本地项目 {fact.StableId} 引用了未知 CurrentDisplayRef。",
                    fact.CurrentDisplayRef,
                    StableId: fact.StableId));
            }
        }

        var assignmentsById = semanticUnderstanding.Assignments.ToDictionary(
            static assignment => assignment.StableId,
            StringComparer.Ordinal);
        var semanticCategoryKeys = semanticUnderstanding.Categories
            .Select(static category => category.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var assignment in semanticUnderstanding.Assignments)
        {
            if (!factsById.ContainsKey(assignment.StableId))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.MissingLocalItemFact,
                    $"Semantic Assignment {assignment.StableId} 没有对应的本地现实 ItemFact。",
                    StableId: assignment.StableId));
            }
        }
        foreach (var fact in factArray)
        {
            if (!assignmentsById.ContainsKey(fact.StableId))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.MissingSemanticAssignment,
                    $"本地项目 {fact.StableId} 没有 Semantic Assignment。",
                    fact.CurrentDisplayRef,
                    StableId: fact.StableId));
            }
        }

        foreach (var rule in blueprint.CategoryRules)
        {
            if (!semanticCategoryKeys.Contains(rule.CategoryKey))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.UnknownCategory,
                    $"Blueprint CategoryRule 引用了当前 Semantic Understanding 中不存在的 Category \"{rule.CategoryKey}\"。",
                    ZoneKey: rule.ZoneKey));
            }
        }

        var itemRefToStableId = bindingArray.ToDictionary(static item => item.ItemRef, static item => item.StableId, StringComparer.Ordinal);
        var overridesByStableId = new Dictionary<string, DesktopWorkspaceBlueprintItemOverride>(StringComparer.Ordinal);
        foreach (var itemOverride in blueprint.ItemOverrides)
        {
            if (!itemRefToStableId.TryGetValue(itemOverride.ItemRef, out var stableId) || !factsById.ContainsKey(stableId))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.UnknownItemRef,
                    $"Blueprint ItemOverride \"{itemOverride.ItemRef}\" 没有当前本地 authority binding。"));
                continue;
            }
            overridesByStableId.Add(stableId, itemOverride);
        }

        if (issues.Count > 0)
            return DesktopWorkspaceCompileResult.Failure(issues);

        var compiledReserved = new List<CompiledWorkspaceReservedRegion>();
        var reservedSlotIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reserved in blueprint.ReservedRegions.OrderBy(static region => region.RegionKey, StringComparer.Ordinal))
        {
            if (!geometryDisplays.TryGetValue(reserved.DisplayRef, out var display))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.UnknownDisplay,
                    $"ReservedRegion {reserved.RegionKey} 引用了未知显示器。",
                    reserved.DisplayRef));
                continue;
            }

            if (!TryQuantize(reserved.Bounds, display, out var nativeRect, out var realizedBounds))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.ReservedRegionQuantizesToEmpty,
                    $"ReservedRegion {reserved.RegionKey} 无法在 Explorer native surface 上形成非空区域。",
                    reserved.DisplayRef));
                continue;
            }

            compiledReserved.Add(new(
                reserved.RegionKey,
                reserved.Purpose,
                reserved.DisplayRef,
                reserved.Bounds,
                realizedBounds,
                nativeRect));
            foreach (var slot in SlotsInside(display, nativeRect))
                reservedSlotIds.Add(slot.SlotId);
        }

        var compiledZoneDrafts = new List<ZoneDraft>();
        foreach (var zone in blueprint.Zones.OrderBy(static zone => zone.ZoneKey, StringComparer.Ordinal))
        {
            if (!geometryDisplays.TryGetValue(zone.DisplayRef, out var display))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.UnknownDisplay,
                    $"Zone {zone.ZoneKey} 引用了未知显示器。",
                    zone.DisplayRef,
                    zone.ZoneKey));
                continue;
            }
            if (policies[zone.DisplayRef].Policy != DesktopWorkspaceDisplayPolicyKind.Design)
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.PreserveDisplayConflict,
                    $"Zone {zone.ZoneKey} 不能落在 Preserve 显示器。",
                    zone.DisplayRef,
                    zone.ZoneKey));
                continue;
            }
            if (!TryQuantize(zone.Bounds, display, out var nativeRect, out var realizedBounds))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.ZoneQuantizesToEmpty,
                    $"Zone {zone.ZoneKey} 无法在 Explorer native surface 上形成非空区域。",
                    zone.DisplayRef,
                    zone.ZoneKey));
                continue;
            }

            compiledZoneDrafts.Add(new(zone, display, nativeRect, realizedBounds));
        }

        ValidateZoneRelations(blueprint, compiledZoneDrafts, issues);
        ValidateZoneOverlap(compiledZoneDrafts, issues);
        if (issues.Count > 0)
            return DesktopWorkspaceCompileResult.Failure(issues);

        var blockedSlotIds = new HashSet<string>(reservedSlotIds, StringComparer.Ordinal);
        foreach (var display in geometry.Displays)
        {
            foreach (var obstacle in display.FixedObstacles)
            {
                foreach (var slotId in obstacle.BlockedSlotIds)
                    blockedSlotIds.Add(slotId);
            }
        }

        // Preserve / Keep / Exclude 项目不参与重新布局，但它们仍是当前桌面上的真实占位。
        foreach (var fact in factArray)
        {
            var policy = policies[fact.CurrentDisplayRef].Policy;
            overridesByStableId.TryGetValue(fact.StableId, out var itemOverride);
            var stationary = policy == DesktopWorkspaceDisplayPolicyKind.Preserve ||
                             itemOverride?.Kind is DesktopWorkspaceItemOverrideKind.Keep or DesktopWorkspaceItemOverrideKind.Exclude;
            if (!stationary)
                continue;

            var display = geometryDisplays[fact.CurrentDisplayRef];
            foreach (var slotId in GetOverlappedSlotIds(fact.PixelPosition, display))
                blockedSlotIds.Add(slotId);
        }

        var rulesByCategory = blueprint.CategoryRules.ToDictionary(
            static rule => rule.CategoryKey,
            static rule => rule.ZoneKey,
            StringComparer.Ordinal);
        var zonesByKey = compiledZoneDrafts.ToDictionary(static zone => zone.Blueprint.ZoneKey, StringComparer.Ordinal);
        var itemsByZone = new Dictionary<string, List<ItemDraft>>(StringComparer.Ordinal);
        var compiledItems = new List<CompiledWorkspaceItem>(factArray.Length);

        foreach (var fact in factArray.OrderBy(static item => item.StableId, StringComparer.Ordinal))
        {
            var assignment = assignmentsById[fact.StableId];
            var policy = policies[fact.CurrentDisplayRef].Policy;
            overridesByStableId.TryGetValue(fact.StableId, out var itemOverride);

            if (policy == DesktopWorkspaceDisplayPolicyKind.Preserve)
            {
                if (itemOverride?.Kind == DesktopWorkspaceItemOverrideKind.PlaceInZone)
                {
                    issues.Add(new(
                        DesktopWorkspaceCompileIssueCode.PreserveDisplayConflict,
                        $"项目 {fact.StableId} 位于 Preserve 显示器，但 Blueprint 要求迁出。",
                        fact.CurrentDisplayRef,
                        itemOverride.ZoneKey,
                        fact.StableId));
                    continue;
                }

                compiledItems.Add(new(
                    fact.StableId,
                    assignment.CategoryKey,
                    fact.CurrentDisplayRef,
                    itemOverride?.Kind == DesktopWorkspaceItemOverrideKind.Exclude
                        ? DesktopWorkspaceCompiledItemDisposition.Exclude
                        : DesktopWorkspaceCompiledItemDisposition.Keep));
                continue;
            }

            if (itemOverride?.Kind == DesktopWorkspaceItemOverrideKind.Keep)
            {
                compiledItems.Add(new(
                    fact.StableId,
                    assignment.CategoryKey,
                    fact.CurrentDisplayRef,
                    DesktopWorkspaceCompiledItemDisposition.Keep));
                continue;
            }
            if (itemOverride?.Kind == DesktopWorkspaceItemOverrideKind.Exclude)
            {
                compiledItems.Add(new(
                    fact.StableId,
                    assignment.CategoryKey,
                    fact.CurrentDisplayRef,
                    DesktopWorkspaceCompiledItemDisposition.Exclude));
                continue;
            }

            if (!fact.IsMovable)
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.NonMovableItemRequiresExplicitPolicy,
                    $"不可移动项目 {fact.StableId} 在 Design 显示器上必须由 Blueprint 明确 Keep / Exclude。",
                    fact.CurrentDisplayRef,
                    StableId: fact.StableId));
                continue;
            }

            var targetZoneKey = itemOverride?.Kind == DesktopWorkspaceItemOverrideKind.PlaceInZone
                ? itemOverride.ZoneKey!
                : rulesByCategory.TryGetValue(assignment.CategoryKey, out var categoryZone)
                    ? categoryZone
                    : null;
            if (targetZoneKey is null)
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.MissingCategoryRule,
                    $"Category {assignment.CategoryKey} 没有 Blueprint Category→Zone rule，且项目 {fact.StableId} 没有 PlaceInZone override。",
                    fact.CurrentDisplayRef,
                    StableId: fact.StableId));
                continue;
            }

            if (!zonesByKey.TryGetValue(targetZoneKey, out var zoneDraft))
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.UnknownDisplay,
                    $"项目 {fact.StableId} 的目标 Zone {targetZoneKey} 无法解析。",
                    ZoneKey: targetZoneKey,
                    StableId: fact.StableId));
                continue;
            }

            if (!itemsByZone.TryGetValue(targetZoneKey, out var zoneItems))
            {
                zoneItems = [];
                itemsByZone.Add(targetZoneKey, zoneItems);
            }
            zoneItems.Add(new(fact, assignment, itemOverride, zoneDraft));
        }

        if (issues.Count > 0)
            return DesktopWorkspaceCompileResult.Failure(issues);

        var compiledZones = new List<CompiledWorkspaceZone>(compiledZoneDrafts.Count);
        foreach (var zoneDraft in compiledZoneDrafts.OrderBy(static zone => zone.Blueprint.ZoneKey, StringComparer.Ordinal))
        {
            var candidateSlots = SlotsInside(zoneDraft.Display, zoneDraft.NativeRect)
                .Where(slot => !blockedSlotIds.Contains(slot.SlotId))
                .ToArray();
            var zoneItems = itemsByZone.TryGetValue(zoneDraft.Blueprint.ZoneKey, out var assigned)
                ? assigned
                : [];

            if (zoneItems.Count > candidateSlots.Length)
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.ZoneCapacityInsufficient,
                    $"Zone {zoneDraft.Blueprint.ZoneKey} 需要 {zoneItems.Count} 个 slot，但忠实量化后只有 {candidateSlots.Length} 个可用 slot。",
                    zoneDraft.Blueprint.DisplayRef,
                    zoneDraft.Blueprint.ZoneKey,
                    RequiredSlots: zoneItems.Count,
                    AvailableSlots: candidateSlots.Length));
                continue;
            }

            var available = new HashSet<string>(candidateSlots.Select(static slot => slot.SlotId), StringComparer.Ordinal);
            var slotById = candidateSlots.ToDictionary(static slot => slot.SlotId, StringComparer.Ordinal);

            // 明确 Item anchor 的少量 override 优先机械落位；同 anchor 下按 StableId 保证确定性。
            foreach (var item in zoneItems
                         .Where(static item => item.Override?.Anchor is not null)
                         .OrderBy(static item => item.Fact.StableId, StringComparer.Ordinal))
            {
                var slot = OrderSlots(
                        candidateSlots.Where(candidate => available.Contains(candidate.SlotId)),
                        zoneDraft.NativeRect,
                        item.Override!.Anchor!.Value,
                        zoneDraft.Blueprint.PackingOrder)
                    .First();
                available.Remove(slot.SlotId);
                compiledItems.Add(ToPlacedItem(item, zoneDraft, slot));
            }

            var remainingItems = zoneItems
                .Where(static item => item.Override?.Anchor is null)
                .OrderBy(static item => item.Fact.StableId, StringComparer.Ordinal)
                .ToArray();
            var orderedSlots = OrderSlots(
                    candidateSlots.Where(candidate => available.Contains(candidate.SlotId)),
                    zoneDraft.NativeRect,
                    zoneDraft.Blueprint.Anchor,
                    zoneDraft.Blueprint.PackingOrder)
                .ToArray();

            for (var index = 0; index < remainingItems.Length; index++)
                compiledItems.Add(ToPlacedItem(remainingItems[index], zoneDraft, orderedSlots[index]));

            compiledZones.Add(new(
                zoneDraft.Blueprint.ZoneKey,
                zoneDraft.Blueprint.Name,
                zoneDraft.Blueprint.DisplayRef,
                zoneDraft.Blueprint.Bounds,
                zoneDraft.RealizedBounds,
                zoneDraft.NativeRect,
                zoneDraft.Blueprint.Anchor,
                zoneDraft.Blueprint.PackingOrder,
                candidateSlots.Length));
        }

        if (issues.Count > 0)
            return DesktopWorkspaceCompileResult.Failure(issues);

        var orderedCompiledItems = compiledItems.OrderBy(static item => item.StableId, StringComparer.Ordinal).ToArray();
        var orderedZones = compiledZones.OrderBy(static zone => zone.ZoneKey, StringComparer.Ordinal).ToArray();
        var orderedReserved = compiledReserved.OrderBy(static region => region.RegionKey, StringComparer.Ordinal).ToArray();
        var fingerprint = ComputePlanFingerprint(
            blueprint,
            geometry,
            orderedZones,
            orderedReserved,
            orderedCompiledItems);

        return DesktopWorkspaceCompileResult.Success(new(
            blueprint.BlueprintId,
            blueprint.Revision,
            geometry.ObservationId,
            geometry.GeometryFingerprint,
            fingerprint,
            orderedZones,
            orderedReserved,
            orderedCompiledItems));
    }

    private static CompiledWorkspaceItem ToPlacedItem(
        ItemDraft item,
        ZoneDraft zone,
        ExplorerNativeSlot slot)
        => new(
            item.Fact.StableId,
            item.Assignment.CategoryKey,
            item.Fact.CurrentDisplayRef,
            DesktopWorkspaceCompiledItemDisposition.Place,
            zone.Blueprint.ZoneKey,
            zone.Blueprint.DisplayRef,
            slot.SlotId,
            slot.PixelPosition);

    private static bool TryQuantize(
        DesktopWorkspaceRelativeRect requested,
        ExplorerDisplayGeometry display,
        out DesktopWorkspaceNativeRect nativeRect,
        out DesktopWorkspaceRelativeRect realized)
    {
        var firstColumn = NearestBoundary(requested.Left, display.Columns);
        var exclusiveColumn = NearestBoundary(requested.Left + requested.Width, display.Columns);
        var firstRow = NearestBoundary(requested.Top, display.Rows);
        var exclusiveRow = NearestBoundary(requested.Top + requested.Height, display.Rows);

        if (exclusiveColumn <= firstColumn || exclusiveRow <= firstRow)
        {
            nativeRect = default;
            realized = default;
            return false;
        }

        nativeRect = new(
            firstColumn,
            firstRow,
            exclusiveColumn - firstColumn,
            exclusiveRow - firstRow);
        realized = new(
            firstColumn / (double)display.Columns,
            firstRow / (double)display.Rows,
            (exclusiveColumn - firstColumn) / (double)display.Columns,
            (exclusiveRow - firstRow) / (double)display.Rows);
        return true;
    }

    private static int NearestBoundary(double normalized, int count)
        => checked((int)Math.Round(normalized * count, MidpointRounding.AwayFromZero));

    private static IEnumerable<ExplorerNativeSlot> SlotsInside(
        ExplorerDisplayGeometry display,
        DesktopWorkspaceNativeRect rect)
        => display.Slots.Where(slot =>
            slot.Column >= rect.FirstColumn && slot.Column < rect.ExclusiveColumn &&
            slot.Row >= rect.FirstRow && slot.Row < rect.ExclusiveRow);

    private static IEnumerable<ExplorerNativeSlot> OrderSlots(
        IEnumerable<ExplorerNativeSlot> slots,
        DesktopWorkspaceNativeRect rect,
        DesktopWorkspaceAnchorKind anchor,
        DesktopWorkspacePackingOrder packingOrder)
    {
        var slotArray = slots.ToArray();
        if (anchor == DesktopWorkspaceAnchorKind.Center)
        {
            var centerColumn = rect.FirstColumn + (rect.ColumnCount - 1) / 2d;
            var centerRow = rect.FirstRow + (rect.RowCount - 1) / 2d;
            return slotArray
                .OrderBy(slot => Math.Abs(slot.Column - centerColumn) + Math.Abs(slot.Row - centerRow))
                .ThenBy(static slot => slot.Row)
                .ThenBy(static slot => slot.Column);
        }

        var rightFirst = anchor == DesktopWorkspaceAnchorKind.TopRight ||
                         anchor == DesktopWorkspaceAnchorKind.BottomRight;
        var bottomFirst = anchor == DesktopWorkspaceAnchorKind.BottomLeft ||
                          anchor == DesktopWorkspaceAnchorKind.BottomRight;

        if (packingOrder == DesktopWorkspacePackingOrder.ColumnMajor)
        {
            return rightFirst
                ? bottomFirst
                    ? slotArray.OrderByDescending(static slot => slot.Column).ThenByDescending(static slot => slot.Row)
                    : slotArray.OrderByDescending(static slot => slot.Column).ThenBy(static slot => slot.Row)
                : bottomFirst
                    ? slotArray.OrderBy(static slot => slot.Column).ThenByDescending(static slot => slot.Row)
                    : slotArray.OrderBy(static slot => slot.Column).ThenBy(static slot => slot.Row);
        }

        return bottomFirst
            ? rightFirst
                ? slotArray.OrderByDescending(static slot => slot.Row).ThenByDescending(static slot => slot.Column)
                : slotArray.OrderByDescending(static slot => slot.Row).ThenBy(static slot => slot.Column)
            : rightFirst
                ? slotArray.OrderBy(static slot => slot.Row).ThenByDescending(static slot => slot.Column)
                : slotArray.OrderBy(static slot => slot.Row).ThenBy(static slot => slot.Column);
    }

    private static IReadOnlyList<string> GetOverlappedSlotIds(
        PixelPoint position,
        ExplorerDisplayGeometry display)
    {
        var firstColumn = FloorDivide((long)position.X - display.GridOrigin.X, display.Spacing.X);
        var firstRow = FloorDivide((long)position.Y - display.GridOrigin.Y, display.Spacing.Y);
        var lastColumn = FloorDivide(
            (long)position.X + display.Spacing.X - 1 - display.GridOrigin.X,
            display.Spacing.X);
        var lastRow = FloorDivide(
            (long)position.Y + display.Spacing.Y - 1 - display.GridOrigin.Y,
            display.Spacing.Y);

        firstColumn = Math.Max(0, firstColumn);
        firstRow = Math.Max(0, firstRow);
        lastColumn = Math.Min(display.Columns - 1L, lastColumn);
        lastRow = Math.Min(display.Rows - 1L, lastRow);
        if (firstColumn > lastColumn || firstRow > lastRow)
            return [];

        var slotIds = new List<string>();
        foreach (var slot in display.Slots)
        {
            if (slot.Column >= firstColumn && slot.Column <= lastColumn &&
                slot.Row >= firstRow && slot.Row <= lastRow)
                slotIds.Add(slot.SlotId);
        }
        return Array.AsReadOnly(slotIds.ToArray());
    }

    private static long FloorDivide(long value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static void ValidateZoneRelations(
        DesktopWorkspaceBlueprint blueprint,
        IReadOnlyList<ZoneDraft> zones,
        ICollection<DesktopWorkspaceCompileIssue> issues)
    {
        var zonesByKey = zones.ToDictionary(static zone => zone.Blueprint.ZoneKey, StringComparer.Ordinal);
        foreach (var relation in blueprint.ZoneRelations)
        {
            if (!zonesByKey.TryGetValue(relation.ZoneKey, out var first) ||
                !zonesByKey.TryGetValue(relation.OtherZoneKey, out var second))
                continue;

            var satisfied = relation.Relation switch
            {
                DesktopWorkspaceZoneRelationKind.LeftOf => first.NativeRect.ExclusiveColumn <= second.NativeRect.FirstColumn,
                DesktopWorkspaceZoneRelationKind.RightOf => first.NativeRect.FirstColumn >= second.NativeRect.ExclusiveColumn,
                DesktopWorkspaceZoneRelationKind.Above => first.NativeRect.ExclusiveRow <= second.NativeRect.FirstRow,
                DesktopWorkspaceZoneRelationKind.Below => first.NativeRect.FirstRow >= second.NativeRect.ExclusiveRow,
                _ => false,
            };
            if (!satisfied)
            {
                issues.Add(new(
                    DesktopWorkspaceCompileIssueCode.ZoneRelationViolated,
                    $"Zone relation {relation.ZoneKey} {relation.Relation} {relation.OtherZoneKey} 在 nearest native quantization 后不成立。",
                    first.Blueprint.DisplayRef,
                    relation.ZoneKey));
            }
        }
    }

    private static void ValidateZoneOverlap(
        IReadOnlyList<ZoneDraft> zones,
        ICollection<DesktopWorkspaceCompileIssue> issues)
    {
        foreach (var group in zones.GroupBy(static zone => zone.Blueprint.DisplayRef, StringComparer.Ordinal))
        {
            var array = group.OrderBy(static zone => zone.Blueprint.ZoneKey, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < array.Length; i++)
            {
                for (var j = i + 1; j < array.Length; j++)
                {
                    if (!Overlaps(array[i].NativeRect, array[j].NativeRect))
                        continue;
                    issues.Add(new(
                        DesktopWorkspaceCompileIssueCode.ZoneOverlap,
                        $"Zone {array[i].Blueprint.ZoneKey} 与 {array[j].Blueprint.ZoneKey} 的 native realization 重叠，Compiler 不得自行裁切。",
                        group.Key,
                        array[i].Blueprint.ZoneKey));
                }
            }
        }
    }

    private static bool Overlaps(DesktopWorkspaceNativeRect a, DesktopWorkspaceNativeRect b)
        => a.FirstColumn < b.ExclusiveColumn && a.ExclusiveColumn > b.FirstColumn &&
           a.FirstRow < b.ExclusiveRow && a.ExclusiveRow > b.FirstRow;

    private static string ComputePlanFingerprint(
        DesktopWorkspaceBlueprint blueprint,
        ExplorerWorkspaceGeometry geometry,
        IEnumerable<CompiledWorkspaceZone> zones,
        IEnumerable<CompiledWorkspaceReservedRegion> reservedRegions,
        IEnumerable<CompiledWorkspaceItem> items)
    {
        var builder = new StringBuilder();
        builder.Append("workspace-plan-v1|")
            .Append(blueprint.BlueprintId.ToString("N"))
            .Append('|').Append(blueprint.Revision)
            .Append('|').Append(geometry.GeometryFingerprint)
            .AppendLine();

        foreach (var zone in zones)
        {
            builder.Append("z|").Append(zone.ZoneKey).Append('|').Append(zone.DisplayRef).Append('|')
                .Append(zone.NativeRect.FirstColumn).Append('|').Append(zone.NativeRect.FirstRow).Append('|')
                .Append(zone.NativeRect.ColumnCount).Append('|').Append(zone.NativeRect.RowCount).Append('|')
                .Append((int)zone.Anchor).Append('|').Append((int)zone.PackingOrder).AppendLine();
        }
        foreach (var region in reservedRegions)
        {
            builder.Append("r|").Append(region.RegionKey).Append('|').Append(region.DisplayRef).Append('|')
                .Append(region.NativeRect.FirstColumn).Append('|').Append(region.NativeRect.FirstRow).Append('|')
                .Append(region.NativeRect.ColumnCount).Append('|').Append(region.NativeRect.RowCount).AppendLine();
        }
        foreach (var item in items)
        {
            builder.Append("i|").Append(item.StableId).Append('|').Append(item.CategoryKey).Append('|')
                .Append(item.SourceDisplayRef).Append('|').Append((int)item.Disposition).Append('|')
                .Append(item.ZoneKey ?? string.Empty).Append('|').Append(item.TargetDisplayRef ?? string.Empty).Append('|')
                .Append(item.TargetSlotId ?? string.Empty).Append('|')
                .Append(item.TargetPixelPosition?.X.ToString() ?? string.Empty).Append('|')
                .Append(item.TargetPixelPosition?.Y.ToString() ?? string.Empty).AppendLine();
        }

        return "workspace-plan-v1:" +
               Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private sealed record ZoneDraft(
        DesktopWorkspaceBlueprintZone Blueprint,
        ExplorerDisplayGeometry Display,
        DesktopWorkspaceNativeRect NativeRect,
        DesktopWorkspaceRelativeRect RealizedBounds);

    private sealed record ItemDraft(
        DesktopWorkspaceCompilationItemFact Fact,
        DesktopSemanticAssignment Assignment,
        DesktopWorkspaceBlueprintItemOverride? Override,
        ZoneDraft Zone);
}
