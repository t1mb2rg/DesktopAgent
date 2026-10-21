using System.Text;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

public sealed record ExplorerGridProbeOccupant(string StableId, PixelPoint Position);

public sealed class ExplorerGridProbeBlockedException(string message) : InvalidOperationException(message);

public sealed record ExplorerGridProbeSlot(PixelPoint Position, string? OccupantStableId)
{
    public bool IsPassive => OccupantStableId is not null;
}

public sealed record ExplorerGridProbePlan(
    IReadOnlyList<ExplorerGridProbeSlot> Horizontal,
    IReadOnlyList<ExplorerGridProbeSlot> Vertical,
    IReadOnlyList<ExplorerGridProbeSlot> Corners)
{
    private const int PreferredMinimumActiveSamplesPerAxis = 3;
    private const int MaximumReportedBlockingSlots = 4;
    private const int MaximumReportedOccupantsPerSlot = 2;
    private const int MaximumReportedStableIdLength = 48;

    private sealed record Scanline(int Coordinate, int[] Along, bool IsHorizontal, SlotClassification[] Slots);

    private sealed record SlotClassification(
        ExplorerGridProbeSlot? Slot,
        IReadOnlyList<ExplorerGridProbeOccupant> OverlappingOccupants,
        bool IsOutsideWorkArea);

    public static ExplorerGridProbePlan Create(
        DisplayDescriptor display,
        PixelPoint spacing,
        PixelPoint phase,
        PixelPoint seed,
        IReadOnlyCollection<ExplorerGridProbeOccupant>? occupants = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (spacing.X <= 0 || spacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing));
        if (phase.X < 0 || phase.X >= spacing.X || phase.Y < 0 || phase.Y >= spacing.Y)
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (!display.WorkArea.Contains(seed))
            throw new ArgumentOutOfRangeException(nameof(seed));

        var firstX = AlignUpToPhase(display.WorkArea.Left, spacing.X, phase.X);
        var firstY = AlignUpToPhase(display.WorkArea.Top, spacing.Y, phase.Y);
        var columns = CountFullyContainedSlots(firstX, display.WorkArea.Right, spacing.X);
        var rows = CountFullyContainedSlots(firstY, display.WorkArea.Bottom, spacing.Y);
        if (columns < 2 || rows < 2)
        {
            throw new ExplorerGridProbeBlockedException(
                $"显示器 {display.DeviceName} 的 WorkArea 无法形成至少 2×2 的安全 Explorer native slot surface。");
        }

        var xCoordinates = Enumerable.Range(0, columns)
            .Select(column => checked((int)((long)firstX + (long)column * spacing.X)))
            .ToArray();
        var yCoordinates = Enumerable.Range(0, rows)
            .Select(row => checked((int)((long)firstY + (long)row * spacing.Y)))
            .ToArray();
        var occupiedRects = (occupants ?? Array.Empty<ExplorerGridProbeOccupant>())
            .Select(occupant => (
                Occupant: occupant,
                Rect: DesktopIconLayoutGeometry.CreateRectChecked(occupant.Position, spacing)))
            .ToArray();
        SlotClassification Classify(PixelPoint point)
        {
            var candidate = DesktopIconLayoutGeometry.CreateRectChecked(point, spacing);
            if (!DesktopIconLayoutGeometry.IsFullyContained(candidate, display.WorkArea))
                return new SlotClassification(null, Array.Empty<ExplorerGridProbeOccupant>(), IsOutsideWorkArea: true);

            var overlapping = occupiedRects
                .Where(occupied => DesktopIconLayoutGeometry.Overlaps(candidate, occupied.Rect))
                .Select(static occupied => occupied.Occupant)
                .ToArray();
            if (overlapping.Length == 0)
                return new SlotClassification(new ExplorerGridProbeSlot(point, OccupantStableId: null), overlapping, IsOutsideWorkArea: false);
            if (overlapping.Length == 1 && overlapping[0].Position == point)
                return new SlotClassification(new ExplorerGridProbeSlot(point, overlapping[0].StableId), overlapping, IsOutsideWorkArea: false);
            return new SlotClassification(null, overlapping, IsOutsideWorkArea: false);
        }

        // 每条扫描线只要求"存在一段足够长的连续可分类槽位"，而不是"整行每个槽位都可分类"：
        // 真实桌面上总会有停留在旧 Explorer 网格位置、或位于 WorkArea 之外（例如任务栏条带内）的图标，
        // 它们的占用矩形会部分压住可测槽位。探针只需要一段连续、完全空闲的槽位即可安全移动并测量；
        // 被部分压住的槽位一律不进入扫描线，所以放宽后仍然不会把测试图标落到重叠位置。
        var minimumHorizontalActive = Math.Min(PreferredMinimumActiveSamplesPerAxis, columns);
        var horizontalLines = yCoordinates
            .Select(y => new Scanline(
                y,
                xCoordinates,
                IsHorizontal: true,
                xCoordinates.Select(x => Classify(new PixelPoint(x, y))).ToArray()))
            .ToArray();
        var horizontal = SelectScanline(horizontalLines, minimumHorizontalActive, seed.Y);
        if (horizontal is null)
        {
            throw new ExplorerGridProbeBlockedException(
                DescribeBlockedScanline(
                    display,
                    horizontalLines,
                    spacing,
                    minimumHorizontalActive,
                    seed.Y,
                    axisName: "横向",
                    coordinateName: "y"));
        }

        var minimumVerticalActive = Math.Min(PreferredMinimumActiveSamplesPerAxis, rows);
        var verticalLines = xCoordinates
            .Select(x => new Scanline(
                x,
                yCoordinates,
                IsHorizontal: false,
                yCoordinates.Select(y => Classify(new PixelPoint(x, y))).ToArray()))
            .ToArray();
        var vertical = SelectScanline(verticalLines, minimumVerticalActive, seed.X);
        if (vertical is null)
        {
            throw new ExplorerGridProbeBlockedException(
                DescribeBlockedScanline(
                    display,
                    verticalLines,
                    spacing,
                    minimumVerticalActive,
                    seed.X,
                    axisName: "纵向",
                    coordinateName: "x"));
        }

        var lastX = xCoordinates[^1];
        var lastY = yCoordinates[^1];
        var perimeter = xCoordinates
            .SelectMany(x => new[] { new PixelPoint(x, firstY), new PixelPoint(x, lastY) })
            .Concat(yCoordinates.SelectMany(y => new[] { new PixelPoint(firstX, y), new PixelPoint(lastX, y) }))
            .Distinct()
            .Select(point => Classify(point).Slot)
            .Where(static slot => slot is not null)
            .Select(static slot => slot!)
            .ToArray();
        var desiredCorners = new[]
        {
            new PixelPoint(firstX, firstY),
            new PixelPoint(lastX, firstY),
            new PixelPoint(firstX, lastY),
            new PixelPoint(lastX, lastY),
        };
        var selectedCorners = new List<ExplorerGridProbeSlot>(4);
        foreach (var desired in desiredCorners)
        {
            var selected = perimeter
                .Where(candidate => selectedCorners.All(existing => existing.Position != candidate.Position))
                .OrderBy(candidate => ManhattanDistance(candidate.Position, desired))
                .FirstOrDefault();
            if (selected is null)
            {
                throw new ExplorerGridProbeBlockedException(
                    $"显示器 {display.DeviceName} 的边缘没有 4 个可安全验证且互不重复的 native slot。" +
                    "精确占位图标可以提供只读被动样本；部分重叠位置不会写入。");
            }

            selectedCorners.Add(selected);
        }
        var corners = selectedCorners.ToArray();

        if (horizontal.Concat(vertical).Concat(corners)
            .Any(slot => !display.WorkArea.Contains(slot.Position)))
            throw new InvalidOperationException("Probe 计划生成了 WorkArea 之外的坐标。");

        return new ExplorerGridProbePlan(
            Array.AsReadOnly(horizontal),
            Array.AsReadOnly(vertical),
            Array.AsReadOnly(corners));
    }

    internal static PixelPoint PhaseOf(PixelPoint point, PixelPoint spacing)
        => new(PositiveModulo(point.X, spacing.X), PositiveModulo(point.Y, spacing.Y));

    internal static bool IsLandingSlotFree(
        PixelPoint target,
        PixelPoint spacing,
        IReadOnlyCollection<ExplorerGridProbeOccupant> occupants)
    {
        ArgumentNullException.ThrowIfNull(occupants);
        var targetRect = DesktopIconLayoutGeometry.CreateRectChecked(target, spacing);
        return occupants.All(occupant =>
            !DesktopIconLayoutGeometry.Overlaps(
                targetRect,
                DesktopIconLayoutGeometry.CreateRectChecked(occupant.Position, spacing)));
    }

    private static ExplorerGridProbeSlot[]? SelectScanline(
        IReadOnlyList<Scanline> lines,
        int minimumActive,
        int seedAlongLine)
    {
        ExplorerGridProbeSlot[]? best = null;
        var bestDistance = long.MaxValue;
        var bestActive = -1;
        var bestLength = -1;
        foreach (var line in lines)
        {
            var run = LongestQualifyingRun(line.Slots, minimumActive);
            if (run is null)
                continue;

            var distance = Math.Abs((long)line.Coordinate - seedAlongLine);
            var isBetter = best is null
                || distance < bestDistance
                || (distance == bestDistance && run.Value.ActiveCount > bestActive)
                || (distance == bestDistance && run.Value.ActiveCount == bestActive && run.Value.Slots.Length > bestLength);
            if (!isBetter)
                continue;

            best = run.Value.Slots;
            bestDistance = distance;
            bestActive = run.Value.ActiveCount;
            bestLength = run.Value.Slots.Length;
        }

        return best;
    }

    private static (ExplorerGridProbeSlot[] Slots, int ActiveCount, int Start)? LongestQualifyingRun(
        IReadOnlyList<SlotClassification> line,
        int minimumActive)
    {
        (ExplorerGridProbeSlot[] Slots, int ActiveCount, int Start)? best = null;
        var index = 0;
        while (index < line.Count)
        {
            if (line[index].Slot is null)
            {
                index++;
                continue;
            }

            var start = index;
            var activeCount = 0;
            while (index < line.Count && line[index].Slot is not null)
            {
                if (!line[index].Slot!.IsPassive)
                    activeCount++;
                index++;
            }

            var slots = line
                .Skip(start)
                .Take(index - start)
                .Select(static entry => entry.Slot!)
                .ToArray();
            var isBetter = best is null
                || activeCount > best.Value.ActiveCount
                || (activeCount == best.Value.ActiveCount && slots.Length > best.Value.Slots.Length);
            if (activeCount >= minimumActive && isBetter)
                best = (slots, activeCount, start);
        }

        return best;
    }

    private static string DescribeBlockedScanline(
        DisplayDescriptor display,
        IReadOnlyList<Scanline> lines,
        PixelPoint spacing,
        int minimumActive,
        int seedAlongLine,
        string axisName,
        string coordinateName)
    {
        var inspected = lines
            .Select(line => (
                Line: line,
                Classifiable: line.Slots.Count(static entry => entry.Slot is not null),
                Active: line.Slots.Count(static entry => entry.Slot is not null && !entry.Slot.IsPassive),
                LongestRun: LongestRunLength(line.Slots)))
            .ToArray();
        var nearest = inspected
            .OrderBy(entry => Math.Abs((long)entry.Line.Coordinate - seedAlongLine))
            .ThenByDescending(static entry => entry.Active)
            .ThenByDescending(static entry => entry.LongestRun)
            .First();

        var text = new StringBuilder();
        text.Append($"显示器 {display.DeviceName} 找不到可安全覆盖且至少包含 {minimumActive} 个主动落点的{axisName} native-slot 扫描线。");
        text.Append($"本次共检查 {lines.Count} 条{axisName}扫描线，没有一条满足。");
        text.Append($"最接近 seed 的一条 {coordinateName}={nearest.Line.Coordinate}：");
        text.Append($"共 {nearest.Line.Slots.Length} 个槽位，可分类 {nearest.Classifiable} 个");
        text.Append($"（主动 {nearest.Active}、被动 {nearest.Classifiable - nearest.Active}），");
        text.Append($"最长连续可分类区间 {nearest.LongestRun} 个槽位。");

        var blocking = nearest.Line.Slots
            .Select((entry, index) => (Entry: entry, Index: index))
            .Where(static pair => pair.Entry.Slot is null)
            .Take(MaximumReportedBlockingSlots)
            .Select(pair => DescribeBlockingSlot(display, nearest.Line, pair.Index, spacing))
            .ToArray();
        if (blocking.Length > 0)
            text.Append("不可分类槽位：" + string.Join("；", blocking) + "。");

        text.Append("原因：这些占用者的占用矩形与可测槽位部分重叠——常见于停留在旧 Explorer 网格位置、");
        text.Append("或位于本显示器 WorkArea 之外（例如任务栏条带内）的图标。");
        text.Append("建议：先把桌面图标与 Explorer 网格对齐（桌面右键 → 查看 → 将图标与网格对齐），");
        text.Append("或把上述图标移出重叠位置后重试。");
        text.Append("精确占位图标可以提供只读被动样本；主动样本不足时 Physical Probe 在写入前停止。");
        return text.ToString();
    }

    private static string DescribeBlockingSlot(
        DisplayDescriptor display,
        Scanline line,
        int index,
        PixelPoint spacing)
    {
        var entry = line.Slots[index];
        var point = line.IsHorizontal
            ? new PixelPoint(line.Along[index], line.Coordinate)
            : new PixelPoint(line.Coordinate, line.Along[index]);
        if (entry.IsOutsideWorkArea)
            return $"({point.X},{point.Y}) 在工作区之外";

        if (entry.OverlappingOccupants.Count == 0)
            return $"({point.X},{point.Y}) 无法判定";

        var occupants = entry.OverlappingOccupants
            .Take(MaximumReportedOccupantsPerSlot)
            .Select(occupant =>
            {
                var occupantRect = DesktopIconLayoutGeometry.CreateRectChecked(occupant.Position, spacing);
                var outside = !DesktopIconLayoutGeometry.IsFullyContained(occupantRect, display.WorkArea);
                return $"{Shorten(occupant.StableId)}@({occupant.Position.X},{occupant.Position.Y})" +
                    (outside ? "[WorkArea 之外]" : string.Empty);
            });
        var suffix = entry.OverlappingOccupants.Count > MaximumReportedOccupantsPerSlot
            ? $"等 {entry.OverlappingOccupants.Count} 个"
            : string.Empty;
        return $"({point.X},{point.Y}) 被 {entry.OverlappingOccupants.Count} 个占用者部分覆盖：" +
            string.Join("、", occupants) + suffix;
    }

    private static string Shorten(string stableId) =>
        stableId.Length <= MaximumReportedStableIdLength
            ? stableId
            : stableId[..MaximumReportedStableIdLength] + "…";

    private static int LongestRunLength(IReadOnlyList<SlotClassification> line)
    {
        var best = 0;
        var current = 0;
        foreach (var entry in line)
        {
            if (entry.Slot is null)
            {
                current = 0;
                continue;
            }

            current++;
            if (current > best)
                best = current;
        }

        return best;
    }

    private static long ManhattanDistance(PixelPoint left, PixelPoint right) =>
        Math.Abs((long)left.X - right.X) + Math.Abs((long)left.Y - right.Y);

    private static int AlignUpToPhase(int value, int spacing, int phase)
    {
        var residue = PositiveModulo(value, spacing);
        var delta = (phase - residue + spacing) % spacing;
        return checked((int)((long)value + delta));
    }

    private static int PositiveModulo(int value, int modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }

    private static int CountFullyContainedSlots(int origin, int exclusiveEnd, int spacing)
    {
        var available = (long)exclusiveEnd - origin;
        return available <= 0 ? 0 : checked((int)(available / spacing));
    }
}
