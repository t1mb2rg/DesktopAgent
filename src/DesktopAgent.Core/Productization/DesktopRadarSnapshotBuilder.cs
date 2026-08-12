using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.Productization;

/// <summary>
/// 将 Fresh DesktopObservation 压缩为 presentation-only DesktopRadarSnapshot。
///
/// Builder 可以在计算过程中读取真实桌面项目坐标，但这些坐标与 item identity
/// 不会进入输出 Snapshot。Snapshot 不得作为 Planner / Writer / Recovery 的执行依据。
/// </summary>
public sealed class DesktopRadarSnapshotBuilder
{
    private static readonly int[] LongAxisGridTiers = [6, 8, 10, 12, 14];
    private const double GridHysteresisScoreMargin = 0.65d;

    public DesktopRadarSnapshot Build(
        DesktopObservation observation,
        DesktopRadarSnapshot? previousSnapshot = null,
        Guid? layoutTemplateId = null,
        int? layoutVersion = null)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var displays = observation.Displays
            .Select(display => BuildDisplaySnapshot(observation, display, previousSnapshot))
            .ToArray();

        return new DesktopRadarSnapshot(
            observation.ObservationId,
            observation.ObservedAtUtc,
            ComputeTopologyFingerprint(observation.Displays),
            layoutTemplateId,
            layoutVersion,
            displays);
    }

    private static DesktopRadarDisplaySnapshot BuildDisplaySnapshot(
        DesktopObservation observation,
        DisplayDescriptor display,
        DesktopRadarSnapshot? previousSnapshot)
    {
        var icons = observation.Icons
            .Where(icon => BelongsToDisplay(icon, display))
            .ToArray();
        var candidates = CreateGridCandidates(display.Bounds);
        var evaluations = candidates
            .Select(candidate => EvaluateGrid(display.Bounds, icons, candidate))
            .ToArray();

        var bestIndex = IndexOfLowestScore(evaluations);
        var selected = ApplyGridHysteresis(
            display.DeviceName,
            evaluations,
            bestIndex,
            previousSnapshot);
        var occupancies = BinIcons(display.Bounds, icons, selected.Columns, selected.Rows);
        var densityCells = BuildDensityCells(occupancies, selected.Columns, selected.Rows);

        return new DesktopRadarDisplaySnapshot(
            display.DeviceName,
            display.Bounds,
            display.IsPrimary,
            icons.Length,
            selected.Columns,
            selected.Rows,
            densityCells);
    }

    private static bool BelongsToDisplay(
        DesktopIconDescriptor icon,
        DisplayDescriptor display)
    {
        if (icon.MonitorDeviceName is not null)
        {
            return string.Equals(
                icon.MonitorDeviceName,
                display.DeviceName,
                StringComparison.Ordinal);
        }

        return display.Bounds.Contains(icon.PixelPosition);
    }

    private static GridCandidate[] CreateGridCandidates(PixelRect bounds)
    {
        var aspect = bounds.Width / (double)bounds.Height;
        return LongAxisGridTiers
            .Select(longAxis => aspect >= 1d
                ? new GridCandidate(
                    longAxis,
                    Math.Max(3, (int)Math.Round(longAxis / aspect, MidpointRounding.AwayFromZero)))
                : new GridCandidate(
                    Math.Max(3, (int)Math.Round(longAxis * aspect, MidpointRounding.AwayFromZero)),
                    longAxis))
            .Distinct()
            .ToArray();
    }

    private static GridEvaluation EvaluateGrid(
        PixelRect bounds,
        IReadOnlyList<DesktopIconDescriptor> icons,
        GridCandidate candidate)
    {
        var occupancy = BinIcons(bounds, icons, candidate.Columns, candidate.Rows);
        if (icons.Count == 0)
        {
            // 空桌面优先粗网格，避免生成没有信息价值的高分辨率 presentation grid。
            return new GridEvaluation(candidate, candidate.Columns * candidate.Rows * 0.002d);
        }

        var nonEmpty = occupancy.Where(value => value > 0).ToArray();
        var totalCells = occupancy.Length;
        var emptyRatio = 1d - (nonEmpty.Length / (double)totalCells);
        var meanNonEmpty = icons.Count / (double)Math.Max(1, nonEmpty.Length);
        var maxOccupancy = nonEmpty.Length == 0 ? 0 : nonEmpty.Max();

        // 目标是形成可读的空间纹理：典型非空 cell 约落在少量项目范围内，
        // 同时惩罚极端稀疏与大堆叠。数值属于实现期 tuning，不是产品语义阈值。
        var score = Math.Abs(meanNonEmpty - 2.4d);
        if (emptyRatio > 0.90d)
            score += (emptyRatio - 0.90d) * 9d;
        if (emptyRatio < 0.30d)
            score += (0.30d - emptyRatio) * 3d;
        if (maxOccupancy > 7)
            score += (maxOccupancy - 7) * 0.22d;

        // 相同表达质量下略微偏向较粗网格，减少视觉噪声。
        score += totalCells * 0.0015d;
        return new GridEvaluation(candidate, score);
    }

    private static GridCandidate ApplyGridHysteresis(
        string displayKey,
        IReadOnlyList<GridEvaluation> evaluations,
        int bestIndex,
        DesktopRadarSnapshot? previousSnapshot)
    {
        var previousDisplay = previousSnapshot?.Displays.FirstOrDefault(display =>
            string.Equals(display.DisplayKey, displayKey, StringComparison.Ordinal));
        if (previousDisplay is null)
            return evaluations[bestIndex].Grid;

        var previousIndex = -1;
        for (var i = 0; i < evaluations.Count; i++)
        {
            var grid = evaluations[i].Grid;
            if (grid.Columns == previousDisplay.GridColumns &&
                grid.Rows == previousDisplay.GridRows)
            {
                previousIndex = i;
                break;
            }
        }

        if (previousIndex < 0)
            return evaluations[bestIndex].Grid;

        var previousEvaluation = evaluations[previousIndex];
        var bestEvaluation = evaluations[bestIndex];
        if (previousEvaluation.Score <= bestEvaluation.Score + GridHysteresisScoreMargin)
            return previousEvaluation.Grid;

        // 即使结构变化明显，也一次只移动一个 tier，避免 Radar 大幅重排。
        var direction = Math.Sign(bestIndex - previousIndex);
        return evaluations[previousIndex + direction].Grid;
    }

    private static int[] BinIcons(
        PixelRect bounds,
        IReadOnlyList<DesktopIconDescriptor> icons,
        int columns,
        int rows)
    {
        var occupancy = new int[columns * rows];
        foreach (var icon in icons)
        {
            var relativeX = Math.Clamp(
                (icon.PixelPosition.X - bounds.Left) / (double)bounds.Width,
                0d,
                Math.BitDecrement(1d));
            var relativeY = Math.Clamp(
                (icon.PixelPosition.Y - bounds.Top) / (double)bounds.Height,
                0d,
                Math.BitDecrement(1d));
            var column = Math.Min(columns - 1, (int)(relativeX * columns));
            var row = Math.Min(rows - 1, (int)(relativeY * rows));
            occupancy[(row * columns) + column]++;
        }

        return occupancy;
    }

    private static IReadOnlyList<DesktopRadarDensityCell> BuildDensityCells(
        IReadOnlyList<int> occupancy,
        int columns,
        int rows)
    {
        var nonEmpty = occupancy.Where(value => value > 0).OrderBy(value => value).ToArray();
        if (nonEmpty.Length == 0)
            return Array.Empty<DesktopRadarDensityCell>();

        var mean = nonEmpty.Average();
        var median = Percentile(nonEmpty, 0.50d);
        var upperQuartile = Percentile(nonEmpty, 0.75d);

        // 与当前 display 的 occupancy distribution 联合计算，同时保留绝对拥挤压力。
        // Grid 已先尝试把典型非空 cell 压到少量项目范围，因此这里无需固定全局分类表。
        var mediumThreshold = Math.Max(
            2,
            (int)Math.Ceiling(Math.Min(3.5d, Math.Max(1.5d, (median + mean) / 2d))));
        var highThreshold = Math.Max(
            mediumThreshold + 1,
            (int)Math.Ceiling(Math.Min(6d, Math.Max(upperQuartile, mean * 1.45d))));

        var cells = new List<DesktopRadarDensityCell>(nonEmpty.Length);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var count = occupancy[(row * columns) + column];
                if (count == 0)
                    continue;

                var density = count >= highThreshold
                    ? DesktopRadarDensityLevel.High
                    : count >= mediumThreshold
                        ? DesktopRadarDensityLevel.Medium
                        : DesktopRadarDensityLevel.Low;
                cells.Add(new DesktopRadarDensityCell(column, row, density));
            }
        }

        return cells;
    }

    private static double Percentile(IReadOnlyList<int> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            throw new ArgumentException("Percentile 输入不能为空。", nameof(sortedValues));

        var index = (sortedValues.Count - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
            return sortedValues[lower];

        var fraction = index - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }

    private static int IndexOfLowestScore(IReadOnlyList<GridEvaluation> evaluations)
    {
        var bestIndex = 0;
        for (var i = 1; i < evaluations.Count; i++)
        {
            if (evaluations[i].Score < evaluations[bestIndex].Score)
                bestIndex = i;
        }

        return bestIndex;
    }

    private static string ComputeTopologyFingerprint(IReadOnlyList<DisplayDescriptor> displays)
    {
        var canonical = string.Join(
            "\n",
            displays
                .OrderBy(display => display.DeviceName, StringComparer.Ordinal)
                .Select(display =>
                    $"{display.DeviceName}|{display.Bounds.Left}|{display.Bounds.Top}|" +
                    $"{display.Bounds.Right}|{display.Bounds.Bottom}|{(display.IsPrimary ? 1 : 0)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private readonly record struct GridCandidate(int Columns, int Rows);

    private readonly record struct GridEvaluation(GridCandidate Grid, double Score);
}
