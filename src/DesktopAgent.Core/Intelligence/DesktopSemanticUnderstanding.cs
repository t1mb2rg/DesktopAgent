namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// ADR-0005 的纯语义分类。它只回答“这类项目是什么”，不携带 Zone、显示器或几何设计意图。
/// </summary>
public sealed record DesktopSemanticCategory
{
    public string Key { get; }
    public string Name { get; }
    public string Description { get; }

    public DesktopSemanticCategory(string key, string name, string description)
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
        return value.Trim();
    }
}

/// <summary>
/// 一个桌面项目的纯语义分类结果。StableId 只作为本地 Observation authority 绑定，
/// 不代表 Provider 可以获得 StableId，也不包含任何 Zone 或目标位置。
/// </summary>
public sealed record DesktopSemanticAssignment
{
    public string StableId { get; }
    public string CategoryKey { get; }
    public double Confidence { get; }
    public string Reason { get; }

    public DesktopSemanticAssignment(
        string stableId,
        string categoryKey,
        double confidence,
        string reason)
    {
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(categoryKey))
            throw new ArgumentException("CategoryKey 不能为空。", nameof(categoryKey));
        if (!double.IsFinite(confidence) || confidence is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence 必须位于 [0, 1]。");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason 不能为空。", nameof(reason));

        StableId = stableId;
        CategoryKey = categoryKey.Trim();
        Confidence = confidence;
        Reason = reason.Trim();
    }
}

/// <summary>
/// 语义理解阶段发现的歧义或待确认问题。它本身不决定是否阻塞 Workspace Design；
/// “是否会实质改变 Blueprint”由后续明确的 Design Gate 判断。
/// </summary>
public sealed record DesktopSemanticQuestion
{
    public string Key { get; }
    public string Text { get; }

    public DesktopSemanticQuestion(string key, string text)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key 不能为空。", nameof(key));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text 不能为空。", nameof(text));

        Key = key.Trim();
        Text = text.Trim();
    }
}

/// <summary>
/// ADR-0005 定义的纯语义桌面理解结果。
///
/// Authority 只包括：桌面语义摘要、Category、Item→Category、置信度与问题。
/// 它故意没有 IntentSummary、Zone、ZoneKey、目标显示器、Reserved Region、网格、Cell 或 Writer authority。
/// </summary>
public sealed record DesktopSemanticUnderstanding
{
    private readonly IReadOnlyList<DesktopSemanticCategory> _categories;
    private readonly IReadOnlyList<DesktopSemanticAssignment> _assignments;
    private readonly IReadOnlyList<DesktopSemanticQuestion> _questions;

    public string DesktopSummary { get; }
    public IReadOnlyList<DesktopSemanticCategory> Categories => _categories;
    public IReadOnlyList<DesktopSemanticAssignment> Assignments => _assignments;
    public IReadOnlyList<DesktopSemanticQuestion> Questions => _questions;

    public DesktopSemanticUnderstanding(
        string desktopSummary,
        IEnumerable<DesktopSemanticCategory> categories,
        IEnumerable<DesktopSemanticAssignment>? assignments = null,
        IEnumerable<DesktopSemanticQuestion>? questions = null)
    {
        if (string.IsNullOrWhiteSpace(desktopSummary))
            throw new ArgumentException("DesktopSummary 不能为空。", nameof(desktopSummary));
        ArgumentNullException.ThrowIfNull(categories);

        var categoryArray = categories.ToArray();
        var assignmentArray = assignments?.ToArray() ?? [];
        var questionArray = questions?.ToArray() ?? [];

        RejectNullElements(categoryArray, nameof(categories));
        RejectNullElements(assignmentArray, nameof(assignments));
        RejectNullElements(questionArray, nameof(questions));

        if (categoryArray.Length == 0)
            throw new ArgumentException("至少需要一个 Category。", nameof(categories));

        EnsureUnique(categoryArray.Select(static category => category.Key), nameof(categories));
        EnsureUnique(assignmentArray.Select(static assignment => assignment.StableId), nameof(assignments));
        EnsureUnique(questionArray.Select(static question => question.Key), nameof(questions));

        var categoryKeys = categoryArray
            .Select(static category => category.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var assignment in assignmentArray)
        {
            if (!categoryKeys.Contains(assignment.CategoryKey))
            {
                throw new ArgumentException(
                    $"Assignment 引用了不存在的 CategoryKey \"{assignment.CategoryKey}\"。",
                    nameof(assignments));
            }
        }

        DesktopSummary = desktopSummary.Trim();
        _categories = Array.AsReadOnly(categoryArray);
        _assignments = Array.AsReadOnly(assignmentArray);
        _questions = Array.AsReadOnly(questionArray);
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
/// 迁移桥：把仍服务 legacy 单屏路径的 DesktopUnderstandingDraft 投影成 ADR-0005 纯语义合同。
///
/// 该转换刻意丢弃 IntentSummary、Zones 与 Assignment.ZoneKey。任何新 Workspace Design 代码都应依赖
/// DesktopSemanticUnderstanding，而不是从 legacy Draft 偷读规划意图。
/// </summary>
public static class DesktopSemanticUnderstandingProjector
{
    public static DesktopSemanticUnderstanding FromLegacyDraft(DesktopUnderstandingDraft legacyDraft)
    {
        ArgumentNullException.ThrowIfNull(legacyDraft);

        return new DesktopSemanticUnderstanding(
            legacyDraft.DesktopSummary,
            legacyDraft.Categories.Select(static category => new DesktopSemanticCategory(
                category.Key,
                category.Name,
                category.Description)),
            legacyDraft.Assignments.Select(static assignment => new DesktopSemanticAssignment(
                assignment.StableId,
                assignment.CategoryKey,
                assignment.Confidence,
                assignment.Reason)),
            legacyDraft.Questions.Select(static question => new DesktopSemanticQuestion(
                question.Key,
                question.Text)));
    }
}
