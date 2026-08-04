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
        if (icon is null) throw new ArgumentNullException(nameof(icon));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentException($"未定义的 Disposition：{disposition}。");
        if (!Enum.IsDefined(placementState))
            throw new ArgumentException($"未定义的 PlacementState：{placementState}。");
        if (currentPosition != icon.PixelPosition)
            throw new ArgumentException(
                "CurrentPosition 必须等于 Icon.PixelPosition。");

        // Disposition consistency
        switch (disposition)
        {
            case DesktopLayoutRuleAwarePreviewItemDisposition.Excluded:
                if (rule is null || rule.Kind != DesktopLayoutIconRuleKind.Exclude)
                    throw new ArgumentException("Excluded 必须对应 Exclude Rule。");
                break;
            case DesktopLayoutRuleAwarePreviewItemDisposition.Pinned:
                if (rule is null || rule.Kind != DesktopLayoutIconRuleKind.Pin)
                    throw new ArgumentException("Pinned 必须对应 Pin Rule。");
                break;
            case DesktopLayoutRuleAwarePreviewItemDisposition.ZoneOverride:
                if (rule is null || rule.Kind != DesktopLayoutIconRuleKind.ZoneOverride)
                    throw new ArgumentException("ZoneOverride 必须对应 ZoneOverride Rule。");
                break;
            case DesktopLayoutRuleAwarePreviewItemDisposition.Default:
                if (rule is not null)
                    throw new ArgumentException("Default Disposition 的 Rule 必须为 null。");
                break;
        }

        // Rule StableId consistency
        if (rule is not null)
        {
            if (!StringComparer.Ordinal.Equals(rule.StableId, icon.StableId))
                throw new ArgumentException(
                    $"Rule.StableId \"{rule.StableId}\" 与 Icon.StableId \"{icon.StableId}\" 不一致。");
        }

        // PlacementState consistency
        switch (placementState)
        {
            case DesktopLayoutRuleAwarePreviewPlacementState.Fixed:
                if (disposition != DesktopLayoutRuleAwarePreviewItemDisposition.Excluded
                    && disposition != DesktopLayoutRuleAwarePreviewItemDisposition.Pinned)
                    throw new ArgumentException("Fixed 只允许 Excluded/Pinned。");
                if (targetPosition.HasValue || zoneId.HasValue
                    || slotIndex.HasValue || targetCellBounds.HasValue || targetZone is not null)
                    throw new ArgumentException("Fixed 不得拥有目标信息。");
                break;
            case DesktopLayoutRuleAwarePreviewPlacementState.Unassigned:
                if (targetPosition.HasValue || zoneId.HasValue
                    || slotIndex.HasValue || targetCellBounds.HasValue || targetZone is not null)
                    throw new ArgumentException("Unassigned 不得拥有目标信息。");
                break;
            case DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget:
                if (!targetPosition.HasValue || !zoneId.HasValue
                    || !slotIndex.HasValue || !targetCellBounds.HasValue || targetZone is null)
                    throw new ArgumentException("AlreadyAtTarget 必须拥有完整目标信息。");
                if (currentPosition != targetPosition.Value)
                    throw new ArgumentException("AlreadyAtTarget 必须 Current==Target。");
                if (targetZone.ZoneId != zoneId.Value)
                    throw new ArgumentException("ZoneId 与 TargetZone.ZoneId 不一致。");
                if (rule is not null && rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride
                    && rule.TargetZoneId != zoneId.Value)
                    throw new ArgumentException("ZoneOverride Rule.TargetZoneId 与 ZoneId 不一致。");
                break;
            case DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired:
                if (!targetPosition.HasValue || !zoneId.HasValue
                    || !slotIndex.HasValue || !targetCellBounds.HasValue || targetZone is null)
                    throw new ArgumentException("MoveRequired 必须拥有完整目标信息。");
                if (currentPosition == targetPosition.Value)
                    throw new ArgumentException("MoveRequired 必须 Current!=Target。");
                if (targetZone.ZoneId != zoneId.Value)
                    throw new ArgumentException("ZoneId 与 TargetZone.ZoneId 不一致。");
                if (rule is not null && rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride
                    && rule.TargetZoneId != zoneId.Value)
                    throw new ArgumentException("ZoneOverride Rule.TargetZoneId 与 ZoneId 不一致。");
                break;
        }

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
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (targetDisplay is null) throw new ArgumentNullException(nameof(targetDisplay));
        if (resolvedTemplate is null) throw new ArgumentNullException(nameof(resolvedTemplate));
        if (resolutionPlan is null) throw new ArgumentNullException(nameof(resolutionPlan));
        if (placementPlan is null) throw new ArgumentNullException(nameof(placementPlan));
        if (inputIcons is null) throw new ArgumentNullException(nameof(inputIcons));
        if (targetDisplayIcons is null) throw new ArgumentNullException(nameof(targetDisplayIcons));
        if (outOfScopeIcons is null) throw new ArgumentNullException(nameof(outOfScopeIcons));
        if (items is null) throw new ArgumentNullException(nameof(items));

        // 引用链一致性
        if (!ReferenceEquals(resolvedTemplate.Template, template))
            throw new ArgumentException("ResolvedTemplate.Template 与 template 不是同一实例。");
        if (!ReferenceEquals(resolutionPlan.Template, template))
            throw new ArgumentException("ResolutionPlan.Template 与 template 不是同一实例。");
        if (!ReferenceEquals(placementPlan.ResolvedTemplate, resolvedTemplate))
            throw new ArgumentException("PlacementPlan.ResolvedTemplate 与 resolvedTemplate 不是同一实例。");
        if (!ReferenceEquals(placementPlan.ResolutionPlan, resolutionPlan))
            throw new ArgumentException("PlacementPlan.ResolutionPlan 与 resolutionPlan 不是同一实例。");

        // TargetDisplay 一致性
        if (!ReferenceEquals(resolutionPlan.TargetDisplay, targetDisplay))
            throw new ArgumentException("ResolutionPlan.TargetDisplay 与 targetDisplay 不是同一实例。");

        // ═══ ResolutionPlan 来源验证 ═══
        // InputIcons 与 resolutionPlan.InputIcons 逐项同引用
        if (inputIcons.Count != resolutionPlan.InputIcons.Count)
            throw new ArgumentException(
                $"InputIcons 数量 ({inputIcons.Count}) != ResolutionPlan.InputIcons ({resolutionPlan.InputIcons.Count})。");
        for (var i = 0; i < inputIcons.Count; i++)
            if (!ReferenceEquals(inputIcons[i], resolutionPlan.InputIcons[i]))
                throw new ArgumentException($"InputIcons[{i}] 与 ResolutionPlan.InputIcons[{i}] 不是同一实例。");

        // TargetDisplayIcons 与 resolutionPlan 逐项同引用
        if (targetDisplayIcons.Count != resolutionPlan.TargetDisplayIcons.Count)
            throw new ArgumentException("TargetDisplayIcons 数量与 ResolutionPlan 不一致。");
        for (var i = 0; i < targetDisplayIcons.Count; i++)
            if (!ReferenceEquals(targetDisplayIcons[i], resolutionPlan.TargetDisplayIcons[i]))
                throw new ArgumentException($"TargetDisplayIcons[{i}] 与 ResolutionPlan 不是同一实例。");

        // OutOfScopeIcons 与 resolutionPlan 逐项同引用
        if (outOfScopeIcons.Count != resolutionPlan.OutOfScopeIcons.Count)
            throw new ArgumentException("OutOfScopeIcons 数量与 ResolutionPlan 不一致。");
        for (var i = 0; i < outOfScopeIcons.Count; i++)
            if (!ReferenceEquals(outOfScopeIcons[i], resolutionPlan.OutOfScopeIcons[i]))
                throw new ArgumentException($"OutOfScopeIcons[{i}] 与 ResolutionPlan 不是同一实例。");

        // 输入集合验证 + 防御性复制
        var inputList = inputIcons.ToList();
        var targetList = targetDisplayIcons.ToList();
        var oosList = outOfScopeIcons.ToList();
        var itemsList = items.ToList();

        // InputIcons: 无 null, Ordinal ASC 唯一, StableId 唯一
        var inputRefs = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
        var inputOrdinals = new HashSet<int>();
        var inputSids = new HashSet<string>(StringComparer.Ordinal);
        int? prev = null;
        for (var i = 0; i < inputList.Count; i++)
        {
            if (inputList[i] is null)
                throw new ArgumentException($"InputIcons[{i}] 为 null。");
            if (!inputRefs.Add(inputList[i]))
                throw new ArgumentException($"InputIcons 图标重复。");
            if (!inputOrdinals.Add(inputList[i].Ordinal))
                throw new ArgumentException($"InputIcons Ordinal {inputList[i].Ordinal} 重复。");
            if (!inputSids.Add(inputList[i].StableId))
                throw new ArgumentException($"InputIcons StableId \"{inputList[i].StableId}\" 重复。");
            if (prev.HasValue && inputList[i].Ordinal <= prev.Value)
                throw new ArgumentException("InputIcons 未按 Ordinal ASC。");
            prev = inputList[i].Ordinal;
        }

        // Target + OOS: 原引用、无重叠、完整覆盖
        var targetRefs = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
        var oosRefs = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < targetList.Count; i++)
        {
            if (targetList[i] is null)
                throw new ArgumentException($"TargetDisplayIcons[{i}] 为 null。");
            if (!inputRefs.Contains(targetList[i]))
                throw new ArgumentException($"TargetDisplayIcons[{i}] 不在 InputIcons 中。");
            if (!targetRefs.Add(targetList[i]))
                throw new ArgumentException("TargetDisplayIcons 重复。");
        }
        for (var i = 0; i < oosList.Count; i++)
        {
            if (oosList[i] is null)
                throw new ArgumentException($"OutOfScopeIcons[{i}] 为 null。");
            if (!inputRefs.Contains(oosList[i]))
                throw new ArgumentException($"OutOfScopeIcons[{i}] 不在 InputIcons 中。");
            if (!oosRefs.Add(oosList[i]))
                throw new ArgumentException("OutOfScopeIcons 重复。");
        }
        foreach (var t in targetRefs)
            if (oosRefs.Contains(t))
                throw new ArgumentException("Target 与 OOS 重叠。");
        if (targetRefs.Count + oosRefs.Count != inputRefs.Count)
            throw new ArgumentException("Target+OOS 未完整覆盖 InputIcons。");

        // Items 与 TargetDisplayIcons 一一对应
        if (itemsList.Count != targetList.Count)
            throw new ArgumentException($"Items 数量 ({itemsList.Count}) ≠ TargetDisplayIcons ({targetList.Count})。");
        var itemIconRefs = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < itemsList.Count; i++)
        {
            if (itemsList[i] is null)
                throw new ArgumentException($"Items[{i}] 为 null。");
            if (!ReferenceEquals(itemsList[i].Icon, targetList[i]))
                throw new ArgumentException($"Items[{i}].Icon 与 TargetDisplayIcons[{i}] 不是同一实例。");
            if (!itemIconRefs.Add(itemsList[i].Icon))
                throw new ArgumentException($"Items 中 Icon 重复。");
        }

        // 分类计数与 PlacementPlan 一致
        int excluded = 0, pinned = 0, ovrAssigned = 0, ovrUnassigned = 0,
            defAssigned = 0, defUnassigned = 0;
        foreach (var item in itemsList)
        {
            switch (item.Disposition)
            {
                case DesktopLayoutRuleAwarePreviewItemDisposition.Excluded: excluded++; break;
                case DesktopLayoutRuleAwarePreviewItemDisposition.Pinned: pinned++; break;
                case DesktopLayoutRuleAwarePreviewItemDisposition.ZoneOverride:
                    if (item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.Unassigned)
                        ovrUnassigned++;
                    else ovrAssigned++;
                    break;
                case DesktopLayoutRuleAwarePreviewItemDisposition.Default:
                    if (item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.Unassigned)
                        defUnassigned++;
                    else defAssigned++;
                    break;
            }
        }
        if (excluded != placementPlan.ExcludedCount)
            throw new ArgumentException($"ExcludedCount 不一致：{excluded} vs {placementPlan.ExcludedCount}。");
        if (pinned != placementPlan.PinnedCount)
            throw new ArgumentException($"PinnedCount 不一致：{pinned} vs {placementPlan.PinnedCount}。");
        if (ovrAssigned != placementPlan.OverrideAssignedCount)
            throw new ArgumentException("OverrideAssignedCount 不一致。");
        if (ovrUnassigned != placementPlan.OverrideUnassignedCount)
            throw new ArgumentException("OverrideUnassignedCount 不一致。");
        if (defAssigned != placementPlan.DefaultAssignedCount)
            throw new ArgumentException("DefaultAssignedCount 不一致。");
        if (defUnassigned != placementPlan.DefaultUnassignedCount)
            throw new ArgumentException("DefaultUnassignedCount 不一致。");

        // ═══ 每个 PreviewItem 精确对应 PlacementPlan 原始来源 ═══
        // 构建 PlacementPlan 引用索引
        var ppExcludedByIcon = new Dictionary<DesktopIconSnapshotItem, DesktopLayoutRuleAwareFixedItem>(
            ReferenceEqualityComparer.Instance);
        foreach (var fi in placementPlan.ExcludedItems)
            ppExcludedByIcon[fi.Icon] = fi;
        var ppPinnedByIcon = new Dictionary<DesktopIconSnapshotItem, DesktopLayoutRuleAwareFixedItem>(
            ReferenceEqualityComparer.Instance);
        foreach (var fi in placementPlan.PinnedItems)
            ppPinnedByIcon[fi.Icon] = fi;
        var ppOvrAssignedByRule = new Dictionary<DesktopLayoutIconRule, DesktopLayoutRuleAwareAssignedItem>(
            ReferenceEqualityComparer.Instance);
        foreach (var ai in placementPlan.OverrideAssignedItems)
            ppOvrAssignedByRule[ai.Rule!] = ai;
        var ppOvrUnassignedByRule = new HashSet<DesktopLayoutIconRule>(
            ReferenceEqualityComparer.Instance);
        foreach (var ou in placementPlan.OverrideUnassignedItems)
            ppOvrUnassignedByRule.Add(ou.Rule);
        var ppDefAssignedByIcon = new Dictionary<DesktopIconSnapshotItem, DesktopLayoutRuleAwareAssignedItem>(
            ReferenceEqualityComparer.Instance);
        foreach (var ai in placementPlan.DefaultAssignedItems)
            ppDefAssignedByIcon[ai.Icon] = ai;
        var ppDefUnassignedIcons = new HashSet<DesktopIconSnapshotItem>(
            ReferenceEqualityComparer.Instance);
        foreach (var du in placementPlan.DefaultUnassignedItems)
            ppDefUnassignedIcons.Add(du);

        for (var i = 0; i < itemsList.Count; i++)
        {
            var item = itemsList[i];
            switch (item.Disposition)
            {
                case DesktopLayoutRuleAwarePreviewItemDisposition.Excluded:
                    if (!ppExcludedByIcon.TryGetValue(item.Icon, out var exFi))
                        throw new ArgumentException($"Items[{i}] Excluded Icon 不在 PlacementPlan.ExcludedItems 中。");
                    if (!ReferenceEquals(exFi.Rule, item.Rule))
                        throw new ArgumentException($"Items[{i}] Excluded Rule 与 PlacementPlan 不一致。");
                    break;
                case DesktopLayoutRuleAwarePreviewItemDisposition.Pinned:
                    if (!ppPinnedByIcon.TryGetValue(item.Icon, out var pinFi))
                        throw new ArgumentException($"Items[{i}] Pinned Icon 不在 PlacementPlan.PinnedItems 中。");
                    if (!ReferenceEquals(pinFi.Rule, item.Rule))
                        throw new ArgumentException($"Items[{i}] Pinned Rule 与 PlacementPlan 不一致。");
                    break;
                case DesktopLayoutRuleAwarePreviewItemDisposition.ZoneOverride:
                    if (item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.Unassigned)
                    {
                        if (!ppOvrUnassignedByRule.Contains(item.Rule!))
                            throw new ArgumentException($"Items[{i}] OverrideUnassigned Rule 不在 PlacementPlan 中。");
                    }
                    else
                    {
                        if (!ppOvrAssignedByRule.TryGetValue(item.Rule!, out var ovrAi))
                            throw new ArgumentException($"Items[{i}] OverrideAssigned Rule 不在 PlacementPlan 中。");
                        if (!ReferenceEquals(ovrAi.Icon, item.Icon))
                            throw new ArgumentException($"Items[{i}] OverrideAssigned Icon 不一致。");
                        if (ovrAi.SlotIndex != item.SlotIndex)
                            throw new ArgumentException($"Items[{i}] OverrideAssigned SlotIndex 不一致。");
                        if (ovrAi.TargetPosition != item.TargetPosition)
                            throw new ArgumentException($"Items[{i}] OverrideAssigned TargetPosition 不一致。");
                    }
                    break;
                case DesktopLayoutRuleAwarePreviewItemDisposition.Default:
                    if (item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.Unassigned)
                    {
                        if (!ppDefUnassignedIcons.Contains(item.Icon))
                            throw new ArgumentException($"Items[{i}] DefaultUnassigned Icon 不在 PlacementPlan 中。");
                    }
                    else
                    {
                        if (!ppDefAssignedByIcon.TryGetValue(item.Icon, out var defAi))
                            throw new ArgumentException($"Items[{i}] DefaultAssigned Icon 不在 PlacementPlan 中。");
                        if (defAi.SlotIndex != item.SlotIndex)
                            throw new ArgumentException($"Items[{i}] DefaultAssigned SlotIndex 不一致。");
                        if (defAi.TargetPosition != item.TargetPosition)
                            throw new ArgumentException($"Items[{i}] DefaultAssigned TargetPosition 不一致。");
                    }
                    break;
            }
        }

        // 统计
        int atTarget = 0, moveReq = 0;
        foreach (var item in itemsList)
        {
            if (item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.AlreadyAtTarget)
                atTarget++;
            else if (item.PlacementState == DesktopLayoutRuleAwarePreviewPlacementState.MoveRequired)
                moveReq++;
        }

        Template = template;
        TargetDisplay = targetDisplay;
        ResolvedTemplate = resolvedTemplate;
        ResolutionPlan = resolutionPlan;
        PlacementPlan = placementPlan;
        InputIcons = inputList.AsReadOnly();
        TargetDisplayIcons = targetList.AsReadOnly();
        OutOfScopeIcons = oosList.AsReadOnly();
        Items = itemsList.AsReadOnly();
        InputIconCount = inputList.Count;
        TargetDisplayIconCount = targetList.Count;
        OutOfScopeIconCount = oosList.Count;
        ExcludedCount = excluded;
        PinnedCount = pinned;
        OverrideAssignedCount = ovrAssigned;
        OverrideUnassignedCount = ovrUnassigned;
        DefaultAssignedCount = defAssigned;
        DefaultUnassignedCount = defUnassigned;
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
