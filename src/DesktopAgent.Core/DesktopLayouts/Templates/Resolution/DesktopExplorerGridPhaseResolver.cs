using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Resolution;

/// <summary>
/// 从当前目标显示器上的真实桌面图标位置推断 Explorer 全局网格相位。
///
/// GetSpacing 只提供网格步长，不提供网格原点。启用“将图标与网格对齐”时，
/// Explorer 可能在 SelectAndPositionItems 返回后把任意目标吸附到自己的全局网格。
/// 只有真实图标的二维余数对 (X mod spacingX, Y mod spacingY) 形成强多数时才采用；
/// 否则返回 null，继续沿用旧的 Zone-local 数学网格，避免对自由摆放桌面做猜测式校准。
/// </summary>
public static class DesktopExplorerGridPhaseResolver
{
    public sealed record Inference(
        PixelPoint Phase,
        int SampleCount,
        int ConsensusCount)
    {
        public double ConsensusRatio =>
            SampleCount == 0 ? 0d : ConsensusCount / (double)SampleCount;
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

        var phaseCounts = new Dictionary<(int X, int Y), int>();
        foreach (var icon in icons)
        {
            if (icon is null)
                throw new ArgumentException("Icons 不能包含 null 元素。", nameof(icons));

            var phase = (
                PositiveModulo(icon.PixelPosition.X, spacing.X),
                PositiveModulo(icon.PixelPosition.Y, spacing.Y));
            phaseCounts.TryGetValue(phase, out var count);
            phaseCounts[phase] = count + 1;
        }

        var mode = phaseCounts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key.X)
            .ThenBy(static pair => pair.Key.Y)
            .First();
        var required = Math.Max(2, (int)Math.Ceiling(icons.Count * minimumConsensusRatio));

        if (mode.Value < required)
            return null;

        return new Inference(
            new PixelPoint(mode.Key.X, mode.Key.Y),
            icons.Count,
            mode.Value);
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
}
