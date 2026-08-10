namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// AI 对逻辑 Zone 的语义位置偏好。这里故意不表达归一化矩形或物理像素坐标。
/// 后续由本地确定性布局编译器把这些提示转换为可验证 Zone。
/// </summary>
public enum DesktopZonePlacementHint
{
    Unspecified = 0,
    Left = 1,
    Right = 2,
    Top = 3,
    Bottom = 4,
    Center = 5,
    TopLeft = 6,
    TopRight = 7,
    BottomLeft = 8,
    BottomRight = 9,
}

/// <summary>AI 对逻辑 Zone 占用规模的有限语义提示。</summary>
public enum DesktopZoneSizeHint
{
    Flexible = 0,
    Compact = 1,
    Medium = 2,
    Large = 3,
}

public sealed class DesktopUnderstandingCategoryDraft
{
    public string Key { get; }
    public string Name { get; }
    public string Description { get; }

    public DesktopUnderstandingCategoryDraft(string key, string name, string description)
    {
        Key = RequireText(key, nameof(key));
        Name = RequireText(name, nameof(name));
        Description = RequireText(description, nameof(description));
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能是空字符串或空白。", parameterName);
        return value;
    }
}

public sealed class DesktopUnderstandingZoneDraft
{
    private readonly IReadOnlyList<string> _categoryKeys;

    public string Key { get; }
    public string Name { get; }
    public Guid? ExistingZoneId { get; }
    public DesktopZonePlacementHint PlacementHint { get; }
    public DesktopZoneSizeHint SizeHint { get; }
    public IReadOnlyList<string> CategoryKeys => _categoryKeys;

    public DesktopUnderstandingZoneDraft(
        string key,
        string name,
        Guid? existingZoneId,
        DesktopZonePlacementHint placementHint,
        DesktopZoneSizeHint sizeHint,
        IEnumerable<string>? categoryKeys = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key 不能为空。", nameof(key));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));
        if (existingZoneId == Guid.Empty)
            throw new ArgumentException("ExistingZoneId 不能为 Guid.Empty。", nameof(existingZoneId));
        if (!Enum.IsDefined(placementHint))
            throw new ArgumentOutOfRangeException(nameof(placementHint));
        if (!Enum.IsDefined(sizeHint))
            throw new ArgumentOutOfRangeException(nameof(sizeHint));

        var copiedCategoryKeys = categoryKeys?.ToArray() ?? Array.Empty<string>();
        if (copiedCategoryKeys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("CategoryKeys 不能包含 null、空字符串或空白。", nameof(categoryKeys));
        if (copiedCategoryKeys.Distinct(StringComparer.Ordinal).Count() != copiedCategoryKeys.Length)
            throw new ArgumentException("CategoryKeys 不能包含重复值。", nameof(categoryKeys));

        Key = key;
        Name = name;
        ExistingZoneId = existingZoneId;
        PlacementHint = placementHint;
        SizeHint = sizeHint;
        _categoryKeys = Array.AsReadOnly(copiedCategoryKeys);
    }
}

public sealed class DesktopUnderstandingAssignment
{
    public string StableId { get; }
    public string CategoryKey { get; }
    public string? ZoneKey { get; }
    public double Confidence { get; }
    public string Reason { get; }

    public DesktopUnderstandingAssignment(
        string stableId,
        string categoryKey,
        string? zoneKey,
        double confidence,
        string reason)
    {
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(categoryKey))
            throw new ArgumentException("CategoryKey 不能为空。", nameof(categoryKey));
        if (zoneKey is not null && string.IsNullOrWhiteSpace(zoneKey))
            throw new ArgumentException("ZoneKey 应使用 null 表示未指定，不能是空白。", nameof(zoneKey));
        if (!double.IsFinite(confidence) || confidence is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence 必须位于 [0, 1]。 ");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason 不能为空。", nameof(reason));

        StableId = stableId;
        CategoryKey = categoryKey;
        ZoneKey = zoneKey;
        Confidence = confidence;
        Reason = reason;
    }
}

public sealed class DesktopUnderstandingQuestion
{
    public string Key { get; }
    public string Text { get; }

    public DesktopUnderstandingQuestion(string key, string text)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key 不能为空。", nameof(key));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text 不能为空。", nameof(text));

        Key = key;
        Text = text;
    }
}

/// <summary>
/// 一轮首次桌面理解得到的纯语义草案。
/// 它可以成为后续用户确认、Profile 与 RuleDraft 的输入，但本身没有任何执行权限。
/// </summary>
public sealed class DesktopUnderstandingDraft
{
    private readonly IReadOnlyList<DesktopUnderstandingCategoryDraft> _categories;
    private readonly IReadOnlyList<DesktopUnderstandingZoneDraft> _zones;
    private readonly IReadOnlyList<DesktopUnderstandingAssignment> _assignments;
    private readonly IReadOnlyList<DesktopUnderstandingQuestion> _questions;

    public string IntentSummary { get; }
    public string DesktopSummary { get; }
    public IReadOnlyList<DesktopUnderstandingCategoryDraft> Categories => _categories;
    public IReadOnlyList<DesktopUnderstandingZoneDraft> Zones => _zones;
    public IReadOnlyList<DesktopUnderstandingAssignment> Assignments => _assignments;
    public IReadOnlyList<DesktopUnderstandingQuestion> Questions => _questions;

    public DesktopUnderstandingDraft(
        string intentSummary,
        string desktopSummary,
        IEnumerable<DesktopUnderstandingCategoryDraft> categories,
        IEnumerable<DesktopUnderstandingZoneDraft>? zones = null,
        IEnumerable<DesktopUnderstandingAssignment>? assignments = null,
        IEnumerable<DesktopUnderstandingQuestion>? questions = null)
    {
        if (string.IsNullOrWhiteSpace(intentSummary))
            throw new ArgumentException("IntentSummary 不能为空。", nameof(intentSummary));
        if (string.IsNullOrWhiteSpace(desktopSummary))
            throw new ArgumentException("DesktopSummary 不能为空。", nameof(desktopSummary));
        ArgumentNullException.ThrowIfNull(categories);

        var copiedCategories = categories.ToArray();
        var copiedZones = zones?.ToArray() ?? Array.Empty<DesktopUnderstandingZoneDraft>();
        var copiedAssignments = assignments?.ToArray() ?? Array.Empty<DesktopUnderstandingAssignment>();
        var copiedQuestions = questions?.ToArray() ?? Array.Empty<DesktopUnderstandingQuestion>();

        RejectNullElements(copiedCategories, nameof(categories));
        RejectNullElements(copiedZones, nameof(zones));
        RejectNullElements(copiedAssignments, nameof(assignments));
        RejectNullElements(copiedQuestions, nameof(questions));

        if (copiedCategories.Length == 0)
            throw new ArgumentException("至少需要一个 Category。", nameof(categories));
        EnsureUnique(copiedCategories.Select(static category => category.Key), nameof(categories));
        EnsureUnique(copiedZones.Select(static zone => zone.Key), nameof(zones));
        EnsureUnique(copiedQuestions.Select(static question => question.Key), nameof(questions));
        EnsureUnique(copiedAssignments.Select(static assignment => assignment.StableId), nameof(assignments));

        var categoryKeys = copiedCategories.Select(static category => category.Key).ToHashSet(StringComparer.Ordinal);
        var zoneKeys = copiedZones.Select(static zone => zone.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var zone in copiedZones)
        {
            if (zone.CategoryKeys.Any(categoryKey => !categoryKeys.Contains(categoryKey)))
                throw new ArgumentException("Zone 引用了不存在的 CategoryKey。", nameof(zones));
        }

        foreach (var assignment in copiedAssignments)
        {
            if (!categoryKeys.Contains(assignment.CategoryKey))
                throw new ArgumentException("Assignment 引用了不存在的 CategoryKey。", nameof(assignments));
            if (assignment.ZoneKey is not null && !zoneKeys.Contains(assignment.ZoneKey))
                throw new ArgumentException("Assignment 引用了不存在的 ZoneKey。", nameof(assignments));
        }

        var reusedZoneIds = copiedZones
            .Where(static zone => zone.ExistingZoneId.HasValue)
            .Select(static zone => zone.ExistingZoneId!.Value)
            .ToArray();
        if (reusedZoneIds.Distinct().Count() != reusedZoneIds.Length)
            throw new ArgumentException("同一个 ExistingZoneId 不能被多个 ZoneDraft 重复复用。", nameof(zones));

        IntentSummary = intentSummary;
        DesktopSummary = desktopSummary;
        _categories = Array.AsReadOnly(copiedCategories);
        _zones = Array.AsReadOnly(copiedZones);
        _assignments = Array.AsReadOnly(copiedAssignments);
        _questions = Array.AsReadOnly(copiedQuestions);
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
