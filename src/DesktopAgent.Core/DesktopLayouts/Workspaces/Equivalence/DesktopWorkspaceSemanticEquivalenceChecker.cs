using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

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
    UnexpectedCompiledItem = 15,
}

public sealed record DesktopWorkspaceEquivalenceIssue(
    DesktopWorkspaceEquivalenceIssueCode Code,
    string Message,
    string? DisplayRef = null,
    string? ZoneKey = null,
    string? StableId = null);

/// <summary>
/// 一块 Zone / Reserved Region 的 native quantization 证明。
/// RequestedBounds 属于 Blueprint 设计空间；RealizedBounds / NativeRect 是独立复算后的 Explorer realization。
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
/// ADR-0005 的 realization certificate。
/// 它不授予 Preview / Candidate / Writer authority，只证明某个 Exact Plan 忠实实现了指定 Blueprint revision。
/// </summary>
public sealed record WorkspaceRealizationCertificate
{
    private readonly IReadOnlyList<WorkspaceGeometryRealizationProof> _zoneProofs;
    private readonly IReadOnlyList<WorkspaceGeometryRealizationProof> _reservedRegionProofs;

    public Guid BlueprintId { get; }
    public int BlueprintRevision { get; }
    public string GeometryFingerprint { get; }
    public string PlanFingerprint { get; }
    public string CertificateFingerprint { get; }
    public IReadOnlyList<WorkspaceGeometryRealizationProof> ZoneProofs => _zoneProofs;
    public IReadOnlyList<WorkspaceGeometryRealizationProof> ReservedRegionProofs => _reservedRegionProofs;

    public WorkspaceRealizationCertificate(
        Guid blueprintId,
        int blueprintRevision,
        string geometryFingerprint,
        string planFingerprint,
        string certificateFingerprint,
        IEnumerable<WorkspaceGeometryRealizationProof> zoneProofs,
        IEnumerable<WorkspaceGeometryRealizationProof> reservedRegionProofs)
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

        var zoneArray = zoneProofs.ToArray();
        var reservedArray = reservedRegionProofs.ToArray();
        RejectNullElements(zoneArray, nameof(zoneProofs));
        RejectNullElements(reservedArray, nameof(reservedRegionProofs));

        BlueprintId = blueprintId;
        BlueprintRevision = blueprintRevision;
        GeometryFingerprint = geometryFingerprint.Trim();
        PlanFingerprint = planFingerprint.Trim();
        CertificateFingerprint = certificateFingerprint.Trim();
        _zoneProofs = Array.AsReadOnly(zoneArray);
        _reservedRegionProofs = Array.AsReadOnly(reservedArray);
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
///
/// 原则：
/// 1. Hard semantics 必须逐条成立；
/// 2. 连续几何只允许 nearest Explorer-native boundary quantization；
/// 3. Blueprint 未指定的自由度可由 Compiler 机械决定，但显式 anchor / packing / policy 不得改变；
/// 4. 不使用“±X% 看起来差不多”的模糊容差。
/// </summary>
public static class DesktopWorkspaceSemanticEquivalenceChecker
{
    public static DesktopWorkspaceEquivalenceResult Check(
        DesktopWorkspaceBlueprint blueprint,
        ExplorerWorkspaceGeometry geometry,
        CompiledWorkspacePlan plan,
        IEnumerable<DesktopWorkspaceCompilationItemRefBinding>? itemRefBindings = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(plan);

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

        var geometryDisplays = geometry.Displays.ToDictionary(static display => display.DisplayRef, StringComparer.Ordinal);
        var blueprintZones = blueprint.Zones.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
        var compiledZones = plan.Zones.ToDictionary(static zone => zone.ZoneKey, StringComparer.Ordinal);
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

        ValidateCategoryRules(blueprint, plan, issues);
        ValidatePreservePolicies(blueprint, plan, issues);
        ValidateItemOverrides(blueprint, plan, itemRefBindings, issues);

        if (issues.Count > 0)
            return DesktopWorkspaceEquivalenceResult.Failure(issues);

        var certificateFingerprint = ComputeCertificateFingerprint(
            blueprint,
            geometry,
            plan,
            zoneProofs,
            reservedProofs);
        return DesktopWorkspaceEquivalenceResult.Success(new(
            blueprint.BlueprintId,
            blueprint.Revision,
            geometry.GeometryFingerprint,
            plan.PlanFingerprint,
            certificateFingerprint,
            zoneProofs,
            reservedProofs));
    }

    private static void ValidateCategoryRules(
        DesktopWorkspaceBlueprint blueprint,
        CompiledWorkspacePlan plan,
        ICollection<DesktopWorkspaceEquivalenceIssue> issues)
    {
        var rulesByCategory = blueprint.CategoryRules.ToDictionary(
            static rule => rule.CategoryKey,
            static rule => rule.ZoneKey,
            StringComparer.Ordinal);
        var overrideItemRefs = blueprint.ItemOverrides
            .Select(static item => item.ItemRef)
            .ToHashSet(StringComparer.Ordinal);

        // ItemRef 与 StableId 的关系在 ValidateItemOverrides 中独立验证；这里仅验证非 override 的 Category→Zone。
        // 若某 StableId 对应 override，调用方绑定后会由 override gate 覆盖。
        var overrideStableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            if (item.Disposition != DesktopWorkspaceCompiledItemDisposition.Place)
                continue;
            if (!rulesByCategory.TryGetValue(item.CategoryKey, out var expectedZone))
                continue;
            if (overrideStableIds.Contains(item.StableId))
                continue;
            if (!string.Equals(item.ZoneKey, expectedZone, StringComparison.Ordinal))
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.CategoryRuleViolated,
                    $"项目 {item.StableId} 没有落入 Category {item.CategoryKey} 指定的 Zone {expectedZone}。",
                    item.TargetDisplayRef,
                    item.ZoneKey,
                    item.StableId));
            }
        }
    }

    private static void ValidateItemOverrides(
        DesktopWorkspaceBlueprint blueprint,
        CompiledWorkspacePlan plan,
        IEnumerable<DesktopWorkspaceCompilationItemRefBinding>? bindings,
        ICollection<DesktopWorkspaceEquivalenceIssue> issues)
    {
        var bindingArray = bindings?.ToArray() ?? [];
        var duplicateRefs = bindingArray.GroupBy(static binding => binding.ItemRef, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1);
        var duplicateStableIds = bindingArray.GroupBy(static binding => binding.StableId, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1);
        if (duplicateRefs || duplicateStableIds)
        {
            issues.Add(new(
                DesktopWorkspaceEquivalenceIssueCode.ItemOverrideUnbound,
                "ItemRef bindings 必须是一对一映射。"));
            return;
        }

        var stableIdByRef = bindingArray.ToDictionary(static binding => binding.ItemRef, static binding => binding.StableId, StringComparer.Ordinal);
        var itemByStableId = plan.Items.ToDictionary(static item => item.StableId, StringComparer.Ordinal);
        var overriddenStableIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var itemOverride in blueprint.ItemOverrides)
        {
            if (!stableIdByRef.TryGetValue(itemOverride.ItemRef, out var stableId) ||
                !itemByStableId.TryGetValue(stableId, out var compiledItem))
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ItemOverrideUnbound,
                    $"ItemOverride {itemOverride.ItemRef} 无法绑定到当前 compiled item。"));
                continue;
            }
            overriddenStableIds.Add(stableId);

            var satisfied = itemOverride.Kind switch
            {
                DesktopWorkspaceItemOverrideKind.PlaceInZone =>
                    compiledItem.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
                    string.Equals(compiledItem.ZoneKey, itemOverride.ZoneKey, StringComparison.Ordinal),
                DesktopWorkspaceItemOverrideKind.Keep =>
                    compiledItem.Disposition == DesktopWorkspaceCompiledItemDisposition.Keep,
                DesktopWorkspaceItemOverrideKind.Exclude =>
                    compiledItem.Disposition == DesktopWorkspaceCompiledItemDisposition.Exclude,
                _ => false,
            };
            if (!satisfied)
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.ItemOverrideViolated,
                    $"ItemOverride {itemOverride.ItemRef} 没有在 Exact Plan 中保持。",
                    compiledItem.TargetDisplayRef,
                    compiledItem.ZoneKey,
                    stableId));
            }
        }

        // 对没有 override 的 Place item 再核一遍 Category→Zone，避免 override 目标与 category rule 的差异被误判。
        var rulesByCategory = blueprint.CategoryRules.ToDictionary(
            static rule => rule.CategoryKey,
            static rule => rule.ZoneKey,
            StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            if (item.Disposition != DesktopWorkspaceCompiledItemDisposition.Place || overriddenStableIds.Contains(item.StableId))
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

    private static void ValidatePreservePolicies(
        DesktopWorkspaceBlueprint blueprint,
        CompiledWorkspacePlan plan,
        ICollection<DesktopWorkspaceEquivalenceIssue> issues)
    {
        var preserveRefs = blueprint.DisplayPolicies
            .Where(static policy => policy.Policy == DesktopWorkspaceDisplayPolicyKind.Preserve)
            .Select(static policy => policy.DisplayRef)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in plan.Items)
        {
            if (!preserveRefs.Contains(item.SourceDisplayRef))
                continue;
            if (item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place)
            {
                issues.Add(new(
                    DesktopWorkspaceEquivalenceIssueCode.PreservePolicyViolated,
                    $"Preserve 显示器 {item.SourceDisplayRef} 上的项目 {item.StableId} 被迁移。",
                    item.SourceDisplayRef,
                    item.ZoneKey,
                    item.StableId));
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

    private static bool SameKeys(IEnumerable<string> first, IEnumerable<string> second)
        => first.ToHashSet(StringComparer.Ordinal).SetEquals(second);

    private static string ComputeCertificateFingerprint(
        DesktopWorkspaceBlueprint blueprint,
        ExplorerWorkspaceGeometry geometry,
        CompiledWorkspacePlan plan,
        IEnumerable<WorkspaceGeometryRealizationProof> zoneProofs,
        IEnumerable<WorkspaceGeometryRealizationProof> reservedProofs)
    {
        var builder = new StringBuilder();
        builder.Append("workspace-certificate-v1|")
            .Append(blueprint.BlueprintId.ToString("N"))
            .Append('|').Append(blueprint.Revision)
            .Append('|').Append(geometry.GeometryFingerprint)
            .Append('|').Append(plan.PlanFingerprint)
            .AppendLine();

        foreach (var proof in zoneProofs.OrderBy(static proof => proof.Key, StringComparer.Ordinal))
            AppendProof(builder, "z", proof);
        foreach (var proof in reservedProofs.OrderBy(static proof => proof.Key, StringComparer.Ordinal))
            AppendProof(builder, "r", proof);

        return "workspace-certificate-v1:" +
               Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendProof(StringBuilder builder, string kind, WorkspaceGeometryRealizationProof proof)
    {
        builder.Append(kind).Append('|').Append(proof.Key).Append('|').Append(proof.DisplayRef).Append('|')
            .Append(proof.NativeRect.FirstColumn).Append('|').Append(proof.NativeRect.FirstRow).Append('|')
            .Append(proof.NativeRect.ColumnCount).Append('|').Append(proof.NativeRect.RowCount).AppendLine();
    }
}
