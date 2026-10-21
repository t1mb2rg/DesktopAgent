using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// 用户已经明确确认的 Category → 当前布局模板 Zone 偏好。
/// 这是软偏好，不等于长期规则，也不能脱离 TemplateId 解释 ZoneId。
/// </summary>
public sealed class DesktopCategoryZonePreference
{
    public Guid PreferenceId { get; }
    public Guid TemplateId { get; }
    public string Category { get; }
    public Guid PreferredZoneId { get; }
    public double Confidence { get; }
    public int EvidenceCount { get; }
    public DateTimeOffset ConfirmedAtUtc { get; }

    public DesktopCategoryZonePreference(
        Guid preferenceId,
        Guid templateId,
        string category,
        Guid preferredZoneId,
        double confidence,
        int evidenceCount,
        DateTimeOffset confirmedAtUtc)
    {
        if (preferenceId == Guid.Empty)
            throw new ArgumentException("PreferenceId 不能为 Guid.Empty。", nameof(preferenceId));
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为 Guid.Empty。", nameof(templateId));
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Category 不能为空。", nameof(category));
        if (preferredZoneId == Guid.Empty)
            throw new ArgumentException("PreferredZoneId 不能为 Guid.Empty。", nameof(preferredZoneId));
        if (!double.IsFinite(confidence) || confidence is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence 必须位于 [0, 1]。");
        if (evidenceCount < 1)
            throw new ArgumentOutOfRangeException(nameof(evidenceCount), "EvidenceCount 必须至少为 1。");
        if (confirmedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ConfirmedAtUtc 必须使用 UTC。", nameof(confirmedAtUtc));

        PreferenceId = preferenceId;
        TemplateId = templateId;
        Category = category;
        PreferredZoneId = preferredZoneId;
        Confidence = confidence;
        EvidenceCount = evidenceCount;
        ConfirmedAtUtc = confirmedAtUtc;
    }
}

public enum DesktopClassificationFeedbackKind
{
    Accepted = 0,
    Corrected = 1,
    Rejected = 2,
}

/// <summary>
/// 一次“系统怎么建议、用户最终怎么选择”的本地历史证据。
/// Evidence 只用于解释与学习候选生成，本身没有 Policy authority，不能自动升级成 Rule。
/// </summary>
public sealed class DesktopClassificationBehaviorEvidence
{
    public Guid EvidenceId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public Guid? TemplateId { get; }
    public string StableId { get; }
    public string DisplayName { get; }
    public string? FileExtension { get; }
    public ItemKind ItemKind { get; }
    public string ProposedCategory { get; }
    public Guid? ProposedZoneId { get; }
    public DesktopItemClassificationSource ProposedSource { get; }
    public DesktopClassificationFeedbackKind FeedbackKind { get; }
    public string? FinalCategory { get; }
    public Guid? FinalZoneId { get; }

    public DesktopClassificationBehaviorEvidence(
        Guid evidenceId,
        DateTimeOffset observedAtUtc,
        Guid? templateId,
        string stableId,
        string displayName,
        string? fileExtension,
        ItemKind itemKind,
        string proposedCategory,
        Guid? proposedZoneId,
        DesktopItemClassificationSource proposedSource,
        DesktopClassificationFeedbackKind feedbackKind,
        string? finalCategory,
        Guid? finalZoneId)
    {
        if (evidenceId == Guid.Empty)
            throw new ArgumentException("EvidenceId 不能为 Guid.Empty。", nameof(evidenceId));
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ObservedAtUtc 必须使用 UTC。", nameof(observedAtUtc));
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为 Guid.Empty。", nameof(templateId));
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("DisplayName 不能为空。", nameof(displayName));
        if (fileExtension is not null)
        {
            if (string.IsNullOrWhiteSpace(fileExtension) ||
                !fileExtension.StartsWith(".", StringComparison.Ordinal) ||
                fileExtension.Length == 1)
            {
                throw new ArgumentException("FileExtension 应为 null 或形如 .pdf。", nameof(fileExtension));
            }
        }
        if (!Enum.IsDefined(itemKind))
            throw new ArgumentOutOfRangeException(nameof(itemKind));
        if (string.IsNullOrWhiteSpace(proposedCategory))
            throw new ArgumentException("ProposedCategory 不能为空。", nameof(proposedCategory));
        if (proposedZoneId == Guid.Empty)
            throw new ArgumentException("ProposedZoneId 不能为 Guid.Empty。", nameof(proposedZoneId));
        if (!Enum.IsDefined(proposedSource))
            throw new ArgumentOutOfRangeException(nameof(proposedSource));
        if (!Enum.IsDefined(feedbackKind))
            throw new ArgumentOutOfRangeException(nameof(feedbackKind));
        if (finalCategory is not null && string.IsNullOrWhiteSpace(finalCategory))
            throw new ArgumentException("FinalCategory 应使用 null 表示不存在，不能是空白。", nameof(finalCategory));
        if (finalZoneId == Guid.Empty)
            throw new ArgumentException("FinalZoneId 不能为 Guid.Empty。", nameof(finalZoneId));

        if ((proposedZoneId.HasValue || finalZoneId.HasValue) && !templateId.HasValue)
            throw new ArgumentException("存在 ZoneId 的行为证据必须同时记录 TemplateId。", nameof(templateId));

        switch (feedbackKind)
        {
            case DesktopClassificationFeedbackKind.Accepted:
                if (finalCategory is null ||
                    !string.Equals(proposedCategory, finalCategory, StringComparison.OrdinalIgnoreCase) ||
                    proposedZoneId != finalZoneId)
                {
                    throw new ArgumentException("Accepted 的 Final 结果必须与 Proposed 结果一致。", nameof(finalCategory));
                }
                break;

            case DesktopClassificationFeedbackKind.Corrected:
                if (finalCategory is null)
                    throw new ArgumentException("Corrected 必须记录 FinalCategory。", nameof(finalCategory));
                if (string.Equals(proposedCategory, finalCategory, StringComparison.OrdinalIgnoreCase) &&
                    proposedZoneId == finalZoneId)
                {
                    throw new ArgumentException("Corrected 必须至少修改 Category 或 Zone。", nameof(finalCategory));
                }
                break;

            case DesktopClassificationFeedbackKind.Rejected:
                if (finalCategory is not null || finalZoneId.HasValue)
                    throw new ArgumentException("Rejected 不应携带 Final 结果。", nameof(finalCategory));
                break;
        }

        EvidenceId = evidenceId;
        ObservedAtUtc = observedAtUtc;
        TemplateId = templateId;
        StableId = stableId;
        DisplayName = displayName;
        FileExtension = fileExtension?.ToLowerInvariant();
        ItemKind = itemKind;
        ProposedCategory = proposedCategory;
        ProposedZoneId = proposedZoneId;
        ProposedSource = proposedSource;
        FeedbackKind = feedbackKind;
        FinalCategory = finalCategory;
        FinalZoneId = finalZoneId;
    }
}
