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
        => Compile(
            understanding,
            name,
            targetDisplayBinding,
            Array.Empty<DesktopReservedRegionConstraint>());

    /// <summary>
    /// 带负空间约束的确定性本地编译。ReservedRegion 只提供语义位置/大小，
    /// 这里才把它变成 NormalizedRect，并从可分配空间中扣除。
    /// </summary>
    public static CompilationResult Compile(
        DesktopUnderstandingDraft understanding,
        string name,
        TargetDisplayBinding? targetDisplayBinding,
        IReadOnlyList<DesktopReservedRegionConstraint> reservedRegions)
    {
        ArgumentNullException.ThrowIfNull(understanding);
        ArgumentNullException.ThrowIfNull(reservedRegions);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));
        if (understanding.Zones.Count == 0)
            throw new InvalidOperationException("AI 没有建议可导入的逻辑 Zone。请先修正 AI 建议后再导入布局工作台。");

        var compiledZones = reservedRegions.Count == 0
            ? CompileZonesLegacy(understanding.Zones)
            : CompileZonesWithReservedSpace(understanding.Zones, reservedRegions);
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

    private static IReadOnlyList<DesktopLayoutZone> CompileZonesLegacy(
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
            result[i] = CreateZone(semanticZone, bounds, i);
        }

        ValidateZones(result);
        return Array.AsReadOnly(result);
    }

    private static IReadOnlyList<DesktopLayoutZone> CompileZonesWithReservedSpace(
        IReadOnlyList<DesktopUnderstandingZoneDraft> semanticZones,
        IReadOnlyList<DesktopReservedRegionConstraint> reservedRegions)
    {
        var freeRects = new List<FreeRect>
        {
            new(0, 0, NormalizedRect.Scale, NormalizedRect.Scale),
        };

        foreach (var reserved in reservedRegions.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var cut = ResolveReservedRect(reserved);
            var next = new List<FreeRect>();
            foreach (var free in freeRects)
                next.AddRange(Subtract(free, cut));
            freeRects = next
                .Where(static rect => rect.Width > 0 && rect.Height > 0)
                .ToList();
        }

        while (freeRects.Count < semanticZones.Count)
        {
            var splitIndex = freeRects
                .Select((rect, index) => (rect, index))
                .Where(static item => item.rect.Width >= 2 || item.rect.Height >= 2)
                .OrderByDescending(static item => item.rect.Area)
                .ThenBy(static item => item.index)
                .Select(static item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (splitIndex < 0)
                throw new InvalidOperationException("保留区域占用过多空间，剩余画布无法容纳全部 Zone。");

            var source = freeRects[splitIndex];
            var (first, second) = Split(source);
            freeRects.RemoveAt(splitIndex);
            freeRects.Add(first);
            freeRects.Add(second);
        }

        if (freeRects.Count == 0)
            throw new InvalidOperationException("保留区域已经占满整个桌面，无法生成布局 Zone。");

        var selectedByKey = new Dictionary<string, FreeRect>(StringComparer.Ordinal);
        var available = freeRects.ToList();
        foreach (var candidate in semanticZones
                     .Select((zone, index) => new IndexedZone(zone, index))
                     .OrderBy(static item => item.Zone.PlacementHint == DesktopZonePlacementHint.Unspecified ? 1 : 0)
                     .ThenByDescending(static item => SizeWeight(item.Zone.SizeHint))
                     .ThenBy(static item => item.Index))
        {
            var (targetX, targetY) = PlacementAnchor(candidate.Zone.PlacementHint);
            var sizeWeight = SizeWeight(candidate.Zone.SizeHint);
            var selected = available
                .OrderBy(rect => SlotScore(rect, targetX, targetY, sizeWeight))
                .ThenByDescending(static rect => rect.Area)
                .ThenBy(static rect => rect.Top)
                .ThenBy(static rect => rect.Left)
                .First();
            available.Remove(selected);
            selectedByKey.Add(candidate.Zone.Key, selected);
        }

        var result = new DesktopLayoutZone[semanticZones.Count];
        for (var i = 0; i < semanticZones.Count; i++)
        {
            var semanticZone = semanticZones[i];
            var selected = selectedByKey[semanticZone.Key];
            result[i] = CreateZone(
                semanticZone,
                new NormalizedRect(
                    selected.Left,
                    selected.Top,
                    selected.Right,
                    selected.Bottom),
                i);
        }

        ValidateZones(result);
        return Array.AsReadOnly(result);
    }

    private static FreeRect ResolveReservedRect(DesktopReservedRegionConstraint reserved)
    {
        var fraction = reserved.SizeHint switch
        {
            DesktopZoneSizeHint.Compact => 0.18d,
            DesktopZoneSizeHint.Medium => 0.28d,
            DesktopZoneSizeHint.Large => 0.40d,
            _ => 0.24d,
        };
        var width = Math.Max(1, (int)Math.Round(NormalizedRect.Scale * fraction));
        var height = width;
        var (anchorX, anchorY) = PlacementAnchor(reserved.PlacementHint);

        var left = anchorX switch
        {
            <= 0.01d => 0,
            >= 0.99d => NormalizedRect.Scale - width,
            _ => (NormalizedRect.Scale - width) / 2,
        };
        var top = anchorY switch
        {
            <= 0.01d => 0,
            >= 0.99d => NormalizedRect.Scale - height,
            _ => (NormalizedRect.Scale - height) / 2,
        };

        return new FreeRect(left, top, left + width, top + height);
    }

    private static IReadOnlyList<FreeRect> Subtract(FreeRect source, FreeRect cut)
    {
        var left = Math.Max(source.Left, cut.Left);
        var top = Math.Max(source.Top, cut.Top);
        var right = Math.Min(source.Right, cut.Right);
        var bottom = Math.Min(source.Bottom, cut.Bottom);
        if (left >= right || top >= bottom)
            return new[] { source };

        var result = new List<FreeRect>(4);
        if (source.Top < top)
            result.Add(new FreeRect(source.Left, source.Top, source.Right, top));
        if (bottom < source.Bottom)
            result.Add(new FreeRect(source.Left, bottom, source.Right, source.Bottom));
        if (source.Left < left)
            result.Add(new FreeRect(source.Left, top, left, bottom));
        if (right < source.Right)
            result.Add(new FreeRect(right, top, source.Right, bottom));
        return result;
    }

    private static (FreeRect First, FreeRect Second) Split(FreeRect source)
    {
        if (source.Width >= source.Height && source.Width >= 2)
        {
            var split = source.Left + (source.Width / 2);
            return (
                new FreeRect(source.Left, source.Top, split, source.Bottom),
                new FreeRect(split, source.Top, source.Right, source.Bottom));
        }

        if (source.Height >= 2)
        {
            var split = source.Top + (source.Height / 2);
            return (
                new FreeRect(source.Left, source.Top, source.Right, split),
                new FreeRect(source.Left, split, source.Right, source.Bottom));
        }

        throw new InvalidOperationException("FreeRect 无法继续拆分。");
    }

    private static double SlotScore(FreeRect rect, double targetX, double targetY, double sizeWeight)
    {
        var centerX = ((rect.Left + rect.Right) / 2d) / NormalizedRect.Scale;
        var centerY = ((rect.Top + rect.Bottom) / 2d) / NormalizedRect.Scale;
        var distance = DistanceSquared(centerX, centerY, targetX, targetY);
        var areaRatio = rect.Area / (double)(NormalizedRect.Scale * NormalizedRect.Scale);
        return distance - (areaRatio * sizeWeight * 0.35d);
    }

    private static DesktopLayoutZone CreateZone(
        DesktopUnderstandingZoneDraft semanticZone,
        NormalizedRect bounds,
        int sortOrder)
        => new(
            semanticZone.ExistingZoneId ?? Guid.NewGuid(),
            semanticZone.Name,
            bounds,
            sortOrder,
            bounds.Width >= bounds.Height
                ? ZonePlacementFlow.RowMajor
                : ZonePlacementFlow.ColumnMajor,
            maxItemCount: null);

    private static void ValidateZones(IReadOnlyList<DesktopLayoutZone> zones)
    {
        _ = new DesktopLayoutTemplate(
            Guid.NewGuid(),
            "AI layout validation",
            version: 1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            zones);
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
    private sealed record FreeRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public long Area => (long)Width * Height;
    }
}
