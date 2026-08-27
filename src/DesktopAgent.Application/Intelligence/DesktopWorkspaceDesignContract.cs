using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 本地为一次 Workspace Blueprint 设计分配的不可变 revision 身份。
/// AI 拥有设计内容，但 BlueprintId / Revision 链由本地产品状态机发号和校验。
/// </summary>
public sealed record DesktopWorkspaceDesignRevisionTarget
{
    public Guid BlueprintId { get; }
    public int Revision { get; }
    public int? ParentRevision { get; }

    public DesktopWorkspaceDesignRevisionTarget(
        Guid blueprintId,
        int revision,
        int? parentRevision = null)
    {
        if (blueprintId == Guid.Empty)
            throw new ArgumentException("BlueprintId 不能为 Guid.Empty。", nameof(blueprintId));
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));

        if (revision == 1)
        {
            if (parentRevision is not null)
                throw new ArgumentException("Revision 1 不能声明 ParentRevision。", nameof(parentRevision));
        }
        else if (!parentRevision.HasValue || parentRevision.Value <= 0 || parentRevision.Value >= revision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parentRevision),
                "Revision > 1 时必须提供大于 0 且小于当前 Revision 的 ParentRevision。");
        }

        BlueprintId = blueprintId;
        Revision = revision;
        ParentRevision = parentRevision;
    }
}

/// <summary>
/// Provider-safe 显示器拓扑描述。DisplayRef / Label 是本次设计请求中的语义引用，
/// RelativeBounds 是归一化 Workspace 拓扑，不包含 Windows DeviceName 或物理像素。
/// </summary>
public sealed record DesktopWorkspaceDesignDisplay
{
    public string DisplayRef { get; }
    public string Label { get; }
    public bool IsPrimary { get; }
    public DesktopWorkspaceRelativeRect RelativeBounds { get; }
    public double AspectRatio { get; }

    public DesktopWorkspaceDesignDisplay(
        string displayRef,
        string label,
        bool isPrimary,
        DesktopWorkspaceRelativeRect relativeBounds,
        double aspectRatio)
    {
        DisplayRef = RequireText(displayRef, nameof(displayRef));
        Label = RequireText(label, nameof(label));
        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0d)
            throw new ArgumentOutOfRangeException(nameof(aspectRatio), "AspectRatio 必须是有限正数。");

        IsPrimary = isPrimary;
        RelativeBounds = relativeBounds;
        AspectRatio = aspectRatio;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

/// <summary>
/// Workspace Designer 默认消费 Category 汇总而不是完整 Item 列表。
/// ItemCount 用于设计规模判断，但不暴露具体项目身份。
/// </summary>
public sealed record DesktopWorkspaceDesignCategorySummary
{
    public string CategoryKey { get; }
    public string Name { get; }
    public string Description { get; }
    public int ItemCount { get; }

    public DesktopWorkspaceDesignCategorySummary(
        string categoryKey,
        string name,
        string description,
        int itemCount)
    {
        CategoryKey = RequireText(categoryKey, nameof(categoryKey));
        Name = RequireText(name, nameof(name));
        Description = RequireText(description, nameof(description));
        if (itemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount));
        ItemCount = itemCount;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

/// <summary>
/// 只有真正需要 Item Override / anchor / Keep / Exclude 的设计关键例外才进入 Provider 请求。
/// ItemRef 与 Label 均由上层生成并显式标记为 Provider-safe，不得使用 StableId、真实路径或 DeviceName。
/// </summary>
public sealed record DesktopWorkspaceDesignCriticalItem
{
    public string ItemRef { get; }
    public string Label { get; }
    public string CategoryKey { get; }
    public string CurrentDisplayRef { get; }
    public bool IsMovable { get; }

    public DesktopWorkspaceDesignCriticalItem(
        string itemRef,
        string label,
        string categoryKey,
        string currentDisplayRef,
        bool isMovable)
    {
        ItemRef = RequireText(itemRef, nameof(itemRef));
        Label = RequireText(label, nameof(label));
        CategoryKey = RequireText(categoryKey, nameof(categoryKey));
        CurrentDisplayRef = RequireText(currentDisplayRef, nameof(currentDisplayRef));
        IsMovable = isMovable;
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }
}

/// <summary>
/// ADR-0005 Workspace Design 的 Provider-safe 输入。
///
/// 设计阶段只得到：纯语义桌面摘要、Category 汇总、归一化多屏拓扑、用户设计要求、
/// 少量设计关键 Item 以及（修订时）上一版 Blueprint。
///
/// 这里故意不存在 StableId、ObservationId、DeviceName、Pixel、Cell、Grid、Explorer Geometry、
/// Candidate、Execution、Recovery 或 Writer authority。
/// </summary>
public sealed record DesktopWorkspaceDesignRequest
{
    public const int MaxDisplays = 16;
    public const int MaxCategories = 128;
    public const int MaxCriticalItems = 64;
    public const int MaxDesktopSummaryChars = 16 * 1024;
    public const int MaxUserGuidanceChars = 16 * 1024;

    private readonly IReadOnlyList<DesktopWorkspaceDesignDisplay> _displays;
    private readonly IReadOnlyList<DesktopWorkspaceDesignCategorySummary> _categories;
    private readonly IReadOnlyList<DesktopWorkspaceDesignCriticalItem> _criticalItems;

    public DesktopWorkspaceDesignRevisionTarget RevisionTarget { get; }
    public string DesktopSummary { get; }
    public string UserGuidance { get; }
    public IReadOnlyList<DesktopWorkspaceDesignDisplay> Displays => _displays;
    public IReadOnlyList<DesktopWorkspaceDesignCategorySummary> Categories => _categories;
    public IReadOnlyList<DesktopWorkspaceDesignCriticalItem> CriticalItems => _criticalItems;
    public DesktopWorkspaceBlueprint? PreviousBlueprint { get; }

    public DesktopWorkspaceDesignRequest(
        DesktopWorkspaceDesignRevisionTarget revisionTarget,
        string desktopSummary,
        string userGuidance,
        IEnumerable<DesktopWorkspaceDesignDisplay> displays,
        IEnumerable<DesktopWorkspaceDesignCategorySummary> categories,
        IEnumerable<DesktopWorkspaceDesignCriticalItem>? criticalItems = null,
        DesktopWorkspaceBlueprint? previousBlueprint = null)
    {
        RevisionTarget = revisionTarget ?? throw new ArgumentNullException(nameof(revisionTarget));
        DesktopSummary = RequireBoundedText(
            desktopSummary,
            MaxDesktopSummaryChars,
            nameof(desktopSummary));
        UserGuidance = RequireBoundedText(
            userGuidance,
            MaxUserGuidanceChars,
            nameof(userGuidance));
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(categories);

        var displayArray = displays.Take(MaxDisplays + 1).ToArray();
        var categoryArray = categories.Take(MaxCategories + 1).ToArray();
        var criticalItemArray = criticalItems?.Take(MaxCriticalItems + 1).ToArray() ?? [];

        ValidateCount(displayArray, MaxDisplays, nameof(displays));
        ValidateCount(categoryArray, MaxCategories, nameof(categories));
        ValidateCount(criticalItemArray, MaxCriticalItems, nameof(criticalItems));
        RejectNullElements(displayArray, nameof(displays));
        RejectNullElements(categoryArray, nameof(categories));
        RejectNullElements(criticalItemArray, nameof(criticalItems));

        if (displayArray.Length == 0)
            throw new ArgumentException("Workspace Design 至少需要一个显示器。", nameof(displays));
        if (categoryArray.Length == 0)
            throw new ArgumentException("Workspace Design 至少需要一个 Category。", nameof(categories));
        if (displayArray.Count(static display => display.IsPrimary) != 1)
            throw new ArgumentException("Workspace Design 必须且只能包含一个 Primary 显示器。", nameof(displays));

        EnsureUnique(displayArray.Select(static display => display.DisplayRef), nameof(displays));
        EnsureUnique(categoryArray.Select(static category => category.CategoryKey), nameof(categories));
        EnsureUnique(criticalItemArray.Select(static item => item.ItemRef), nameof(criticalItems));

        var displayRefs = displayArray
            .Select(static display => display.DisplayRef)
            .ToHashSet(StringComparer.Ordinal);
        var categoryKeys = categoryArray
            .Select(static category => category.CategoryKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in criticalItemArray)
        {
            if (!displayRefs.Contains(item.CurrentDisplayRef))
                throw new ArgumentException(
                    $"CriticalItem 引用了未知 DisplayRef \"{item.CurrentDisplayRef}\"。",
                    nameof(criticalItems));
            if (!categoryKeys.Contains(item.CategoryKey))
                throw new ArgumentException(
                    $"CriticalItem 引用了未知 CategoryKey \"{item.CategoryKey}\"。",
                    nameof(criticalItems));
        }

        ValidateRevision(previousBlueprint, revisionTarget);

        _displays = Array.AsReadOnly(displayArray);
        _categories = Array.AsReadOnly(categoryArray);
        _criticalItems = Array.AsReadOnly(criticalItemArray);
        PreviousBlueprint = previousBlueprint;
    }

    private static void ValidateRevision(
        DesktopWorkspaceBlueprint? previousBlueprint,
        DesktopWorkspaceDesignRevisionTarget target)
    {
        if (target.Revision == 1)
        {
            if (previousBlueprint is not null)
                throw new ArgumentException("初始 Blueprint 设计不能携带 PreviousBlueprint。", nameof(previousBlueprint));
            return;
        }

        if (previousBlueprint is null)
            throw new ArgumentException("Revision > 1 时必须提供 PreviousBlueprint。", nameof(previousBlueprint));
        if (previousBlueprint.BlueprintId != target.BlueprintId ||
            previousBlueprint.Revision != target.ParentRevision)
        {
            throw new ArgumentException(
                "PreviousBlueprint 必须与 RevisionTarget 的 BlueprintId / ParentRevision 对齐。",
                nameof(previousBlueprint));
        }
    }

    private static string RequireBoundedText(string value, int maxChars, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("文本不能为空。", parameterName);
        if (value.Length > maxChars)
            throw new ArgumentOutOfRangeException(parameterName, $"文本不能超过 {maxChars} 个字符。");
        return value.Trim();
    }

    private static void ValidateCount<T>(T[] values, int maxCount, string parameterName)
    {
        if (values.Length > maxCount)
            throw new ArgumentOutOfRangeException(parameterName, $"集合最多支持 {maxCount} 项。");
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
/// Workspace Designer 单次结果。成功只表示 Provider 形成了结构上有效、引用边界合法且绑定目标 revision 的 Blueprint；
/// 它仍未经过 Explorer Geometry、Compile、Semantic Equivalence 或 Preview。
/// </summary>
public sealed record DesktopWorkspaceDesignProviderResult
{
    public IntelligenceProviderStatus Status { get; }
    public DesktopWorkspaceBlueprint? Blueprint { get; }
    public string? DiagnosticMessage { get; }
    public int? HttpStatusCode { get; }
    public bool Succeeded => Status == IntelligenceProviderStatus.Success && Blueprint is not null;

    private DesktopWorkspaceDesignProviderResult(
        IntelligenceProviderStatus status,
        DesktopWorkspaceBlueprint? blueprint,
        string? diagnosticMessage,
        int? httpStatusCode)
    {
        Status = status;
        Blueprint = blueprint;
        DiagnosticMessage = diagnosticMessage;
        HttpStatusCode = httpStatusCode;
    }

    public static DesktopWorkspaceDesignProviderResult Success(
        DesktopWorkspaceDesignRequest request,
        DesktopWorkspaceBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(blueprint);

        if (blueprint.BlueprintId != request.RevisionTarget.BlueprintId ||
            blueprint.Revision != request.RevisionTarget.Revision ||
            blueprint.ParentRevision != request.RevisionTarget.ParentRevision)
        {
            throw new ArgumentException(
                "Provider Blueprint 必须绑定请求指定的 BlueprintId / Revision / ParentRevision。",
                nameof(blueprint));
        }

        ValidateProviderReferences(request, blueprint);

        return new(
            IntelligenceProviderStatus.Success,
            blueprint,
            diagnosticMessage: null,
            httpStatusCode: null);
    }

    public static DesktopWorkspaceDesignProviderResult Failure(
        IntelligenceProviderStatus status,
        string? diagnosticMessage = null,
        int? httpStatusCode = null)
    {
        if (status == IntelligenceProviderStatus.Success)
            throw new ArgumentException("Failure 不能使用 Success 状态。", nameof(status));

        return new(status, null, diagnosticMessage, httpStatusCode);
    }

    private static void ValidateProviderReferences(
        DesktopWorkspaceDesignRequest request,
        DesktopWorkspaceBlueprint blueprint)
    {
        var requestedDisplayRefs = request.Displays
            .Select(static display => display.DisplayRef)
            .ToHashSet(StringComparer.Ordinal);
        var policyDisplayRefs = blueprint.DisplayPolicies
            .Select(static policy => policy.DisplayRef)
            .ToHashSet(StringComparer.Ordinal);

        if (!requestedDisplayRefs.SetEquals(policyDisplayRefs))
        {
            throw new ArgumentException(
                "Provider Blueprint 必须为请求中的每块显示器且仅为这些显示器声明 Design / Preserve policy。",
                nameof(blueprint));
        }

        if (blueprint.Zones.Any(zone => !requestedDisplayRefs.Contains(zone.DisplayRef)) ||
            blueprint.ReservedRegions.Any(region => !requestedDisplayRefs.Contains(region.DisplayRef)))
        {
            throw new ArgumentException(
                "Provider Blueprint 引用了请求之外的 DisplayRef。",
                nameof(blueprint));
        }

        var requestedCategoryKeys = request.Categories
            .Select(static category => category.CategoryKey)
            .ToHashSet(StringComparer.Ordinal);
        if (blueprint.CategoryRules.Any(rule => !requestedCategoryKeys.Contains(rule.CategoryKey)))
        {
            throw new ArgumentException(
                "Provider Blueprint 引用了请求之外的 CategoryKey。",
                nameof(blueprint));
        }

        var requestedItemRefs = request.CriticalItems
            .Select(static item => item.ItemRef)
            .ToHashSet(StringComparer.Ordinal);
        if (blueprint.ItemOverrides.Any(item => !requestedItemRefs.Contains(item.ItemRef)))
        {
            throw new ArgumentException(
                "Provider Blueprint ItemOverride 引用了请求之外的 ItemRef。",
                nameof(blueprint));
        }
    }
}

public interface IDesktopWorkspaceDesigner
{
    Task<DesktopWorkspaceDesignProviderResult> DesignAsync(
        DesktopWorkspaceDesignRequest request,
        CancellationToken cancellationToken = default);
}
