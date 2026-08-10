using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 一轮首次桌面理解的 UI 无关输入。
/// CurrentIntentSummary 是上一轮 AI 归纳后的有界意图摘要；LatestUserGuidance 只携带本轮新增用户修正，
/// 避免把完整聊天历史无限堆入模型上下文。
/// </summary>
public sealed class DesktopFirstRunUnderstandingRequest
{
    public const int MaxItems = 512;
    public const int MaxExistingZones = 32;
    public const int MaxIntentSummaryChars = 8192;
    public const int MaxUserGuidanceChars = 4096;

    private readonly IReadOnlyList<DesktopItemFacts> _items;
    private readonly IReadOnlyList<IntelligenceZoneOption> _existingZones;

    public IReadOnlyList<DesktopItemFacts> Items => _items;
    public IReadOnlyList<IntelligenceZoneOption> ExistingZones => _existingZones;
    public string? CurrentIntentSummary { get; }
    public string? LatestUserGuidance { get; }

    public DesktopFirstRunUnderstandingRequest(
        IEnumerable<DesktopItemFacts> items,
        IEnumerable<IntelligenceZoneOption>? existingZones = null,
        string? currentIntentSummary = null,
        string? latestUserGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 只枚举到“允许上限 + 1”。这样上限不仅约束最终契约，也约束构造请求时的瞬时内存分配。
        var copiedItems = items.Take(MaxItems + 1).ToArray();
        var copiedZones = existingZones?
            .Take(MaxExistingZones + 1)
            .ToArray() ?? Array.Empty<IntelligenceZoneOption>();

        if (copiedItems.Length == 0)
            throw new ArgumentException("首次理解至少需要一个桌面项目。", nameof(items));
        if (copiedItems.Length > MaxItems)
            throw new ArgumentOutOfRangeException(nameof(items), $"单轮首次理解最多支持 {MaxItems} 个桌面项目。");
        if (copiedItems.Any(static item => item is null))
            throw new ArgumentException("Items 不能包含 null。", nameof(items));
        if (copiedZones.Length > MaxExistingZones)
            throw new ArgumentOutOfRangeException(nameof(existingZones), $"单轮首次理解最多支持 {MaxExistingZones} 个已有 Zone。");
        if (copiedZones.Any(static zone => zone is null))
            throw new ArgumentException("ExistingZones 不能包含 null。", nameof(existingZones));

        if (copiedItems.Select(static item => item.StableId).Distinct(StringComparer.Ordinal).Count() != copiedItems.Length)
            throw new ArgumentException("Items 不能包含重复 StableId。", nameof(items));
        if (copiedZones.Select(static zone => zone.ZoneId).Distinct().Count() != copiedZones.Length)
            throw new ArgumentException("ExistingZones 不能包含重复 ZoneId。", nameof(existingZones));

        CurrentIntentSummary = ValidateOptionalText(
            currentIntentSummary,
            MaxIntentSummaryChars,
            nameof(currentIntentSummary));
        LatestUserGuidance = ValidateOptionalText(
            latestUserGuidance,
            MaxUserGuidanceChars,
            nameof(latestUserGuidance));

        _items = Array.AsReadOnly(copiedItems);
        _existingZones = Array.AsReadOnly(copiedZones);
    }

    private static string? ValidateOptionalText(string? value, int maxChars, string parameterName)
    {
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("可选文本应使用 null 表示不存在，不能是空白。", parameterName);
        if (value.Length > maxChars)
            throw new ArgumentOutOfRangeException(parameterName, $"文本不能超过 {maxChars} 个字符。");
        return value;
    }
}
