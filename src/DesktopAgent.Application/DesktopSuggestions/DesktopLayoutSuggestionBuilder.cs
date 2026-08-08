using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopSuggestions;

/// <summary>
/// 把一份 current stable Observation + State Diff + 显式模板绑定转换为 M2 Candidate-backed Suggestion。
/// 本类型只读：不依赖 Writer、ExecutionCoordinator、Recovery store 或 WPF。
/// </summary>
public sealed class DesktopLayoutSuggestionBuilder
{
    private readonly IDesktopLayoutTemplateStore _templateStore;
    private readonly IDesktopLayoutIconRuleStore _ruleStore;
    private readonly DesktopLayoutSnapshotFactory _snapshotFactory;
    private readonly TimeProvider _timeProvider;

    public DesktopLayoutSuggestionBuilder(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        DesktopLayoutSnapshotFactory snapshotFactory,
        TimeProvider? timeProvider = null)
    {
        _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DesktopLayoutSuggestionBuildResult> BuildAsync(
        DesktopLayoutSuggestionBinding binding,
        DesktopStateChangeSet changeSet,
        DesktopObservation currentObservation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(currentObservation);
        ct.ThrowIfCancellationRequested();

        if (changeSet.CurrentObservationId != currentObservation.ObservationId)
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.ObservationMismatch,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.ObservationMismatch,
                    $"State Diff 当前 Observation ({changeSet.CurrentObservationId:D}) 与提供的 fresh Observation ({currentObservation.ObservationId:D}) 不一致。"));
        }

        if (!changeSet.HasChanges)
        {
            return new DesktopLayoutSuggestionBuildResult(
                DesktopLayoutSuggestionBuildStatus.NoChanges,
                null,
                null,
                Array.Empty<DesktopLayoutSuggestionIssue>());
        }

        DesktopLayoutTemplate? template;
        try
        {
            template = await _templateStore.GetAsync(binding.TemplateId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.TemplateLoadFailed,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.TemplateLoadFailed,
                    $"读取模板 {binding.TemplateId:D} 失败：{ex.GetType().Name}: {ex.Message}"));
        }

        if (template is null)
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.TemplateMissing,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.TemplateMissing,
                    $"后台绑定的模板 {binding.TemplateId:D} 不存在。"));
        }

        if (template.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget
            && string.IsNullOrWhiteSpace(binding.SelectedTargetDeviceName))
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.BindingIncomplete,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.SelectedTargetRequired,
                    "模板使用 SelectedTarget 绑定，但后台 Suggestion Binding 未提供 SelectedTargetDeviceName。"));
        }

        DesktopLayoutIconRuleSet? ruleSet;
        try
        {
            ruleSet = await _ruleStore.GetAsync(binding.TemplateId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.RuleSetLoadFailed,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.RuleSetLoadFailed,
                    $"读取模板 {binding.TemplateId:D} 的规则集失败：{ex.GetType().Name}: {ex.Message}"));
        }

        if (ruleSet is null)
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.RuleSetMissing,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.RuleSetMissing,
                    $"模板 {binding.TemplateId:D} 的规则集不存在。"));
        }

        if (ruleSet.TemplateId != template.TemplateId)
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.RuleSetMissing,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.RuleSetTemplateMismatch,
                    $"规则集 TemplateId ({ruleSet.TemplateId:D}) 与模板 ({template.TemplateId:D}) 不一致。"));
        }

        var selectedTarget = template.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget
            ? binding.SelectedTargetDeviceName
            : null;

        string targetDeviceName;
        try
        {
            targetDeviceName = DesktopLayoutTargetDisplayResolver.Resolve(
                template.TargetDisplayBinding,
                currentObservation.Displays,
                selectedTarget).DeviceName;
        }
        catch (Exception ex)
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.PreviewRejected,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.TargetDisplayResolutionFailed,
                    $"目标显示器解析失败：{ex.Message}"));
        }

        // 与 M2 手动 Preview 保持同一输入顺序语义：沿用当前 fresh Shell observation 的 Icons 顺序，
        // 不在 M3 私自按 StableId 重排，否则 Suggestion 与用户确认后的 M2 rebuild 可能产生不同 slot 分配。
        var currentRead = new DesktopIconReadResult(
            currentObservation.Icons,
            0,
            currentObservation.IconReadConnectionMethod,
            currentObservation.IconReadTime,
            null);
        var snapshot = _snapshotFactory.Create(
            currentObservation.Icons,
            targetDeviceName,
            currentObservation.IconReadConnectionMethod);

        DesktopLayoutRuleAwarePreviewPlan previewPlan;
        try
        {
            previewPlan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
                template,
                ruleSet,
                currentObservation.Displays,
                selectedTarget,
                currentObservation.PositioningCapabilities.Spacing,
                snapshot.Icons);
        }
        catch (Exception ex)
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.PreviewRejected,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.PreviewPlanningFailed,
                    $"规则感知 Preview 生成失败：{ex.GetType().Name}: {ex.Message}"));
        }

        var candidateBuild = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            previewPlan,
            currentRead,
            currentObservation.Displays,
            currentObservation.PositioningCapabilities,
            _snapshotFactory);

        if (!candidateBuild.IsSuccess || candidateBuild.Candidate is null)
        {
            var issues = candidateBuild.Issues.Count == 0
                ? new[]
                {
                    new DesktopLayoutSuggestionIssue(
                        DesktopLayoutSuggestionIssueCode.CandidateRejected,
                        "M2 Candidate Planner 拒绝生成候选，但未返回具体 issue。")
                }
                : candidateBuild.Issues.Select(issue =>
                    new DesktopLayoutSuggestionIssue(
                        DesktopLayoutSuggestionIssueCode.CandidateRejected,
                        $"[{issue.Code}] {issue.Message}",
                        issue.StableId)).ToArray();

            return new DesktopLayoutSuggestionBuildResult(
                DesktopLayoutSuggestionBuildStatus.CandidateRejected,
                null,
                null,
                issues);
        }

        var candidate = candidateBuild.Candidate;
        if (candidate.IsNoOp)
        {
            return new DesktopLayoutSuggestionBuildResult(
                DesktopLayoutSuggestionBuildStatus.NoActionNeeded,
                null,
                candidate,
                Array.Empty<DesktopLayoutSuggestionIssue>());
        }

        if (!candidate.IsExecutable)
        {
            return Rejected(
                DesktopLayoutSuggestionBuildStatus.CandidateRejected,
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.CandidateRejected,
                    "M2 Candidate 已生成，但当前候选不可执行；M3 不把它提升为 Suggestion。"));
        }

        var suggestion = new DesktopLayoutSuggestion(
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            currentObservation.ObservationId,
            changeSet.PreviousObservationId,
            binding,
            changeSet,
            candidate);

        return new DesktopLayoutSuggestionBuildResult(
            DesktopLayoutSuggestionBuildStatus.Suggested,
            suggestion,
            candidate,
            Array.Empty<DesktopLayoutSuggestionIssue>());
    }

    private static DesktopLayoutSuggestionBuildResult Rejected(
        DesktopLayoutSuggestionBuildStatus status,
        params DesktopLayoutSuggestionIssue[] issues) =>
        new(status, null, null, issues);
}
