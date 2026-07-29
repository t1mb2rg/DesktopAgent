using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Resolution;

/// <summary>
/// 解析后的单个分区。Zone 是原始领域对象，PixelBounds 是解析后的像素坐标。
/// </summary>
public sealed record ResolvedDesktopLayoutZone
{
    public DesktopLayoutZone Zone { get; }
    public PixelRect PixelBounds { get; }
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
        long effectiveCapacity)
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
        ColumnCount = columnCount;
        RowCount = rowCount;
        TheoreticalCapacity = theoreticalCapacity;
        EffectiveCapacity = effectiveCapacity;
    }
}
