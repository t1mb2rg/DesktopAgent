using System.Text;
using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>
/// Workspace Design 的本地 authority bundle。
/// Request 是 Provider-safe；其余 ItemFacts / bindings / source snapshot 只留在本地 Compiler 与 Preview 链路。
/// </summary>
public sealed record DesktopWorkspaceDesignPreparation(
    DesktopWorkspaceDesignRequest Request,
    DesktopSemanticUnderstanding SemanticUnderstanding,
    IReadOnlyList<DesktopWorkspaceCompilationItemFact> ItemFacts,
    IReadOnlyList<DesktopWorkspaceCompilationItemRefBinding> ItemRefBindings,
    DesktopWorkspacePreviewSourceSnapshot SourceSnapshot);

/// <summary>
/// 把同一次桌面语义理解与 Observation 投影成 ADR-0005 Workspace Design 请求。
/// 新 Workspace 路径不读取 legacy Zones / ZoneKey；只使用纯语义分类和显式 conversation layout constraints。
/// </summary>
public static class DesktopWorkspaceDesignPreparationFactory
{
    /// <summary>
    /// 复用语义理解，以本次读取的几何和位置生成新的预览起点。
    /// 项目语义事实变化时必须先重新理解；纯位置变化不使分类失效。
    /// </summary>
    public static DesktopWorkspaceDesignPreparation CreateFromFreshObservation(
        DesktopUnderstandingDraft understanding,
        DesktopObservation understandingObservation,
        DesktopObservation freshObservation,
        ExplorerWorkspaceGeometry freshGeometry,
        DesktopLayoutConversationState conversationState)
    {
        ArgumentNullException.ThrowIfNull(understandingObservation);
        ArgumentNullException.ThrowIfNull(freshObservation);
        if (!string.Equals(
                DesktopSemanticUnderstandingSourceFingerprint.Create(understandingObservation),
                DesktopSemanticUnderstandingSourceFingerprint.Create(freshObservation),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "桌面项目的名称、类型或集合自上次理解后发生变化，请先重新理解桌面，再生成精确预览。");
        }

        return Create(understanding, freshObservation, freshGeometry, conversationState);
    }

    public static DesktopWorkspaceDesignPreparation Create(
        DesktopUnderstandingDraft legacyUnderstanding,
        DesktopObservation observation,
        ExplorerWorkspaceGeometry geometry,
        DesktopLayoutConversationState conversationState)
    {
        ArgumentNullException.ThrowIfNull(legacyUnderstanding);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(conversationState);

        if (geometry.ObservationId != observation.ObservationId)
            throw new ArgumentException("Explorer Geometry 必须来自同一次 DesktopObservation。", nameof(geometry));

        var semantics = DesktopSemanticUnderstandingProjector.FromLegacyDraft(legacyUnderstanding);
        EnsureSemanticCoverage(semantics, observation);

        var displayByDeviceName = geometry.Displays.ToDictionary(
            static display => display.DeviceName,
            StringComparer.Ordinal);
        var displayRefByStableId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var icon in observation.Icons)
        {
            if (icon.MonitorDeviceName is null ||
                !displayByDeviceName.TryGetValue(icon.MonitorDeviceName, out var display))
            {
                throw new InvalidDataException(
                    $"项目 \"{icon.DisplayName}\" 无法映射到当前 Explorer Workspace Geometry。");
            }
            displayRefByStableId.Add(icon.StableId, display.DisplayRef);
        }

        var assignmentByStableId = semantics.Assignments.ToDictionary(
            static assignment => assignment.StableId,
            StringComparer.Ordinal);
        var criticalStableIds = observation.Icons
            .Where(static icon => !IsMovable(icon))
            .Select(static icon => icon.StableId)
            .Concat(conversationState.ItemDisplayTargets.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static stableId => stableId, StringComparer.Ordinal)
            .ToArray();
        if (criticalStableIds.Length > DesktopWorkspaceDesignRequest.MaxCriticalItems)
        {
            throw new InvalidDataException(
                $"设计关键项目有 {criticalStableIds.Length} 个，超过 Provider-safe 上限 " +
                $"{DesktopWorkspaceDesignRequest.MaxCriticalItems}。");
        }

        var iconByStableId = observation.Icons.ToDictionary(static icon => icon.StableId, StringComparer.Ordinal);
        var itemRefByStableId = criticalStableIds
            .Select((stableId, index) => (stableId, itemRef: $"item-{index + 1:D4}"))
            .ToDictionary(static item => item.stableId, static item => item.itemRef, StringComparer.Ordinal);

        foreach (var stableId in conversationState.ItemDisplayTargets.Keys)
        {
            if (!iconByStableId.ContainsKey(stableId))
                throw new InvalidDataException("Conversation item target 引用了当前 Observation 之外的项目。");
        }

        var criticalItems = criticalStableIds.Select(stableId =>
        {
            var icon = iconByStableId[stableId];
            var assignment = assignmentByStableId[stableId];
            return new DesktopWorkspaceDesignCriticalItem(
                itemRefByStableId[stableId],
                icon.DisplayName,
                assignment.CategoryKey,
                displayRefByStableId[stableId],
                IsMovable(icon));
        }).ToArray();
        var bindings = criticalStableIds
            .Select(stableId => new DesktopWorkspaceCompilationItemRefBinding(
                itemRefByStableId[stableId],
                stableId))
            .ToArray();

        var itemFacts = observation.Icons
            .OrderBy(static icon => icon.StableId, StringComparer.Ordinal)
            .Select(icon => new DesktopWorkspaceCompilationItemFact(
                icon.StableId,
                displayRefByStableId[icon.StableId],
                icon.PixelPosition,
                IsMovable(icon)))
            .ToArray();
        var presentations = observation.Icons
            .OrderBy(static icon => icon.StableId, StringComparer.Ordinal)
            .Select(static icon => new DesktopWorkspacePreviewItemPresentation(
                icon.StableId,
                icon.DisplayName))
            .ToArray();

        var designDisplays = BuildDesignDisplays(geometry);
        var categories = semantics.Categories
            .Select(category => new DesktopWorkspaceDesignCategorySummary(
                category.Key,
                category.Name,
                category.Description,
                semantics.Assignments.Count(assignment => string.Equals(
                    assignment.CategoryKey,
                    category.Key,
                    StringComparison.Ordinal))))
            .ToArray();
        var guidance = BuildProviderSafeGuidance(
            legacyUnderstanding,
            conversationState,
            itemRefByStableId);

        var request = new DesktopWorkspaceDesignRequest(
            new DesktopWorkspaceDesignRevisionTarget(Guid.NewGuid(), 1),
            semantics.DesktopSummary,
            guidance,
            designDisplays,
            categories,
            criticalItems);
        var snapshot = new DesktopWorkspacePreviewSourceSnapshot(
            observation.ObservationId,
            itemFacts,
            presentations);

        return new DesktopWorkspaceDesignPreparation(
            request,
            semantics,
            Array.AsReadOnly(itemFacts),
            Array.AsReadOnly(bindings),
            snapshot);
    }

    private static IReadOnlyList<DesktopWorkspaceDesignDisplay> BuildDesignDisplays(
        ExplorerWorkspaceGeometry geometry)
    {
        var left = geometry.Displays.Min(static display => display.Bounds.Left);
        var top = geometry.Displays.Min(static display => display.Bounds.Top);
        var right = geometry.Displays.Max(static display => display.Bounds.Right);
        var bottom = geometry.Displays.Max(static display => display.Bounds.Bottom);
        var width = (double)((long)right - left);
        var height = (double)((long)bottom - top);
        if (width <= 0d || height <= 0d)
            throw new InvalidDataException("当前虚拟桌面拓扑没有正面积。");

        var primary = geometry.Displays.Single(static display => display.IsPrimary);
        var labels = BuildDisplayLabels(geometry.Displays, primary);
        var result = geometry.Displays
            .OrderBy(static display => display.IsPrimary ? 0 : 1)
            .ThenBy(static display => display.DisplayRef, StringComparer.Ordinal)
            .Select(display =>
            {
                var normalizedLeft = ((long)display.Bounds.Left - left) / width;
                var normalizedTop = ((long)display.Bounds.Top - top) / height;
                var normalizedRight = ((long)display.Bounds.Right - left) / width;
                var normalizedBottom = ((long)display.Bounds.Bottom - top) / height;
                var bounds = CreateClampedRelativeRect(
                    normalizedLeft,
                    normalizedTop,
                    normalizedRight,
                    normalizedBottom);
                var aspect = (double)display.Bounds.Width / display.Bounds.Height;
                return new DesktopWorkspaceDesignDisplay(
                    display.DisplayRef,
                    labels[display.DisplayRef],
                    display.IsPrimary,
                    bounds,
                    aspect);
            })
            .ToArray();
        return Array.AsReadOnly(result);
    }

    private static IReadOnlyDictionary<string, string> BuildDisplayLabels(
        IReadOnlyList<ExplorerDisplayGeometry> displays,
        ExplorerDisplayGeometry primary)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [primary.DisplayRef] = "主屏",
        };
        foreach (var display in displays.Where(static display => !display.IsPrimary))
        {
            var horizontalGap = display.Bounds.Right <= primary.Bounds.Left
                ? primary.Bounds.Left - display.Bounds.Right
                : display.Bounds.Left >= primary.Bounds.Right
                    ? display.Bounds.Left - primary.Bounds.Right
                    : 0;
            var verticalGap = display.Bounds.Bottom <= primary.Bounds.Top
                ? primary.Bounds.Top - display.Bounds.Bottom
                : display.Bounds.Top >= primary.Bounds.Bottom
                    ? display.Bounds.Top - primary.Bounds.Bottom
                    : 0;

            string label;
            if (horizontalGap > 0 && horizontalGap >= verticalGap)
                label = display.Bounds.Right <= primary.Bounds.Left ? "左副屏" : "右副屏";
            else if (verticalGap > 0)
                label = display.Bounds.Bottom <= primary.Bounds.Top ? "上方副屏" : "下方副屏";
            else
            {
                var primaryCenterX = ((long)primary.Bounds.Left + primary.Bounds.Right) / 2d;
                var primaryCenterY = ((long)primary.Bounds.Top + primary.Bounds.Bottom) / 2d;
                var centerX = ((long)display.Bounds.Left + display.Bounds.Right) / 2d;
                var centerY = ((long)display.Bounds.Top + display.Bounds.Bottom) / 2d;
                var dx = centerX - primaryCenterX;
                var dy = centerY - primaryCenterY;
                label = Math.Abs(dx) >= Math.Abs(dy)
                    ? dx < 0 ? "左副屏" : "右副屏"
                    : dy < 0 ? "上方副屏" : "下方副屏";
            }

            if (labels.Values.Contains(label, StringComparer.Ordinal))
                label = $"{label} {display.DisplayRef}";
            labels.Add(display.DisplayRef, label);
        }
        return labels;
    }

    private static DesktopWorkspaceRelativeRect CreateClampedRelativeRect(
        double left,
        double top,
        double right,
        double bottom)
    {
        left = Math.Clamp(left, 0d, 1d);
        top = Math.Clamp(top, 0d, 1d);
        right = Math.Clamp(right, left, 1d);
        bottom = Math.Clamp(bottom, top, 1d);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0d || height <= 0d)
            throw new InvalidDataException("显示器归一化拓扑没有正面积。");
        return new DesktopWorkspaceRelativeRect(left, top, width, height);
    }

    private static string BuildProviderSafeGuidance(
        DesktopUnderstandingDraft legacyUnderstanding,
        DesktopLayoutConversationState conversationState,
        IReadOnlyDictionary<string, string> itemRefByStableId)
    {
        var builder = new StringBuilder();
        builder.AppendLine(conversationState.IntentSummary.Length > 0
            ? conversationState.IntentSummary
            : legacyUnderstanding.IntentSummary);

        foreach (var target in conversationState.CategoryDisplayTargets
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"类别 {target.Key} 的目标屏为 {target.Value}。");
        }
        foreach (var target in conversationState.ItemDisplayTargets
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!itemRefByStableId.TryGetValue(target.Key, out var itemRef))
                throw new InvalidDataException("ItemDisplayTarget 缺少 Provider-safe ItemRef binding。");
            builder.AppendLine($"项目 {itemRef} 的目标屏为 {target.Value}。");
        }
        foreach (var load in conversationState.DisplayLoadPreferences
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"显示器 {load.Key} 的负载偏好为 {load.Value}。");
        }
        foreach (var reserved in conversationState.ReservedRegions
                     .OrderBy(static region => region.Key, StringComparer.Ordinal))
        {
            builder.AppendLine(
                $"显示器 {reserved.DisplayRef} 保留区域 {reserved.Key}：" +
                $"{reserved.PlacementHint} / {reserved.SizeHint}，用途：{reserved.Purpose}。");
        }

        var result = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException("Workspace Design 缺少用户布局目标。");
        return result;
    }

    private static void EnsureSemanticCoverage(
        DesktopSemanticUnderstanding semantics,
        DesktopObservation observation)
    {
        var semanticIds = semantics.Assignments
            .Select(static assignment => assignment.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var observationIds = observation.Icons
            .Select(static icon => icon.StableId)
            .ToHashSet(StringComparer.Ordinal);
        if (!semanticIds.SetEquals(observationIds))
            throw new InvalidDataException("纯语义 Understanding 必须完整覆盖当前 Observation 项目集合。 ");
    }

    private static bool IsMovable(DesktopIconDescriptor icon)
        => !icon.IsSystemItem && icon.ItemKind != ItemKind.SystemItem;
}
