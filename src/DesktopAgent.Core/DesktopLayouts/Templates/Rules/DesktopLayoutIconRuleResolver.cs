using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 规则解析器 — 将 RuleSet 应用于输入图标集合，生成 ResolutionPlan。
/// 纯 Core，无副作用，不修改输入。
/// </summary>
public static class DesktopLayoutIconRuleResolver
{
    public static DesktopLayoutIconRuleResolutionPlan Resolve(
        DesktopLayoutTemplate template,
        DisplayDescriptor targetDisplay,
        IReadOnlyList<DesktopIconSnapshotItem> icons,
        DesktopLayoutIconRuleSet ruleSet)
    {
        // ═══ 输入验证 ═══
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (targetDisplay is null) throw new ArgumentNullException(nameof(targetDisplay));
        if (icons is null) throw new ArgumentNullException(nameof(icons));
        if (ruleSet is null) throw new ArgumentNullException(nameof(ruleSet));
        if (ruleSet.TemplateId != template.TemplateId)
            throw new ArgumentException(
                $"RuleSet.TemplateId ({ruleSet.TemplateId}) 与 Template.TemplateId ({template.TemplateId}) 不一致。",
                nameof(ruleSet));

        // 验证 icons：无 null 元素、Ordinal 非负且唯一、StableId 唯一
        ValidateInputIcons(icons);

        // ═══ ZoneOverride Zone 验证 ═══
        // 即使没有图标匹配，只要 RuleSet 中有指向不存在 Zone 的 Override，也必须 fail
        var zoneById = template.Zones.ToDictionary(z => z.ZoneId);
        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
            {
                if (!zoneById.TryGetValue(rule.TargetZoneId!.Value, out _))
                    throw new ArgumentException(
                        $"ZoneOverride 规则的 TargetZoneId ({rule.TargetZoneId}) " +
                        $"在模板 \"{template.Name}\" 的 Zones 中不存在。");
            }
        }

        // ═══ 图标分区：TargetDisplay vs OutOfScope ═══
        var targetIcons = new List<DesktopIconSnapshotItem>();
        var oosIcons = new List<DesktopIconSnapshotItem>();
        foreach (var icon in icons)
        {
            if (string.Equals(icon.MonitorDeviceName, targetDisplay.DeviceName, StringComparison.Ordinal))
                targetIcons.Add(icon);
            else
                oosIcons.Add(icon);
        }

        // ═══ 规则索引：StableId → Rule ═══
        var ruleByStableId = new Dictionary<string, DesktopLayoutIconRule>(StringComparer.Ordinal);
        foreach (var rule in ruleSet.Rules)
            ruleByStableId[rule.StableId] = rule;

        // ═══ Target 图标规则匹配 ═══
        var defaultManaged = new List<DesktopIconSnapshotItem>();
        var pinnedMatches = new List<DesktopLayoutResolvedIconRule>();
        var excludedMatches = new List<DesktopLayoutResolvedIconRule>();
        var zoneOverrideMatches = new List<DesktopLayoutResolvedIconRule>();

        var matchedRules = new HashSet<DesktopLayoutIconRule>(ReferenceEqualityComparer.Instance);

        foreach (var icon in targetIcons)
        {
            if (ruleByStableId.TryGetValue(icon.StableId, out var rule))
            {
                matchedRules.Add(rule);
                DesktopLayoutResolvedIconRule resolved;
                switch (rule.Kind)
                {
                    case DesktopLayoutIconRuleKind.Exclude:
                        resolved = new DesktopLayoutResolvedIconRule(
                            rule, icon, null, isOnTargetDisplay: true);
                        excludedMatches.Add(resolved);
                        break;
                    case DesktopLayoutIconRuleKind.Pin:
                        resolved = new DesktopLayoutResolvedIconRule(
                            rule, icon, null, isOnTargetDisplay: true);
                        pinnedMatches.Add(resolved);
                        break;
                    case DesktopLayoutIconRuleKind.ZoneOverride:
                        var zone = zoneById[rule.TargetZoneId!.Value];
                        resolved = new DesktopLayoutResolvedIconRule(
                            rule, icon, zone, isOnTargetDisplay: true);
                        zoneOverrideMatches.Add(resolved);
                        break;
                    default:
                        throw new ArgumentException($"无效的 RuleKind：{(int)rule.Kind}。");
                }
            }
            else
            {
                defaultManaged.Add(icon);
            }
        }

        // ═══ OutOfScope 图标规则匹配 ═══
        var oosRuleMatches = new List<DesktopLayoutResolvedIconRule>();
        foreach (var icon in oosIcons)
        {
            if (ruleByStableId.TryGetValue(icon.StableId, out var rule))
            {
                matchedRules.Add(rule);
                DesktopLayoutZone? zone = null;
                if (rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
                    zone = zoneById[rule.TargetZoneId!.Value];
                var resolved = new DesktopLayoutResolvedIconRule(
                    rule, icon, zone, isOnTargetDisplay: false);
                oosRuleMatches.Add(resolved);
            }
        }

        // ═══ Unmatched 规则 ═══
        var unmatchedRules = new List<DesktopLayoutIconRule>();
        foreach (var rule in ruleSet.Rules)
        {
            if (!matchedRules.Contains(rule))
                unmatchedRules.Add(rule);
        }
        // 按 StableId Ordinal ASC
        unmatchedRules.Sort((a, b) => StringComparer.Ordinal.Compare(a.StableId, b.StableId));

        // ═══ 输出排序 ═══
        // 图标集合按 Ordinal ASC
        targetIcons.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        oosIcons.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        defaultManaged.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        // 规则匹配集合按 Icon.Ordinal ASC
        pinnedMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));
        excludedMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));
        zoneOverrideMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));
        oosRuleMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));

        return new DesktopLayoutIconRuleResolutionPlan(
            template, targetDisplay, ruleSet,
            icons.ToList().AsReadOnly(),
            targetIcons.AsReadOnly(),
            oosIcons.AsReadOnly(),
            defaultManaged.AsReadOnly(),
            pinnedMatches.AsReadOnly(),
            excludedMatches.AsReadOnly(),
            zoneOverrideMatches.AsReadOnly(),
            oosRuleMatches.AsReadOnly(),
            unmatchedRules.AsReadOnly());
    }

    private static void ValidateInputIcons(IReadOnlyList<DesktopIconSnapshotItem> icons)
    {
        if (icons.Count == 0)
            return;

        var seenOrdinals = new HashSet<int>();
        var seenStableIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var icon in icons)
        {
            if (icon is null)
                throw new ArgumentException("icons 不能包含 null 元素。", nameof(icons));
            if (icon.Ordinal < 0)
                throw new ArgumentException(
                    $"图标 Ordinal 必须非负，实际 {icon.Ordinal}。", nameof(icons));
            if (!seenOrdinals.Add(icon.Ordinal))
                throw new ArgumentException(
                    $"图标 Ordinal 必须唯一，重复值：{icon.Ordinal}。", nameof(icons));
            if (!seenStableIds.Add(icon.StableId))
                throw new ArgumentException(
                    $"图标 StableId 必须唯一（Ordinal），重复值：\"{icon.StableId}\"。", nameof(icons));
        }
    }
}
