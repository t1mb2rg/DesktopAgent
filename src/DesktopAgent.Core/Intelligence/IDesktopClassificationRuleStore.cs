namespace DesktopAgent.Core.Intelligence;

/// <summary>Provider / UI 无关的 M4 分类规则持久化边界。</summary>
public interface IDesktopClassificationRuleStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>按 RuleId 新增或完整替换一条规则。</summary>
    Task SaveAsync(
        DesktopClassificationRule rule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 返回全部持久化规则，使用稳定 RuleId 顺序。
    /// 实现必须自行施加有界读取，不能把损坏/超量数据库无界装入内存。
    /// </summary>
    Task<IReadOnlyList<DesktopClassificationRule>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid ruleId,
        CancellationToken cancellationToken = default);
}
