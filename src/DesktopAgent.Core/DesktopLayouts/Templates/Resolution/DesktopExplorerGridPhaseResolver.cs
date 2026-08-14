using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Resolution;

/// <summary>
/// 从当前目标显示器上的真实桌面图标位置推断 Explorer 全局网格相位。
///
/// GetSpacing 只提供网格步长，不提供网格原点。启用“将图标与网格对齐”时，
/// Explorer 可能在 SelectAndPositionItems 返回后把任意目标吸附到自己的全局网格。
/// 这里仅在当前图标位置对 X/Y 余数形成强多数时采用推断；否则返回 null，
/// 继续沿用旧的 Zone-local 数学网格，避免对自由摆放桌面做猜测式校准。
/// </summary>
public static class DesktopExplorerGridPhaseResolver
{
    public sealed record Inference(
        PixelPoint Phase,
        int SampleCount,
        int XConsensusCount,
        int YConsensusCount)
    {
        public double XConsensusRatio => SampleCount == 0 ? 0d : XConsensusCount / (double)SampleCount;
        public double YConsensusRatio => SampleCount == 0 ? 0d : YConsensusCount / (double)SampleCount;
    }

    public static Inference? TryInfer(
        IReadOnlyList<DesktopIconSnapshotItem> icons,
        PixelPoint spacing,
        double minimumConsensusRatio = 0.60d)
    {
        ArgumentNullException.ThrowIfNull(icons);
        if (spacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing.X 必须大于 0。");
        if (spacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing.Y 必须大于 0。");
        if (minimumConsensusRatio is <= 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(minimumConsensusRatio));

        // 单个图标不足以区分“真实全局网格”与“用户恰好放在这里”。
        if (icons.Count < 2)
            return null;

        var xCounts = new Dictionary<int, int>();
        var yCounts = new Dictionary<int, int>();
        foreach (var icon in icons)
        {
            if (icon is null)
                throw new ArgumentException("Icons 不能包含 null 元素。", nameof(icons));

            Increment(xCounts, PositiveModulo(icon.PixelPosition.X, spacing.X));
            Increment(yCounts, PositiveModulo(icon.PixelPosition.Y, spacing.Y));
        }

        var xMode = SelectMode(xCounts);
        var yMode = SelectMode(yCounts);
        var required = Math.Max(2, (int)Math.Ceiling(icons.Count * minimumConsensusRatio));

        if (xMode.Count < required || yMode.Count < required)
            return null;

        return new Inference(
            new PixelPoint(xMode.Residue, yMode.Residue),
            icons.Count,
            xMode.Count,
            yMode.Count);
    }

    internal static int AlignUpToPhase(int value, int spacing, int phase)
    {
        if (spacing <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing));
        if (phase < 0 || phase >= spacing)
            throw new ArgumentOutOfRangeException(nameof(phase));

        var residue = PositiveModulo(value, spacing);
        var delta = (phase - residue + spacing) % spacing;
        return checked((int)((long)value + delta));
    }

    private static int PositiveModulo(int value, int modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }

    private static void Increment(Dictionary<int, int> counts, int residue)
    {
        counts.TryGetValue(residue, out var count);
        counts[residue] = count + 1;
    }

    private static (int Residue, int Count) SelectMode(IReadOnlyDictionary<int, int> counts)
        => counts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Select(static pair => (pair.Key, pair.Value))
            .First();
}
