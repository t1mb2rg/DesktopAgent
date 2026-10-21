using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;

public enum DesktopWorkspaceEquivalenceIssueCode
{
    BlueprintIdentityMismatch = 0,
    GeometryIdentityMismatch = 1,
    ZoneSetMismatch = 2,
    ZoneDisplayMismatch = 3,
    ZoneQuantizationMismatch = 4,
    ZoneAnchorMismatch = 5,
    ZonePackingOrderMismatch = 6,
    ZoneRelationViolated = 7,
    ReservedRegionSetMismatch = 8,
    ReservedRegionDisplayMismatch = 9,
    ReservedRegionQuantizationMismatch = 10,
    CategoryRuleViolated = 11,
    ItemOverrideUnbound = 12,
    ItemOverrideViolated = 13,
    PreservePolicyViolated = 14,
    CompiledItemSetMismatch = 15,
    NonMovablePolicyViolated = 16,
    ItemTargetOutsideZone = 17,
    ItemTargetPixelMismatch = 18,
    ItemPackingMismatch = 19,
    ZoneAvailableSurfaceMismatch = 20,
}

public sealed record DesktopWorkspaceEquivalenceIssue(
    DesktopWorkspaceEquivalenceIssueCode Code,
    string Message,
    string? DisplayRef = null,
    string? ZoneKey = null,
    string? StableId = null);

/// <summary>
/// 一块 Zone / Reserved Region 的 native quantization 证明。
/// RequestedBounds 属于 Blueprint 设计空间；RealizedBounds / NativeRect 是 Checker 独立复算后的 Explorer realization。
/// </summary>
public sealed record WorkspaceGeometryRealizationProof
{
    public string Key { get; }
    public string DisplayRef { get; }
    public DesktopWorkspaceRelativeRect RequestedBounds { get; }
    public DesktopWorkspaceRelativeRect RealizedBounds { get; }
    public DesktopWorkspaceNativeRect NativeRect { get; }

    public WorkspaceGeometryRealizationProof(
        string key,
        string displayRef,
        DesktopWorkspaceRelativeRect requestedBounds,
        DesktopWorkspaceRelativeRect realizedBounds,
        DesktopWorkspaceNativeRect nativeRect)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key 不能为空。", nameof(key));
        if (string.IsNullOrWhiteSpace(displayRef))
            throw new ArgumentException("DisplayRef 不能为空。", nameof(displayRef));

        Key = key.Trim();
        DisplayRef = displayRef.Trim();
        RequestedBounds = requestedBounds;
        RealizedBounds = realizedBounds;
        NativeRect = nativeRect;
    }
}

/// <summary>
/// 一个项目的语义 realization 证明。只记录已经验证通过的最终事实，不授予执行 authority。
/// </summary>
public sealed record WorkspaceItemRealizationProof
{
    public string StableId { get; }
    public string SourceDisplayRef { get; }
    public string CategoryKey { get; }
    public DesktopWorkspaceCompiledItemDisposition Disposition { get; }
    public string? ZoneKey { get; }
    public string? TargetDisplayRef { get; }
    public string? TargetSlotId { get; }

    public WorkspaceItemRealizationProof(
        string stableId,
        string sourceDisplayRef,
        string categoryKey,
        DesktopWorkspaceCompiledItemDisposition disposition,
        string? zoneKey,
        string? targetDisplayRef,
        string? targetSlotId)
    {
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(sourceDisplayRef))
            throw new ArgumentException("SourceDisplayRef 不能为空。", nameof(sourceDisplayRef));
        if (string.IsNullOrWhiteSpace(categoryKey))
            throw new ArgumentException("CategoryKey 不能为空。", nameof(categoryKey));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition));

        StableId = stableId;
        SourceDisplayRef = sourceDisplayRef.Trim();
        CategoryKey = categoryKey.Trim();
        Disposition = disposition;
        ZoneKey = zoneKey;
        TargetDisplayRef = targetDisplayRef;
        TargetSlotId = targetSlotId;
    }
}

/// <summary>
/// ADR-0005 的 realization certificate。
/// 它只证明某个 Exact Plan 忠实实现指定 Blueprint revision，不授予 Preview / Candidate / Writer authority。
/// </summary>
public sealed record WorkspaceRealizationCertificate
{
    private readonly IReadOnlyList<WorkspaceGeometryRealizationProof> _zoneProofs;
    private readonly IReadOnlyList<WorkspaceGeometryRealizationProof> _reservedRegionProofs;
    private readonly IReadOnlyList<WorkspaceItemRealizationProof> _itemProofs;

    public Guid BlueprintId { get; }
    public int BlueprintRevision { get; }
    public string GeometryFingerprint { get; }
    public string PlanFingerprint { get; }
    public string CertificateFingerprint { get; }
    public IReadOnlyList<WorkspaceGeometryRealizationProof> ZoneProofs => _zoneProofs;
    public IReadOnlyList<WorkspaceGeometryRealizationProof> ReservedRegionProofs => _reservedRegionProofs;
    public IReadOnlyList<WorkspaceItemRealizationProof> ItemProofs => _itemProofs;

    public WorkspaceRealizationCertificate(
        Guid blueprintId,
        int blueprintRevision,
        string geometryFingerprint,
        string planFingerprint,
        string certificateFingerprint,
        IEnumerable<WorkspaceGeometryRealizationProof> zoneProofs,
        IEnumerable<WorkspaceGeometryRealizationProof> reservedRegionProofs,
        IEnumerable<WorkspaceItemRealizationProof> itemProofs)
    {
        if (blueprintId == Guid.Empty)
            throw new ArgumentException("BlueprintId 不能为 Guid.Empty。", nameof(blueprintId));
        if (blueprintRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(blueprintRevision));
        if (string.IsNullOrWhiteSpace(geometryFingerprint))
            throw new ArgumentException("GeometryFingerprint 不能为空。", nameof(geometryFingerprint));
        if (string.IsNullOrWhiteSpace(planFingerprint))
            throw new ArgumentException("PlanFingerprint 不能为空。", nameof(planFingerprint));
        if (string.IsNullOrWhiteSpace(certificateFingerprint))
            throw new ArgumentException("CertificateFingerprint 不能为空。", nameof(certificateFingerprint));
        ArgumentNullException.ThrowIfNull(zoneProofs);
        ArgumentNullException.ThrowIfNull(reservedRegionProofs);
        ArgumentNullException.ThrowIfNull(itemProofs);

        var zoneArray = zoneProofs.ToArray();
        var reservedArray = reservedRegionProofs.ToArray();
        var itemArray = itemProofs.ToArray();
        RejectNullElements(zoneArray, nameof(zoneProofs));
        RejectNullElements(reservedArray, nameof(reservedRegionProofs));
        RejectNullElements(itemArray, nameof(itemProofs));

        BlueprintId = blueprintId;
        BlueprintRevision = blueprintRevision;
        GeometryFingerprint = geometryFingerprint.Trim();
        PlanFingerprint = planFingerprint.Trim();
        CertificateFingerprint = certificateFingerprint.Trim();
        _zoneProofs = Array.AsReadOnly(zoneArray);
        _reservedRegionProofs = Array.AsReadOnly(reservedArray);
        _itemProofs = Array.AsReadOnly(itemArray);
    }

    private static void RejectNullElements<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        if (values.Any(static value => value is null))
            throw new ArgumentException("集合不能包含 null。", parameterName);
    }
}

public sealed class DesktopWorkspaceEquivalenceResult
{
    public WorkspaceRealizationCertificate? Certificate { get; }
    public IReadOnlyList<DesktopWorkspaceEquivalenceIssue> Issues { get; }
    public bool Equivalent => Certificate is not null && Issues.Count == 0;

    private DesktopWorkspaceEquivalenceResult(
        WorkspaceRealizationCertificate? certificate,
        IEnumerable<DesktopWorkspaceEquivalenceIssue> issues)
    {
        var issueArray = issues?.ToArray() ?? throw new ArgumentNullException(nameof(issues));
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("Issues 不能包含 null。", nameof(issues));
        if ((certificate is null) == (issueArray.Length == 0))
            throw new ArgumentException("EquivalenceResult 必须是完整 Equivalent 或完整 NotEquivalent。 ");

        Certificate = certificate;
        Issues = Array.AsReadOnly(issueArray);
    }

    public static DesktopWorkspaceEquivalenceResult Success(WorkspaceRealizationCertificate certificate)
        => new(certificate ?? throw new ArgumentNullException(nameof(certificate)), []);

    public static DesktopWorkspaceEquivalenceResult Failure(IEnumerable<DesktopWorkspaceEquivalenceIssue> issues)
    {
        var issueArray = issues?.ToArray() ?? throw new ArgumentNullException(nameof(issues));
        if (issueArray.Length == 0)
            throw new ArgumentException("Failure 至少需要一个 issue。", nameof(issues));
        return new(null, issueArray);
    }
}

/// <summary>
/// 对 Compiler 输出做独立、确定性的 Blueprint semantic-equivalence 验证。
/// Checker 独立复算 quantization、stationary occupancy 与 deterministic packing，不调用 Compiler helper。
/// </summary>
public static class DesktopWorkspaceSemanticEquivalenceChecker
{
    public static DesktopWorkspaceEquivalenceResult Check(
        DesktopWorkspaceBlueprint blueprint,
        ExplorerWorkspaceGeometry geometry,
        CompiledWorkspacePlan plan,
        IEnumerable<DesktopWorkspaceCompilationItemFact> itemFacts,
        IEnumerable<DesktopWorkspaceCompilationItemRefBinding>? itemRefBindings = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(itemFacts);

        var issues = new List<DesktopWorkspaceEquivalenceIssue>();
        if (plan.BlueprintId != blueprint.BlueprintId || plan.BlueprintRevision != blueprint.Revision)
        {
            issues.Add(new(
                DesktopWorkspaceEquivalenceIssueCode.BlueprintIdentityMismatch,
                "Compiled plan 没有绑定当前 Blueprint revision。"));
        }
        if (!string.Equals(plan.GeometryFingerprint, geometry.GeometryFingerprint, StringComparison.Ordinal) ||
            plan.ObservationId != geometry.ObservationId)
        {
            issues.Add(new(
                DesktopWorkspaceEquivalenceIssueCode.GeometryIdentityMismatch,
                "Compiled plan 没有绑定当前 Explorer geometry / observation。"));
        }
        if (issues.Count > 0)
            return DesktopWorkspaceEquivalenceResult.Failure(issues);

        var factArray = itemFacts.ToArray();
        if (factArray.Any(static fact => fact is null))
            throw new ArgumentException("ItemFacts 不能包含 null。", nameof(itemFacts));
        if (factArray.Select(static fact => fact.StableId).Distinct(StringComparer.Ordinal).Count() != factArray.Length ||
            plan.Items.Select(static item => item.StableId).Distinct(StringComparer.Ordinal).Count() != plan.Items.Count)
        {
            issues.Add(new(
                DesktopWorkspaceEquivalenceIssueCode.CompiledItemSetMismatch,
                "ItemFacts / Compiled Items 必须各自具有唯一 StableId。"));
            return DesktopWorkspaceEquivalenceResult.Failure(issues);
        }

        var factsById = factArray.ToDictionary(static fact => fact.StableId, StringComparer.Ordinal);
        var planItemsById = plan.Items.ToDictionary(static item => item.StableId, StringComparer.Ordinal);
        if (!SameKeys(factsById.Keys, planItemsById.Keys))
        {
            issues.Add(new(
                DesktopWorkspaceEquivalenceIssueCode.CompiledItemSetMismatch,
                "Compiled plan 的项目集合与当前 local ItemFacts 不一致。"));
        }

        var bindingArray = itemRefBindings?.ToArray() ?? [];
        if (bindingArray.Any(static binding => binding is null) ||
            bindingArray.Select(static binding => binding.ItemRef).Distinct(StringComparer.Ordinal).Count() != bindingArray.Length ||
            bindingArray.Select(static binding => binding.StableId).Distinct(StringComparer.Ordinal).Count() != bindingArray.Length)
        {
            issues.Add(new(
                DesktopWorkspaceEquivalenceIssueCode.ItemOverrideUnbound,
                "ItemRef bindings 必须是无 null、无重复的一对一映射。"));
        }

        if (issues.Count > 0)
            return DesktopWorkspaceEquivalenceResult.Failure(issues);

        var geometryDisplays = geometry.Displays.ToDictionary(static display => display.DisplayRef, StringComparer.Ordinal);
        var blueprintZones = blueprint.Zones.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
        var compiledZones = plan.Zones.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
        var policies = blueprint.DisplayPolicies.ToDictionary(static policy => policy.DisplayRef, StringComparer.Ordinal);
        var stableIdByItemRef = bindingArray.ToDictionary(static binding => binding.ItemRef, static binding => binding.StableId, StringComparer.Ordinal);
        var overridesByStableId = ResolveOverrides(blueprint, stableIdByItemRef, planItemsById, issues);

        if (!SameKeys(blueprintZones.Keys, compiledZones.Keys))
        {
            issues.Add(new(
                DesktopWorkspaceEquivalenceIssueCode.ZoneSetMismatch,
                "Compiled plan 的 Zone 集合与 Blueprint 不一致。"));
        }

        var zoneProofs = new List<WorkspaceGeometryRealizationProof>();
        foreach (var blueprintZone in blueprint.Zones.OrderBy(static zone => zone.ZoneKey, StringComparer.Ordinal))
        {
            if (!compiledZones.TryGetValue(blueprintZone.ZoneKey, out var compiledZone))
                continue;
            if (!string.Equals(blueprintZone.DisplayRef, compiledZone.DisplayRef, StringComparison.Ordinal))
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ZoneDisplayMismatch,
                    $"Zone {blueprintZone.ZoneKey} 被实现到错误显示器。",
                    compiledZone.DisplayRef,
                    blueprintZone.ZoneKey));
                continue;
            }
            if (!geometryDisplays.TryGetValue(blueprintZone.DisplayRef, out var display))
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ZoneDisplayMismatch,
                    $"Zone {blueprintZone.ZoneKey} 引用的显示器不在当前 Geometry 中。",
                    blueprintZone.DisplayRef,
                    blueprintZone.ZoneKey));
                continue;
            }

            if (!TryQuantize(blueprintZone.Bounds, display, out var expectedRect, out var expectedBounds) ||
                compiledZone.NativeRect != expectedRect ||
                compiledZone.RealizedBounds != expectedBounds ||
                compiledZone.RequestedBounds != blueprintZone.Bounds)
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ZoneQuantizationMismatch,
                    $"Zone {blueprintZone.ZoneKey} 不是 Blueprint 几何的 nearest native quantization。",
                    blueprintZone.DisplayRef,
                    blueprintZone.ZoneKey));
            }
            else
            {
                zoneProofs.Add(new(
                    blueprintZone.ZoneKey,
                    blueprintZone.DisplayRef,
                    blueprintZone.Bounds,
                    expectedBounds,
                    expectedRect));
            }

            if (compiledZone.Anchor != blueprintZone.Anchor)
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ZoneAnchorMismatch,
                    $"Zone {blueprintZone.ZoneKey} 的 anchor 被改变。",
                    blueprintZone.DisplayRef,
                    blueprintZone.ZoneKey));
            }
            if (compiledZone.PackingOrder != blueprintZone.PackingOrder)
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ZonePackingOrderMismatch,
                    $"Zone {blueprintZone.ZoneKey} 的 packing order 被改变。",
                    blueprintZone.DisplayRef,
                    blueprintZone.ZoneKey));
            }
        }

        ValidateZoneRelations(blueprint, compiledZones, issues);

        var blueprintReserved = blueprint.ReservedRegions.ToDictionary(static region => region.RegionKey, StringComparer.Ordinal);
        var compiledReserved = plan.ReservedRegions.ToDictionary(static region => region.RegionKey, StringComparer.Ordinal);
        if (!SameKeys(blueprintReserved.Keys, compiledReserved.Keys))
        {
            issues.Add(new(
                DesktopWorkspaceEquivalenceIssueCode.ReservedRegionSetMismatch,
                "Compiled plan 的 Reserved Region 集合与 Blueprint 不一致。"));
        }

        var reservedProofs = new List<WorkspaceGeometryRealizationProof>();
        foreach (var blueprintRegion in blueprint.ReservedRegions.OrderBy(static region => region.RegionKey, StringComparer.Ordinal))
        {
            if (!compiledReserved.TryGetValue(blueprintRegion.RegionKey, out var compiledRegion))
                continue;
            if (!string.Equals(blueprintRegion.DisplayRef, compiledRegion.DisplayRef, StringComparison.Ordinal))
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ReservedRegionDisplayMismatch,
                    $"Reserved Region {blueprintRegion.RegionKey} 被实现到错误显示器。",
                    compiledRegion.DisplayRef));
                continue;
            }
            if (!geometryDisplays.TryGetValue(blueprintRegion.DisplayRef, out var display))
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ReservedRegionDisplayMismatch,
                    $"Reserved Region {blueprintRegion.RegionKey} 引用的显示器不在当前 Geometry 中。",
                    blueprintRegion.DisplayRef));
                continue;
            }

            if (!TryQuantize(blueprintRegion.Bounds, display, out var expectedRect, out var expectedBounds) ||
                compiledRegion.NativeRect != expectedRect ||
                compiledRegion.RealizedBounds != expectedBounds ||
                compiledRegion.RequestedBounds != blueprintRegion.Bounds)
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ReservedRegionQuantizationMismatch,
                    $"Reserved Region {blueprintRegion.RegionKey} 不是 Blueprint 几何的 nearest native quantization。",
                    blueprintRegion.DisplayRef));
            }
            else
            {
                reservedProofs.Add(new(
                    blueprintRegion.RegionKey,
                    blueprintRegion.DisplayRef,
                    blueprintRegion.Bounds,
                    expectedBounds,
                    expectedRect));
            }
        }

        ValidateItemPoliciesAndRules(
            blueprint,
            plan,
            factsById,
            policies,
            overridesByStableId,
            issues);
        ValidateIndependentPacking(
            blueprint,
            geometry,
            plan,
            factsById,
            compiledZones,
            overridesByStableId,
            issues);

        if (issues.Count > 0)
            return DesktopWorkspaceEquivalenceResult.Failure(issues);

        var itemProofs = plan.Items
            .OrderBy(static item => item.StableId, StringComparer.Ordinal)
            .Select(static item => new WorkspaceItemRealizationProof(
                item.StableId,
                item.SourceDisplayRef,
                item.CategoryKey,
                item.Disposition,
                item.ZoneKey,
                item.TargetDisplayRef,
                item.TargetSlotId))
            .ToArray();
        var certificateFingerprint = ComputeCertificateFingerprint(
            blueprint,
            geometry,
            plan,
            zoneProofs,
            reservedProofs,
            itemProofs);
        return DesktopWorkspaceEquivalenceResult.Success(new(
            blueprint.BlueprintId,
            blueprint.Revision,
            geometry.GeometryFingerprint,
            plan.PlanFingerprint,
            certificateFingerprint,
            zoneProofs,
            reservedProofs,
            itemProofs));
    }

    private static Dictionary<string, DesktopWorkspaceBlueprintItemOverride> ResolveOverrides(
        DesktopWorkspaceBlueprint blueprint,
        IReadOnlyDictionary<string, string> stableIdByItemRef,
        IReadOnlyDictionary<string, CompiledWorkspaceItem> planItemsById,
        ICollection<DesktopWorkspaceEquivalenceIssue> issues)
    {
        var result = new Dictionary<string, DesktopWorkspaceBlueprintItemOverride>(StringComparer.Ordinal);
        foreach (var itemOverride in blueprint.ItemOverrides)
        {
            if (!stableIdByItemRef.TryGetValue(itemOverride.ItemRef, out var stableId) ||
                !planItemsById.ContainsKey(stableId))
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ItemOverrideUnbound,
                    $"ItemOverride {itemOverride.ItemRef} 无法绑定到当前 compiled item。"));
                continue;
            }
            result.Add(stableId, itemOverride);
        }
        return result;
    }

    private static void ValidateItemPoliciesAndRules(
        DesktopWorkspaceBlueprint blueprint,
        CompiledWorkspacePlan plan,
        IReadOnlyDictionary<string, DesktopWorkspaceCompilationItemFact> factsById,
        IReadOnlyDictionary<string, DesktopWorkspaceBlueprintDisplayPolicy> policies,
        IReadOnlyDictionary<string, DesktopWorkspaceBlueprintItemOverride> overridesByStableId,
        ICollection<DesktopWorkspaceEquivalenceIssue> issues)
    {
        var rulesByCategory = blueprint.CategoryRules.ToDictionary(
            static rule => rule.CategoryKey,
            static rule => rule.ZoneKey,
            StringComparer.Ordinal);

        foreach (var item in plan.Items)
        {
            if (!factsById.TryGetValue(item.StableId, out var fact))
                continue;
            if (!string.Equals(item.SourceDisplayRef, fact.CurrentDisplayRef, StringComparison.Ordinal))
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.CompiledItemSetMismatch,
                    $"项目 {item.StableId} 的 SourceDisplayRef 与 local fact 不一致。",
                    item.SourceDisplayRef,
                    item.ZoneKey,
                    item.StableId));
            }

            if (!fact.IsMovable && item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place)
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.NonMovablePolicyViolated,
                    $"不可移动项目 {item.StableId} 被 Exact Plan 安排移动。",
                    item.SourceDisplayRef,
                    item.ZoneKey,
                    item.StableId));
            }

            if (policies.TryGetValue(item.SourceDisplayRef, out var sourcePolicy) &&
                sourcePolicy.Policy == DesktopWorkspaceDisplayPolicyKind.Preserve &&
                item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place)
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.PreservePolicyViolated,
                    $"Preserve 显示器 {item.SourceDisplayRef} 上的项目 {item.StableId} 被迁移。",
                    item.SourceDisplayRef,
                    item.ZoneKey,
                    item.StableId));
            }

            overridesByStableId.TryGetValue(item.StableId, out var itemOverride);
            if (itemOverride is not null)
            {
                var satisfied = itemOverride.Kind switch
                {
                    DesktopWorkspaceItemOverrideKind.PlaceInZone =>
                        item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
                        string.Equals(item.ZoneKey, itemOverride.ZoneKey, StringComparison.Ordinal),
                    DesktopWorkspaceItemOverrideKind.Keep =>
                        item.Disposition == DesktopWorkspaceCompiledItemDisposition.Keep,
                    DesktopWorkspaceItemOverrideKind.Exclude =>
                        item.Disposition == DesktopWorkspaceCompiledItemDisposition.Exclude,
                    _ => false,
                };
                if (!satisfied)
                {
                    issues.Add(new(
                        DesktopWorkspaceEquivalenceIssueCode.ItemOverrideViolated,
                        $"项目 {item.StableId} 的 Blueprint ItemOverride 未保持。",
                        item.TargetDisplayRef,
                        item.ZoneKey,
                        item.StableId));
                }
                continue;
            }

            if (item.Disposition != DesktopWorkspaceCompiledItemDisposition.Place)
                continue;
            if (!rulesByCategory.TryGetValue(item.CategoryKey, out var expectedZone) ||
                !string.Equals(item.ZoneKey, expectedZone, StringComparison.Ordinal))
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.CategoryRuleViolated,
                    $"项目 {item.StableId} 的 Category→Zone hard semantic 未保持。",
                    item.TargetDisplayRef,
                    item.ZoneKey,
                    item.StableId));
            }
        }
    }

    private static void ValidateIndependentPacking(
        DesktopWorkspaceBlueprint blueprint,
        ExplorerWorkspaceGeometry geometry,
        CompiledWorkspacePlan plan,
        IReadOnlyDictionary<string, DesktopWorkspaceCompilationItemFact> factsById,
        IReadOnlyDictionary<string, CompiledWorkspaceZone> compiledZones,
        IReadOnlyDictionary<string, DesktopWorkspaceBlueprintItemOverride> overridesByStableId,
        ICollection<DesktopWorkspaceEquivalenceIssue> issues)
    {
        var blockedSlotIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var display in geometry.Displays)
        {
            foreach (var obstacle in display.FixedObstacles)
            {
                foreach (var slotId in obstacle.BlockedSlotIds)
                    blockedSlotIds.Add(slotId);
            }
        }
        foreach (var region in plan.ReservedRegions)
        {
            var display = geometry.ResolveDisplay(region.DisplayRef);
            foreach (var slot in SlotsInside(display, region.NativeRect))
                blockedSlotIds.Add(slot.SlotId);
        }
        foreach (var item in plan.Items)
        {
            if (item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place ||
                !factsById.TryGetValue(item.StableId, out var fact))
                continue;
            var display = geometry.ResolveDisplay(fact.CurrentDisplayRef);
            foreach (var slotId in GetOverlappedSlotIds(fact.PixelPosition, display))
                blockedSlotIds.Add(slotId);
        }

        foreach (var blueprintZone in blueprint.Zones.OrderBy(static zone => zone.ZoneKey, StringComparer.Ordinal))
        {
            if (!compiledZones.TryGetValue(blueprintZone.ZoneKey, out var compiledZone))
                continue;
            var display = geometry.ResolveDisplay(compiledZone.DisplayRef);
            var candidateSlots = SlotsInside(display, compiledZone.NativeRect)
                .Where(slot => !blockedSlotIds.Contains(slot.SlotId))
                .ToArray();
            if (compiledZone.AvailableSlotCount != candidateSlots.Length)
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ZoneAvailableSurfaceMismatch,
                    $"Zone {compiledZone.ZoneKey} 的 AvailableSlotCount 与独立复算不一致。",
                    compiledZone.DisplayRef,
                    compiledZone.ZoneKey));
            }

            var zoneItems = plan.Items
                .Where(item => item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
                               string.Equals(item.ZoneKey, compiledZone.ZoneKey, StringComparison.Ordinal))
                .ToArray();
            var candidateById = candidateSlots.ToDictionary(static slot => slot.SlotId, StringComparer.Ordinal);
            var targetIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in zoneItems)
            {
                if (item.TargetSlotId is null || !candidateById.TryGetValue(item.TargetSlotId, out var slot) ||
                    !string.Equals(item.TargetDisplayRef, compiledZone.DisplayRef, StringComparison.Ordinal))
                {
                    issues.Add(new(
                        DesktopWorkspaceEquivalenceIssueCode.ItemTargetOutsideZone,
                        $"项目 {item.StableId} 的目标不在 Zone {compiledZone.ZoneKey} 的可用 native surface。",
                        item.TargetDisplayRef,
                        compiledZone.ZoneKey,
                        item.StableId));
                    continue;
                }
                if (!targetIds.Add(item.TargetSlotId))
                {
                    issues.Add(new(
                        DesktopWorkspaceEquivalenceIssueCode.ItemTargetOutsideZone,
                        $"Zone {compiledZone.ZoneKey} 存在重复 TargetSlotId {item.TargetSlotId}。",
                        compiledZone.DisplayRef,
                        compiledZone.ZoneKey,
                        item.StableId));
                }
                if (item.TargetPixelPosition != slot.PixelPosition)
                {
                    issues.Add(new(
                        DesktopWorkspaceEquivalenceIssueCode.ItemTargetPixelMismatch,
                        $"项目 {item.StableId} 的 TargetPixelPosition 与 TargetSlotId 不一致。",
                        compiledZone.DisplayRef,
                        compiledZone.ZoneKey,
                        item.StableId));
                }
            }

            var available = new HashSet<string>(candidateSlots.Select(static slot => slot.SlotId), StringComparer.Ordinal);
            var expectedSlotByStableId = new Dictionary<string, ExplorerNativeSlot>(StringComparer.Ordinal);
            foreach (var item in zoneItems
                         .Where(item => overridesByStableId.TryGetValue(item.StableId, out var itemOverride) && itemOverride.Anchor.HasValue)
                         .OrderBy(static item => item.StableId, StringComparer.Ordinal))
            {
                var itemOverride = overridesByStableId[item.StableId];
                var expected = OrderSlots(
                        candidateSlots.Where(slot => available.Contains(slot.SlotId)),
                        compiledZone.NativeRect,
                        itemOverride.Anchor!.Value,
                        compiledZone.PackingOrder)
                    .FirstOrDefault();
                if (expected is null)
                    continue;
                expectedSlotByStableId[item.StableId] = expected;
                available.Remove(expected.SlotId);
            }

            var remainingItems = zoneItems
                .Where(item => !expectedSlotByStableId.ContainsKey(item.StableId))
                .OrderBy(static item => item.StableId, StringComparer.Ordinal)
                .ToArray();
            var remainingSlots = OrderSlots(
                    candidateSlots.Where(slot => available.Contains(slot.SlotId)),
                    compiledZone.NativeRect,
                    compiledZone.Anchor,
                    compiledZone.PackingOrder)
                .ToArray();
            for (var index = 0; index < remainingItems.Length && index < remainingSlots.Length; index++)
                expectedSlotByStableId[remainingItems[index].StableId] = remainingSlots[index];

            foreach (var item in zoneItems)
            {
                if (!expectedSlotByStableId.TryGetValue(item.StableId, out var expected))
                    continue;
                if (!string.Equals(item.TargetSlotId, expected.SlotId, StringComparison.Ordinal))
                {
                    issues.Add(new(
                        DesktopWorkspaceEquivalenceIssueCode.ItemPackingMismatch,
                        $"项目 {item.StableId} 没有按 Blueprint anchor / packing 规则落到独立复算的 native slot。",
                        compiledZone.DisplayRef,
                        compiledZone.ZoneKey,
                        item.StableId));
                }
            }
        }
    }

    private static void ValidateZoneRelations(
        DesktopWorkspaceBlueprint blueprint,
        IReadOnlyDictionary<string, CompiledWorkspaceZone> compiledZones,
        ICollection<DesktopWorkspaceEquivalenceIssue> issues)
    {
        foreach (var relation in blueprint.ZoneRelations)
        {
            if (!compiledZones.TryGetValue(relation.ZoneKey, out var first) ||
                !compiledZones.TryGetValue(relation.OtherZoneKey, out var second))
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
                    DesktopWorkspaceEquivalenceIssueCode.ZoneRelationViolated,
                    $"Zone relation {relation.ZoneKey} {relation.Relation} {relation.OtherZoneKey} 未保持。",
                    first.DisplayRef,
                    relation.ZoneKey));
            }
        }
    }

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

        return display.Slots
            .Where(slot => slot.Column >= firstColumn && slot.Column <= lastColumn &&
                           slot.Row >= firstRow && slot.Row <= lastRow)
            .Select(static slot => slot.SlotId)
            .ToArray();
    }

    private static long FloorDivide(long value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static bool SameKeys(IEnumerable<string> first, IEnumerable<string> second)
        => first.ToHashSet(StringComparer.Ordinal).SetEquals(second);

    private static string ComputeCertificateFingerprint(
        DesktopWorkspaceBlueprint blueprint,
        ExplorerWorkspaceGeometry geometry,
        CompiledWorkspacePlan plan,
        IEnumerable<WorkspaceGeometryRealizationProof> zoneProofs,
        IEnumerable<WorkspaceGeometryRealizationProof> reservedProofs,
        IEnumerable<WorkspaceItemRealizationProof> itemProofs)
    {
        var builder = new StringBuilder();
        builder.Append("workspace-certificate-v1|")
            .Append(blueprint.BlueprintId.ToString("N"))
            .Append('|').Append(blueprint.Revision)
            .Append('|').Append(geometry.GeometryFingerprint)
            .Append('|').Append(plan.PlanFingerprint)
            .AppendLine();

        foreach (var proof in zoneProofs.OrderBy(static proof => proof.Key, StringComparer.Ordinal))
            AppendGeometryProof(builder, "z", proof);
        foreach (var proof in reservedProofs.OrderBy(static proof => proof.Key, StringComparer.Ordinal))
            AppendGeometryProof(builder, "r", proof);
        foreach (var proof in itemProofs.OrderBy(static proof => proof.StableId, StringComparer.Ordinal))
        {
            builder.Append("i|").Append(proof.StableId).Append('|').Append(proof.SourceDisplayRef).Append('|')
                .Append(proof.CategoryKey).Append('|').Append((int)proof.Disposition).Append('|')
                .Append(proof.ZoneKey ?? string.Empty).Append('|').Append(proof.TargetDisplayRef ?? string.Empty).Append('|')
                .Append(proof.TargetSlotId ?? string.Empty).AppendLine();
        }

        return "workspace-certificate-v1:" +
               Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendGeometryProof(StringBuilder builder, string kind, WorkspaceGeometryRealizationProof proof)
    {
        builder.Append(kind).Append('|').Append(proof.Key).Append('|').Append(proof.DisplayRef).Append('|')
            .Append(proof.NativeRect.FirstColumn).Append('|').Append(proof.NativeRect.FirstRow).Append('|')
            .Append(proof.NativeRect.ColumnCount).Append('|').Append(proof.NativeRect.RowCount).AppendLine();
    }
}
