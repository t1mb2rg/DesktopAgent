using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 固定障碍项 — Exclude 或 Pin 规则的结果。
/// 表示一个图标保持当前位置、不产生移动目标、作为固定障碍占用桌面空间。
/// </summary>
public sealed class DesktopLayoutRuleAwareFixedItem
{
    /// <summary>原始图标（InputIcons 中的原对象引用）。</summary>
    public DesktopIconSnapshotItem Icon { get; }

    /// <summary>匹配的规则（RuleSet 中的原对象引用）。</summary>
    public DesktopLayoutIconRule Rule { get; }

    /// <summary>固定障碍类型。</summary>
    public DesktopLayoutIconRuleKind Kind { get; }

    /// <summary>图标当前像素位置。</summary>
    public PixelPoint CurrentPosition { get; }

    /// <summary>
    /// 固定障碍矩形 [position, position+spacing)，半开边界。
    /// 任何与此矩形发生半开重叠的候选槽位均不可用。
    /// </summary>
    public PixelRect FixedRect { get; }

    public DesktopLayoutRuleAwareFixedItem(
        DesktopIconSnapshotItem icon,
        DesktopLayoutIconRule rule,
        PixelPoint iconSpacing)
    {
        if (icon is null) throw new ArgumentNullException(nameof(icon));
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (rule.Kind != DesktopLayoutIconRuleKind.Exclude
            && rule.Kind != DesktopLayoutIconRuleKind.Pin)
            throw new ArgumentException(
                $"FixedItem 只支持 Exclude 或 Pin 规则，实际 Kind={rule.Kind}。", nameof(rule));
        if (iconSpacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.X 必须大于 0，实际 {iconSpacing.X}。");
        if (iconSpacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.Y 必须大于 0，实际 {iconSpacing.Y}。");

        // 验证 Rule.StableId 与 Icon.StableId 匹配
        if (!StringComparer.Ordinal.Equals(rule.StableId, icon.StableId))
            throw new ArgumentException(
                $"Rule.StableId \"{rule.StableId}\" 与 Icon.StableId \"{icon.StableId}\" 不一致。");

        Icon = icon;
        Rule = rule;
        Kind = rule.Kind;
        CurrentPosition = icon.PixelPosition;

        // CreateRectChecked 使用 checked 安全策略
        FixedRect = DesktopIconLayoutGeometry.CreateRectChecked(CurrentPosition, iconSpacing);
    }
}
