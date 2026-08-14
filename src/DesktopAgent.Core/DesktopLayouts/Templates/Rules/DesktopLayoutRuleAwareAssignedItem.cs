using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Rules;

/// <summary>
/// 规则感知的已分配图标项 — 不可变 sealed class。
/// 包含图标、Zone、槽位坐标、分配来源和可选规则。
/// SlotIndex 仅要求 < Zone.TheoreticalCapacity（允许跳过被固定障碍阻塞的槽位）。
/// </summary>
public sealed class DesktopLayoutRuleAwareAssignedItem
{
    public DesktopIconSnapshotItem Icon { get; }
    public ResolvedDesktopLayoutZone Zone { get; }
    public PixelPoint IconSpacing { get; }
    public int SlotIndex { get; }
    public int ColumnIndex { get; }
    public int RowIndex { get; }
    public PixelPoint TargetPosition { get; }
    public PixelRect TargetCellBounds { get; }
    public DesktopLayoutRuleAwareAssignmentSource Source { get; }
    public DesktopLayoutIconRule? Rule { get; }
    public bool IsOverride => Source == DesktopLayoutRuleAwareAssignmentSource.Override;
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

        if (!Enum.IsDefined(source))
            throw new ArgumentException(
                $"未定义的 AssignmentSource 值：{(int)source}。", nameof(source));

        if (source == DesktopLayoutRuleAwareAssignmentSource.Override)
        {
            if (rule is null)
                throw new ArgumentException("Override 分配必须提供 Rule。", nameof(rule));
            if (rule.Kind != DesktopLayoutIconRuleKind.ZoneOverride)
                throw new ArgumentException(
                    $"Override 分配的 Rule Kind 必须为 ZoneOverride，实际 {rule.Kind}。", nameof(rule));
            if (!StringComparer.Ordinal.Equals(rule.StableId, icon.StableId))
                throw new ArgumentException(
                    $"Override Rule.StableId \"{rule.StableId}\" 与 Icon.StableId \"{icon.StableId}\" 不一致。");
            if (!rule.TargetZoneId.HasValue)
                throw new ArgumentException("Override Rule.TargetZoneId 必须存在。", nameof(rule));
            if (rule.TargetZoneId.Value != zone.Zone.ZoneId)
                throw new ArgumentException(
                    $"Override Rule.TargetZoneId ({rule.TargetZoneId}) 与 Zone.ZoneId ({zone.Zone.ZoneId}) 不一致。",
                    nameof(zone));
        }
        else if (source == DesktopLayoutRuleAwareAssignmentSource.Default && rule is not null)
        {
            throw new ArgumentException("Default 分配的 Rule 必须为 null。", nameof(rule));
        }

        if (zone.Zone.PlacementFlow != ZonePlacementFlow.ColumnMajor
            && zone.Zone.PlacementFlow != ZonePlacementFlow.RowMajor)
            throw new ArgumentException(
                $"Zone PlacementFlow 值 ({zone.Zone.PlacementFlow}) 未定义。ZoneId=\"{zone.Zone.ZoneId:D}\"。",
                nameof(zone));

        int columnIndex, rowIndex;
        if (zone.Zone.PlacementFlow == ZonePlacementFlow.ColumnMajor)
        {
            if (zone.RowCount <= 0)
                throw new InvalidOperationException(
                    $"ColumnMajor 要求 RowCount > 0，实际 RowCount={zone.RowCount}。");
            columnIndex = slotIndex / zone.RowCount;
            rowIndex = slotIndex % zone.RowCount;
        }
        else
        {
            if (zone.ColumnCount <= 0)
                throw new InvalidOperationException(
                    $"RowMajor 要求 ColumnCount > 0，实际 ColumnCount={zone.ColumnCount}。");
            rowIndex = slotIndex / zone.ColumnCount;
            columnIndex = slotIndex % zone.ColumnCount;
        }

        if (columnIndex < 0 || columnIndex >= zone.ColumnCount)
            throw new InvalidOperationException(
                $"ColumnIndex ({columnIndex}) 不在 [0, {zone.ColumnCount}) 范围内。ZoneId=\"{zone.Zone.ZoneId:D}\", SlotIndex={slotIndex}。");
        if (rowIndex < 0 || rowIndex >= zone.RowCount)
            throw new InvalidOperationException(
                $"RowIndex ({rowIndex}) 不在 [0, {zone.RowCount}) 范围内。ZoneId=\"{zone.Zone.ZoneId:D}\", SlotIndex={slotIndex}。");

        // 从解析阶段给出的 PlacementOrigin 开始，保持目标点与 Explorer 全局网格相位一致。
        long targetX = (long)zone.PlacementOrigin.X + (long)columnIndex * iconSpacing.X;
        long targetY = (long)zone.PlacementOrigin.Y + (long)rowIndex * iconSpacing.Y;

        PixelPoint targetPosition;
        try
        {
            targetPosition = new PixelPoint(checked((int)targetX), checked((int)targetY));
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"TargetPosition 计算溢出：X={targetX}, Y={targetY}。ZoneId=\"{zone.Zone.ZoneId:D}\", SlotIndex={slotIndex}。",
                ex);
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
                $"IconSpacing=({iconSpacing.X},{iconSpacing.Y})。ZoneId=\"{zone.Zone.ZoneId:D}\", SlotIndex={slotIndex}。",
                ex);
        }

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
