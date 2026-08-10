using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// UI 无关的桌面项目分类请求。
/// M4-01 只包含当前分类所需的最小事实和已有 Zone 候选；后续 Profile / first-run context 可在不改变执行边界的前提下扩展。
/// </summary>
public sealed class DesktopItemClassificationRequest
{
    private readonly IReadOnlyList<IntelligenceZoneOption> _zones;

    public DesktopItemFacts Item { get; }
    public IReadOnlyList<IntelligenceZoneOption> Zones => _zones;

    public DesktopItemClassificationRequest(
        DesktopItemFacts item,
        IEnumerable<IntelligenceZoneOption>? zones = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));

        var copied = zones?.ToArray() ?? Array.Empty<IntelligenceZoneOption>();
        if (copied.Any(static zone => zone is null))
            throw new ArgumentException("Zones 不能包含 null。", nameof(zones));

        var duplicate = copied
            .GroupBy(static zone => zone.ZoneId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Zones 包含重复 ZoneId：{duplicate.Key}。", nameof(zones));

        _zones = Array.AsReadOnly(copied);
    }
}
