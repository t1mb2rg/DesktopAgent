using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopSuggestions;

/// <summary>
/// M3 后台建议使用的显式模板绑定。
/// TemplateId 必须由用户配置/选择提供；M3-03A 不实现“随便挑一个模板”的隐式策略。
/// SelectedTargetDeviceName 仅在模板本身使用 SelectedTarget binding 时有意义。
/// </summary>
public sealed record DesktopLayoutSuggestionBinding
{
    public Guid TemplateId { get; }
    public string? SelectedTargetDeviceName { get; }

    public DesktopLayoutSuggestionBinding(
        Guid templateId,
        string? selectedTargetDeviceName = null)
    {
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为空。", nameof(templateId));
        if (selectedTargetDeviceName is not null
            && string.IsNullOrWhiteSpace(selectedTargetDeviceName))
        {
            throw new ArgumentException(
                "SelectedTargetDeviceName 提供时不能是空字符串或空白。",
                nameof(selectedTargetDeviceName));
        }

        TemplateId = templateId;
        SelectedTargetDeviceName = selectedTargetDeviceName;
    }
}

public enum DesktopLayoutSuggestionBuildStatus
{
    Suggested = 0,
    NoChanges = 1,
    NoActionNeeded = 2,
    ObservationMismatch = 3,
    TemplateMissing = 4,
    TemplateLoadFailed = 5,
    RuleSetMissing = 6,
    RuleSetLoadFailed = 7,
    BindingIncomplete = 8,
    PreviewRejected = 9,
    CandidateRejected = 10,
}

public enum DesktopLayoutSuggestionIssueCode
{
    ObservationMismatch = 0,
    TemplateMissing = 1,
    TemplateLoadFailed = 2,
    RuleSetMissing = 3,
    RuleSetLoadFailed = 4,
    RuleSetTemplateMismatch = 5,
    SelectedTargetRequired = 6,
    TargetDisplayResolutionFailed = 7,
    PreviewPlanningFailed = 8,
    CandidateRejected = 9,
}

public sealed record DesktopLayoutSuggestionIssue(
    DesktopLayoutSuggestionIssueCode Code,
    string Message,
    string? StableId = null);

/// <summary>
/// 一份 M3 建议记录。
/// Candidate 只表示“在 SourceObservationId 对应事实下，M2 Planner 当时能生成什么计划”。
/// 它不构成 Writer 授权；真正执行必须回到 M2 手动 fresh reread / rebuild / gate / coordinator 链。
/// </summary>
public sealed record DesktopLayoutSuggestion
{
    public Guid SuggestionId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public Guid SourceObservationId { get; }
    public Guid PreviousObservationId { get; }
    public DesktopLayoutSuggestionBinding Binding { get; }
    public DesktopStateChangeSet SourceChangeSet { get; }
    public DesktopLayoutRuleAwareExecutionCandidate Candidate { get; }

    public Guid TemplateId => Binding.TemplateId;
    public string TargetDeviceName => Candidate.TargetDeviceName;
    public int MoveRequiredCount => Candidate.MoveRequiredCount;
    public int ExecutionStepCount => Candidate.ExecutionStepCount;
    public bool UsesTemporaryStaging => Candidate.UsesTemporaryStaging;

    public DesktopLayoutSuggestion(
        Guid suggestionId,
        DateTimeOffset createdAtUtc,
        Guid sourceObservationId,
        Guid previousObservationId,
        DesktopLayoutSuggestionBinding binding,
        DesktopStateChangeSet sourceChangeSet,
        DesktopLayoutRuleAwareExecutionCandidate candidate)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("SuggestionId 不能为空。", nameof(suggestionId));
        if (sourceObservationId == Guid.Empty)
            throw new ArgumentException("SourceObservationId 不能为空。", nameof(sourceObservationId));
        if (previousObservationId == Guid.Empty)
            throw new ArgumentException("PreviousObservationId 不能为空。", nameof(previousObservationId));
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(sourceChangeSet);
        ArgumentNullException.ThrowIfNull(candidate);

        if (sourceChangeSet.CurrentObservationId != sourceObservationId)
        {
            throw new ArgumentException(
                "SourceChangeSet.CurrentObservationId 必须与 SourceObservationId 一致。",
                nameof(sourceChangeSet));
        }
        if (sourceChangeSet.PreviousObservationId != previousObservationId)
        {
            throw new ArgumentException(
                "SourceChangeSet.PreviousObservationId 必须与 PreviousObservationId 一致。",
                nameof(sourceChangeSet));
        }
        if (!sourceChangeSet.HasChanges)
            throw new ArgumentException("Suggestion 必须来自非空 State Diff。", nameof(sourceChangeSet));
        if (!candidate.IsExecutable || candidate.IsNoOp)
        {
            throw new ArgumentException(
                "Suggestion 只能包含可执行且非 NoOp 的 M2 Candidate。",
                nameof(candidate));
        }

        SuggestionId = suggestionId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        SourceObservationId = sourceObservationId;
        PreviousObservationId = previousObservationId;
        Binding = binding;
        SourceChangeSet = sourceChangeSet;
        Candidate = candidate;
    }
}

public sealed record DesktopLayoutSuggestionBuildResult
{
    public DesktopLayoutSuggestionBuildStatus Status { get; }
    public DesktopLayoutSuggestion? Suggestion { get; }
    public DesktopLayoutRuleAwareExecutionCandidate? Candidate { get; }
    public IReadOnlyList<DesktopLayoutSuggestionIssue> Issues { get; }

    public bool IsSuggested => Status == DesktopLayoutSuggestionBuildStatus.Suggested;

    public DesktopLayoutSuggestionBuildResult(
        DesktopLayoutSuggestionBuildStatus status,
        DesktopLayoutSuggestion? suggestion,
        DesktopLayoutRuleAwareExecutionCandidate? candidate,
        IReadOnlyList<DesktopLayoutSuggestionIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        if (status == DesktopLayoutSuggestionBuildStatus.Suggested
            && (suggestion is null || candidate is null || !ReferenceEquals(suggestion.Candidate, candidate)))
        {
            throw new ArgumentException(
                "Suggested 必须提供 Suggestion 及其同一 Candidate 实例。",
                nameof(status));
        }
        if (status != DesktopLayoutSuggestionBuildStatus.Suggested && suggestion is not null)
            throw new ArgumentException("非 Suggested 状态不得携带 Suggestion。", nameof(suggestion));
        if (status == DesktopLayoutSuggestionBuildStatus.NoActionNeeded
            && (candidate is null || !candidate.IsNoOp))
        {
            throw new ArgumentException(
                "NoActionNeeded 必须携带 NoOp Candidate。",
                nameof(candidate));
        }
        if (status is not DesktopLayoutSuggestionBuildStatus.Suggested
            and not DesktopLayoutSuggestionBuildStatus.NoActionNeeded
            && candidate is not null)
        {
            throw new ArgumentException(
                "只有 Suggested / NoActionNeeded 可以携带 Candidate。",
                nameof(candidate));
        }

        Status = status;
        Suggestion = suggestion;
        Candidate = candidate;
        Issues = Array.AsReadOnly(issues.ToArray());
    }
}
