using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
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

        var preview = DesktopLayoutImpactPreviewService.Create(
            draft,
            observation,
            selectedTargetDeviceName);

        return CreateFromPreview(preview, timeProvider);
    }

    /// <summary>
    /// 从用户实际看过的 Preview 黑匣子建立确认 Candidate。
    /// 这里严禁重新读取桌面，否则“确认内容”和“confirmed Candidate”会偷偷来自两次不同事实。
    /// </summary>
    public static DesktopLayoutDirectApplyPreparation CreateFromPreview(
        DesktopLayoutImpactPreview preview,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var observation = preview.Observation;
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
            preview.FrozenDraft,
            preview.SelectedTargetDeviceName,
            preview,
            buildResult);
    }

    /// <summary>
    /// 判断当前编辑器状态是否仍与用户看过的冻结 Draft 具有同一物理执行语义。
    /// 名称变化不影响坐标，因此有意忽略 Name；几何、排序、流向、容量、显示器绑定和 RuleSet 必须完全一致。
    /// </summary>
    public static bool HasSameExecutionSemantics(
        DesktopLayoutEditDraft current,
        DesktopLayoutEditDraft frozen)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(frozen);

        if (current.TemplateId != frozen.TemplateId || current.SourceVersion != frozen.SourceVersion)
            return false;

        if (current.TargetDisplayBinding.Kind != frozen.TargetDisplayBinding.Kind ||
            !string.Equals(
                current.TargetDisplayBinding.DeviceName ?? string.Empty,
                frozen.TargetDisplayBinding.DeviceName ?? string.Empty,
                StringComparison.Ordinal))
        {
            return false;
        }

        var currentZones = current.Zones.OrderBy(zone => zone.ZoneId).ToArray();
        var frozenZones = frozen.Zones.OrderBy(zone => zone.ZoneId).ToArray();
        if (currentZones.Length != frozenZones.Length)
            return false;

        for (var i = 0; i < currentZones.Length; i++)
        {
            var a = currentZones[i];
            var b = frozenZones[i];
            if (a.ZoneId != b.ZoneId ||
                a.Bounds != b.Bounds ||
                a.SortOrder != b.SortOrder ||
                a.PlacementFlow != b.PlacementFlow ||
                a.MaxItemCount != b.MaxItemCount)
            {
                return false;
            }
        }

        return RuleSetsEquivalent(current.RuleSet, frozen.RuleSet);
    }

    private static bool RuleSetsEquivalent(
        DesktopLayoutIconRuleSet current,
        DesktopLayoutIconRuleSet frozen)
    {
        if (current.TemplateId != frozen.TemplateId || current.Rules.Count != frozen.Rules.Count)
            return false;

        for (var i = 0; i < current.Rules.Count; i++)
        {
            var a = current.Rules[i];
            var b = frozen.Rules[i];
            if (!string.Equals(a.StableId, b.StableId, StringComparison.Ordinal) ||
                a.Kind != b.Kind ||
                a.TargetZoneId != b.TargetZoneId)
            {
                return false;
            }
        }

        return true;
    }
}
