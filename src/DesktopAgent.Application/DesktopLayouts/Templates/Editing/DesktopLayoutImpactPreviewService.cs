using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 一次基于新鲜 DesktopObservation 生成的只读影响预览。
///
/// 为了让后续 Direct Apply 能证明“执行的就是用户实际看过的那一版”，预览同时冻结：
/// 当前 Draft、原始 Observation、SelectedTarget 与显式跨屏入站授权。
/// 它仍然不包含 WriterCommand；任何真实执行都必须重新观察并 Fresh Replan，
/// 再与这份冻结事实做语义比较。
/// </summary>
public sealed record DesktopLayoutImpactPreview
{
    public Guid ObservationId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DesktopLayoutRuleAwarePreviewPlan Plan { get; }
    public DesktopLayoutEditDraft FrozenDraft { get; }
    public DesktopObservation Observation { get; }
    public string? SelectedTargetDeviceName { get; }
    public IReadOnlyList<string> ExplicitInboundStableIds { get; }

    public DesktopLayoutImpactPreview(
        Guid observationId,
        DateTimeOffset observedAtUtc,
        DesktopLayoutRuleAwarePreviewPlan plan,
        DesktopLayoutEditDraft frozenDraft,
        DesktopObservation observation,
        string? selectedTargetDeviceName,
        IReadOnlyCollection<string>? explicitInboundStableIds = null)
    {
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为 Guid.Empty。", nameof(observationId));
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frozenDraft);
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.ObservationId != observationId)
            throw new ArgumentException("ObservationId 与 Observation.ObservationId 不一致。", nameof(observationId));

        var inbound = NormalizeInboundStableIds(explicitInboundStableIds);
        var planAuthorized = plan.ResolutionPlan.ExplicitInboundStableIds;
        if (!inbound.SequenceEqual(planAuthorized, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "显式跨屏授权 StableId 与 PreviewPlan.ResolutionPlan 不一致。",
                nameof(explicitInboundStableIds));
        }

        ObservationId = observationId;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        Plan = plan;
        FrozenDraft = frozenDraft;
        Observation = observation;
        SelectedTargetDeviceName = selectedTargetDeviceName;
        ExplicitInboundStableIds = Array.AsReadOnly(inbound.ToArray());
    }

    private static IReadOnlyList<string> NormalizeInboundStableIds(
        IReadOnlyCollection<string>? explicitInboundStableIds)
    {
        if (explicitInboundStableIds is null || explicitInboundStableIds.Count == 0)
            return Array.Empty<string>();

        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stableId in explicitInboundStableIds)
        {
            if (string.IsNullOrWhiteSpace(stableId))
                throw new ArgumentException(
                    "显式入站 StableId 不能包含 null、空或纯空白。",
                    nameof(explicitInboundStableIds));
            if (!normalized.Add(stableId))
                throw new ArgumentException(
                    $"显式入站 StableId \"{stableId}\" 重复。",
                    nameof(explicitInboundStableIds));
        }

        return normalized
            .OrderBy(static stableId => stableId, StringComparer.Ordinal)
            .ToArray();
    }
}

public static class DesktopLayoutImpactPreviewService
{
    public static DesktopLayoutImpactPreview Create(
        DesktopLayoutEditDraft draft,
        DesktopObservation observation,
        string? selectedTargetDeviceName = null,
        IReadOnlyCollection<string>? explicitInboundStableIds = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(observation);

        // 如果调用方没有额外提供，统一使用 Draft 自己冻结的跨屏授权。
        // 因此布局页的 Preview、Overlay、DirectApply 不需要各自维护一份 UI 旁路状态。
        var effectiveInbound = explicitInboundStableIds ?? draft.ExplicitInboundStableIds;

        var frozenDraft = new DesktopLayoutEditDraft(
            draft.TemplateId,
            draft.SourceVersion,
            draft.Name,
            draft.TargetDisplayBinding,
            draft.Zones,
            draft.RuleSet,
            effectiveInbound);

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

        var inboundSet = effectiveInbound.ToHashSet(StringComparer.Ordinal);
        var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
            template,
            frozenDraft.RuleSet,
            observation.Displays,
            selectedTargetDeviceName,
            observation.PositioningCapabilities.Spacing,
            icons,
            inboundSet);

        return new DesktopLayoutImpactPreview(
            observation.ObservationId,
            observation.ObservedAtUtc,
            plan,
            frozenDraft,
            observation,
            selectedTargetDeviceName,
            effectiveInbound);
    }
}
