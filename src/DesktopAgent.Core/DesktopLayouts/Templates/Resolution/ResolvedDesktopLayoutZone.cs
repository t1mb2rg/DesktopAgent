using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Resolution;

/// <summary>
/// 解析后的单个分区。Zone 是原始领域对象，PixelBounds 是用户定义分区的像素边界；
/// PlacementOrigin 是分区内部真正用于排列图标的第一个网格槽位原点。
/// 默认二者左上角一致；当 fresh 桌面能可靠推断 Explorer 全局网格相位时，
/// PlacementOrigin 会向分区内部对齐到该相位，而不会改变 PixelBounds 本身。
/// </summary>
public sealed record ResolvedDesktopLayoutZone
{
    public DesktopLayoutZone Zone { get; }
    public PixelRect PixelBounds { get; }
    public PixelPoint PlacementOrigin { get; }
    public int ColumnCount { get; }
    public int RowCount { get; }
    public long TheoreticalCapacity { get; }
    public long EffectiveCapacity { get; }

    public bool HasCapacity => EffectiveCapacity > 0;

    public ResolvedDesktopLayoutZone(
        DesktopLayoutZone zone,
        PixelRect pixelBounds,
        int columnCount,
        int rowCount,
        long theoreticalCapacity,
        long effectiveCapacity,
        PixelPoint? placementOrigin = null)
    {
        if (zone is null)
            throw new ArgumentNullException(nameof(zone));

        // long 安全验证 PixelBounds，避免 int 减法溢出
        long pixelWidth = (long)pixelBounds.Right - pixelBounds.Left;
        long pixelHeight = (long)pixelBounds.Bottom - pixelBounds.Top;

        if (pixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelBounds),
                $"PixelBounds 宽度必须大于 0。PixelBounds=({pixelBounds.Left},{pixelBounds.Top})-({pixelBounds.Right},{pixelBounds.Bottom})，计算宽度={pixelWidth}。");
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelBounds),
                $"PixelBounds 高度必须大于 0。PixelBounds=({pixelBounds.Left},{pixelBounds.Top})-({pixelBounds.Right},{pixelBounds.Bottom})，计算高度={pixelHeight}。");
        if (pixelWidth > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pixelBounds),
                $"PixelBounds 宽度 ({pixelWidth}) 超过 int.MaxValue。PixelBounds=({pixelBounds.Left},{pixelBounds.Top})-({pixelBounds.Right},{pixelBounds.Bottom})。");
        if (pixelHeight > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pixelBounds),
                $"PixelBounds 高度 ({pixelHeight}) 超过 int.MaxValue。PixelBounds=({pixelBounds.Left},{pixelBounds.Top})-({pixelBounds.Right},{pixelBounds.Bottom})。");

        var effectiveOrigin = placementOrigin ?? new PixelPoint(pixelBounds.Left, pixelBounds.Top);
        if (effectiveOrigin.X < pixelBounds.Left)
            throw new ArgumentOutOfRangeException(nameof(placementOrigin),
                $"PlacementOrigin.X ({effectiveOrigin.X}) 不能位于 PixelBounds.Left ({pixelBounds.Left}) 左侧。");
        if (effectiveOrigin.Y < pixelBounds.Top)
            throw new ArgumentOutOfRangeException(nameof(placementOrigin),
                $"PlacementOrigin.Y ({effectiveOrigin.Y}) 不能位于 PixelBounds.Top ({pixelBounds.Top}) 上方。");

        if (columnCount < 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        if (rowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount));

        var expectedTheoretical = checked((long)columnCount * rowCount);
        if (theoreticalCapacity != expectedTheoretical)
            throw new ArgumentOutOfRangeException(nameof(theoreticalCapacity),
                $"TheoreticalCapacity ({theoreticalCapacity}) 必须等于 ColumnCount×RowCount ({expectedTheoretical})。");

        if (effectiveCapacity < 0 || effectiveCapacity > theoreticalCapacity)
            throw new ArgumentOutOfRangeException(nameof(effectiveCapacity),
                $"EffectiveCapacity ({effectiveCapacity}) 不在 [0, {theoreticalCapacity}] 范围内。");

        if (zone.MaxItemCount.HasValue)
        {
            var expectedEffective = Math.Min(theoreticalCapacity, (long)zone.MaxItemCount.Value);
            if (effectiveCapacity != expectedEffective)
                throw new ArgumentOutOfRangeException(nameof(effectiveCapacity),
                    $"Zone.MaxItemCount={zone.MaxItemCount} 时 EffectiveCapacity 应为 {expectedEffective}，实际 {effectiveCapacity}。");
        }
        else
        {
            if (effectiveCapacity != theoreticalCapacity)
                throw new ArgumentOutOfRangeException(nameof(effectiveCapacity),
                    $"Zone.MaxItemCount=null 时 EffectiveCapacity 应等于 TheoreticalCapacity ({theoreticalCapacity})，实际 {effectiveCapacity}。");
        }

        Zone = zone;
        PixelBounds = pixelBounds;
        PlacementOrigin = effectiveOrigin;
        ColumnCount = columnCount;
        RowCount = rowCount;
        TheoreticalCapacity = theoreticalCapacity;
        EffectiveCapacity = effectiveCapacity;
    }
}
