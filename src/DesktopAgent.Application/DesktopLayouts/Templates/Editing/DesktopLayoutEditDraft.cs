using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 产品布局编辑中的内存草稿。草稿本身没有 Writer authority；
/// Preview 与 Save 都必须从草稿重新构造经过 Core 验证的不可变领域对象。
/// </summary>
public sealed class DesktopLayoutEditDraft
{
    public Guid TemplateId { get; }
    public int SourceVersion { get; }
    public string Name { get; }
    public TargetDisplayBinding TargetDisplayBinding { get; }
    public IReadOnlyList<DesktopLayoutZone> Zones { get; }
    public DesktopLayoutIconRuleSet RuleSet { get; }

    public bool IsNew => SourceVersion == 0;

    public DesktopLayoutEditDraft(
        Guid templateId,
        int sourceVersion,
        string name,
        TargetDisplayBinding targetDisplayBinding,
        IReadOnlyList<DesktopLayoutZone> zones,
        DesktopLayoutIconRuleSet ruleSet)
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

        TemplateId = templateId;
        SourceVersion = sourceVersion;
        Name = name.Trim();
        TargetDisplayBinding = targetDisplayBinding;
        Zones = Array.AsReadOnly(zones.ToArray());
        RuleSet = ruleSet;
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
            RuleSet);

    public DesktopLayoutEditDraft WithZones(IReadOnlyList<DesktopLayoutZone> zones)
        => new(
            TemplateId,
            SourceVersion,
            Name,
            TargetDisplayBinding,
            zones,
            RuleSet);

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
    /// </summary>
    public DesktopLayoutTemplate BuildNextPersistedTemplate()
        => new(
            TemplateId,
            Name,
            SourceVersion == 0 ? 1 : checked(SourceVersion + 1),
            TargetDisplayBinding,
            Zones);
}
