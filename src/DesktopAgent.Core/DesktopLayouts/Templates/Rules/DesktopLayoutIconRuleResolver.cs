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
        DesktopLayoutIconRuleSet ruleSet,
        IReadOnlySet<string>? explicitInboundStableIds = null)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (targetDisplay is null) throw new ArgumentNullException(nameof(targetDisplay));
        if (icons is null) throw new ArgumentNullException(nameof(icons));
        if (ruleSet is null) throw new ArgumentNullException(nameof(ruleSet));
        if (ruleSet.TemplateId != template.TemplateId)
            throw new ArgumentException(
                $"RuleSet.TemplateId ({ruleSet.TemplateId}) 与 Template.TemplateId ({template.TemplateId}) 不一致。",
                nameof(ruleSet));

        ValidateInputIcons(icons);

        var explicitInbound = explicitInboundStableIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : explicitInboundStableIds.ToHashSet(StringComparer.Ordinal);
        foreach (var stableId in explicitInbound)
        {
            if (string.IsNullOrWhiteSpace(stableId))
                throw new ArgumentException(
                    "explicitInboundStableIds 不能包含 null、空或纯空白 StableId。",
                    nameof(explicitInboundStableIds));
        }

        var zoneById = template.Zones.ToDictionary(z => z.ZoneId);
        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride
                && !zoneById.ContainsKey(rule.TargetZoneId!.Value))
            {
                throw new ArgumentException(
                    $"ZoneOverride 规则的 TargetZoneId ({rule.TargetZoneId}) " +
                    $"在模板 \"{template.Name}\" 的 Zones 中不存在。");
            }
        }

        var ruleByStableId = new Dictionary<string, DesktopLayoutIconRule>(StringComparer.Ordinal);
        foreach (var rule in ruleSet.Rules)
            ruleByStableId[rule.StableId] = rule;

        // 所有显式跨屏授权都必须指向本目标布局中的 ZoneOverride。
        // 这条验证独立于“当前是否已经迁入成功”，所以成功落屏后授权可继续保留。
        foreach (var stableId in explicitInbound)
        {
            if (!ruleByStableId.TryGetValue(stableId, out var rule)
                || rule.Kind != DesktopLayoutIconRuleKind.ZoneOverride)
            {
                throw new ArgumentException(
                    $"显式跨屏授权 StableId \"{stableId}\" 必须在目标布局中存在 ZoneOverride 规则。",
                    nameof(explicitInboundStableIds));
            }
        }

        var targetIcons = new List<DesktopIconSnapshotItem>();
        var inboundIcons = new List<DesktopIconSnapshotItem>();
        var oosIcons = new List<DesktopIconSnapshotItem>();
        var matchedExplicitInbound = new HashSet<string>(StringComparer.Ordinal);

        foreach (var icon in icons)
        {
            var isOnTargetDisplay = string.Equals(
                icon.MonitorDeviceName,
                targetDisplay.DeviceName,
                StringComparison.Ordinal);

            if (isOnTargetDisplay)
            {
                targetIcons.Add(icon);
                if (explicitInbound.Contains(icon.StableId))
                    matchedExplicitInbound.Add(icon.StableId);
                continue;
            }

            if (explicitInbound.Contains(icon.StableId))
            {
                if (icon.MonitorDeviceName is null)
                {
                    throw new ArgumentException(
                        $"显式跨屏迁入 StableId \"{icon.StableId}\" 没有当前 MonitorDeviceName，" +
                        "无法证明跨屏来源。",
                        nameof(explicitInboundStableIds));
                }

                inboundIcons.Add(icon);
                matchedExplicitInbound.Add(icon.StableId);
                continue;
            }

            oosIcons.Add(icon);
        }

        if (matchedExplicitInbound.Count != explicitInbound.Count)
        {
            var missing = explicitInbound
                .Where(stableId => !matchedExplicitInbound.Contains(stableId))
                .OrderBy(static stableId => stableId, StringComparer.Ordinal)
                .ToArray();
            throw new ArgumentException(
                $"explicitInboundStableIds 中有 {missing.Length} 个 StableId 未能匹配到当前真实桌面：" +
                string.Join(", ", missing.Select(static stableId => $"\"{stableId}\"")) + "。",
                nameof(explicitInboundStableIds));
        }

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

        foreach (var icon in inboundIcons)
        {
            var rule = ruleByStableId[icon.StableId];
            matchedRules.Add(rule);
            var zone = zoneById[rule.TargetZoneId!.Value];
            zoneOverrideMatches.Add(new DesktopLayoutResolvedIconRule(
                rule,
                icon,
                zone,
                isOnTargetDisplay: false));
        }

        var oosRuleMatches = new List<DesktopLayoutResolvedIconRule>();
        foreach (var icon in oosIcons)
        {
            if (ruleByStableId.TryGetValue(icon.StableId, out var rule))
            {
                matchedRules.Add(rule);
                DesktopLayoutZone? zone = null;
                if (rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
                    zone = zoneById[rule.TargetZoneId!.Value];
                oosRuleMatches.Add(new DesktopLayoutResolvedIconRule(
                    rule, icon, zone, isOnTargetDisplay: false));
            }
        }

        var unmatchedRules = new List<DesktopLayoutIconRule>();
        foreach (var rule in ruleSet.Rules)
        {
            if (!matchedRules.Contains(rule))
                unmatchedRules.Add(rule);
        }
        unmatchedRules.Sort((a, b) => StringComparer.Ordinal.Compare(a.StableId, b.StableId));

        targetIcons.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        inboundIcons.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        oosIcons.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        defaultManaged.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        pinnedMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));
        excludedMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));
        zoneOverrideMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));
        oosRuleMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));

        return new DesktopLayoutIconRuleResolutionPlan(
            template,
            targetDisplay,
            ruleSet,
            explicitInbound.OrderBy(static stableId => stableId, StringComparer.Ordinal).ToArray(),
            icons.OrderBy(static icon => icon.Ordinal).ToList().AsReadOnly(),
            targetIcons.AsReadOnly(),
            inboundIcons.AsReadOnly(),
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
