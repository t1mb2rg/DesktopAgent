using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 规则与真实图标的精确匹配结果 — 不可变 sealed class。
/// Rule.StableId 与 Icon.StableId 必须 StringComparer.Ordinal 完全相同。
/// </summary>
public sealed class DesktopLayoutResolvedIconRule
{
    /// <summary>匹配到的规则（RuleSet 中的原对象引用）。</summary>
    public DesktopLayoutIconRule Rule { get; }

    /// <summary>匹配到的图标（InputIcons 中的原对象引用）。</summary>
    public DesktopIconSnapshotItem Icon { get; }

    /// <summary>
    /// ZoneOverride 时对应的模板 Zone；Exclude/Pin 时为 null。
    /// 必须是模板 Zones 中的原对象引用。
    /// </summary>
    public DesktopLayoutZone? TargetZone { get; }

    /// <summary>图标是否位于目标显示器上。</summary>
    public bool IsOnTargetDisplay { get; }

    /// <summary>规则类型快捷访问。</summary>
    public DesktopLayoutIconRuleKind Kind => Rule.Kind;

    public DesktopLayoutResolvedIconRule(
        DesktopLayoutIconRule rule,
        DesktopIconSnapshotItem icon,
        DesktopLayoutZone? targetZone,
        bool isOnTargetDisplay)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (icon is null) throw new ArgumentNullException(nameof(icon));

        // StableId 精确匹配
        if (!StringComparer.Ordinal.Equals(rule.StableId, icon.StableId))
            throw new ArgumentException(
                $"Rule.StableId \"{rule.StableId}\" 与 Icon.StableId \"{icon.StableId}\" 不一致。");

        // 验证 TargetZone 与 Rule 类型一致
        switch (rule.Kind)
        {
            case DesktopLayoutIconRuleKind.Exclude:
            case DesktopLayoutIconRuleKind.Pin:
                if (targetZone is not null)
                    throw new ArgumentException(
                        $"{rule.Kind} 规则的 TargetZone 必须为 null。", nameof(targetZone));
                break;
            case DesktopLayoutIconRuleKind.ZoneOverride:
                if (targetZone is null)
                    throw new ArgumentException(
                        "ZoneOverride 规则的 TargetZone 必须非 null。", nameof(targetZone));
                if (targetZone.ZoneId != rule.TargetZoneId!.Value)
                    throw new ArgumentException(
                        $"TargetZone.ZoneId ({targetZone.ZoneId}) 与 Rule.TargetZoneId ({rule.TargetZoneId}) 不一致。",
                        nameof(targetZone));
                break;
            default:
                throw new ArgumentException($"无效的 RuleKind：{(int)rule.Kind}。", nameof(rule));
        }

        Rule = rule;
        Icon = icon;
        TargetZone = targetZone;
        IsOnTargetDisplay = isOnTargetDisplay;
    }
}
