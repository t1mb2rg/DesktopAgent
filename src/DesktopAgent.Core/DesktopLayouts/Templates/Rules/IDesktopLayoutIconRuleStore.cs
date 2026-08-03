namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 桌面图标规则持久化存储。
/// 独立于 IDesktopLayoutTemplateStore，按 TemplateId 原子完整替换。
/// </summary>
public interface IDesktopLayoutIconRuleStore
{
    /// <summary>初始化数据库（幂等）。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 TemplateId 原子完整替换规则集合。
    /// 保存空 RuleSet 即删除该模板全部规则。
    /// </summary>
    Task SaveAsync(
        DesktopLayoutIconRuleSet ruleSet,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取模板的完整规则集合。
    /// 模板不存在时返回 null；模板存在但无规则时返回
    /// DesktopLayoutIconRuleSet.Empty(templateId)。
    /// </summary>
    Task<DesktopLayoutIconRuleSet?> GetAsync(
        Guid templateId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除模板的全部规则（不删除模板本身）。
    /// 返回是否实际删除至少一条规则。
    /// </summary>
    Task<bool> DeleteAsync(
        Guid templateId,
        CancellationToken cancellationToken = default);
}
