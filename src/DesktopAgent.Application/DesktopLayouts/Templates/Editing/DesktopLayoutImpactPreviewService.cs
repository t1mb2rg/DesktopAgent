using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 一次基于新鲜 DesktopObservation 生成的只读影响预览。
///
/// 为了让后续 Direct Apply 能证明“执行的就是用户实际看过的那一版”，预览同时冻结：
/// 当前 Draft、原始 Observation 与 SelectedTarget。它仍然不包含 WriterCommand；
/// 任何真实执行都必须重新观察并 Fresh Replan，再与这份冻结事实做语义比较。
/// </summary>
public sealed record DesktopLayoutImpactPreview
{
    public Guid ObservationId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DesktopLayoutRuleAwarePreviewPlan Plan { get; }
    public DesktopLayoutEditDraft FrozenDraft { get; }
    public DesktopObservation Observation { get; }
    public string? SelectedTargetDeviceName { get; }

    public DesktopLayoutImpactPreview(
        Guid observationId,
        DateTimeOffset observedAtUtc,
        DesktopLayoutRuleAwarePreviewPlan plan,
        DesktopLayoutEditDraft frozenDraft,
        DesktopObservation observation,
        string? selectedTargetDeviceName)
    {
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为 Guid.Empty。", nameof(observationId));
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frozenDraft);
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.ObservationId != observationId)
            throw new ArgumentException("ObservationId 与 Observation.ObservationId 不一致。", nameof(observationId));

        ObservationId = observationId;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        Plan = plan;
        FrozenDraft = frozenDraft;
        Observation = observation;
        SelectedTargetDeviceName = selectedTargetDeviceName;
    }
}

public static class DesktopLayoutImpactPreviewService
{
    public static DesktopLayoutImpactPreview Create(
        DesktopLayoutEditDraft draft,
        DesktopObservation observation,
        string? selectedTargetDeviceName = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(observation);

        // 明确冻结生成本次 Preview 时的草稿语义，不能让后续 UI 修改悄悄改变这份证据。
        var frozenDraft = new DesktopLayoutEditDraft(
            draft.TemplateId,
            draft.SourceVersion,
            draft.Name,
            draft.TargetDisplayBinding,
            draft.Zones,
            draft.RuleSet);

        var template = frozenDraft.BuildPreviewTemplate();
        var icons = observation.Icons
            .Select((icon, index) => new DesktopIconSnapshotItem(
                index,
                icon.StableId,
                icon.DisplayName,
                icon.ParsingName,
                icon.ItemKind,
                icon.PixelPosition,
                icon.MonitorDeviceName,
                icon.IsSystemItem,
                icon.IsFileSystemItem))
            .ToArray();

        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template,
            frozenDraft.RuleSet,
            observation.Displays,
            selectedTargetDeviceName,
            observation.PositioningCapabilities.Spacing,
            icons);

        return new DesktopLayoutImpactPreview(
            observation.ObservationId,
            observation.ObservedAtUtc,
            plan,
            frozenDraft,
            observation,
            selectedTargetDeviceName);
    }
}
