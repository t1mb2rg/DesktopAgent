using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;

public enum DesktopExplorerWorkspaceGeometryIssueCode
{
    NoDisplays = 0,
    PrimaryDisplayCountInvalid = 1,
    InvalidDisplayGeometry = 2,
    InvalidIconIdentity = 3,
    UnknownIconDisplay = 4,
    IconOutsideAssignedDisplay = 5,
    GridPhaseUnavailable = 6,
    GridPhaseConflict = 7,
    DisplayHasNoNativeSlots = 8,
}

public sealed record DesktopExplorerWorkspaceGeometryIssue(
    DesktopExplorerWorkspaceGeometryIssueCode Code,
    string Message,
    string? DisplayRef = null,
    string? ItemDisplayName = null);

/// <summary>
/// Explorer Geometry Adapter 的 fail-closed 结果。失败只阻塞后续 Compile；
/// DesktopObservation 与 Semantic Understanding 本身仍然是有效的当前事实。
/// </summary>
public sealed class DesktopExplorerWorkspaceGeometryResult
{
    public ExplorerWorkspaceGeometry? Geometry { get; }
    public IReadOnlyList<DesktopExplorerWorkspaceGeometryIssue> Issues { get; }
    public bool Succeeded => Geometry is not null && Issues.Count == 0;

    private DesktopExplorerWorkspaceGeometryResult(
        ExplorerWorkspaceGeometry? geometry,
        IEnumerable<DesktopExplorerWorkspaceGeometryIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var issueArray = issues.ToArray();
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("Issues 不能包含 null。", nameof(issues));

        var isSuccess = geometry is not null && issueArray.Length == 0;
        var isFailure = geometry is null && issueArray.Length > 0;
        if (!isSuccess && !isFailure)
            throw new ArgumentException("GeometryResult 必须是完整成功或完整失败状态。");

        Geometry = geometry;
        Issues = Array.AsReadOnly(issueArray);
    }

    internal static DesktopExplorerWorkspaceGeometryResult Success(ExplorerWorkspaceGeometry geometry)
        => new(geometry ?? throw new ArgumentNullException(nameof(geometry)), []);

    internal static DesktopExplorerWorkspaceGeometryResult Failure(
        IEnumerable<DesktopExplorerWorkspaceGeometryIssue> issues)
        => new(null, issues);
}

/// <summary>
/// ADR-0005 Explorer Geometry Adapter。
///
/// 它只把 fresh DesktopObservation 变成当前 Windows 可施工的 native slot surface：
/// WorkArea、spacing、phase、负坐标、fixed obstacles 与精确像素槽位。
///
/// 关键区别：当前可移动图标不需要先被解释成某个 CurrentCell。
/// 自由摆放 / boundary / tail item 仍然可以作为 Observation 事实存在；只要整体 phase
/// 有足够证据，Geometry Adapter 就能建立目标施工面。
/// </summary>
public static class DesktopExplorerWorkspaceGeometryAdapter
{
    private const double MinimumGridConsensusRatio = 0.60d;

    public static DesktopExplorerWorkspaceGeometryResult Build(DesktopObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var issues = ValidateObservation(observation);
        if (issues.Count > 0)
            return DesktopExplorerWorkspaceGeometryResult.Failure(issues);

        var displayEntries = CreateDisplayEntries(observation.Displays);
        var entriesByDeviceName = displayEntries.ToDictionary(
            static entry => entry.Display.DeviceName,
            StringComparer.Ordinal);

        foreach (var icon in observation.Icons)
        {
            if (icon.MonitorDeviceName is null ||
                !entriesByDeviceName.TryGetValue(icon.MonitorDeviceName, out var entry))
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.UnknownIconDisplay,
                    $"项目 \"{icon.DisplayName}\" 无法归属到当前显示器拓扑。",
                    ItemDisplayName: icon.DisplayName));
                continue;
            }

            if (!entry.Display.Bounds.Contains(icon.PixelPosition))
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.IconOutsideAssignedDisplay,
                    $"项目 \"{icon.DisplayName}\" 的物理位置不在其归属显示器 {entry.DisplayRef} 内。",
                    entry.DisplayRef,
                    icon.DisplayName));
            }
        }

        if (issues.Count > 0)
            return DesktopExplorerWorkspaceGeometryResult.Failure(issues);

        var spacing = observation.PositioningCapabilities.Spacing;
        if (!TryResolveSharedGridPhase(
                observation,
                displayEntries,
                spacing,
                out var phase,
                out var phaseConflict))
        {
            issues.Add(phaseConflict
                ? new(
                    DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseConflict,
                    "多块显示器形成可靠但不一致的 Explorer phase，拒绝猜测 native slot surface。")
                : new(
                    DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseUnavailable,
                    "当前图标位置没有形成足够可靠的 Explorer phase，拒绝猜测 native slot surface。"));
            return DesktopExplorerWorkspaceGeometryResult.Failure(issues);
        }

        var displayGeometries = new List<ExplorerDisplayGeometry>(displayEntries.Count);
        foreach (var entry in displayEntries)
        {
            var origin = new PixelPoint(
                AlignUpToPhase(entry.Display.WorkArea.Left, spacing.X, phase.X),
                AlignUpToPhase(entry.Display.WorkArea.Top, spacing.Y, phase.Y));
            var columns = CountFullyContainedSlots(origin.X, entry.Display.WorkArea.Right, spacing.X);
            var rows = CountFullyContainedSlots(origin.Y, entry.Display.WorkArea.Bottom, spacing.Y);
            if (columns <= 0 || rows <= 0)
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.DisplayHasNoNativeSlots,
                    $"显示器 {entry.DisplayRef} 的 WorkArea 无法形成安全 Explorer-native slot。",
                    entry.DisplayRef));
                continue;
            }

            var slots = CreateSlots(entry.DisplayRef, origin, spacing, columns, rows);
            var fixedObstacles = CreateFixedObstacles(
                observation,
                entry,
                origin,
                spacing,
                columns,
                rows,
                slots);

            displayGeometries.Add(new ExplorerDisplayGeometry(
                entry.DisplayRef,
                entry.Display.DeviceName,
                entry.Display.Bounds,
                entry.Display.WorkArea,
                entry.Display.IsPrimary,
                entry.Display.DpiX,
                entry.Display.DpiY,
                spacing,
                phase,
                origin,
                columns,
                rows,
                slots,
                fixedObstacles));
        }

        if (issues.Count > 0)
            return DesktopExplorerWorkspaceGeometryResult.Failure(issues);

        var fingerprint = ComputeGeometryFingerprint(
            observation.PositioningCapabilities.IsAutoArrangeEnabled,
            spacing,
            phase,
            displayGeometries);
        return DesktopExplorerWorkspaceGeometryResult.Success(new ExplorerWorkspaceGeometry(
            observation.ObservationId,
            fingerprint,
            observation.PositioningCapabilities.IsAutoArrangeEnabled,
            spacing,
            displayGeometries));
    }

    private static List<DesktopExplorerWorkspaceGeometryIssue> ValidateObservation(
        DesktopObservation observation)
    {
        var issues = new List<DesktopExplorerWorkspaceGeometryIssue>();
        if (observation.Displays.Count == 0)
        {
            issues.Add(new(
                DesktopExplorerWorkspaceGeometryIssueCode.NoDisplays,
                "DesktopObservation 没有显示器，无法建立 Explorer 几何。"));
            return issues;
        }

        var primaryCount = observation.Displays.Count(static display => display.IsPrimary);
        if (primaryCount != 1)
        {
            issues.Add(new(
                DesktopExplorerWorkspaceGeometryIssueCode.PrimaryDisplayCountInvalid,
                $"Explorer 几何要求恰好一块主显示器，当前 {primaryCount} 块。"));
        }

        foreach (var display in observation.Displays)
        {
            if (string.IsNullOrWhiteSpace(display.DeviceName) ||
                !IsPositive(display.Bounds) ||
                !IsPositive(display.WorkArea) ||
                !Contains(display.Bounds, display.WorkArea) ||
                display.DpiX == 0 ||
                display.DpiY == 0)
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.InvalidDisplayGeometry,
                    "显示器身份、Bounds、WorkArea 或 DPI 无效，无法建立 Explorer 几何。"));
            }
        }

        foreach (var icon in observation.Icons)
        {
            if (icon is null ||
                string.IsNullOrEmpty(icon.StableId) ||
                string.IsNullOrWhiteSpace(icon.DisplayName) ||
                !Enum.IsDefined(icon.ItemKind))
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.InvalidIconIdentity,
                    "桌面项目缺少有效本地身份或显示名称。",
                    ItemDisplayName: icon?.DisplayName));
            }
        }

        return issues;
    }

    private static bool TryResolveSharedGridPhase(
        DesktopObservation observation,
        IReadOnlyList<DisplayEntry> displayEntries,
        PixelPoint spacing,
        out PixelPoint phase,
        out bool conflict)
    {
        var reliable = new List<DesktopExplorerGridPhaseResolver.Inference>();
        foreach (var entry in displayEntries)
        {
            var positions = observation.Icons
                .Where(icon => string.Equals(
                    icon.MonitorDeviceName,
                    entry.Display.DeviceName,
                    StringComparison.Ordinal))
                .Select(static icon => icon.PixelPosition)
                .ToArray();
            var inference = DesktopExplorerGridPhaseResolver.TryInfer(
                positions,
                spacing,
                MinimumGridConsensusRatio);
            if (inference is not null)
                reliable.Add(inference);
        }

        if (reliable.Count > 0)
        {
            var shared = reliable[0].Phase;
            conflict = reliable.Skip(1).Any(inference => inference.Phase != shared);
            phase = shared;
            return !conflict;
        }

        var global = DesktopExplorerGridPhaseResolver.TryInfer(
            observation.Icons.Select(static icon => icon.PixelPosition).ToArray(),
            spacing,
            MinimumGridConsensusRatio);
        if (global is null)
        {
            phase = default;
            conflict = false;
            return false;
        }

        phase = global.Phase;
        conflict = false;
        return true;
    }

    private static IReadOnlyList<ExplorerNativeSlot> CreateSlots(
        string displayRef,
        PixelPoint origin,
        PixelPoint spacing,
        int columns,
        int rows)
    {
        var slots = new ExplorerNativeSlot[checked(columns * rows)];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var position = new PixelPoint(
                    checked((int)((long)origin.X + (long)column * spacing.X)),
                    checked((int)((long)origin.Y + (long)row * spacing.Y)));
                slots[index++] = new ExplorerNativeSlot(
                    CreateSlotId(displayRef, column, row),
                    column,
                    row,
                    position);
            }
        }

        return Array.AsReadOnly(slots);
    }

    private static IReadOnlyList<ExplorerFixedObstacle> CreateFixedObstacles(
        DesktopObservation observation,
        DisplayEntry entry,
        PixelPoint origin,
        PixelPoint spacing,
        int columns,
        int rows,
        IReadOnlyList<ExplorerNativeSlot> slots)
    {
        var slotIds = slots.ToDictionary(
            static slot => (slot.Column, slot.Row),
            static slot => slot.SlotId);
        var obstacles = observation.Icons
            .Where(icon =>
                string.Equals(icon.MonitorDeviceName, entry.Display.DeviceName, StringComparison.Ordinal) &&
                (icon.IsSystemItem || icon.ItemKind == ItemKind.SystemItem))
            .OrderBy(static icon => icon.StableId, StringComparer.Ordinal)
            .Select(icon => new ExplorerFixedObstacle(
                icon.StableId,
                icon.DisplayName,
                icon.PixelPosition,
                GetBlockedSlotIds(
                    icon.PixelPosition,
                    origin,
                    spacing,
                    columns,
                    rows,
                    slotIds)))
            .ToArray();
        return Array.AsReadOnly(obstacles);
    }

    private static IReadOnlyList<string> GetBlockedSlotIds(
        PixelPoint position,
        PixelPoint origin,
        PixelPoint spacing,
        int columns,
        int rows,
        IReadOnlyDictionary<(int Column, int Row), string> slotIds)
    {
        var firstColumn = FloorDivide((long)position.X - origin.X, spacing.X);
        var firstRow = FloorDivide((long)position.Y - origin.Y, spacing.Y);
        var lastColumn = FloorDivide(
            (long)position.X + spacing.X - 1 - origin.X,
            spacing.X);
        var lastRow = FloorDivide(
            (long)position.Y + spacing.Y - 1 - origin.Y,
            spacing.Y);

        firstColumn = Math.Max(0, firstColumn);
        firstRow = Math.Max(0, firstRow);
        lastColumn = Math.Min(columns - 1L, lastColumn);
        lastRow = Math.Min(rows - 1L, lastRow);
        if (firstColumn > lastColumn || firstRow > lastRow)
            return [];

        var blocked = new List<string>();
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                blocked.Add(slotIds[(checked((int)column), checked((int)row))]);
            }
        }

        return Array.AsReadOnly(blocked.ToArray());
    }

    private static IReadOnlyList<DisplayEntry> CreateDisplayEntries(
        IReadOnlyList<DisplayDescriptor> displays)
    {
        var primary = displays.Single(static display => display.IsPrimary);
        var entries = new List<DisplayEntry>(displays.Count)
        {
            new("primary", primary),
        };

        var secondaryIndex = 1;
        foreach (var display in displays
                     .Where(static display => !display.IsPrimary)
                     .OrderBy(static display => display.Bounds.Left)
                     .ThenBy(static display => display.Bounds.Top)
                     .ThenBy(static display => display.DeviceName, StringComparer.Ordinal))
        {
            entries.Add(new($"secondary_{secondaryIndex++}", display));
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    private static string ComputeGeometryFingerprint(
        bool isAutoArrangeEnabled,
        PixelPoint spacing,
        PixelPoint phase,
        IReadOnlyList<ExplorerDisplayGeometry> displays)
    {
        var canonicalDisplays = string.Join(
            "\n",
            displays
                .OrderBy(static display => display.DisplayRef, StringComparer.Ordinal)
                .Select(display =>
                {
                    var obstacles = string.Join(
                        ",",
                        display.FixedObstacles
                            .OrderBy(static obstacle => obstacle.StableId, StringComparer.Ordinal)
                            .Select(obstacle =>
                                $"{obstacle.StableId}@{obstacle.PixelPosition.X}:{obstacle.PixelPosition.Y}" +
                                $"[{string.Join(";", obstacle.BlockedSlotIds.OrderBy(static id => id, StringComparer.Ordinal))}]"));
                    return
                        $"{display.DisplayRef}|{display.DeviceName}|" +
                        $"{display.Bounds.Left}|{display.Bounds.Top}|{display.Bounds.Right}|{display.Bounds.Bottom}|" +
                        $"{display.WorkArea.Left}|{display.WorkArea.Top}|{display.WorkArea.Right}|{display.WorkArea.Bottom}|" +
                        $"{display.DpiX}|{display.DpiY}|{(display.IsPrimary ? 1 : 0)}|" +
                        $"{display.GridOrigin.X}|{display.GridOrigin.Y}|{display.Columns}|{display.Rows}|{obstacles}";
                }));

        var canonical =
            $"auto={(isAutoArrangeEnabled ? 1 : 0)}|spacing={spacing.X}:{spacing.Y}|phase={phase.X}:{phase.Y}\n" +
            canonicalDisplays;
        return $"explorer-geometry-v1:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
    }

    private static string CreateSlotId(string displayRef, int column, int row)
        => $"{displayRef}:slot:{column:D4}:{row:D4}";

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

    private static long FloorDivide(long value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static bool IsPositive(PixelRect rect)
        => (long)rect.Right - rect.Left > 0 && (long)rect.Bottom - rect.Top > 0;

    private static bool Contains(PixelRect outer, PixelRect inner)
        => inner.Left >= outer.Left && inner.Top >= outer.Top &&
           inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private sealed record DisplayEntry(string DisplayRef, DisplayDescriptor Display);
}
