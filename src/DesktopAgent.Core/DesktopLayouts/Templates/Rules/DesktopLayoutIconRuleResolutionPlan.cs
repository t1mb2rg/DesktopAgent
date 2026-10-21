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

    /// <summary>
    /// 产品层显式提交的跨屏迁入授权 StableId。它是用户意图，不等于“当前仍在别屏”。
    /// 某个图标迁移成功后可以继续保留在此集合中，此时它会作为普通 TargetDisplayIcon 管理。
    /// </summary>
    public IReadOnlyList<string> ExplicitInboundStableIds { get; }

    // ═══ 图标分区 ═══

    /// <summary>全部输入图标（按 Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopIconSnapshotItem> InputIcons { get; }

    /// <summary>当前物理位于目标显示器的图标（按 Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopIconSnapshotItem> TargetDisplayIcons { get; }

    /// <summary>
    /// 当前物理不在目标显示器，但由产品层显式授权迁入、且存在 ZoneOverride 的图标。
    /// </summary>
    public IReadOnlyList<DesktopIconSnapshotItem> InboundMigrationIcons { get; }

    /// <summary>既不在目标显示器，也未被显式授权迁入的图标（按 Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopIconSnapshotItem> OutOfScopeIcons { get; }

    /// <summary>无规则或未匹配规则的默认管理图标（仅来自当前物理目标屏，按 Ordinal ASC）。</summary>
    public IReadOnlyList<DesktopIconSnapshotItem> DefaultManagedIcons { get; }

    // ═══ 规则匹配 ═══

    public IReadOnlyList<DesktopLayoutResolvedIconRule> PinnedMatches { get; }
    public IReadOnlyList<DesktopLayoutResolvedIconRule> ExcludedMatches { get; }

    /// <summary>
    /// ZoneOverride 规则匹配。包含目标屏内 Override，以及显式授权且仍在屏外的迁入 Override；
    /// 后者 IsOnTargetDisplay=false。
    /// </summary>
    public IReadOnlyList<DesktopLayoutResolvedIconRule> ZoneOverrideMatches { get; }

    public IReadOnlyList<DesktopLayoutResolvedIconRule> OutOfScopeRuleMatches { get; }
    public IReadOnlyList<DesktopLayoutIconRule> UnmatchedRules { get; }

    // ═══ 统计 ═══

    public int InputIconCount => InputIcons.Count;
    public int ExplicitInboundAuthorizationCount => ExplicitInboundStableIds.Count;
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
    public bool HasExplicitInboundAuthorization => ExplicitInboundAuthorizationCount > 0;
    public bool HasInboundMigrations => InboundMigrationIconCount > 0;

    internal DesktopLayoutIconRuleResolutionPlan(
        DesktopLayoutTemplate template,
        DisplayDescriptor targetDisplay,
        DesktopLayoutIconRuleSet ruleSet,
        IReadOnlyList<string> explicitInboundStableIds,
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

        ExplicitInboundStableIds = explicitInboundStableIds.ToList().AsReadOnly();
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
