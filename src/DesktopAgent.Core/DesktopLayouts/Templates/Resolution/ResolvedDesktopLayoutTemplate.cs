using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Resolution;

/// <summary>
/// 解析后的布局模板。Template 是原始领域对象，Zones 是解析后的分区列表。
/// </summary>
public sealed record ResolvedDesktopLayoutTemplate
{
    public DesktopLayoutTemplate Template { get; }
    public PixelRect WorkArea { get; }
    public PixelPoint IconSpacing { get; }
    public IReadOnlyList<ResolvedDesktopLayoutZone> Zones { get; }
    public long TotalTheoreticalCapacity { get; }
    public long TotalEffectiveCapacity { get; }

    public ResolvedDesktopLayoutTemplate(
        DesktopLayoutTemplate template,
        PixelRect workArea,
        PixelPoint iconSpacing,
        IReadOnlyList<ResolvedDesktopLayoutZone> zones)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));

        long waWidth = (long)workArea.Right - workArea.Left;
        long waHeight = (long)workArea.Bottom - workArea.Top;
        if (waWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea 宽度必须大于 0。WorkArea=({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom})，宽度={waWidth}。");
        if (waHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea 高度必须大于 0。WorkArea=({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom})，高度={waHeight}。");
        if (waWidth > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea 宽度 ({waWidth}) 超过 int.MaxValue。WorkArea=({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom})。");
        if (waHeight > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea 高度 ({waHeight}) 超过 int.MaxValue。WorkArea=({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom})。");

        if (iconSpacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.X 必须大于 0，实际 {iconSpacing.X}。");
        if (iconSpacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.Y 必须大于 0，实际 {iconSpacing.Y}。");
        if (zones is null) throw new ArgumentNullException(nameof(zones));

        var zoneList = zones.ToList();

        if (zoneList.Count == 0)
            throw new ArgumentException("Zones 不能为空。", nameof(zones));
        if (zoneList.Count != template.Zones.Count)
            throw new ArgumentException(
                $"Zones 数量 ({zoneList.Count}) 与 Template.Zones 数量 ({template.Zones.Count}) 不一致。",
                nameof(zones));

        for (var i = 0; i < zoneList.Count; i++)
        {
            if (zoneList[i] is null)
                throw new ArgumentException($"Zones[{i}] 为 null。", nameof(zones));
            if (!ReferenceEquals(zoneList[i].Zone, template.Zones[i]))
                throw new ArgumentException(
                    $"Zones[{i}] 的 Zone 与 Template.Zones[{i}] 不是同一实例。", nameof(zones));

            var resolvedZone = zoneList[i];
            var pb = resolvedZone.PixelBounds;
            if (!DesktopIconLayoutGeometry.IsFullyContained(pb, workArea))
                throw new ArgumentException(
                    $"Zones[{i}] PixelBounds ({pb.Left},{pb.Top})-({pb.Right},{pb.Bottom}) " +
                    $"不完全位于 WorkArea ({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom}) 内。",
                    nameof(zones));

            // 容量从 PlacementOrigin 而不是可视 PixelBounds 左上角开始计算。
            // 默认两者一致；Explorer 网格相位校准时 PlacementOrigin 会向内偏移。
            long availableWidth = Math.Max(0L, (long)pb.Right - resolvedZone.PlacementOrigin.X);
            long availableHeight = Math.Max(0L, (long)pb.Bottom - resolvedZone.PlacementOrigin.Y);
            int expectedCol = checked((int)(availableWidth / iconSpacing.X));
            int expectedRow = checked((int)(availableHeight / iconSpacing.Y));
            if (resolvedZone.ColumnCount != expectedCol)
                throw new ArgumentException(
                    $"Zones[{i}] ZoneId=\"{resolvedZone.Zone.ZoneId:D}\" " +
                    $"PlacementOrigin=({resolvedZone.PlacementOrigin.X},{resolvedZone.PlacementOrigin.Y}) " +
                    $"PixelBounds=({pb.Left},{pb.Top})-({pb.Right},{pb.Bottom}) " +
                    $"IconSpacing=({iconSpacing.X},{iconSpacing.Y}) " +
                    $"ColumnCount: 期望 {expectedCol}，实际 {resolvedZone.ColumnCount}。",
                    nameof(zones));
            if (resolvedZone.RowCount != expectedRow)
                throw new ArgumentException(
                    $"Zones[{i}] ZoneId=\"{resolvedZone.Zone.ZoneId:D}\" " +
                    $"PlacementOrigin=({resolvedZone.PlacementOrigin.X},{resolvedZone.PlacementOrigin.Y}) " +
                    $"PixelBounds=({pb.Left},{pb.Top})-({pb.Right},{pb.Bottom}) " +
                    $"IconSpacing=({iconSpacing.X},{iconSpacing.Y}) " +
                    $"RowCount: 期望 {expectedRow}，实际 {resolvedZone.RowCount}。",
                    nameof(zones));
        }

        for (var i = 0; i < zoneList.Count; i++)
        {
            for (var j = i + 1; j < zoneList.Count; j++)
            {
                if (DesktopIconLayoutGeometry.Overlaps(zoneList[i].PixelBounds, zoneList[j].PixelBounds))
                    throw new ArgumentException(
                        $"Zones[{i}] 与 Zones[{j}] PixelBounds 重叠。", nameof(zones));
            }
        }

        long totalTheo = 0;
        long totalEff = 0;
        foreach (var z in zoneList)
        {
            totalTheo = checked(totalTheo + z.TheoreticalCapacity);
            totalEff = checked(totalEff + z.EffectiveCapacity);
        }

        Template = template;
        WorkArea = workArea;
        IconSpacing = iconSpacing;
        Zones = zoneList.AsReadOnly();
        TotalTheoreticalCapacity = totalTheo;
        TotalEffectiveCapacity = totalEff;
    }
}
