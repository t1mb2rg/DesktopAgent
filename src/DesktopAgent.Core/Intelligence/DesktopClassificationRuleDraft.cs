namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// M4-05 自然语言规则生成得到的可检查草稿。
/// Draft 本身没有 Policy authority；只有用户确认后才转换为 ExplicitUser Rule。
/// </summary>
public sealed class DesktopClassificationRuleDraft
{
    public const int MaxNameLength = 128;
    public const int MaxCategoryLength = 128;
    public const int MaxReasonLength = 1024;

    public Guid DraftId { get; }
    public string Name { get; }
    public DesktopClassificationRuleCondition Condition { get; }
    public string Category { get; }
    public Guid? TargetTemplateId { get; }
    public Guid? TargetZoneId { get; }
    public string Reason { get; }

    public DesktopClassificationRuleDraft(
        Guid draftId,
        string name,
        DesktopClassificationRuleCondition condition,
        string category,
        Guid? targetTemplateId,
        Guid? targetZoneId,
        string reason)
    {
        if (draftId == Guid.Empty)
            throw new ArgumentException("DraftId 不能为 Guid.Empty。", nameof(draftId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            throw new ArgumentException($"Name 必须为 1..{MaxNameLength} 个字符的非空文本。", nameof(name));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        if (string.IsNullOrWhiteSpace(category) || category.Length > MaxCategoryLength)
            throw new ArgumentException($"Category 必须为 1..{MaxCategoryLength} 个字符的非空文本。", nameof(category));
        if (targetTemplateId == Guid.Empty)
            throw new ArgumentException("TargetTemplateId 不能为 Guid.Empty。", nameof(targetTemplateId));
        if (targetZoneId == Guid.Empty)
            throw new ArgumentException("TargetZoneId 不能为 Guid.Empty。", nameof(targetZoneId));
        if (targetTemplateId.HasValue != targetZoneId.HasValue)
            throw new ArgumentException("TargetTemplateId 与 TargetZoneId 必须同时存在或同时为 null。", nameof(targetZoneId));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonLength)
            throw new ArgumentException($"Reason 必须为 1..{MaxReasonLength} 个字符的非空文本。", nameof(reason));

        DraftId = draftId;
        Name = name;
        Category = category;
        TargetTemplateId = targetTemplateId;
        TargetZoneId = targetZoneId;
        Reason = reason;
    }

    /// <summary>
    /// 用户在 UI 中修改草稿时使用；保留 DraftId 与原始 AI explanation，替换可编辑规则字段。
    /// </summary>
    public DesktopClassificationRuleDraft WithEdits(
        string name,
        DesktopClassificationRuleCondition condition,
        string category,
        Guid? targetTemplateId,
        Guid? targetZoneId)
        => new(
            DraftId,
            name,
            condition,
            category,
            targetTemplateId,
            targetZoneId,
            Reason);

    /// <summary>
    /// 只有显式用户确认后才把 Draft 铸成正式 Rule；不会自动持久化。
    /// </summary>
    public DesktopClassificationRule Confirm(Guid ruleId, int priority = 0)
        => new(
            ruleId,
            Name,
            Condition,
            Category,
            TargetTemplateId,
            TargetZoneId,
            DesktopClassificationRuleAuthority.ExplicitUser,
            DesktopClassificationRuleProvenance.AiProposedAndConfirmed,
            priority,
            isEnabled: true);
}
