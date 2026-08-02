using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Assignment;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Preview;

/// <summary>
/// 完整的布局预览计划。包含目标显示器解析、分区分配、坐标规划和位置差异。
/// </summary>
public sealed record DesktopLayoutPreviewPlan
{
    public DesktopLayoutTemplate Template { get; }
    public DisplayDescriptor TargetDisplay { get; }
    public ResolvedDesktopLayoutTemplate ResolvedTemplate { get; }
    public DesktopLayoutAssignmentPlan AssignmentPlan { get; }
    public DesktopLayoutPlacementPlan PlacementPlan { get; }

    public IReadOnlyList<DesktopIconSnapshotItem> InputIcons { get; }
    public IReadOnlyList<DesktopIconSnapshotItem> TargetDisplayIcons { get; }
    public IReadOnlyList<DesktopIconSnapshotItem> OutOfScopeIcons { get; }
    public IReadOnlyList<DesktopLayoutPreviewItem> Items { get; }

    public int InputIconCount { get; }
    public int TargetDisplayIconCount { get; }
    public int OutOfScopeIconCount { get; }

    public int AssignedCount { get; }
    public int AlreadyAtTargetCount { get; }
    public int MoveRequiredCount { get; }
    public int UnassignedCount { get; }

    public bool IsComplete { get; }
    public bool HasChanges { get; }
    public bool IsNoOp { get; }

    public DesktopLayoutPreviewPlan(
        DesktopLayoutTemplate template,
        DisplayDescriptor targetDisplay,
        ResolvedDesktopLayoutTemplate resolvedTemplate,
        DesktopLayoutAssignmentPlan assignmentPlan,
        DesktopLayoutPlacementPlan placementPlan,
        IReadOnlyList<DesktopIconSnapshotItem> inputIcons,
        IReadOnlyList<DesktopIconSnapshotItem> targetDisplayIcons,
        IReadOnlyList<DesktopIconSnapshotItem> outOfScopeIcons,
        IReadOnlyList<DesktopLayoutPreviewItem> items)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (targetDisplay is null) throw new ArgumentNullException(nameof(targetDisplay));
        if (resolvedTemplate is null) throw new ArgumentNullException(nameof(resolvedTemplate));
        if (assignmentPlan is null) throw new ArgumentNullException(nameof(assignmentPlan));
        if (placementPlan is null) throw new ArgumentNullException(nameof(placementPlan));
        if (inputIcons is null) throw new ArgumentNullException(nameof(inputIcons));
        if (targetDisplayIcons is null) throw new ArgumentNullException(nameof(targetDisplayIcons));
        if (outOfScopeIcons is null) throw new ArgumentNullException(nameof(outOfScopeIcons));
        if (items is null) throw new ArgumentNullException(nameof(items));

        var inputList = inputIcons.ToList();
        var targetList = targetDisplayIcons.ToList();
        var oosList = outOfScopeIcons.ToList();
        var itemsList = items.ToList();

        // ═══ 规划链一致性 ═══
        if (!ReferenceEquals(template, resolvedTemplate.Template))
            throw new ArgumentException(
                "Template 与 ResolvedTemplate.Template 不是同一实例。", nameof(resolvedTemplate));
        if (targetDisplay.WorkArea != resolvedTemplate.WorkArea)
            throw new ArgumentException(
                "TargetDisplay.WorkArea 与 ResolvedTemplate.WorkArea 不一致。", nameof(resolvedTemplate));
        if (!ReferenceEquals(assignmentPlan.ResolvedTemplate, resolvedTemplate))
            throw new ArgumentException(
                "AssignmentPlan.ResolvedTemplate 与 ResolvedTemplate 不是同一实例。", nameof(assignmentPlan));
        if (!ReferenceEquals(placementPlan.AssignmentPlan, assignmentPlan))
            throw new ArgumentException(
                "PlacementPlan.AssignmentPlan 与 AssignmentPlan 不是同一实例。", nameof(placementPlan));

        // ═══ TargetDisplayIcons == AssignmentPlan.InputIcons (同序同引用) ═══
        if (targetList.Count != assignmentPlan.InputIcons.Count)
            throw new ArgumentException(
                $"TargetDisplayIcons 数量 ({targetList.Count}) 不等于 " +
                $"AssignmentPlan.InputIcons 数量 ({assignmentPlan.InputIcons.Count})。",
                nameof(targetDisplayIcons));
        for (var i = 0; i < targetList.Count; i++)
        {
            if (!ReferenceEquals(targetList[i], assignmentPlan.InputIcons[i]))
                throw new ArgumentException(
                    $"TargetDisplayIcons[{i}] 与 AssignmentPlan.InputIcons[{i}] 不是同一实例。",
                    nameof(targetDisplayIcons));
        }

        // ═══ Input 引用分割 ═══
        var inputRefs = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
        var targetRefs = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);
        var oosRefs = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);

        for (var i = 0; i < inputList.Count; i++)
        {
            if (inputList[i] is null)
                throw new ArgumentException($"InputIcons[{i}] 为 null。", nameof(inputIcons));
            if (i > 0 && inputList[i].Ordinal <= inputList[i - 1].Ordinal)
                throw new ArgumentException("InputIcons 未按 Ordinal ASC。", nameof(inputIcons));
            if (!inputRefs.Add(inputList[i]))
                throw new ArgumentException(
                    $"InputIcons 中图标实例重复（索引 {i}, StableId=\"{inputList[i].StableId}\"）。",
                    nameof(inputIcons));
        }

        var inputOrdinals = new HashSet<int>();
        var inputStableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var icon in inputList)
        {
            if (!inputOrdinals.Add(icon.Ordinal))
                throw new ArgumentException($"InputIcons Ordinal {icon.Ordinal} 重复。", nameof(inputIcons));
            if (!inputStableIds.Add(icon.StableId))
                throw new ArgumentException($"InputIcons StableId \"{icon.StableId}\" 重复。", nameof(inputIcons));
        }

        for (var i = 0; i < targetList.Count; i++)
        {
            if (targetList[i] is null)
                throw new ArgumentException($"TargetDisplayIcons[{i}] 为 null。", nameof(targetDisplayIcons));
            if (i > 0 && targetList[i].Ordinal <= targetList[i - 1].Ordinal)
                throw new ArgumentException("TargetDisplayIcons 未按 Ordinal ASC。", nameof(targetDisplayIcons));
            if (!targetRefs.Add(targetList[i]))
                throw new ArgumentException(
                    $"TargetDisplayIcons 中图标实例重复（索引 {i}）。", nameof(targetDisplayIcons));
            if (!string.Equals(targetList[i].MonitorDeviceName, targetDisplay.DeviceName, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"TargetDisplayIcons[{i}] MonitorDeviceName \"{targetList[i].MonitorDeviceName}\" " +
                    $"不等于 TargetDisplay.DeviceName \"{targetDisplay.DeviceName}\"。",
                    nameof(targetDisplayIcons));
            if (!inputRefs.Contains(targetList[i]))
                throw new ArgumentException(
                    $"TargetDisplayIcons[{i}] (Ordinal={targetList[i].Ordinal}) 不在 InputIcons 引用集中。",
                    nameof(targetDisplayIcons));
        }

        for (var i = 0; i < oosList.Count; i++)
        {
            if (oosList[i] is null)
                throw new ArgumentException($"OutOfScopeIcons[{i}] 为 null。", nameof(outOfScopeIcons));
            if (i > 0 && oosList[i].Ordinal <= oosList[i - 1].Ordinal)
                throw new ArgumentException("OutOfScopeIcons 未按 Ordinal ASC。", nameof(outOfScopeIcons));
            if (!oosRefs.Add(oosList[i]))
                throw new ArgumentException(
                    $"OutOfScopeIcons 中图标实例重复（索引 {i}）。", nameof(outOfScopeIcons));
            // 引用重叠检查优先于 MonitorDeviceName 检查
            if (targetRefs.Contains(oosList[i]))
                throw new ArgumentException(
                    $"OutOfScopeIcons[{i}] (Ordinal={oosList[i].Ordinal}) " +
                    "与 TargetDisplayIcons 存在引用重叠。",
                    nameof(outOfScopeIcons));
            if (string.Equals(oosList[i].MonitorDeviceName ?? "", targetDisplay.DeviceName, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"OutOfScopeIcons[{i}] (Ordinal={oosList[i].Ordinal}) 属于目标显示器。",
                    nameof(outOfScopeIcons));
            if (!inputRefs.Contains(oosList[i]))
                throw new ArgumentException(
                    $"OutOfScopeIcons[{i}] (Ordinal={oosList[i].Ordinal}) 不在 InputIcons 引用集中。",
                    nameof(outOfScopeIcons));
        }

        // Target ∩ OutOfScope 引用不重叠
        foreach (var t in targetRefs)
        {
            if (oosRefs.Contains(t))
                throw new ArgumentException(
                    "TargetDisplayIcons 与 OutOfScopeIcons 存在引用重叠。",
                    nameof(targetDisplayIcons));
        }

        // Input == Target ∪ OutOfScope (引用级别)
        if (inputRefs.Count != targetRefs.Count + oosRefs.Count)
            throw new ArgumentException(
                $"InputIcons 引用数 ({inputRefs.Count}) ≠ " +
                $"TargetDisplayIcons 引用数 ({targetRefs.Count}) + " +
                $"OutOfScopeIcons 引用数 ({oosRefs.Count})。");

        var mergedRefs = new HashSet<DesktopIconSnapshotItem>(targetRefs, ReferenceEqualityComparer.Instance);
        foreach (var o in oosRefs) mergedRefs.Add(o);
        if (!mergedRefs.SetEquals(inputRefs))
            throw new ArgumentException(
                "InputIcons 引用集不等于 TargetDisplayIcons ∪ OutOfScopeIcons 引用集。");

        // ═══ Items 逐项验证 ═══
        if (itemsList.Count != targetList.Count)
            throw new ArgumentException(
                $"Items 数量 ({itemsList.Count}) 不等于 TargetDisplayIconCount ({targetList.Count})。",
                nameof(items));

        // 构建 placement lookup by icon reference + unassigned set
        var placementByIcon = new Dictionary<DesktopIconSnapshotItem, DesktopIconTargetPlacement>(
            ReferenceEqualityComparer.Instance);
        foreach (var p in placementPlan.Placements)
        {
            if (!placementByIcon.TryAdd(p.Icon, p))
                throw new ArgumentException(
                    "PlacementPlan.Placements 中存在重复图标引用。", nameof(placementPlan));
        }

        var unassignedIconSet = new HashSet<DesktopIconSnapshotItem>(
            placementPlan.UnassignedIcons, ReferenceEqualityComparer.Instance);

        var consumedPlacements = new HashSet<DesktopIconTargetPlacement>(ReferenceEqualityComparer.Instance);
        var consumedUnassigned = new HashSet<DesktopIconSnapshotItem>(ReferenceEqualityComparer.Instance);

        for (var i = 0; i < itemsList.Count; i++)
        {
            if (itemsList[i] is null)
                throw new ArgumentException($"Items[{i}] 为 null。", nameof(items));

            var item = itemsList[i];
            var targetIcon = targetList[i];

            if (!ReferenceEquals(item.Icon, targetIcon))
                throw new ArgumentException(
                    $"Items[{i}].Icon 与 TargetDisplayIcons[{i}] 不是同一实例。" +
                    $" Item.StableId=\"{item.Icon.StableId}\", Target.StableId=\"{targetIcon.StableId}\"。",
                    nameof(items));

            // CurrentPosition 必须等于图标当前位置
            if (item.CurrentPosition != targetIcon.PixelPosition)
                throw new ArgumentException(
                    $"Items[{i}] CurrentPosition ({item.CurrentPosition.X},{item.CurrentPosition.Y}) " +
                    $"不等于 Icon.PixelPosition ({targetIcon.PixelPosition.X},{targetIcon.PixelPosition.Y})。",
                    nameof(items));

            if (placementByIcon.TryGetValue(targetIcon, out var expectedPlacement))
            {
                // Assigned
                if (item.Placement is null)
                    throw new ArgumentException(
                        $"Items[{i}] 应为 Assigned 但 Placement 为 null。", nameof(items));

                if (!ReferenceEquals(item.Placement, expectedPlacement))
                    throw new ArgumentException(
                        $"Items[{i}].Placement 不是来自 PlacementPlan 的预期 Placement。",
                        nameof(items));

                if (item.State != DesktopLayoutPreviewItemState.AlreadyAtTarget
                    && item.State != DesktopLayoutPreviewItemState.MoveRequired)
                    throw new ArgumentException(
                        $"Items[{i}] State 应为 AlreadyAtTarget 或 MoveRequired，实际 {item.State}。",
                        nameof(items));

                if (!item.IsAssigned)
                    throw new ArgumentException(
                        $"Items[{i}] IsAssigned 应为 true。", nameof(items));

                if (item.TargetPosition != expectedPlacement.TargetPosition)
                    throw new ArgumentException(
                        $"Items[{i}] TargetPosition 与 Placement.TargetPosition 不一致。",
                        nameof(items));

                var expDX = (long)expectedPlacement.TargetPosition.X - (long)targetIcon.PixelPosition.X;
                var expDY = (long)expectedPlacement.TargetPosition.Y - (long)targetIcon.PixelPosition.Y;

                if (expDX == 0 && expDY == 0)
                {
                    if (item.State != DesktopLayoutPreviewItemState.AlreadyAtTarget)
                        throw new ArgumentException(
                            $"Items[{i}] 当前=目标但 State={item.State}，应为 AlreadyAtTarget。",
                            nameof(items));
                    if (item.RequiresMove)
                        throw new ArgumentException(
                            $"Items[{i}] 当前=目标但 RequiresMove=true。", nameof(items));
                    if (item.DeltaX != 0L || item.DeltaY != 0L)
                        throw new ArgumentException(
                            $"Items[{i}] 当前=目标但 Delta=({item.DeltaX},{item.DeltaY})。",
                            nameof(items));
                }
                else
                {
                    if (item.State != DesktopLayoutPreviewItemState.MoveRequired)
                        throw new ArgumentException(
                            $"Items[{i}] 当前≠目标但 State={item.State}，应为 MoveRequired。",
                            nameof(items));
                    if (!item.RequiresMove)
                        throw new ArgumentException(
                            $"Items[{i}] 当前≠目标但 RequiresMove=false。", nameof(items));
                    if (item.DeltaX != expDX || item.DeltaY != expDY)
                        throw new ArgumentException(
                            $"Items[{i}] Delta=({item.DeltaX},{item.DeltaY}) " +
                            $"≠ 预期 ({expDX},{expDY})。", nameof(items));
                }

                consumedPlacements.Add(expectedPlacement);
            }
            else if (unassignedIconSet.Contains(targetIcon))
            {
                // Unassigned
                if (item.State != DesktopLayoutPreviewItemState.Unassigned)
                    throw new ArgumentException(
                        $"Items[{i}] 在 UnassignedIcons 中但 State={item.State}。",
                        nameof(items));
                if (item.Placement is not null)
                    throw new ArgumentException(
                        $"Items[{i}] 为 Unassigned 但 Placement 非 null。", nameof(items));
                if (item.IsAssigned)
                    throw new ArgumentException(
                        $"Items[{i}] 为 Unassigned 但 IsAssigned=true。", nameof(items));
                if (item.RequiresMove)
                    throw new ArgumentException(
                        $"Items[{i}] 为 Unassigned 但 RequiresMove=true。", nameof(items));
                if (item.TargetPosition is not null)
                    throw new ArgumentException(
                        $"Items[{i}] 为 Unassigned 但 TargetPosition 非 null。", nameof(items));
                if (item.DeltaX is not null || item.DeltaY is not null)
                    throw new ArgumentException(
                        $"Items[{i}] 为 Unassigned 但 Delta 非 null。", nameof(items));

                consumedUnassigned.Add(targetIcon);
            }
            else
            {
                throw new ArgumentException(
                    $"Items[{i}] 图标 (Ordinal={targetIcon.Ordinal}, StableId=\"{targetIcon.StableId}\") " +
                    "既不在 PlacementPlan.Placements 中也不在 UnassignedIcons 中。",
                    nameof(items));
            }
        }

        // 所有 Placement 和 Unassigned 均被消费
        if (consumedPlacements.Count != placementPlan.PlacementCount)
            throw new ArgumentException(
                $"被 Items 消费的 Placement 数 ({consumedPlacements.Count}) " +
                $"≠ PlacementPlan.PlacementCount ({placementPlan.PlacementCount})。",
                nameof(items));

        if (consumedUnassigned.Count != placementPlan.UnassignedCount)
            throw new ArgumentException(
                $"被 Items 消费的 Unassigned 数 ({consumedUnassigned.Count}) " +
                $"≠ PlacementPlan.UnassignedCount ({placementPlan.UnassignedCount})。",
                nameof(items));

        // ═══ 统计 ═══
        int assigned = 0, atTarget = 0, moveRequired = 0, unassigned = 0;
        foreach (var item in itemsList)
        {
            switch (item.State)
            {
                case DesktopLayoutPreviewItemState.AlreadyAtTarget:
                    assigned++; atTarget++; break;
                case DesktopLayoutPreviewItemState.MoveRequired:
                    assigned++; moveRequired++; break;
                case DesktopLayoutPreviewItemState.Unassigned:
                    unassigned++; break;
            }
        }

        Template = template;
        TargetDisplay = targetDisplay;
        ResolvedTemplate = resolvedTemplate;
        AssignmentPlan = assignmentPlan;
        PlacementPlan = placementPlan;
        InputIcons = inputList.AsReadOnly();
        TargetDisplayIcons = targetList.AsReadOnly();
        OutOfScopeIcons = oosList.AsReadOnly();
        Items = itemsList.AsReadOnly();
        InputIconCount = inputList.Count;
        TargetDisplayIconCount = targetList.Count;
        OutOfScopeIconCount = oosList.Count;
        AssignedCount = assigned;
        AlreadyAtTargetCount = atTarget;
        MoveRequiredCount = moveRequired;
        UnassignedCount = unassigned;
        IsComplete = placementPlan.IsComplete;
        HasChanges = moveRequired > 0;
        IsNoOp = IsComplete && moveRequired == 0 && unassigned == 0;
    }
}
