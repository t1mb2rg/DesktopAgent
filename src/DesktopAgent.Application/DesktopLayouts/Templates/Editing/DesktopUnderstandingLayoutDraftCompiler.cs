using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// 把 M4 的纯语义 DesktopUnderstandingDraft 编译为产品布局工作台使用的
/// DesktopLayoutEditDraft。编译只发生在内存中，不持久化、不预览，也没有 Writer authority。
/// </summary>
public static class DesktopUnderstandingLayoutDraftCompiler
{
    public sealed record CompilationResult(
        DesktopLayoutEditDraft Draft,
        int SourceAssignmentCount,
        int MappedAssignmentCount,
        int UnmappedAssignmentCount);

    public static CompilationResult Compile(
        DesktopUnderstandingDraft understanding,
        string name = "AI 建议布局",
        TargetDisplayBinding? targetDisplayBinding = null)
    {
        ArgumentNullException.ThrowIfNull(understanding);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));
        if (understanding.Zones.Count == 0)
            throw new InvalidOperationException("AI 没有建议可导入的逻辑 Zone。请先修正 AI 建议后再导入布局工作台。");

        var compiledZones = CompileZones(understanding.Zones);
        var zoneIdsByKey = understanding.Zones
            .Select((zone, index) => (zone.Key, compiledZones[index].ZoneId))
            .ToDictionary(static pair => pair.Key, static pair => pair.ZoneId, StringComparer.Ordinal);

        var templateId = Guid.NewGuid();
        var rules = new List<DesktopLayoutIconRule>(understanding.Assignments.Count);
        var unmapped = 0;

        foreach (var assignment in understanding.Assignments)
        {
            if (assignment.ZoneKey is null ||
                !zoneIdsByKey.TryGetValue(assignment.ZoneKey, out var targetZoneId))
            {
                unmapped++;
                continue;
            }

            rules.Add(DesktopLayoutIconRule.ForZoneOverride(
                assignment.StableId,
                targetZoneId));
        }

        var ruleSet = new DesktopLayoutIconRuleSet(templateId, rules);
        var draft = new DesktopLayoutEditDraft(
            templateId,
            sourceVersion: 0,
            name.Trim(),
            targetDisplayBinding ?? TargetDisplayBinding.ForPrimaryDisplay(),
            compiledZones,
            ruleSet);

        return new CompilationResult(
            draft,
            understanding.Assignments.Count,
            rules.Count,
            unmapped);
    }

    private static IReadOnlyList<DesktopLayoutZone> CompileZones(
        IReadOnlyList<DesktopUnderstandingZoneDraft> semanticZones)
    {
        var count = semanticZones.Count;
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling(count / (double)columns);

        var availableCells = new List<GridCell>(rows * columns);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                availableCells.Add(new GridCell(
                    row,
                    column,
                    (column + 0.5d) / columns,
                    (row + 0.5d) / rows));
            }
        }

        var cellByZoneKey = new Dictionary<string, GridCell>(StringComparer.Ordinal);
        foreach (var candidate in semanticZones
                     .Select((zone, index) => new IndexedZone(zone, index))
                     .OrderBy(static item => item.Zone.PlacementHint == DesktopZonePlacementHint.Unspecified ? 1 : 0)
                     .ThenByDescending(static item => SizeWeight(item.Zone.SizeHint))
                     .ThenBy(static item => item.Index))
        {
            var (targetX, targetY) = PlacementAnchor(candidate.Zone.PlacementHint);
            var selected = availableCells
                .OrderBy(cell => DistanceSquared(cell.CenterX, cell.CenterY, targetX, targetY))
                .ThenBy(static cell => cell.Row)
                .ThenBy(static cell => cell.Column)
                .First();

            availableCells.Remove(selected);
            cellByZoneKey.Add(candidate.Zone.Key, selected);
        }

        // SizeHint 仍由本地确定性编译器解释。它不会让单个 Zone 任意扩张并产生重叠，
        // 而是影响所在行/列在完整 0..10000 画布中的权重。
        var columnWeights = Enumerable.Repeat(1d, columns).ToArray();
        var rowWeights = Enumerable.Repeat(1d, rows).ToArray();
        foreach (var zone in semanticZones)
        {
            var cell = cellByZoneKey[zone.Key];
            var weight = SizeWeight(zone.SizeHint);
            columnWeights[cell.Column] = Math.Max(columnWeights[cell.Column], weight);
            rowWeights[cell.Row] = Math.Max(rowWeights[cell.Row], weight);
        }

        var xBounds = BuildBoundaries(columnWeights);
        var yBounds = BuildBoundaries(rowWeights);
        var result = new DesktopLayoutZone[count];

        for (var i = 0; i < count; i++)
        {
            var semanticZone = semanticZones[i];
            var cell = cellByZoneKey[semanticZone.Key];
            var bounds = new NormalizedRect(
                xBounds[cell.Column],
                yBounds[cell.Row],
                xBounds[cell.Column + 1],
                yBounds[cell.Row + 1]);

            result[i] = new DesktopLayoutZone(
                semanticZone.ExistingZoneId ?? Guid.NewGuid(),
                semanticZone.Name,
                bounds,
                i,
                bounds.Width >= bounds.Height
                    ? ZonePlacementFlow.RowMajor
                    : ZonePlacementFlow.ColumnMajor,
                maxItemCount: null);
        }

        // 这里故意借领域构造函数再做一次“不重叠 / ID / SortOrder”完整验证。
        _ = new DesktopLayoutTemplate(
            Guid.NewGuid(),
            "AI layout validation",
            version: 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            result);

        return Array.AsReadOnly(result);
    }

    private static int[] BuildBoundaries(IReadOnlyList<double> weights)
    {
        var boundaries = new int[weights.Count + 1];
        boundaries[0] = 0;
        boundaries[^1] = NormalizedRect.Scale;

        var total = weights.Sum();
        var cumulative = 0d;
        for (var i = 1; i < weights.Count; i++)
        {
            cumulative += weights[i - 1];
            boundaries[i] = (int)Math.Round(
                NormalizedRect.Scale * cumulative / total,
                MidpointRounding.AwayFromZero);
        }

        return boundaries;
    }

    private static double SizeWeight(DesktopZoneSizeHint hint)
        => hint switch
        {
            DesktopZoneSizeHint.Compact => 1d,
            DesktopZoneSizeHint.Medium => 1.5d,
            DesktopZoneSizeHint.Large => 2d,
            _ => 1.25d,
        };

    private static (double X, double Y) PlacementAnchor(DesktopZonePlacementHint hint)
        => hint switch
        {
            DesktopZonePlacementHint.Left => (0d, 0.5d),
            DesktopZonePlacementHint.Right => (1d, 0.5d),
            DesktopZonePlacementHint.Top => (0.5d, 0d),
            DesktopZonePlacementHint.Bottom => (0.5d, 1d),
            DesktopZonePlacementHint.TopLeft => (0d, 0d),
            DesktopZonePlacementHint.TopRight => (1d, 0d),
            DesktopZonePlacementHint.BottomLeft => (0d, 1d),
            DesktopZonePlacementHint.BottomRight => (1d, 1d),
            _ => (0.5d, 0.5d),
        };

    private static double DistanceSquared(
        double x1,
        double y1,
        double x2,
        double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    private sealed record IndexedZone(DesktopUnderstandingZoneDraft Zone, int Index);
    private sealed record GridCell(int Row, int Column, double CenterX, double CenterY);
}
