using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 规则感知的已分配图标项 — 不可变 sealed class。
/// 包含图标、Zone、槽位坐标、分配来源和可选规则。
/// SlotIndex 仅要求 &lt; Zone.TheoreticalCapacity（允许跳过被固定障碍阻塞的槽位）。
/// </summary>
public sealed class DesktopLayoutRuleAwareAssignedItem
{
    /// <summary>原始图标（InputIcons 中的原对象引用）。</summary>
    public DesktopIconSnapshotItem Icon { get; }

    /// <summary>分配到的分区（ResolvedTemplate 中的原对象引用）。</summary>
    public ResolvedDesktopLayoutZone Zone { get; }

    /// <summary>图标间距。</summary>
    public PixelPoint IconSpacing { get; }

    /// <summary>槽位索引（按 PlacementFlow 确定性计算）。</summary>
    public int SlotIndex { get; }

    /// <summary>网格列索引。</summary>
    public int ColumnIndex { get; }

    /// <summary>网格行索引。</summary>
    public int RowIndex { get; }

    /// <summary>目标像素位置（网格单元左上角）。</summary>
    public PixelPoint TargetPosition { get; }

    /// <summary>目标单元格边界 [pos, pos+spacing)。</summary>
    public PixelRect TargetCellBounds { get; }

    /// <summary>分配来源：Override 或 Default。</summary>
    public DesktopLayoutRuleAwareAssignmentSource Source { get; }

    /// <summary>
    /// ZoneOverride 匹配的规则；Default 分配时为 null。
    /// 必须是 RuleSet 中的原对象引用。
    /// </summary>
    public DesktopLayoutIconRule? Rule { get; }

    /// <summary>是否为 Override 分配。</summary>
    public bool IsOverride => Source == DesktopLayoutRuleAwareAssignmentSource.Override;

    /// <summary>是否为 Default 分配。</summary>
    public bool IsDefault => Source == DesktopLayoutRuleAwareAssignmentSource.Default;

    public DesktopLayoutRuleAwareAssignedItem(
        DesktopIconSnapshotItem icon,
        ResolvedDesktopLayoutZone zone,
        PixelPoint iconSpacing,
        int slotIndex,
        DesktopLayoutRuleAwareAssignmentSource source,
        DesktopLayoutIconRule? rule)
    {
        if (icon is null) throw new ArgumentNullException(nameof(icon));
        if (zone is null) throw new ArgumentNullException(nameof(zone));
        if (iconSpacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.X 必须大于 0，实际 {iconSpacing.X}。");
        if (iconSpacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.Y 必须大于 0，实际 {iconSpacing.Y}。");
        if (slotIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(slotIndex),
                $"SlotIndex 不能为负数，实际 {slotIndex}。");
        if ((long)slotIndex >= zone.TheoreticalCapacity)
            throw new ArgumentOutOfRangeException(nameof(slotIndex),
                $"SlotIndex ({slotIndex}) 超过 Zone TheoreticalCapacity ({zone.TheoreticalCapacity})。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\"。");

        if (source == DesktopLayoutRuleAwareAssignmentSource.Override)
        {
            if (rule is null)
                throw new ArgumentException(
                    "Override 分配必须提供 Rule。", nameof(rule));
            if (rule.Kind != DesktopLayoutIconRuleKind.ZoneOverride)
                throw new ArgumentException(
                    $"Override 分配的 Rule Kind 必须为 ZoneOverride，实际 {rule.Kind}。", nameof(rule));
        }
        else // Default
        {
            if (rule is not null)
                throw new ArgumentException(
                    "Default 分配的 Rule 必须为 null。", nameof(rule));
        }

        if (zone.Zone.PlacementFlow != ZonePlacementFlow.ColumnMajor
            && zone.Zone.PlacementFlow != ZonePlacementFlow.RowMajor)
            throw new ArgumentException(
                $"Zone PlacementFlow 值 ({zone.Zone.PlacementFlow}) 未定义。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\"。", nameof(zone));

        // 从 SlotIndex 推导 ColumnIndex / RowIndex
        int columnIndex, rowIndex;
        if (zone.Zone.PlacementFlow == ZonePlacementFlow.ColumnMajor)
        {
            if (zone.RowCount <= 0)
                throw new InvalidOperationException(
                    $"ColumnMajor 要求 RowCount > 0，实际 RowCount={zone.RowCount}。");

            columnIndex = slotIndex / zone.RowCount;
            rowIndex = slotIndex % zone.RowCount;
        }
        else // RowMajor
        {
            if (zone.ColumnCount <= 0)
                throw new InvalidOperationException(
                    $"RowMajor 要求 ColumnCount > 0，实际 ColumnCount={zone.ColumnCount}。");

            rowIndex = slotIndex / zone.ColumnCount;
            columnIndex = slotIndex % zone.ColumnCount;
        }

        if (columnIndex < 0 || columnIndex >= zone.ColumnCount)
            throw new InvalidOperationException(
                $"ColumnIndex ({columnIndex}) 不在 [0, {zone.ColumnCount}) 范围内。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\", SlotIndex={slotIndex}。");
        if (rowIndex < 0 || rowIndex >= zone.RowCount)
            throw new InvalidOperationException(
                $"RowIndex ({rowIndex}) 不在 [0, {zone.RowCount}) 范围内。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\", SlotIndex={slotIndex}。");

        // 坐标计算：Zone 左上角为锚点，long 中间运算
        long targetX = (long)zone.PixelBounds.Left + (long)columnIndex * iconSpacing.X;
        long targetY = (long)zone.PixelBounds.Top + (long)rowIndex * iconSpacing.Y;

        PixelPoint targetPosition;
        try
        {
            targetPosition = new PixelPoint(checked((int)targetX), checked((int)targetY));
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"TargetPosition 计算溢出：X={targetX}, Y={targetY}。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\", SlotIndex={slotIndex}。", ex);
        }

        PixelRect targetCellBounds;
        try
        {
            targetCellBounds = DesktopIconLayoutGeometry.CreateRectChecked(targetPosition, iconSpacing);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"TargetCellBounds 计算溢出：TargetPosition=({targetPosition.X},{targetPosition.Y}), " +
                $"IconSpacing=({iconSpacing.X},{iconSpacing.Y})。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\", SlotIndex={slotIndex}。", ex);
        }

        // TargetCellBounds 必须完整位于 Zone.PixelBounds 内
        if (!DesktopIconLayoutGeometry.IsFullyContained(targetCellBounds, zone.PixelBounds))
            throw new InvalidOperationException(
                $"TargetCellBounds ({targetCellBounds.Left},{targetCellBounds.Top})-({targetCellBounds.Right},{targetCellBounds.Bottom}) " +
                $"不完全位于 Zone.PixelBounds ({zone.PixelBounds.Left},{zone.PixelBounds.Top})-({zone.PixelBounds.Right},{zone.PixelBounds.Bottom}) 内。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\", SlotIndex={slotIndex}。");

        Icon = icon;
        Zone = zone;
        IconSpacing = iconSpacing;
        SlotIndex = slotIndex;
        ColumnIndex = columnIndex;
        RowIndex = rowIndex;
        TargetPosition = targetPosition;
        TargetCellBounds = targetCellBounds;
        Source = source;
        Rule = rule;
    }
}
