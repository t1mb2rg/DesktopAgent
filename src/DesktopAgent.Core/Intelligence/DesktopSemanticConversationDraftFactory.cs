namespace DesktopAgent.Core.Intelligence;

/// <summary>纯语义缓存经过 fresh 核验后适配旧对话上下文；不恢复分区、位置或执行状态。</summary>
public static class DesktopSemanticConversationDraftFactory
{
    public static DesktopUnderstandingDraft Create(DesktopSemanticUnderstanding understanding, string? intentSummary)
        => new(string.IsNullOrWhiteSpace(intentSummary) ? "继续讨论桌面设计" : intentSummary,
            understanding.DesktopSummary,
            understanding.Categories.Select(category => new DesktopUnderstandingCategoryDraft(category.Key, category.Name, category.Description)),
            assignments: understanding.Assignments.Select(assignment => new DesktopUnderstandingAssignment(
                assignment.StableId, assignment.CategoryKey, null, assignment.Confidence, assignment.Reason)),
            questions: understanding.Questions.Select(question => new DesktopUnderstandingQuestion(question.Key, question.Text)));
}
