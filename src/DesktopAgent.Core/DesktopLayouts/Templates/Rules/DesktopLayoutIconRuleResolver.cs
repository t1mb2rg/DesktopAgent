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

        // ═══ 规则索引：StableId → Rule ═══
        var ruleByStableId = new Dictionary<string, DesktopLayoutIconRule>(StringComparer.Ordinal);
        foreach (var rule in ruleSet.Rules)
            ruleByStableId[rule.StableId] = rule;

        // ═══ 图标分区：物理 Target / 显式 Inbound / 真正 OutOfScope ═══
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

                if (!ruleByStableId.TryGetValue(icon.StableId, out var inboundRule)
                    || inboundRule.Kind != DesktopLayoutIconRuleKind.ZoneOverride)
                {
                    throw new ArgumentException(
                        $"显式跨屏迁入 StableId \"{icon.StableId}\" 必须在目标布局中存在 ZoneOverride 规则。",
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
                $"explicitInboundStableIds 中有 {missing.Length} 个 StableId 未能匹配到屏外真实图标：" +
                string.Join(", ", missing.Select(static stableId => $"\"{stableId}\"")) + "。",
                nameof(explicitInboundStableIds));
        }

        // ═══ 目标管理范围规则匹配 ═══
        var defaultManaged = new List<DesktopIconSnapshotItem>();
        var pinnedMatches = new List<DesktopLayoutResolvedIconRule>();
        var excludedMatches = new List<DesktopLayoutResolvedIconRule>();
        var zoneOverrideMatches = new List<DesktopLayoutResolvedIconRule>();

        var matchedRules = new HashSet<DesktopLayoutIconRule>(ReferenceEqualityComparer.Instance);

        // 物理位于目标屏：保持旧规则语义。
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

        // 显式迁入：必须是 ZoneOverride，并明确保留 IsOnTargetDisplay=false 作为来源证据。
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

        // ═══ 真正 OutOfScope 图标规则匹配 ═══
        // 未被产品显式授权迁入的屏外 ZoneOverride 继续保持历史语义：记录但不生效。
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
        inboundIcons.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        oosIcons.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        defaultManaged.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        // 规则匹配集合按 Icon.Ordinal ASC
        pinnedMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));
        excludedMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));
        zoneOverrideMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));
        oosRuleMatches.Sort((a, b) => a.Icon.Ordinal.CompareTo(b.Icon.Ordinal));

        return new DesktopLayoutIconRuleResolutionPlan(
            template, targetDisplay, ruleSet,
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
