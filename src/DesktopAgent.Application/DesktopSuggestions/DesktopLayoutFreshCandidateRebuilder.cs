using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopSuggestions;

public enum DesktopLayoutFreshCandidateRebuildStatus
{
    Ready = 0,
    NoActionNeeded = 1,
    TemplateMissing = 2,
    TemplateLoadFailed = 3,
    RuleSetMissing = 4,
    RuleSetLoadFailed = 5,
    BindingIncomplete = 6,
    TargetDisplayResolutionFailed = 7,
    PreviewPlanningFailed = 8,
    CandidateRejected = 9,
}

public sealed record DesktopLayoutFreshCandidateRebuildIssue(
    DesktopLayoutFreshCandidateRebuildStatus Status,
    string Message,
    string? StableId = null);

public sealed record DesktopLayoutFreshCandidateRebuildResult
{
    public DesktopLayoutFreshCandidateRebuildStatus Status { get; }
    public Guid ObservationId { get; }
    public DesktopLayoutRuleAwareExecutionCandidate? Candidate { get; }
    public IReadOnlyList<DesktopLayoutFreshCandidateRebuildIssue> Issues { get; }

    public bool IsReady => Status == DesktopLayoutFreshCandidateRebuildStatus.Ready;

    public DesktopLayoutFreshCandidateRebuildResult(
        DesktopLayoutFreshCandidateRebuildStatus status,
        Guid observationId,
        DesktopLayoutRuleAwareExecutionCandidate? candidate,
        IReadOnlyList<DesktopLayoutFreshCandidateRebuildIssue> issues)
    {
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为空。", nameof(observationId));
        ArgumentNullException.ThrowIfNull(issues);

        if (status == DesktopLayoutFreshCandidateRebuildStatus.Ready
            && (candidate is null || !candidate.IsExecutable || candidate.IsNoOp))
        {
            throw new ArgumentException(
                "Ready 必须携带可执行且非 NoOp 的 fresh Candidate。",
                nameof(candidate));
        }

        if (status == DesktopLayoutFreshCandidateRebuildStatus.NoActionNeeded
            && (candidate is null || !candidate.IsNoOp))
        {
            throw new ArgumentException(
                "NoActionNeeded 必须携带 NoOp Candidate。",
                nameof(candidate));
        }

        if (status is not DesktopLayoutFreshCandidateRebuildStatus.Ready
            and not DesktopLayoutFreshCandidateRebuildStatus.NoActionNeeded
            && candidate is not null)
        {
            throw new ArgumentException(
                "Rejected rebuild 不得携带 Candidate。",
                nameof(candidate));
        }

        Status = status;
        ObservationId = observationId;
        Candidate = candidate;
        Issues = Array.AsReadOnly(issues.ToArray());
    }
}

public interface IDesktopLayoutFreshCandidateRebuilder
{
    Task<DesktopLayoutFreshCandidateRebuildResult> RebuildAsync(
        DesktopLayoutSuggestionBinding binding,
        DesktopObservation freshObservation,
        CancellationToken ct = default);
}

/// <summary>
/// 基于一份 fresh stable Observation 重新加载模板/规则并重建 M2 Candidate。
/// 不使用旧 Suggestion Candidate，不需要 State Diff，也不依赖 Writer / Coordinator。
/// </summary>
public sealed class DesktopLayoutFreshCandidateRebuilder : IDesktopLayoutFreshCandidateRebuilder
{
    private readonly IDesktopLayoutTemplateStore _templateStore;
    private readonly IDesktopLayoutIconRuleStore _ruleStore;
    private readonly DesktopLayoutSnapshotFactory _snapshotFactory;

    public DesktopLayoutFreshCandidateRebuilder(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        DesktopLayoutSnapshotFactory snapshotFactory)
    {
        _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
    }

    public async Task<DesktopLayoutFreshCandidateRebuildResult> RebuildAsync(
        DesktopLayoutSuggestionBinding binding,
        DesktopObservation freshObservation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(freshObservation);
        ct.ThrowIfCancellationRequested();

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
            return Reject(
                DesktopLayoutFreshCandidateRebuildStatus.TemplateLoadFailed,
                freshObservation.ObservationId,
                $"读取模板 {binding.TemplateId:D} 失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (template is null)
        {
            return Reject(
                DesktopLayoutFreshCandidateRebuildStatus.TemplateMissing,
                freshObservation.ObservationId,
                $"绑定模板 {binding.TemplateId:D} 不存在。");
        }

        if (template.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget
            && string.IsNullOrWhiteSpace(binding.SelectedTargetDeviceName))
        {
            return Reject(
                DesktopLayoutFreshCandidateRebuildStatus.BindingIncomplete,
                freshObservation.ObservationId,
                "模板使用 SelectedTarget，但 binding 未提供 SelectedTargetDeviceName。");
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
            return Reject(
                DesktopLayoutFreshCandidateRebuildStatus.RuleSetLoadFailed,
                freshObservation.ObservationId,
                $"读取模板 {binding.TemplateId:D} 的规则集失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (ruleSet is null || ruleSet.TemplateId != template.TemplateId)
        {
            return Reject(
                DesktopLayoutFreshCandidateRebuildStatus.RuleSetMissing,
                freshObservation.ObservationId,
                ruleSet is null
                    ? $"模板 {binding.TemplateId:D} 的规则集不存在。"
                    : $"规则集 TemplateId ({ruleSet.TemplateId:D}) 与模板 ({template.TemplateId:D}) 不一致。");
        }

        var selectedTarget = template.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget
            ? binding.SelectedTargetDeviceName
            : null;

        string targetDeviceName;
        try
        {
            targetDeviceName = DesktopLayoutTargetDisplayResolver.Resolve(
                template.TargetDisplayBinding,
                freshObservation.Displays,
                selectedTarget).DeviceName;
        }
        catch (Exception ex)
        {
            return Reject(
                DesktopLayoutFreshCandidateRebuildStatus.TargetDisplayResolutionFailed,
                freshObservation.ObservationId,
                $"目标显示器解析失败：{ex.Message}");
        }

        var currentRead = new DesktopIconReadResult(
            freshObservation.Icons,
            0,
            freshObservation.IconReadConnectionMethod,
            freshObservation.IconReadTime,
            null);
        var snapshot = _snapshotFactory.Create(
            freshObservation.Icons,
            targetDeviceName,
            freshObservation.IconReadConnectionMethod);

        DesktopLayoutRuleAwarePreviewPlan previewPlan;
        try
        {
            previewPlan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
                template,
                ruleSet,
                freshObservation.Displays,
                selectedTarget,
                freshObservation.PositioningCapabilities.Spacing,
                snapshot.Icons);
        }
        catch (Exception ex)
        {
            return Reject(
                DesktopLayoutFreshCandidateRebuildStatus.PreviewPlanningFailed,
                freshObservation.ObservationId,
                $"规则感知 Preview 生成失败：{ex.GetType().Name}: {ex.Message}");
        }

        var candidateBuild = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            previewPlan,
            currentRead,
            freshObservation.Displays,
            freshObservation.PositioningCapabilities,
            _snapshotFactory);

        if (!candidateBuild.IsSuccess || candidateBuild.Candidate is null)
        {
            var issues = candidateBuild.Issues.Count == 0
                ? new[]
                {
                    new DesktopLayoutFreshCandidateRebuildIssue(
                        DesktopLayoutFreshCandidateRebuildStatus.CandidateRejected,
                        "M2 Candidate Planner 拒绝生成 fresh Candidate，但未返回具体 issue。")
                }
                : candidateBuild.Issues.Select(issue =>
                    new DesktopLayoutFreshCandidateRebuildIssue(
                        DesktopLayoutFreshCandidateRebuildStatus.CandidateRejected,
                        $"[{issue.Code}] {issue.Message}",
                        issue.StableId)).ToArray();

            return new DesktopLayoutFreshCandidateRebuildResult(
                DesktopLayoutFreshCandidateRebuildStatus.CandidateRejected,
                freshObservation.ObservationId,
                null,
                issues);
        }

        var candidate = candidateBuild.Candidate;
        if (candidate.IsNoOp)
        {
            return new DesktopLayoutFreshCandidateRebuildResult(
                DesktopLayoutFreshCandidateRebuildStatus.NoActionNeeded,
                freshObservation.ObservationId,
                candidate,
                Array.Empty<DesktopLayoutFreshCandidateRebuildIssue>());
        }

        if (!candidate.IsExecutable)
        {
            return Reject(
                DesktopLayoutFreshCandidateRebuildStatus.CandidateRejected,
                freshObservation.ObservationId,
                "fresh M2 Candidate 已生成，但当前候选不可执行。");
        }

        return new DesktopLayoutFreshCandidateRebuildResult(
            DesktopLayoutFreshCandidateRebuildStatus.Ready,
            freshObservation.ObservationId,
            candidate,
            Array.Empty<DesktopLayoutFreshCandidateRebuildIssue>());
    }

    private static DesktopLayoutFreshCandidateRebuildResult Reject(
        DesktopLayoutFreshCandidateRebuildStatus status,
        Guid observationId,
        string message) =>
        new(
            status,
            observationId,
            null,
            new[] { new DesktopLayoutFreshCandidateRebuildIssue(status, message) });
}
