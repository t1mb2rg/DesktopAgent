using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Placement;

/// <summary>
/// 单个图标在分区内的目标放置位置。
/// 由 SlotIndex 推导 ColumnIndex、RowIndex、TargetPosition 和 TargetCellBounds。
/// TargetPosition 为网格单元左上角，不是中心点。
/// </summary>
public sealed record DesktopIconTargetPlacement
{
    public DesktopIconSnapshotItem Icon { get; }
    public ResolvedDesktopLayoutZone Zone { get; }
    public PixelPoint IconSpacing { get; }

    public int SlotIndex { get; }
    public int ColumnIndex { get; }
    public int RowIndex { get; }

    public PixelPoint TargetPosition { get; }
    public PixelRect TargetCellBounds { get; }

    public DesktopIconTargetPlacement(
        DesktopIconSnapshotItem icon,
        ResolvedDesktopLayoutZone zone,
        PixelPoint iconSpacing,
        int slotIndex)
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

        // EffectiveCapacity 为 0 时拒绝任何 Placement
        if (zone.EffectiveCapacity == 0)
            throw new ArgumentException(
                $"Zone EffectiveCapacity 为 0，不允许创建 Placement。ZoneId=\"{zone.Zone.ZoneId:D}\"。",
                nameof(zone));

        if ((long)slotIndex >= zone.EffectiveCapacity)
            throw new ArgumentOutOfRangeException(nameof(slotIndex),
                $"SlotIndex ({slotIndex}) 超过 Zone EffectiveCapacity ({zone.EffectiveCapacity})。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\"。");

        if ((long)slotIndex >= zone.TheoreticalCapacity)
            throw new ArgumentOutOfRangeException(nameof(slotIndex),
                $"SlotIndex ({slotIndex}) 超过 Zone TheoreticalCapacity ({zone.TheoreticalCapacity})。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\"。");

        // PlacementFlow 必须是已定义值
        if (zone.Zone.PlacementFlow != ZonePlacementFlow.ColumnMajor
            && zone.Zone.PlacementFlow != ZonePlacementFlow.RowMajor)
            throw new ArgumentException(
                $"Zone PlacementFlow 值 ({zone.Zone.PlacementFlow}) 未定义。" +
                $"ZoneId=\"{zone.Zone.ZoneId:D}\"。", nameof(zone));

        int columnIndex, rowIndex;

        if (zone.Zone.PlacementFlow == ZonePlacementFlow.ColumnMajor)
        {
            // ColumnMajor：纵向优先
            // slotIndex / RowCount = 列索引，slotIndex % RowCount = 行索引
            if (zone.RowCount <= 0)
                throw new InvalidOperationException(
                    $"ColumnMajor 要求 RowCount > 0，实际 RowCount={zone.RowCount}。" +
                    $"ZoneId=\"{zone.Zone.ZoneId:D}\"。");

            columnIndex = slotIndex / zone.RowCount;
            rowIndex = slotIndex % zone.RowCount;
        }
        else // RowMajor
        {
            // RowMajor：横向优先
            // slotIndex / ColumnCount = 行索引，slotIndex % ColumnCount = 列索引
            if (zone.ColumnCount <= 0)
                throw new InvalidOperationException(
                    $"RowMajor 要求 ColumnCount > 0，实际 ColumnCount={zone.ColumnCount}。" +
                    $"ZoneId=\"{zone.Zone.ZoneId:D}\"。");

            rowIndex = slotIndex / zone.ColumnCount;
            columnIndex = slotIndex % zone.ColumnCount;
        }

        // ColumnIndex / RowIndex 范围验证
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
    }
}
