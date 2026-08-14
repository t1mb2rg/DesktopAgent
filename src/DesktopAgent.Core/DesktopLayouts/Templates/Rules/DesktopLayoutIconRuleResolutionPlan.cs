using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 规则解析结果 — 不可变 sealed class。
/// 包含图标分区、规则匹配、未匹配规则，全部引用身份验证。
/// </summary>
public sealed class DesktopLayoutIconRuleResolutionPlan
{
    public DesktopLayoutTemplate Template { get; }
    public DisplayDescriptor TargetDisplay { get; }
    public DesktopLayoutIconRuleSet RuleSet { get; }

    // ═══ 图标分区 ═══

    /// <summary>全部输入图标（按 Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopIconSnapshotItem> InputIcons { get; }

    /// <summary>当前物理位于目标显示器的图标（按 Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopIconSnapshotItem> TargetDisplayIcons { get; }

    /// <summary>
    /// 当前物理不在目标显示器，但由产品层显式授权迁入、且存在 ZoneOverride 的图标。
    /// AI 初始规则不会自动进入这里；调用方必须单独提交 explicit-inbound StableId 白名单。
    /// </summary>
    public IReadOnlyList<DesktopIconSnapshotItem> InboundMigrationIcons { get; }

    /// <summary>既不在目标显示器，也未被显式授权迁入的图标（按 Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopIconSnapshotItem> OutOfScopeIcons { get; }

    /// <summary>无规则或未匹配规则的默认管理图标（仅来自当前物理目标屏，按 Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopIconSnapshotItem> DefaultManagedIcons { get; }

    // ═══ 规则匹配 ═══

    /// <summary>Pin 规则匹配（按 Icon.Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopLayoutResolvedIconRule> PinnedMatches { get; }

    /// <summary>Exclude 规则匹配（按 Icon.Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopLayoutResolvedIconRule> ExcludedMatches { get; }

    /// <summary>
    /// ZoneOverride 规则匹配（按 Icon.Ordinal ASC）。
    /// 包含目标屏内 Override，以及显式授权的跨屏迁入 Override；后者 IsOnTargetDisplay=false。
    /// </summary>
    public IReadOnlyList<DesktopLayoutResolvedIconRule> ZoneOverrideMatches { get; }

    /// <summary>真正 OutOfScope 图标的规则匹配（按 Icon.Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopLayoutResolvedIconRule> OutOfScopeRuleMatches { get; }

    /// <summary>未匹配到任何图标的规则（按 StableId Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopLayoutIconRule> UnmatchedRules { get; }

    // ═══ 统计 ═══

    public int InputIconCount => InputIcons.Count;
    public int TargetDisplayIconCount => TargetDisplayIcons.Count;
    public int InboundMigrationIconCount => InboundMigrationIcons.Count;
    public int ManagedTargetIconCount => TargetDisplayIconCount + InboundMigrationIconCount;
    public int OutOfScopeIconCount => OutOfScopeIcons.Count;
    public int DefaultManagedIconCount => DefaultManagedIcons.Count;
    public int PinnedCount => PinnedMatches.Count;
    public int ExcludedCount => ExcludedMatches.Count;
    public int ZoneOverrideCount => ZoneOverrideMatches.Count;
    public int InboundZoneOverrideCount => ZoneOverrideMatches.Count(match => !match.IsOnTargetDisplay);
    public int OutOfScopeRuleMatchCount => OutOfScopeRuleMatches.Count;
    public int UnmatchedRuleCount => UnmatchedRules.Count;
    public int EffectiveRuleCount => PinnedCount + ExcludedCount + ZoneOverrideCount;
    public bool HasRules => RuleSet.HasRules;
    public bool HasEffectiveRules => EffectiveRuleCount > 0;
    public bool HasUnmatchedRules => UnmatchedRuleCount > 0;
    public bool HasInboundMigrations => InboundMigrationIconCount > 0;

    internal DesktopLayoutIconRuleResolutionPlan(
        DesktopLayoutTemplate template,
        DisplayDescriptor targetDisplay,
        DesktopLayoutIconRuleSet ruleSet,
        IReadOnlyList<DesktopIconSnapshotItem> inputIcons,
        IReadOnlyList<DesktopIconSnapshotItem> targetDisplayIcons,
        IReadOnlyList<DesktopIconSnapshotItem> inboundMigrationIcons,
        IReadOnlyList<DesktopIconSnapshotItem> outOfScopeIcons,
        IReadOnlyList<DesktopIconSnapshotItem> defaultManagedIcons,
        IReadOnlyList<DesktopLayoutResolvedIconRule> pinnedMatches,
        IReadOnlyList<DesktopLayoutResolvedIconRule> excludedMatches,
        IReadOnlyList<DesktopLayoutResolvedIconRule> zoneOverrideMatches,
        IReadOnlyList<DesktopLayoutResolvedIconRule> outOfScopeRuleMatches,
        IReadOnlyList<DesktopLayoutIconRule> unmatchedRules)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));
        TargetDisplay = targetDisplay ?? throw new ArgumentNullException(nameof(targetDisplay));
        RuleSet = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));

        // 防御性复制
        InputIcons = inputIcons.ToList().AsReadOnly();
        TargetDisplayIcons = targetDisplayIcons.ToList().AsReadOnly();
        InboundMigrationIcons = inboundMigrationIcons.ToList().AsReadOnly();
        OutOfScopeIcons = outOfScopeIcons.ToList().AsReadOnly();
        DefaultManagedIcons = defaultManagedIcons.ToList().AsReadOnly();
        PinnedMatches = pinnedMatches.ToList().AsReadOnly();
        ExcludedMatches = excludedMatches.ToList().AsReadOnly();
        ZoneOverrideMatches = zoneOverrideMatches.ToList().AsReadOnly();
        OutOfScopeRuleMatches = outOfScopeRuleMatches.ToList().AsReadOnly();
        UnmatchedRules = unmatchedRules.ToList().AsReadOnly();
    }
}
