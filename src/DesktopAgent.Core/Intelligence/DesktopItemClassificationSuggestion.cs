namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// 某个来源对桌面项目分类的候选建议。
/// Suggestion 只是证据，不是最终 Policy 决定，更不是桌面写入授权。
/// </summary>
public sealed class DesktopItemClassificationSuggestion
{
    /// <summary>建议的语义分类名称。</summary>
    public string Category { get; }

    /// <summary>建议目标 Zone；仅完成语义分类而不推荐 Zone 时可为 null。</summary>
    public Guid? SuggestedZoneId { get; }

    /// <summary>建议置信度，闭区间 [0, 1]。</summary>
    public double Confidence { get; }

    /// <summary>用户或诊断层可以展示的简短理由。</summary>
    public string Reason { get; }

    /// <summary>该建议来自哪里。</summary>
    public DesktopItemClassificationSource Source { get; }

    public DesktopItemClassificationSuggestion(
        string category,
        Guid? suggestedZoneId,
        double confidence,
        string reason,
        DesktopItemClassificationSource source)
    {
        if (category is null)
            throw new ArgumentNullException(nameof(category));
        if (category.Length == 0)
            throw new ArgumentException("Category 不能为空字符串。", nameof(category));
        if (suggestedZoneId == Guid.Empty)
            throw new ArgumentException("SuggestedZoneId 不能为 Guid.Empty。", nameof(suggestedZoneId));
        if (!double.IsFinite(confidence) || confidence < 0d || confidence > 1d)
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence 必须是 [0, 1] 范围内的有限值。");
        if (reason is null)
            throw new ArgumentNullException(nameof(reason));
        if (reason.Length == 0)
            throw new ArgumentException("Reason 不能为空字符串。", nameof(reason));
        if (!Enum.IsDefined(source))
            throw new ArgumentException($"无效的 ClassificationSource：{(int)source}。", nameof(source));

        Category = category;
        SuggestedZoneId = suggestedZoneId;
        Confidence = confidence;
        Reason = reason;
        Source = source;
    }
}
