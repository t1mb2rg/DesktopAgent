using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 用户已经看过并可用于后续 Direct Apply 二次校验的冻结确认对象。
/// 只包含不可变领域事实与执行候选，不拥有 Writer authority。
/// </summary>
public sealed record DesktopLayoutDirectApplyPreparation
{
    public DesktopLayoutEditDraft FrozenDraft { get; }
    public string? SelectedTargetDeviceName { get; }
    public DesktopLayoutImpactPreview Preview { get; }
    public DesktopLayoutRuleAwareExecutionCandidateBuildResult CandidateBuildResult { get; }

    public bool IsReady => CandidateBuildResult.IsSuccess && CandidateBuildResult.Candidate is not null;
    public DesktopLayoutRuleAwareExecutionCandidate? Candidate => CandidateBuildResult.Candidate;

    public DesktopLayoutDirectApplyPreparation(
        DesktopLayoutEditDraft frozenDraft,
        string? selectedTargetDeviceName,
        DesktopLayoutImpactPreview preview,
        DesktopLayoutRuleAwareExecutionCandidateBuildResult candidateBuildResult)
    {
        FrozenDraft = frozenDraft ?? throw new ArgumentNullException(nameof(frozenDraft));
        SelectedTargetDeviceName = selectedTargetDeviceName;
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
        CandidateBuildResult = candidateBuildResult
            ?? throw new ArgumentNullException(nameof(candidateBuildResult));
    }
}

/// <summary>
/// 把“当前草稿 + 一次完整 fresh DesktopObservation”冻结成用户确认对象。
/// 本服务纯 Application/Core：不包含 WriterCommand，不执行 Writer，也不读取持久化模板替代当前草稿。
/// </summary>
public static class DesktopLayoutDirectApplyPreparationService
{
    public static DesktopLayoutDirectApplyPreparation Create(
        DesktopLayoutEditDraft draft,
        DesktopObservation observation,
        string? selectedTargetDeviceName = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(observation);

        // DesktopLayoutEditDraft 自身已对 Zones 做防御性复制，并通过领域构造函数校验。
        // 这里再构造一份，明确冻结用户实际预览的语义，而不是后续从 SQLite 重读旧版本。
        var frozenDraft = new DesktopLayoutEditDraft(
            draft.TemplateId,
            draft.SourceVersion,
            draft.Name,
            draft.TargetDisplayBinding,
            draft.Zones,
            draft.RuleSet);

        var preview = DesktopLayoutImpactPreviewService.Create(
            frozenDraft,
            observation,
            selectedTargetDeviceName);

        var currentRead = new DesktopIconReadResult(
            observation.Icons,
            FailedItemCount: 0,
            observation.IconReadConnectionMethod,
            observation.IconReadTime,
            PrimaryPathException: null);

        var buildResult = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
            preview.Plan,
            currentRead,
            observation.Displays,
            observation.PositioningCapabilities,
            new DesktopLayoutSnapshotFactory(timeProvider));

        return new DesktopLayoutDirectApplyPreparation(
            frozenDraft,
            selectedTargetDeviceName,
            preview,
            buildResult);
    }
}
