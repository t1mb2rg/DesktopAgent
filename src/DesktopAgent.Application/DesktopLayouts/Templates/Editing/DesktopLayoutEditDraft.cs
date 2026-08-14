using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 产品布局编辑中的内存草稿。草稿本身没有 Writer authority；
/// Preview 与 Save 都必须从草稿重新构造经过 Core 验证的不可变领域对象。
///
/// ExplicitInboundStableIds 只存在于内存编辑语义中，表示用户明确授权从其他显示器迁入
/// 本草稿目标显示器的项目。它不会进入 DesktopLayoutTemplate / SQLite；普通布局默认为空。
/// </summary>
public sealed class DesktopLayoutEditDraft
{
    public Guid TemplateId { get; }
    public int SourceVersion { get; }
    public string Name { get; }
    public TargetDisplayBinding TargetDisplayBinding { get; }
    public IReadOnlyList<DesktopLayoutZone> Zones { get; }
    public DesktopLayoutIconRuleSet RuleSet { get; }
    public IReadOnlyList<string> ExplicitInboundStableIds { get; }

    public bool IsNew => SourceVersion == 0;
    public bool HasExplicitInbound => ExplicitInboundStableIds.Count > 0;

    public DesktopLayoutEditDraft(
        Guid templateId,
        int sourceVersion,
        string name,
        TargetDisplayBinding targetDisplayBinding,
        IReadOnlyList<DesktopLayoutZone> zones,
        DesktopLayoutIconRuleSet ruleSet,
        IReadOnlyCollection<string>? explicitInboundStableIds = null)
    {
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为 Guid.Empty。", nameof(templateId));
        if (sourceVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));
        ArgumentNullException.ThrowIfNull(targetDisplayBinding);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(ruleSet);
        if (zones.Count == 0)
            throw new ArgumentException("草稿至少需要一个 Zone。", nameof(zones));
        if (ruleSet.TemplateId != templateId)
            throw new ArgumentException("RuleSet.TemplateId 必须与草稿 TemplateId 一致。", nameof(ruleSet));

        // 用领域构造函数做一次完整结构校验，但不推进 Version。
        _ = new DesktopLayoutTemplate(
            templateId,
            name.Trim(),
            sourceVersion == 0 ? 1 : sourceVersion,
            targetDisplayBinding,
            zones);

        var inbound = NormalizeExplicitInbound(explicitInboundStableIds, ruleSet);

        TemplateId = templateId;
        SourceVersion = sourceVersion;
        Name = name.Trim();
        TargetDisplayBinding = targetDisplayBinding;
        Zones = Array.AsReadOnly(zones.ToArray());
        RuleSet = ruleSet;
        ExplicitInboundStableIds = Array.AsReadOnly(inbound);
    }

    public static DesktopLayoutEditDraft FromPersisted(
        DesktopLayoutTemplate template,
        DesktopLayoutIconRuleSet? ruleSet)
    {
        ArgumentNullException.ThrowIfNull(template);
        var effectiveRules = ruleSet ?? DesktopLayoutIconRuleSet.Empty(template.TemplateId);
        return new DesktopLayoutEditDraft(
            template.TemplateId,
            template.Version,
            template.Name,
            template.TargetDisplayBinding,
            template.Zones,
            effectiveRules);
    }

    public DesktopLayoutEditDraft WithMetadata(
        string name,
        TargetDisplayBinding targetDisplayBinding)
        => new(
            TemplateId,
            SourceVersion,
            name,
            targetDisplayBinding,
            Zones,
            RuleSet,
            ExplicitInboundStableIds);

    public DesktopLayoutEditDraft WithZones(IReadOnlyList<DesktopLayoutZone> zones)
        => new(
            TemplateId,
            SourceVersion,
            Name,
            TargetDisplayBinding,
            zones,
            RuleSet,
            ExplicitInboundStableIds);

    public DesktopLayoutEditDraft WithRuleSet(
        DesktopLayoutIconRuleSet ruleSet,
        IReadOnlyCollection<string>? explicitInboundStableIds = null)
        => new(
            TemplateId,
            SourceVersion,
            Name,
            TargetDisplayBinding,
            Zones,
            ruleSet,
            explicitInboundStableIds ?? ExplicitInboundStableIds);

    /// <summary>
    /// Preview 使用当前版本语义，不产生可执行命令。
    /// </summary>
    public DesktopLayoutTemplate BuildPreviewTemplate()
        => new(
            TemplateId,
            Name,
            SourceVersion == 0 ? 1 : SourceVersion,
            TargetDisplayBinding,
            Zones);

    /// <summary>
    /// 保存时新模板从 V1 开始，已有模板严格推进一个版本。
    /// ExplicitInboundStableIds 有意不进入持久化模板；跨屏迁移是当前工作区的显式用户意图，
    /// 在物理迁移完成后目标 ZoneOverride 本身即可表达落屏后的稳定布局。
    /// </summary>
    public DesktopLayoutTemplate BuildNextPersistedTemplate()
        => new(
            TemplateId,
            Name,
            SourceVersion == 0 ? 1 : checked(SourceVersion + 1),
            TargetDisplayBinding,
            Zones);

    private static string[] NormalizeExplicitInbound(
        IReadOnlyCollection<string>? explicitInboundStableIds,
        DesktopLayoutIconRuleSet ruleSet)
    {
        if (explicitInboundStableIds is null || explicitInboundStableIds.Count == 0)
            return Array.Empty<string>();

        var zoneOverrides = ruleSet.Rules
            .Where(static rule => rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
            .Select(static rule => rule.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var normalized = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stableId in explicitInboundStableIds)
        {
            if (string.IsNullOrWhiteSpace(stableId))
                throw new ArgumentException(
                    "ExplicitInboundStableIds 不能包含 null、空或纯空白。",
                    nameof(explicitInboundStableIds));
            if (!normalized.Add(stableId))
                throw new ArgumentException(
                    $"ExplicitInboundStableIds 中 StableId \"{stableId}\" 重复。",
                    nameof(explicitInboundStableIds));
            if (!zoneOverrides.Contains(stableId))
                throw new ArgumentException(
                    $"显式迁入 StableId \"{stableId}\" 必须在本 Draft 中拥有 ZoneOverride 规则。",
                    nameof(explicitInboundStableIds));
        }

        return normalized
            .OrderBy(static stableId => stableId, StringComparer.Ordinal)
            .ToArray();
    }
}
