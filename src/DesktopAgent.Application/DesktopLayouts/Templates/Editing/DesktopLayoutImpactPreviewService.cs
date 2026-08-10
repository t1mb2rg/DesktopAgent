using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 一次基于新鲜 DesktopObservation 生成的只读影响预览。
/// 此对象故意不包含 WriterCommand；任何未来执行都必须重新观察并 Fresh Replan。
/// </summary>
public sealed record DesktopLayoutImpactPreview(
    Guid ObservationId,
    DateTimeOffset ObservedAtUtc,
    DesktopLayoutRuleAwarePreviewPlan Plan);

public static class DesktopLayoutImpactPreviewService
{
    public static DesktopLayoutImpactPreview Create(
        DesktopLayoutEditDraft draft,
        DesktopObservation observation,
        string? selectedTargetDeviceName = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(observation);

        var template = draft.BuildPreviewTemplate();
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
            draft.RuleSet,
            observation.Displays,
            selectedTargetDeviceName,
            observation.PositioningCapabilities.Spacing,
            icons);

        return new DesktopLayoutImpactPreview(
            observation.ObservationId,
            observation.ObservedAtUtc,
            plan);
    }
}
