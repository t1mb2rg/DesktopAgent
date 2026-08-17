using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

public enum DesktopDisplayLoadPreference
{
    Unspecified = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// 只给 AI / 产品编排层使用的语义显示器引用。
/// DisplayRef 是 request/session-local 语义键，不是 Windows DeviceName。
/// </summary>
public sealed record DesktopSemanticDisplayContext
{
    public string DisplayRef { get; }
    public bool IsPrimary { get; }
    public int CurrentItemCount { get; }

    public DesktopSemanticDisplayContext(string displayRef, bool isPrimary, int currentItemCount)
    {
        if (string.IsNullOrWhiteSpace(displayRef))
            throw new ArgumentException("DisplayRef 不能为空。", nameof(displayRef));
        if (currentItemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(currentItemCount));

        DisplayRef = displayRef.Trim();
        IsPrimary = isPrimary;
        CurrentItemCount = currentItemCount;
    }
}

/// <summary>
/// 后续对话只需要紧凑项目目录，不再把完整 DesktopItemFacts 重新做一次全量理解。
/// StableId 仅用于 Provider 返回后的本地映射；Provider 实际只看到临时 item_ref。
/// </summary>
public sealed record DesktopLayoutConversationItemContext
{
    public string StableId { get; }
    public string DisplayName { get; }
    public string CategoryKey { get; }
    public string CurrentDisplayRef { get; }

    public DesktopLayoutConversationItemContext(
        string stableId,
        string displayName,
        string categoryKey,
        string currentDisplayRef)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("DisplayName 不能为空。", nameof(displayName));
        if (string.IsNullOrWhiteSpace(categoryKey))
            throw new ArgumentException("CategoryKey 不能为空。", nameof(categoryKey));
        if (string.IsNullOrWhiteSpace(currentDisplayRef))
            throw new ArgumentException("CurrentDisplayRef 不能为空。", nameof(currentDisplayRef));

        StableId = stableId;
        DisplayName = displayName.Trim();
        CategoryKey = categoryKey.Trim();
        CurrentDisplayRef = currentDisplayRef.Trim();
    }
}

/// <summary>
/// 负空间是一等布局约束：这里明确不要放桌面项目。
/// AI 只能给语义位置/大小，本地 Compiler 决定 NormalizedRect。
/// </summary>
public sealed record DesktopReservedRegionConstraint
{
    public string Key { get; }
    public string DisplayRef { get; }
    public DesktopZonePlacementHint PlacementHint { get; }
    public DesktopZoneSizeHint SizeHint { get; }
    public string Purpose { get; }

    public DesktopReservedRegionConstraint(
        string key,
        string displayRef,
        DesktopZonePlacementHint placementHint,
        DesktopZoneSizeHint sizeHint,
        string purpose)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key 不能为空。", nameof(key));
        if (string.IsNullOrWhiteSpace(displayRef))
            throw new ArgumentException("DisplayRef 不能为空。", nameof(displayRef));
        if (!Enum.IsDefined(placementHint))
            throw new ArgumentException("PlacementHint 未定义。", nameof(placementHint));
        if (!Enum.IsDefined(sizeHint))
            throw new ArgumentException("SizeHint 未定义。", nameof(sizeHint));
        if (string.IsNullOrWhiteSpace(purpose))
            throw new ArgumentException("Purpose 不能为空。", nameof(purpose));

        Key = key.Trim();
        DisplayRef = displayRef.Trim();
        PlacementHint = placementHint;
        SizeHint = sizeHint;
        Purpose = purpose.Trim();
    }
}

/// <summary>
/// 对话式布局的累计状态。它不是第二份 DesktopUnderstandingDraft：
/// 内容语义继续来自首次完整理解；这里只保存用户后来对“怎么摆”的修正。
/// </summary>
public sealed record DesktopLayoutConversationState
{
    public string IntentSummary { get; }
    public IReadOnlyDictionary<string, string> CategoryDisplayTargets { get; }
    public IReadOnlyDictionary<string, string> ItemDisplayTargets { get; }
    public IReadOnlyDictionary<string, DesktopDisplayLoadPreference> DisplayLoadPreferences { get; }
    public IReadOnlyList<DesktopReservedRegionConstraint> ReservedRegions { get; }

    public static DesktopLayoutConversationState Empty { get; } = new(
        string.Empty,
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, DesktopDisplayLoadPreference>(StringComparer.Ordinal),
        Array.Empty<DesktopReservedRegionConstraint>());

    public DesktopLayoutConversationState(
        string? intentSummary,
        IReadOnlyDictionary<string, string>? categoryDisplayTargets,
        IReadOnlyDictionary<string, string>? itemDisplayTargets,
        IReadOnlyDictionary<string, DesktopDisplayLoadPreference>? displayLoadPreferences,
        IReadOnlyList<DesktopReservedRegionConstraint>? reservedRegions)
    {
        IntentSummary = (intentSummary ?? string.Empty).Trim();
        CategoryDisplayTargets = CopyTargets(categoryDisplayTargets, nameof(categoryDisplayTargets));
        ItemDisplayTargets = CopyTargets(itemDisplayTargets, nameof(itemDisplayTargets));
        DisplayLoadPreferences = CopyLoads(displayLoadPreferences);
        ReservedRegions = CopyReserved(reservedRegions);
    }

    public DesktopLayoutConversationState Apply(DesktopLayoutConversationDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var categoryTargets = CategoryDisplayTargets.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var pair in delta.CategoryDisplayTargets)
            categoryTargets[pair.Key] = pair.Value;

        var itemTargets = ItemDisplayTargets.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var pair in delta.ItemDisplayTargets)
            itemTargets[pair.Key] = pair.Value;

        var loads = DisplayLoadPreferences.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var pair in delta.DisplayLoadPreferences)
            loads[pair.Key] = pair.Value;

        var reserved = ReservedRegions
            .Where(region => !delta.RemoveReservedRegionKeys.Contains(region.Key, StringComparer.Ordinal))
            .ToDictionary(region => region.Key, StringComparer.Ordinal);
        foreach (var region in delta.UpsertReservedRegions)
            reserved[region.Key] = region;

        return new DesktopLayoutConversationState(
            delta.IntentSummary,
            categoryTargets,
            itemTargets,
            loads,
            reserved.Values.OrderBy(region => region.Key, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyDictionary<string, string> CopyTargets(
        IReadOnlyDictionary<string, string>? source,
        string parameterName)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is null)
            return result;

        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                throw new ArgumentException("目标映射不能包含空 Key/Value。", parameterName);
            result.Add(pair.Key.Trim(), pair.Value.Trim());
        }
        return result;
    }

    private static IReadOnlyDictionary<string, DesktopDisplayLoadPreference> CopyLoads(
        IReadOnlyDictionary<string, DesktopDisplayLoadPreference>? source)
    {
        var result = new Dictionary<string, DesktopDisplayLoadPreference>(StringComparer.Ordinal);
        if (source is null)
            return result;

        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new ArgumentException("DisplayLoadPreferences 不能包含空 DisplayRef。", nameof(source));
            if (!Enum.IsDefined(pair.Value))
                throw new ArgumentException("DisplayLoadPreference 未定义。", nameof(source));
            result.Add(pair.Key.Trim(), pair.Value);
        }
        return result;
    }

    private static IReadOnlyList<DesktopReservedRegionConstraint> CopyReserved(
        IReadOnlyList<DesktopReservedRegionConstraint>? source)
    {
        if (source is null || source.Count == 0)
            return Array.Empty<DesktopReservedRegionConstraint>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in source)
        {
            ArgumentNullException.ThrowIfNull(region);
            if (!seen.Add(region.Key))
                throw new ArgumentException($"ReservedRegion Key \"{region.Key}\" 重复。", nameof(source));
        }
        return Array.AsReadOnly(source.ToArray());
    }
}

/// <summary>
/// 一轮轻量 AI 对话只返回变化，不重新生成全部 123 个 Assignment。
/// </summary>
public sealed record DesktopLayoutConversationDelta
{
    public string IntentSummary { get; }
    public IReadOnlyDictionary<string, string> CategoryDisplayTargets { get; }
    public IReadOnlyDictionary<string, string> ItemDisplayTargets { get; }
    public IReadOnlyDictionary<string, DesktopDisplayLoadPreference> DisplayLoadPreferences { get; }
    public IReadOnlyList<DesktopReservedRegionConstraint> UpsertReservedRegions { get; }
    public IReadOnlyList<string> RemoveReservedRegionKeys { get; }

    public DesktopLayoutConversationDelta(
        string intentSummary,
        IReadOnlyDictionary<string, string>? categoryDisplayTargets = null,
        IReadOnlyDictionary<string, string>? itemDisplayTargets = null,
        IReadOnlyDictionary<string, DesktopDisplayLoadPreference>? displayLoadPreferences = null,
        IReadOnlyList<DesktopReservedRegionConstraint>? upsertReservedRegions = null,
        IReadOnlyList<string>? removeReservedRegionKeys = null)
    {
        if (string.IsNullOrWhiteSpace(intentSummary))
            throw new ArgumentException("IntentSummary 不能为空。", nameof(intentSummary));

        IntentSummary = intentSummary.Trim();
        CategoryDisplayTargets = CopyStringMap(categoryDisplayTargets);
        ItemDisplayTargets = CopyStringMap(itemDisplayTargets);
        DisplayLoadPreferences = CopyLoadMap(displayLoadPreferences);
        UpsertReservedRegions = Array.AsReadOnly((upsertReservedRegions ?? []).ToArray());
        RemoveReservedRegionKeys = Array.AsReadOnly((removeReservedRegionKeys ?? []).ToArray());
    }

    private static IReadOnlyDictionary<string, string> CopyStringMap(
        IReadOnlyDictionary<string, string>? source)
        => source is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : source.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, DesktopDisplayLoadPreference> CopyLoadMap(
        IReadOnlyDictionary<string, DesktopDisplayLoadPreference>? source)
        => source is null
            ? new Dictionary<string, DesktopDisplayLoadPreference>(StringComparer.Ordinal)
            : source.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
}

public sealed record DesktopLayoutConversationRequest
{
    public DesktopUnderstandingDraft SemanticUnderstanding { get; }
    public IReadOnlyList<DesktopSemanticDisplayContext> Displays { get; }
    public IReadOnlyList<DesktopLayoutConversationItemContext> Items { get; }
    public DesktopLayoutConversationState CurrentState { get; }
    public string LatestUserGuidance { get; }

    public DesktopLayoutConversationRequest(
        DesktopUnderstandingDraft semanticUnderstanding,
        IReadOnlyList<DesktopSemanticDisplayContext> displays,
        IReadOnlyList<DesktopLayoutConversationItemContext> items,
        DesktopLayoutConversationState currentState,
        string latestUserGuidance)
    {
        SemanticUnderstanding = semanticUnderstanding ?? throw new ArgumentNullException(nameof(semanticUnderstanding));
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(items);
        CurrentState = currentState ?? throw new ArgumentNullException(nameof(currentState));
        if (string.IsNullOrWhiteSpace(latestUserGuidance))
            throw new ArgumentException("LatestUserGuidance 不能为空。", nameof(latestUserGuidance));
        if (displays.Count == 0)
            throw new ArgumentException("至少需要一块语义显示器。", nameof(displays));

        Displays = Array.AsReadOnly(displays.ToArray());
        Items = Array.AsReadOnly(items.ToArray());
        LatestUserGuidance = latestUserGuidance.Trim();
    }
}
