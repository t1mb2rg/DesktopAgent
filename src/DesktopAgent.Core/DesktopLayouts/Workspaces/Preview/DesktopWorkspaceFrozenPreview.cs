using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;

/// <summary>
/// Frozen Preview 的本地展示 sidecar。DisplayName 只服务本地 UI，StableId 不会被发送给 Workspace Designer。
/// </summary>
public sealed record DesktopWorkspacePreviewItemPresentation
{
    public string StableId { get; }
    public string DisplayName { get; }

    public DesktopWorkspacePreviewItemPresentation(string stableId, string displayName)
    {
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("DisplayName 不能为空。", nameof(displayName));

        StableId = stableId;
        DisplayName = displayName.Trim();
    }
}

/// <summary>
/// Frozen Preview 的源侧本地事实快照。ObservationId 把用户看到的起点位置与同一次 Explorer Geometry 绑定，
/// 防止拿旧 item positions 与新 geometry 拼成伪造 Preview。
/// </summary>
public sealed record DesktopWorkspacePreviewSourceSnapshot
{
    private readonly IReadOnlyList<DesktopWorkspaceCompilationItemFact> _itemFacts;
    private readonly IReadOnlyList<DesktopWorkspacePreviewItemPresentation> _itemPresentations;

    public Guid ObservationId { get; }
    public IReadOnlyList<DesktopWorkspaceCompilationItemFact> ItemFacts => _itemFacts;
    public IReadOnlyList<DesktopWorkspacePreviewItemPresentation> ItemPresentations => _itemPresentations;

    public DesktopWorkspacePreviewSourceSnapshot(
        Guid observationId,
        IEnumerable<DesktopWorkspaceCompilationItemFact> itemFacts,
        IEnumerable<DesktopWorkspacePreviewItemPresentation> itemPresentations)
    {
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为 Guid.Empty。", nameof(observationId));
        ArgumentNullException.ThrowIfNull(itemFacts);
        ArgumentNullException.ThrowIfNull(itemPresentations);

        var factArray = itemFacts.ToArray();
        var presentationArray = itemPresentations.ToArray();
        RejectNullElements(factArray, nameof(itemFacts));
        RejectNullElements(presentationArray, nameof(itemPresentations));
        EnsureUnique(factArray.Select(static item => item.StableId), nameof(itemFacts));
        EnsureUnique(presentationArray.Select(static item => item.StableId), nameof(itemPresentations));

        var factIds = factArray.Select(static item => item.StableId).ToHashSet(StringComparer.Ordinal);
        var presentationIds = presentationArray.Select(static item => item.StableId).ToHashSet(StringComparer.Ordinal);
        if (!factIds.SetEquals(presentationIds))
            throw new ArgumentException("ItemPresentations 必须完整覆盖源 ItemFacts。", nameof(itemPresentations));

        ObservationId = observationId;
        _itemFacts = Array.AsReadOnly(factArray);
        _itemPresentations = Array.AsReadOnly(presentationArray);
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

/// <summary>
/// 整个多屏桌面拓扑中的归一化显示器矩形。它只用于 Preview 画布，不参与 Blueprint 设计或 Compiler 决策。
/// </summary>
public readonly record struct DesktopWorkspacePreviewNormalizedRect
{
    private const double Epsilon = 1e-9;

    public double Left { get; }
    public double Top { get; }
    public double Width { get; }
    public double Height { get; }

    public DesktopWorkspacePreviewNormalizedRect(double left, double top, double width, double height)
    {
        RequireFinite(left, nameof(left));
        RequireFinite(top, nameof(top));
        RequireFinite(width, nameof(width));
        RequireFinite(height, nameof(height));

        var right = left + width;
        var bottom = top + height;
        if (left < -Epsilon || left > 1d + Epsilon)
            throw new ArgumentOutOfRangeException(nameof(left));
        if (top < -Epsilon || top > 1d + Epsilon)
            throw new ArgumentOutOfRangeException(nameof(top));
        if (width <= 0d || right > 1d + Epsilon)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0d || bottom > 1d + Epsilon)
            throw new ArgumentOutOfRangeException(nameof(height));

        var normalizedLeft = Math.Clamp(left, 0d, 1d);
        var normalizedTop = Math.Clamp(top, 0d, 1d);
        var normalizedRight = Math.Clamp(right, 0d, 1d);
        var normalizedBottom = Math.Clamp(bottom, 0d, 1d);
        if (normalizedRight <= normalizedLeft || normalizedBottom <= normalizedTop)
            throw new ArgumentException("Preview 拓扑矩形必须具有正面积。");

        Left = normalizedLeft;
        Top = normalizedTop;
        Width = normalizedRight - normalizedLeft;
        Height = normalizedBottom - normalizedTop;
    }

    private static void RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>Frozen Preview 中用户实际看到的一项桌面项目起点与精确终点。</summary>
public sealed record DesktopWorkspaceFrozenPreviewItem
{
    public string StableId { get; }
    public string DisplayName { get; }
    public string CategoryKey { get; }
    public string SourceDisplayRef { get; }
    public PixelPoint SourcePixelPosition { get; }
    public bool IsMovable { get; }
    public DesktopWorkspaceCompiledItemDisposition Disposition { get; }
    public string? ZoneKey { get; }
    public string? TargetDisplayRef { get; }
    public string? TargetSlotId { get; }
    public PixelPoint? TargetPixelPosition { get; }

    public bool IsCrossDisplayMove =>
        Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
        !string.Equals(SourceDisplayRef, TargetDisplayRef, StringComparison.Ordinal);

    public bool IsPositionChange =>
        Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
        TargetPixelPosition.HasValue &&
        (IsCrossDisplayMove || SourcePixelPosition != TargetPixelPosition.Value);

    internal DesktopWorkspaceFrozenPreviewItem(
        DesktopWorkspaceCompilationItemFact fact,
        DesktopWorkspacePreviewItemPresentation presentation,
        CompiledWorkspaceItem compiled)
    {
        StableId = fact.StableId;
        DisplayName = presentation.DisplayName;
        CategoryKey = compiled.CategoryKey;
        SourceDisplayRef = fact.CurrentDisplayRef;
        SourcePixelPosition = fact.PixelPosition;
        IsMovable = fact.IsMovable;
        Disposition = compiled.Disposition;
        ZoneKey = compiled.ZoneKey;
        TargetDisplayRef = compiled.TargetDisplayRef;
        TargetSlotId = compiled.TargetSlotId;
        TargetPixelPosition = compiled.TargetPixelPosition;
    }
}

/// <summary>Frozen Preview 中一块显示器的本地可视化表面。</summary>
public sealed record DesktopWorkspaceFrozenPreviewDisplay
{
    private readonly IReadOnlyList<CompiledWorkspaceZone> _zones;
    private readonly IReadOnlyList<CompiledWorkspaceReservedRegion> _reservedRegions;

    public string DisplayRef { get; }

    /// <summary>
    /// 物理显示器名（例如 \\.\DISPLAY1）。
    ///
    /// DisplayRef 是 Blueprint/编译契约里的逻辑引用（primary、secondary_1…），
    /// 跨机器可读；DeviceName 才是这台机器上的一张真实显示器。
    /// 站定授权之类的记录绑定到具体设备，必须用 DeviceName。
    /// </summary>
    public string DeviceName { get; }
    public bool IsPrimary { get; }
    public DesktopWorkspaceDisplayPolicyKind Policy { get; }
    public DesktopWorkspacePreviewNormalizedRect TopologyBounds { get; }
    public PixelRect PixelBounds { get; }
    public PixelRect WorkArea { get; }
    public IReadOnlyList<CompiledWorkspaceZone> Zones => _zones;
    public IReadOnlyList<CompiledWorkspaceReservedRegion> ReservedRegions => _reservedRegions;
    public int SourceItemCount { get; }
    public int TargetPlacementCount { get; }
    public int InboundCount { get; }
    public int OutboundCount { get; }
    public int KeepCount { get; }
    public int ExcludeCount { get; }
    public int MoveCount { get; }
    public int AlreadyAtTargetCount { get; }

    internal DesktopWorkspaceFrozenPreviewDisplay(
        ExplorerDisplayGeometry geometry,
        DesktopWorkspaceDisplayPolicyKind policy,
        DesktopWorkspacePreviewNormalizedRect topologyBounds,
        IEnumerable<CompiledWorkspaceZone> zones,
        IEnumerable<CompiledWorkspaceReservedRegion> reservedRegions,
        IReadOnlyList<DesktopWorkspaceFrozenPreviewItem> items)
    {
        var zoneArray = zones.ToArray();
        var reservedArray = reservedRegions.ToArray();

        DisplayRef = geometry.DisplayRef;
        DeviceName = geometry.DeviceName;
        IsPrimary = geometry.IsPrimary;
        Policy = policy;
        TopologyBounds = topologyBounds;
        PixelBounds = geometry.Bounds;
        WorkArea = geometry.WorkArea;
        _zones = Array.AsReadOnly(zoneArray);
        _reservedRegions = Array.AsReadOnly(reservedArray);

        SourceItemCount = items.Count(item =>
            string.Equals(item.SourceDisplayRef, DisplayRef, StringComparison.Ordinal));
        TargetPlacementCount = items.Count(item =>
            item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
            string.Equals(item.TargetDisplayRef, DisplayRef, StringComparison.Ordinal));
        InboundCount = items.Count(item =>
            item.IsCrossDisplayMove &&
            string.Equals(item.TargetDisplayRef, DisplayRef, StringComparison.Ordinal));
        OutboundCount = items.Count(item =>
            item.IsCrossDisplayMove &&
            string.Equals(item.SourceDisplayRef, DisplayRef, StringComparison.Ordinal));
        KeepCount = items.Count(item =>
            item.Disposition == DesktopWorkspaceCompiledItemDisposition.Keep &&
            string.Equals(item.SourceDisplayRef, DisplayRef, StringComparison.Ordinal));
        ExcludeCount = items.Count(item =>
            item.Disposition == DesktopWorkspaceCompiledItemDisposition.Exclude &&
            string.Equals(item.SourceDisplayRef, DisplayRef, StringComparison.Ordinal));
        MoveCount = items.Count(item =>
            item.IsPositionChange &&
            string.Equals(item.TargetDisplayRef, DisplayRef, StringComparison.Ordinal));
        AlreadyAtTargetCount = items.Count(item =>
            item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
            !item.IsPositionChange &&
            string.Equals(item.TargetDisplayRef, DisplayRef, StringComparison.Ordinal));
    }
}

/// <summary>
/// ADR-0005 Frozen Workspace Preview。
///
/// 它冻结并绑定：immutable Blueprint revision、当前 Observation / Explorer geometry identity、
/// 用户可见 source-state snapshot、exact CompiledWorkspacePlan，以及独立 semantic-equivalence Certificate。
///
/// Preview 只是一份“用户看到了什么”的只读签名对象。它不是 Confirmed Candidate，
/// 不授予 Execution / Recovery / Writer authority。
/// </summary>
public sealed record DesktopWorkspaceFrozenPreview
{
    private readonly IReadOnlyList<DesktopWorkspaceFrozenPreviewDisplay> _displays;
    private readonly IReadOnlyList<DesktopWorkspaceFrozenPreviewItem> _items;

    public Guid PreviewId { get; }
    public string PreviewFingerprint { get; }
    public string SourceStateFingerprint { get; }
    public Guid SourceObservationId { get; }
    public DesktopWorkspaceBlueprint Blueprint { get; }
    public CompiledWorkspacePlan ExactPlan { get; }
    public WorkspaceRealizationCertificate RealizationCertificate { get; }
    public IReadOnlyList<DesktopWorkspaceFrozenPreviewDisplay> Displays => _displays;
    public IReadOnlyList<DesktopWorkspaceFrozenPreviewItem> Items => _items;
    public int CrossDisplayAssignmentCount { get; }
    public int MoveCount { get; }
    public int KeepCount { get; }
    public int ExcludeCount { get; }

    internal DesktopWorkspaceFrozenPreview(
        string previewFingerprint,
        string sourceStateFingerprint,
        Guid sourceObservationId,
        DesktopWorkspaceBlueprint blueprint,
        CompiledWorkspacePlan exactPlan,
        WorkspaceRealizationCertificate realizationCertificate,
        IEnumerable<DesktopWorkspaceFrozenPreviewDisplay> displays,
        IEnumerable<DesktopWorkspaceFrozenPreviewItem> items)
    {
        if (string.IsNullOrWhiteSpace(previewFingerprint))
            throw new ArgumentException("PreviewFingerprint 不能为空。", nameof(previewFingerprint));
        if (string.IsNullOrWhiteSpace(sourceStateFingerprint))
            throw new ArgumentException("SourceStateFingerprint 不能为空。", nameof(sourceStateFingerprint));
        if (sourceObservationId == Guid.Empty)
            throw new ArgumentException("SourceObservationId 不能为 Guid.Empty。", nameof(sourceObservationId));
        Blueprint = blueprint ?? throw new ArgumentNullException(nameof(blueprint));
        ExactPlan = exactPlan ?? throw new ArgumentNullException(nameof(exactPlan));
        RealizationCertificate = realizationCertificate ?? throw new ArgumentNullException(nameof(realizationCertificate));
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(items);

        var displayArray = displays.ToArray();
        var itemArray = items.ToArray();
        if (displayArray.Length == 0)
            throw new ArgumentException("Frozen Preview 至少需要一块显示器。", nameof(displays));
        if (displayArray.Any(static display => display is null))
            throw new ArgumentException("Displays 不能包含 null。", nameof(displays));
        if (itemArray.Any(static item => item is null))
            throw new ArgumentException("Items 不能包含 null。", nameof(items));

        PreviewId = Guid.NewGuid();
        PreviewFingerprint = previewFingerprint.Trim();
        SourceStateFingerprint = sourceStateFingerprint.Trim();
        SourceObservationId = sourceObservationId;
        _displays = Array.AsReadOnly(displayArray);
        _items = Array.AsReadOnly(itemArray);
        CrossDisplayAssignmentCount = itemArray.Count(static item => item.IsCrossDisplayMove);
        MoveCount = itemArray.Count(static item => item.IsPositionChange);
        KeepCount = itemArray.Count(static item => item.Disposition == DesktopWorkspaceCompiledItemDisposition.Keep);
        ExcludeCount = itemArray.Count(static item => item.Disposition == DesktopWorkspaceCompiledItemDisposition.Exclude);
    }
}

public static class DesktopWorkspaceFrozenPreviewFactory
{
    /// <summary>
    /// 只有已经通过独立 semantic-equivalence 的 exact plan 才能被冻结成 Preview。
    /// Factory 会再次复算 Certificate 并与传入 Certificate 对齐，拒绝伪造、陈旧证明或跨 Observation 拼接。
    /// </summary>
    public static DesktopWorkspaceFrozenPreview Create(
        DesktopWorkspaceBlueprint blueprint,
        ExplorerWorkspaceGeometry geometry,
        CompiledWorkspacePlan exactPlan,
        WorkspaceRealizationCertificate certificate,
        DesktopWorkspacePreviewSourceSnapshot sourceSnapshot,
        IEnumerable<DesktopWorkspaceCompilationItemRefBinding>? itemRefBindings = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(exactPlan);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(sourceSnapshot);

        if (sourceSnapshot.ObservationId != geometry.ObservationId ||
            sourceSnapshot.ObservationId != exactPlan.ObservationId)
        {
            throw new ArgumentException(
                "Frozen Preview 的 source snapshot / geometry / exact plan 必须绑定同一次 Observation。",
                nameof(sourceSnapshot));
        }

        var factArray = sourceSnapshot.ItemFacts.ToArray();
        var presentationArray = sourceSnapshot.ItemPresentations.ToArray();
        var bindingArray = itemRefBindings?.ToArray() ?? [];
        RejectNullElements(bindingArray, nameof(itemRefBindings));

        var planStableIds = exactPlan.Items
            .Select(static item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var factStableIds = factArray
            .Select(static item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        if (!planStableIds.SetEquals(factStableIds))
            throw new ArgumentException("SourceSnapshot 必须完整覆盖 ExactPlan 项目集合。", nameof(sourceSnapshot));

        var equivalence = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            blueprint,
            geometry,
            exactPlan,
            factArray,
            bindingArray);
        if (!equivalence.Equivalent)
            throw new ArgumentException("Frozen Preview 只能从 Blueprint-equivalent ExactPlan 创建。", nameof(exactPlan));

        var recomputedCertificate = equivalence.Certificate!;
        if (!CertificateMatches(certificate, recomputedCertificate))
            throw new ArgumentException("Realization Certificate 与当前 Blueprint / Geometry / ExactPlan 不一致。", nameof(certificate));

        var factsById = factArray.ToDictionary(static item => item.StableId, StringComparer.Ordinal);
        var presentationById = presentationArray.ToDictionary(static item => item.StableId, StringComparer.Ordinal);
        var items = exactPlan.Items
            .OrderBy(static item => item.StableId, StringComparer.Ordinal)
            .Select(item => new DesktopWorkspaceFrozenPreviewItem(
                factsById[item.StableId],
                presentationById[item.StableId],
                item))
            .ToArray();

        var policies = blueprint.DisplayPolicies.ToDictionary(
            static policy => policy.DisplayRef,
            static policy => policy.Policy,
            StringComparer.Ordinal);
        if (geometry.Displays.Any(display => !policies.ContainsKey(display.DisplayRef)))
            throw new ArgumentException("Blueprint 没有完整覆盖当前 Preview 显示器 policy。", nameof(blueprint));

        var topology = BuildTopologyBounds(geometry.Displays);
        var displays = geometry.Displays
            .OrderBy(static display => display.IsPrimary ? 0 : 1)
            .ThenBy(static display => display.DisplayRef, StringComparer.Ordinal)
            .Select(display => new DesktopWorkspaceFrozenPreviewDisplay(
                display,
                policies[display.DisplayRef],
                topology[display.DisplayRef],
                exactPlan.Zones.Where(zone => string.Equals(zone.DisplayRef, display.DisplayRef, StringComparison.Ordinal)),
                exactPlan.ReservedRegions.Where(region => string.Equals(region.DisplayRef, display.DisplayRef, StringComparison.Ordinal)),
                items))
            .ToArray();

        var sourceStateFingerprint = ComputeSourceStateFingerprint(factArray);
        var previewFingerprint = ComputePreviewFingerprint(
            blueprint,
            geometry,
            exactPlan,
            recomputedCertificate,
            sourceStateFingerprint,
            presentationArray);

        return new DesktopWorkspaceFrozenPreview(
            previewFingerprint,
            sourceStateFingerprint,
            sourceSnapshot.ObservationId,
            blueprint,
            exactPlan,
            recomputedCertificate,
            displays,
            items);
    }

    private static bool CertificateMatches(
        WorkspaceRealizationCertificate actual,
        WorkspaceRealizationCertificate expected)
        => actual.BlueprintId == expected.BlueprintId &&
           actual.BlueprintRevision == expected.BlueprintRevision &&
           string.Equals(actual.GeometryFingerprint, expected.GeometryFingerprint, StringComparison.Ordinal) &&
           string.Equals(actual.PlanFingerprint, expected.PlanFingerprint, StringComparison.Ordinal) &&
           string.Equals(actual.CertificateFingerprint, expected.CertificateFingerprint, StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, DesktopWorkspacePreviewNormalizedRect> BuildTopologyBounds(
        IReadOnlyList<ExplorerDisplayGeometry> displays)
    {
        var left = displays.Min(static display => display.Bounds.Left);
        var top = displays.Min(static display => display.Bounds.Top);
        var right = displays.Max(static display => display.Bounds.Right);
        var bottom = displays.Max(static display => display.Bounds.Bottom);
        var width = (long)right - left;
        var height = (long)bottom - top;
        if (width <= 0 || height <= 0)
            throw new ArgumentException("显示器拓扑必须具有正面积。", nameof(displays));

        return displays.ToDictionary(
            static display => display.DisplayRef,
            display => new DesktopWorkspacePreviewNormalizedRect(
                ((long)display.Bounds.Left - left) / (double)width,
                ((long)display.Bounds.Top - top) / (double)height,
                ((long)display.Bounds.Right - display.Bounds.Left) / (double)width,
                ((long)display.Bounds.Bottom - display.Bounds.Top) / (double)height),
            StringComparer.Ordinal);
    }

    private static string ComputeSourceStateFingerprint(
        IEnumerable<DesktopWorkspaceCompilationItemFact> itemFacts)
    {
        var builder = new StringBuilder("workspace-preview-source-v1\n");
        foreach (var fact in itemFacts.OrderBy(static item => item.StableId, StringComparer.Ordinal))
        {
            builder.Append(fact.StableId).Append('|')
                .Append(fact.CurrentDisplayRef).Append('|')
                .Append(fact.PixelPosition.X).Append('|')
                .Append(fact.PixelPosition.Y).Append('|')
                .Append(fact.IsMovable ? '1' : '0')
                .AppendLine();
        }

        return "workspace-preview-source-v1:" +
               Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string ComputePreviewFingerprint(
        DesktopWorkspaceBlueprint blueprint,
        ExplorerWorkspaceGeometry geometry,
        CompiledWorkspacePlan exactPlan,
        WorkspaceRealizationCertificate certificate,
        string sourceStateFingerprint,
        IEnumerable<DesktopWorkspacePreviewItemPresentation> presentations)
    {
        var builder = new StringBuilder("workspace-preview-v1|")
            .Append(blueprint.BlueprintId.ToString("N")).Append('|')
            .Append(blueprint.Revision).Append('|')
            .Append(geometry.ObservationId.ToString("N")).Append('|')
            .Append(geometry.GeometryFingerprint).Append('|')
            .Append(exactPlan.PlanFingerprint).Append('|')
            .Append(certificate.CertificateFingerprint).Append('|')
            .Append(sourceStateFingerprint)
            .AppendLine();

        AppendBlueprintMaterial(builder, blueprint);
        foreach (var presentation in presentations.OrderBy(static item => item.StableId, StringComparer.Ordinal))
        {
            builder.Append("p|")
                .Append(presentation.StableId).Append('|')
                .Append(presentation.DisplayName)
                .AppendLine();
        }

        return "workspace-preview-v1:" +
               Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendBlueprintMaterial(StringBuilder builder, DesktopWorkspaceBlueprint blueprint)
    {
        builder.Append("summary|").Append(blueprint.DesignSummary).AppendLine();
        foreach (var policy in blueprint.DisplayPolicies.OrderBy(static item => item.DisplayRef, StringComparer.Ordinal))
            builder.Append("dp|").Append(policy.DisplayRef).Append('|').Append((int)policy.Policy).AppendLine();
        foreach (var zone in blueprint.Zones.OrderBy(static item => item.ZoneKey, StringComparer.Ordinal))
        {
            builder.Append("z|").Append(zone.ZoneKey).Append('|').Append(zone.Name).Append('|').Append(zone.DisplayRef).Append('|')
                .Append(zone.Bounds.Left).Append('|').Append(zone.Bounds.Top).Append('|')
                .Append(zone.Bounds.Width).Append('|').Append(zone.Bounds.Height).Append('|')
                .Append((int)zone.Anchor).Append('|').Append((int)zone.PackingOrder).AppendLine();
        }
        foreach (var relation in blueprint.ZoneRelations
                     .OrderBy(static item => item.ZoneKey, StringComparer.Ordinal)
                     .ThenBy(static item => item.Relation)
                     .ThenBy(static item => item.OtherZoneKey, StringComparer.Ordinal))
        {
            builder.Append("rel|").Append(relation.ZoneKey).Append('|').Append((int)relation.Relation).Append('|')
                .Append(relation.OtherZoneKey).AppendLine();
        }
        foreach (var rule in blueprint.CategoryRules.OrderBy(static item => item.CategoryKey, StringComparer.Ordinal))
            builder.Append("cr|").Append(rule.CategoryKey).Append('|').Append(rule.ZoneKey).AppendLine();
        foreach (var itemOverride in blueprint.ItemOverrides.OrderBy(static item => item.ItemRef, StringComparer.Ordinal))
        {
            builder.Append("io|").Append(itemOverride.ItemRef).Append('|').Append((int)itemOverride.Kind).Append('|')
                .Append(itemOverride.ZoneKey ?? string.Empty).Append('|')
                .Append(itemOverride.Anchor.HasValue ? ((int)itemOverride.Anchor.Value).ToString() : string.Empty)
                .AppendLine();
        }
        foreach (var region in blueprint.ReservedRegions.OrderBy(static item => item.RegionKey, StringComparer.Ordinal))
        {
            builder.Append("rr|").Append(region.RegionKey).Append('|').Append(region.Purpose).Append('|').Append(region.DisplayRef).Append('|')
                .Append(region.Bounds.Left).Append('|').Append(region.Bounds.Top).Append('|')
                .Append(region.Bounds.Width).Append('|').Append(region.Bounds.Height).AppendLine();
        }
    }

    private static void RejectNullElements<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        if (values.Any(static value => value is null))
            throw new ArgumentException("集合不能包含 null。", parameterName);
    }
}
