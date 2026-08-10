using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// UI 无关的桌面项目分类请求。
/// 正文上下文只能由显式启用的本地 content-analysis gate 注入；请求本身不包含文件路径。
/// </summary>
public sealed class DesktopItemClassificationRequest
{
    private readonly IReadOnlyList<IntelligenceZoneOption> _zones;

    public DesktopItemFacts Item { get; }
    public IReadOnlyList<IntelligenceZoneOption> Zones => _zones;
    public bool ContentAnalysisEnabled { get; }
    public DesktopItemContentExcerpt? ContentExcerpt { get; }

    public DesktopItemClassificationRequest(
        DesktopItemFacts item,
        IEnumerable<IntelligenceZoneOption>? zones = null,
        bool contentAnalysisEnabled = false,
        DesktopItemContentExcerpt? contentExcerpt = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        if (!contentAnalysisEnabled && contentExcerpt is not null)
        {
            throw new ArgumentException(
                "ContentAnalysisEnabled=false 时不能携带 ContentExcerpt。",
                nameof(contentExcerpt));
        }

        var copied = zones?
            .Take(DesktopClassificationPolicyResolver.MaxZonesPerResolution + 1)
            .ToArray() ?? Array.Empty<IntelligenceZoneOption>();
        if (copied.Length > DesktopClassificationPolicyResolver.MaxZonesPerResolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zones),
                $"单次分类请求最多支持 {DesktopClassificationPolicyResolver.MaxZonesPerResolution} 个 Zone。");
        }
        if (copied.Any(static zone => zone is null))
            throw new ArgumentException("Zones 不能包含 null。", nameof(zones));

        var duplicate = copied
            .GroupBy(static zone => zone.ZoneId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Zones 包含重复 ZoneId：{duplicate.Key}。", nameof(zones));

        _zones = Array.AsReadOnly(copied);
        ContentAnalysisEnabled = contentAnalysisEnabled;
        ContentExcerpt = contentExcerpt;
    }
}
