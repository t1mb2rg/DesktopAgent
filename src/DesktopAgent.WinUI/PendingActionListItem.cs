using DesktopAgent.Core.Automation;

namespace DesktopAgent.WinUI;

/// <summary>
/// M5-03 只读 Pending Queue 的产品展示模型。
/// 不携带 WriterCommand，也不把展示模型反向当作执行输入。
/// </summary>
public sealed class PendingActionListItem
{
    public string DisplayName { get; }
    public string CreatedAtText { get; }
    public string DecisionText { get; }
    public string QueueReasonText { get; }
    public string ExplanationText { get; }

    private PendingActionListItem(
        string displayName,
        string createdAtText,
        string decisionText,
        string queueReasonText,
        string explanationText)
    {
        DisplayName = displayName;
        CreatedAtText = createdAtText;
        DecisionText = decisionText;
        QueueReasonText = queueReasonText;
        ExplanationText = explanationText;
    }

    public static PendingActionListItem From(DesktopPendingAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var decisionText = action.Decision is null
            ? "尚无可执行分类决定"
            : action.Decision.TargetZoneId.HasValue
                ? $"{action.Decision.Category} · 已建议目标区域"
                : action.Decision.Category;

        var explanation = action.Decision?.Explanation;
        if (string.IsNullOrEmpty(explanation))
            explanation = "需要后续产品流程补全决定后才能确认。";

        return new PendingActionListItem(
            action.Item.DisplayName,
            action.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            decisionText,
            FormatQueueReason(action.QueueReason),
            explanation);
    }

    private static string FormatQueueReason(DesktopAutomationPolicyReason reason)
        => reason switch
        {
            DesktopAutomationPolicyReason.NoExecutableDecision => "等待补全决定",
            DesktopAutomationPolicyReason.UserResolutionDeferred => "等待用户决定",
            DesktopAutomationPolicyReason.AiOnlyUnconfirmed => "新的 AI 判断",
            DesktopAutomationPolicyReason.NoExecutionAuthorization => "尚未授权",
            DesktopAutomationPolicyReason.ManualMode => "Manual 模式",
            DesktopAutomationPolicyReason.AssistedRequiresConfirmedPolicy => "等待确认长期策略",
            DesktopAutomationPolicyReason.GeometryEvidenceNotMeasured => "网格位置尚未实测，等待确认",
            _ => reason.ToString(),
        };
}
