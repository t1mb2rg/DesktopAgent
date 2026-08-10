namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// M4 Policy 在比较规则、Profile 与 AI Suggestion 后形成的最终分类决定。
/// Decision 仍只表达“应如何理解 / 推荐”，不携带物理坐标或 Writer 命令。
/// </summary>
public sealed class DesktopItemClassificationDecision
{
    /// <summary>最终采用的语义分类名称。</summary>
    public string Category { get; }

    /// <summary>最终推荐的目标 Zone；仅分类而不产生 Zone 推荐时可为 null。</summary>
    public Guid? TargetZoneId { get; }

    /// <summary>最终决定的置信度，闭区间 [0, 1]。</summary>
    public double Confidence { get; }

    /// <summary>最终决定主要采用的语义来源。</summary>
    public DesktopItemClassificationSource Source { get; }

    /// <summary>解释为何最终采用该分类 / Zone。</summary>
    public string Explanation { get; }

    public DesktopItemClassificationDecision(
        string category,
        Guid? targetZoneId,
        double confidence,
        DesktopItemClassificationSource source,
        string explanation)
    {
        if (category is null)
            throw new ArgumentNullException(nameof(category));
        if (category.Length == 0)
            throw new ArgumentException("Category 不能为空字符串。", nameof(category));
        if (targetZoneId == Guid.Empty)
            throw new ArgumentException("TargetZoneId 不能为 Guid.Empty。", nameof(targetZoneId));
        if (!double.IsFinite(confidence) || confidence < 0d || confidence > 1d)
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence 必须是 [0, 1] 范围内的有限值。");
        if (!Enum.IsDefined(source))
            throw new ArgumentException($"无效的 ClassificationSource：{(int)source}。", nameof(source));
        if (explanation is null)
            throw new ArgumentNullException(nameof(explanation));
        if (explanation.Length == 0)
            throw new ArgumentException("Explanation 不能为空字符串。", nameof(explanation));

        Category = category;
        TargetZoneId = targetZoneId;
        Confidence = confidence;
        Source = source;
        Explanation = explanation;
    }
}
