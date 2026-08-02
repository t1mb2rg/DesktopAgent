namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 模板级规则集合 — 不可变 sealed class。
/// 同一 StableId 最多一条规则，按 StableId Ordinal 升序保存。
/// </summary>
public sealed class DesktopLayoutIconRuleSet
{
    /// <summary>所属模板 ID。</summary>
    public Guid TemplateId { get; }

    /// <summary>规则列表，按 StableId Ordinal ASC。</summary>
    public IReadOnlyList<DesktopLayoutIconRule> Rules { get; }

    /// <summary>是否包含任何规则。</summary>
    public bool HasRules => Rules.Count > 0;

    /// <summary>规则数量。</summary>
    public int RuleCount => Rules.Count;

    public DesktopLayoutIconRuleSet(Guid templateId, IReadOnlyList<DesktopLayoutIconRule> rules)
    {
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为 Guid.Empty。", nameof(templateId));
        if (rules is null)
            throw new ArgumentNullException(nameof(rules));

        // 验证无 null 元素 + StableId 唯一
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            if (r is null)
                throw new ArgumentException("Rules 不能包含 null 元素。", nameof(rules));
            if (!seen.Add(r.StableId))
                throw new ArgumentException(
                    $"同一 StableId 不允许出现多条规则：\"{r.StableId}\"。", nameof(rules));
        }

        TemplateId = templateId;

        // 按 StableId Ordinal ASC 排序，防御性复制
        Rules = rules
            .OrderBy(r => r.StableId, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>创建空规则集。</summary>
    public static DesktopLayoutIconRuleSet Empty(Guid templateId)
        => new(templateId, Array.Empty<DesktopLayoutIconRule>());
}
