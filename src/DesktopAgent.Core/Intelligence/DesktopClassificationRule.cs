using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.Intelligence;

/// <summary>分类规则的决策权威。数值越小，语义优先级越高。</summary>
public enum DesktopClassificationRuleAuthority
{
    ExplicitUser = 0,
    DeterministicLocal = 1,
}

/// <summary>
/// 规则来自哪里，与规则当前拥有多高决策权分开记录。
/// AI 建议一旦由用户明确确认，可以拥有 ExplicitUser authority，但 provenance 仍保留 AI 痕迹。
/// </summary>
public enum DesktopClassificationRuleProvenance
{
    UserAuthored = 0,
    BuiltIn = 1,
    AiProposedAndConfirmed = 2,
    LearnedAndConfirmed = 3,
}

/// <summary>
/// v1.0 有界、确定性的分类匹配条件。所有非空条件使用 AND 组合。
/// 不支持脚本和无界正则；FileNamePattern 仅支持 '*' 与 '?' glob。
/// </summary>
public sealed class DesktopClassificationRuleCondition
{
    public string? StableId { get; }
    public string? FileExtension { get; }
    public string? FileNamePattern { get; }
    public ItemKind? ItemKind { get; }

    /// <summary>用于同 authority / priority 下的稳定“更具体规则优先”。</summary>
    public int SpecificityScore { get; }

    public DesktopClassificationRuleCondition(
        string? stableId = null,
        string? fileExtension = null,
        string? fileNamePattern = null,
        ItemKind? itemKind = null)
    {
        if (stableId is not null && stableId.Length == 0)
            throw new ArgumentException("StableId 应使用 null 表示未指定，不能是空字符串。", nameof(stableId));
        if (fileExtension is not null)
        {
            if (string.IsNullOrWhiteSpace(fileExtension))
                throw new ArgumentException("FileExtension 应使用 null 表示未指定，不能是空白。", nameof(fileExtension));
            if (!fileExtension.StartsWith('.', StringComparison.Ordinal) || fileExtension.Length == 1)
                throw new ArgumentException("FileExtension 必须形如 .pdf。", nameof(fileExtension));
        }
        if (fileNamePattern is not null)
        {
            if (string.IsNullOrWhiteSpace(fileNamePattern))
                throw new ArgumentException("FileNamePattern 应使用 null 表示未指定，不能是空白。", nameof(fileNamePattern));
            if (fileNamePattern.Length > 256)
                throw new ArgumentOutOfRangeException(nameof(fileNamePattern), "FileNamePattern 最多 256 字符。");
        }
        if (itemKind.HasValue && !Enum.IsDefined(itemKind.Value))
            throw new ArgumentOutOfRangeException(nameof(itemKind));
        if (stableId is null && fileExtension is null && fileNamePattern is null && !itemKind.HasValue)
            throw new ArgumentException("分类规则至少需要一个匹配条件。");

        StableId = stableId;
        FileExtension = fileExtension?.ToLowerInvariant();
        FileNamePattern = fileNamePattern;
        ItemKind = itemKind;

        // 精确身份远比文件名模式更具体；文件名模式又比扩展名 / ItemKind 更具体。
        // 分值只负责同 authority / priority 的 tie-break，不跨越 authority 或显式 Priority。
        SpecificityScore =
            (StableId is null ? 0 : 1000) +
            (FileNamePattern is null ? 0 : 100) +
            (FileExtension is null ? 0 : 10) +
            (ItemKind.HasValue ? 1 : 0);
    }

    public bool Matches(DesktopItemFacts item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (StableId is not null && !string.Equals(StableId, item.StableId, StringComparison.Ordinal))
            return false;
        if (FileExtension is not null &&
            !string.Equals(FileExtension, item.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (FileNamePattern is not null && !GlobMatches(FileNamePattern, item.DisplayName))
            return false;
        if (ItemKind.HasValue && ItemKind.Value != item.ItemKind)
            return false;

        return true;
    }

    /// <summary>
    /// 线性回退式 glob matcher，只支持 '*' 与 '?'，OrdinalIgnoreCase。
    /// 不使用 Regex，因此没有正则回溯或用户提供脚本的执行面。
    /// </summary>
    private static bool GlobMatches(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var lastStar = -1;
        var retryValueIndex = -1;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == '?' || CharsEqualIgnoreCase(pattern[patternIndex], value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                lastStar = patternIndex++;
                retryValueIndex = valueIndex;
                continue;
            }

            if (lastStar >= 0)
            {
                patternIndex = lastStar + 1;
                valueIndex = ++retryValueIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            patternIndex++;

        return patternIndex == pattern.Length;
    }

    private static bool CharsEqualIgnoreCase(char left, char right)
        => char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
}

/// <summary>
/// M4 语义分类规则。Category 必填；TargetZoneId 可选，因此 Category 不与某个 Zone 永久绑定。
/// </summary>
public sealed class DesktopClassificationRule
{
    public Guid RuleId { get; }
    public string Name { get; }
    public DesktopClassificationRuleCondition Condition { get; }
    public string Category { get; }
    public Guid? TargetZoneId { get; }
    public DesktopClassificationRuleAuthority Authority { get; }
    public DesktopClassificationRuleProvenance Provenance { get; }
    public int Priority { get; }
    public bool IsEnabled { get; }

    public DesktopClassificationRule(
        Guid ruleId,
        string name,
        DesktopClassificationRuleCondition condition,
        string category,
        Guid? targetZoneId,
        DesktopClassificationRuleAuthority authority,
        DesktopClassificationRuleProvenance provenance,
        int priority = 0,
        bool isEnabled = true)
    {
        if (ruleId == Guid.Empty)
            throw new ArgumentException("RuleId 不能为 Guid.Empty。", nameof(ruleId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Category 不能为空。", nameof(category));
        if (targetZoneId == Guid.Empty)
            throw new ArgumentException("TargetZoneId 不能为 Guid.Empty。", nameof(targetZoneId));
        if (!Enum.IsDefined(authority))
            throw new ArgumentOutOfRangeException(nameof(authority));
        if (!Enum.IsDefined(provenance))
            throw new ArgumentOutOfRangeException(nameof(provenance));
        if (priority is < -10000 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(priority), "Priority 必须位于 [-10000, 10000]。");

        if (provenance == DesktopClassificationRuleProvenance.BuiltIn &&
            authority != DesktopClassificationRuleAuthority.DeterministicLocal)
        {
            throw new ArgumentException("BuiltIn 规则必须使用 DeterministicLocal authority。", nameof(authority));
        }
        if (provenance != DesktopClassificationRuleProvenance.BuiltIn &&
            authority != DesktopClassificationRuleAuthority.ExplicitUser)
        {
            throw new ArgumentException("用户创建/确认的规则必须使用 ExplicitUser authority。", nameof(authority));
        }

        RuleId = ruleId;
        Name = name;
        Category = category;
        TargetZoneId = targetZoneId;
        Authority = authority;
        Provenance = provenance;
        Priority = priority;
        IsEnabled = isEnabled;
    }

    public DesktopItemClassificationSource ClassificationSource
        => Authority switch
        {
            DesktopClassificationRuleAuthority.ExplicitUser => DesktopItemClassificationSource.ExplicitUserRule,
            DesktopClassificationRuleAuthority.DeterministicLocal => DesktopItemClassificationSource.DeterministicLocalRule,
            _ => throw new InvalidOperationException($"未知 Authority：{Authority}。"),
        };
}
