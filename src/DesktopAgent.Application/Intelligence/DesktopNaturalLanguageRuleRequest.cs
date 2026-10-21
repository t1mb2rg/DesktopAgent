using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// UI 无关的自然语言规则请求。StableId 不发送给 Provider；若用户表达“这个项目”，
/// Provider 只能返回 match_current_item=true，再由本地代码绑定 ReferenceItem.StableId。
/// </summary>
public sealed class DesktopNaturalLanguageRuleRequest
{
    public const int MaxInstructionLength = 4096;

    private readonly IReadOnlyList<IntelligenceZoneOption> _zones;

    public string Instruction { get; }
    public Guid ActiveTemplateId { get; }
    public DesktopItemFacts? ReferenceItem { get; }
    public IReadOnlyList<IntelligenceZoneOption> Zones => _zones;

    public DesktopNaturalLanguageRuleRequest(
        string instruction,
        Guid activeTemplateId,
        IEnumerable<IntelligenceZoneOption>? zones = null,
        DesktopItemFacts? referenceItem = null)
    {
        if (string.IsNullOrWhiteSpace(instruction) || instruction.Length > MaxInstructionLength)
        {
            throw new ArgumentException(
                $"Instruction 必须为 1..{MaxInstructionLength} 个字符的非空文本。",
                nameof(instruction));
        }
        if (activeTemplateId == Guid.Empty)
            throw new ArgumentException("ActiveTemplateId 不能为 Guid.Empty。", nameof(activeTemplateId));

        var copiedZones = zones?
            .Take(DesktopClassificationPolicyResolver.MaxZonesPerResolution + 1)
            .ToArray() ?? Array.Empty<IntelligenceZoneOption>();
        if (copiedZones.Length > DesktopClassificationPolicyResolver.MaxZonesPerResolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zones),
                $"单次 RuleDraft 请求最多支持 {DesktopClassificationPolicyResolver.MaxZonesPerResolution} 个 Zone。");
        }
        if (copiedZones.Any(static zone => zone is null))
            throw new ArgumentException("Zones 不能包含 null。", nameof(zones));
        if (copiedZones.Select(static zone => zone.ZoneId).Distinct().Count() != copiedZones.Length)
            throw new ArgumentException("Zones 不能包含重复 ZoneId。", nameof(zones));

        Instruction = instruction;
        ActiveTemplateId = activeTemplateId;
        ReferenceItem = referenceItem;
        _zones = Array.AsReadOnly(copiedZones);
    }
}
