using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Preview;

/// <summary>预览项处置类型。</summary>
public enum DesktopLayoutRuleAwarePreviewItemDisposition
{
    Excluded,
    Pinned,
    ZoneOverride,
    Default,
}

/// <summary>预览项放置状态。</summary>
public enum DesktopLayoutRuleAwarePreviewPlacementState
{
    Fixed,
    AlreadyAtTarget,
    MoveRequired,
    Unassigned,
}

/// <summary>
/// 规则感知预览项 — 不可变 sealed class。
/// 包含图标、规则、处置、放置状态、坐标和 Zone 信息。
/// </summary>
public sealed class DesktopLayoutRuleAwarePreviewItem
{
    public DesktopIconSnapshotItem Icon { get; }
    public DesktopLayoutIconRule? Rule { get; }
    public DesktopLayoutRuleAwarePreviewItemDisposition Disposition { get; }
    public DesktopLayoutRuleAwarePreviewPlacementState PlacementState { get; }
    public PixelPoint CurrentPosition { get; }
    public PixelPoint? TargetPosition { get; }
    public Guid? ZoneId { get; }
    public int? SlotIndex { get; }
    public PixelRect? TargetCellBounds { get; }
    public PixelRect CurrentCellBounds { get; }
    public DesktopLayoutZone? TargetZone { get; }

    public bool IsAssigned => PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget
        || PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired;
    public bool RequiresMove => PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired;

    internal DesktopLayoutRuleAwarePreviewItem(
        DesktopIconSnapshotItem icon,
        DesktopLayoutIconRule? rule,
        DesktopLayoutRuleAwarePreviewItemDisposition disposition,
        DesktopLayoutRuleAwarePreviewPlacementState placementState,
        PixelPoint currentPosition,
        PixelPoint? targetPosition,
        Guid? zoneId,
        int? slotIndex,
        PixelRect? targetCellBounds,
        PixelRect currentCellBounds,
        DesktopLayoutZone? targetZone)
    {
        Icon = icon;
        Rule = rule;
        Disposition = disposition;
        PlacementState = placementState;
        CurrentPosition = currentPosition;
        TargetPosition = targetPosition;
        ZoneId = zoneId;
        SlotIndex = slotIndex;
        TargetCellBounds = targetCellBounds;
        CurrentCellBounds = currentCellBounds;
        TargetZone = targetZone;
    }
}

/// <summary>
/// 规则感知预览计划 — 不可变 sealed class。
/// 包含完整的输入、分区、规则感知布局和投影预览项。
/// </summary>
public sealed class DesktopLayoutRuleAwarePreviewPlan
{
    public DesktopLayoutTemplate Template { get; }
    public DisplayDescriptor TargetDisplay { get; }
    public ResolvedDesktopLayoutTemplate ResolvedTemplate { get; }
    public DesktopLayoutIconRuleResolutionPlan ResolutionPlan { get; }
    public DesktopLayoutRuleAwarePlacementPlan PlacementPlan { get; }

    public IReadOnlyList<DesktopIconSnapshotItem> InputIcons { get; }
    public IReadOnlyList<DesktopIconSnapshotItem> TargetDisplayIcons { get; }
    public IReadOnlyList<DesktopIconSnapshotItem> OutOfScopeIcons { get; }
    public IReadOnlyList<DesktopLayoutRuleAwarePreviewItem> Items { get; }

    public int InputIconCount { get; }
    public int TargetDisplayIconCount { get; }
    public int OutOfScopeIconCount { get; }
    public int ExcludedCount { get; }
    public int PinnedCount { get; }
    public int OverrideAssignedCount { get; }
    public int OverrideUnassignedCount { get; }
    public int DefaultAssignedCount { get; }
    public int DefaultUnassignedCount { get; }
    public int AlreadyAtTargetCount { get; }
    public int MoveRequiredCount { get; }
    public bool IsComplete { get; }
    public bool HasChanges { get; }

    internal DesktopLayoutRuleAwarePreviewPlan(
        DesktopLayoutTemplate template,
        DisplayDescriptor targetDisplay,
        ResolvedDesktopLayoutTemplate resolvedTemplate,
        DesktopLayoutIconRuleResolutionPlan resolutionPlan,
        DesktopLayoutRuleAwarePlacementPlan placementPlan,
        IReadOnlyList<DesktopIconSnapshotItem> inputIcons,
        IReadOnlyList<DesktopIconSnapshotItem> targetDisplayIcons,
        IReadOnlyList<DesktopIconSnapshotItem> outOfScopeIcons,
        IReadOnlyList<DesktopLayoutRuleAwarePreviewItem> items)
    {
        Template = template;
        TargetDisplay = targetDisplay;
        ResolvedTemplate = resolvedTemplate;
        ResolutionPlan = resolutionPlan;
        PlacementPlan = placementPlan;

        var itemsList = items.ToList();
        InputIcons = inputIcons.ToList().AsReadOnly();
        TargetDisplayIcons = targetDisplayIcons.ToList().AsReadOnly();
        OutOfScopeIcons = outOfScopeIcons.ToList().AsReadOnly();
        Items = itemsList.AsReadOnly();

        InputIconCount = InputIcons.Count;
        TargetDisplayIconCount = TargetDisplayIcons.Count;
        OutOfScopeIconCount = OutOfScopeIcons.Count;
        ExcludedCount = placementPlan.ExcludedCount;
        PinnedCount = placementPlan.PinnedCount;
        OverrideAssignedCount = placementPlan.OverrideAssignedCount;
        OverrideUnassignedCount = placementPlan.OverrideUnassignedCount;
        DefaultAssignedCount = placementPlan.DefaultAssignedCount;
        DefaultUnassignedCount = placementPlan.DefaultUnassignedCount;

        int atTarget = 0, moveReq = 0;
        foreach (var item in itemsList)
        {
            if (item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget)
                atTarget++;
            else if (item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired)
                moveReq++;
        }
        AlreadyAtTargetCount = atTarget;
        MoveRequiredCount = moveReq;
        IsComplete = placementPlan.IsComplete;
        HasChanges = moveReq > 0;
    }
}

/// <summary>
/// 规则感知预览规划器 — 纯 Core，无副作用。
/// 串联规则解析 → 规则感知 Placement → 投影预览项。
/// </summary>
public static class DesktopLayoutRuleAwarePreviewPlanner
{
    public static DesktopLayoutRuleAwarePreviewPlan Plan(
        DesktopLayoutTemplate template,
        DesktopLayoutIconRuleSet ruleSet,
        IReadOnlyList<DisplayDescriptor> displays,
        string? selectedTargetDeviceName,
        PixelPoint iconSpacing,
        IReadOnlyList<DesktopIconSnapshotItem> icons)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (ruleSet is null) throw new ArgumentNullException(nameof(ruleSet));
        if (displays is null) throw new ArgumentNullException(nameof(displays));
        if (icons is null) throw new ArgumentNullException(nameof(icons));
        if (iconSpacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing));
        if (iconSpacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing));
        if (ruleSet.TemplateId != template.TemplateId)
            throw new ArgumentException(
                $"RuleSet.TemplateId ({ruleSet.TemplateId}) 与 Template.TemplateId ({template.TemplateId}) 不一致。");

        // 图标验证 + 按 Ordinal ASC
        var iconList = new List<DesktopIconSnapshotItem>(icons.Count);
        var seenOrdinals = new HashSet<int>();
        var seenStableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var icon in icons)
        {
            if (icon is null) throw new ArgumentException("Icons 不能包含 null 元素。");
            if (icon.Ordinal < 0) throw new ArgumentOutOfRangeException(nameof(icons));
            if (!seenOrdinals.Add(icon.Ordinal))
                throw new ArgumentException($"Icons Ordinal {icon.Ordinal} 重复。");
            if (!seenStableIds.Add(icon.StableId))
                throw new ArgumentException($"Icons StableId \"{icon.StableId}\" 重复。");
            iconList.Add(icon);
        }
        iconList.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        // 解析目标显示器
        var targetDisplay = DesktopLayoutTargetDisplayResolver.Resolve(
            template.TargetDisplayBinding, displays, selectedTargetDeviceName);

        // 分离目标显示器和 OutOfScope 图标
        var targetIcons = new List<DesktopIconSnapshotItem>();
        var oosIcons = new List<DesktopIconSnapshotItem>();
        foreach (var icon in iconList)
        {
            if (string.Equals(icon.MonitorDeviceName, targetDisplay.DeviceName, StringComparison.Ordinal))
                targetIcons.Add(icon);
            else
                oosIcons.Add(icon);
        }

        // 模板解析
        var resolvedTemplate = DesktopLayoutTemplateResolver.Resolve(
            template, targetDisplay.WorkArea, iconSpacing);

        // 规则解析
        var resolutionPlan = DesktopLayoutIconRuleResolver.Resolve(
            template, targetDisplay, icons, ruleSet);

        // 规则感知 Placement
        var placementPlan = DesktopLayoutRuleAwarePlacementPlanner.Plan(
            resolvedTemplate, resolutionPlan, iconSpacing);

        // 投影 PreviewItems
        var items = new List<DesktopLayoutRuleAwarePreviewItem>();

        foreach (var fi in placementPlan.ExcludedItems)
        {
            items.Add(new DesktopLayoutRuleAwarePreviewItem(
                fi.Icon, fi.Rule,
                DesktopLayoutRuleAwarePreviewItemDisposition.Excluded,
                DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
                fi.CurrentPosition, null, null, null, null, fi.FixedRect, null));
        }

        foreach (var fi in placementPlan.PinnedItems)
        {
            items.Add(new DesktopLayoutRuleAwarePreviewItem(
                fi.Icon, fi.Rule,
                DesktopLayoutRuleAwarePreviewItemDisposition.Pinned,
                DesktopLayoutRuleAwarePreviewPlacementState.Fixed,
                fi.CurrentPosition, null, null, null, null, fi.FixedRect, null));
        }

        foreach (var ai in placementPlan.OverrideAssignedItems)
        {
            var atTarget = ai.TargetPosition == ai.Icon.PixelPosition;
            items.Add(new DesktopLayoutRuleAwarePreviewItem(
                ai.Icon, ai.Rule,
                DesktopLayoutRuleAwarePreviewItemDisposition.ZoneOverride,
                atTarget ? DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget
                         : DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired,
                ai.Icon.PixelPosition, ai.TargetPosition,
                ai.Zone.Zone.ZoneId, ai.SlotIndex, ai.TargetCellBounds,
                DesktopIconLayoutGeometry.CreateRectChecked(ai.Icon.PixelPosition, iconSpacing),
                ai.Zone.Zone));
        }

        foreach (var ou in placementPlan.OverrideUnassignedItems)
        {
            items.Add(new DesktopLayoutRuleAwarePreviewItem(
                ou.Icon, ou.Rule,
                DesktopLayoutRuleAwarePreviewItemDisposition.ZoneOverride,
                DesktopLayoutRuleAwarePreviewPlacementState.Unassigned,
                ou.Icon.PixelPosition, null, null, null, null,
                DesktopIconLayoutGeometry.CreateRectChecked(ou.Icon.PixelPosition, iconSpacing),
                null));
        }

        foreach (var ai in placementPlan.DefaultAssignedItems)
        {
            var atTarget = ai.TargetPosition == ai.Icon.PixelPosition;
            items.Add(new DesktopLayoutRuleAwarePreviewItem(
                ai.Icon, null,
                DesktopLayoutRuleAwarePreviewItemDisposition.Default,
                atTarget ? DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget
                         : DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired,
                ai.Icon.PixelPosition, ai.TargetPosition,
                ai.Zone.Zone.ZoneId, ai.SlotIndex, ai.TargetCellBounds,
                DesktopIconLayoutGeometry.CreateRectChecked(ai.Icon.PixelPosition, iconSpacing),
                ai.Zone.Zone));
        }

        foreach (var du in placementPlan.DefaultUnassignedItems)
        {
            items.Add(new DesktopLayoutRuleAwarePreviewItem(
                du, null,
                DesktopLayoutRuleAwarePreviewItemDisposition.Default,
                DesktopLayoutRuleAwarePreviewPlacementState.Unassigned,
                du.PixelPosition, null, null, null, null,
                DesktopIconLayoutGeometry.CreateRectChecked(du.PixelPosition, iconSpacing),
                null));
        }

        // 按 original TargetDisplayIcons 顺序排列
        var itemByIcon = new Dictionary<DesktopIconSnapshotItem, DesktopLayoutRuleAwarePreviewItem>(
            ReferenceEqualityComparer.Instance);
        foreach (var item in items)
            itemByIcon[item.Icon] = item;

        var orderedItems = new List<DesktopLayoutRuleAwarePreviewItem>();
        foreach (var tIcon in targetIcons)
        {
            if (itemByIcon.TryGetValue(tIcon, out var item))
                orderedItems.Add(item);
            else
                throw new InvalidOperationException(
                    $"图标 (Ordinal={tIcon.Ordinal}, StableId=\"{tIcon.StableId}\") 未映射到 PreviewItem。");
        }

        return new DesktopLayoutRuleAwarePreviewPlan(
            template, targetDisplay, resolvedTemplate, resolutionPlan, placementPlan,
            iconList, targetIcons, oosIcons, orderedItems);
    }
}
