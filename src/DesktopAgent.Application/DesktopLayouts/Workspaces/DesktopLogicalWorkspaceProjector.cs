using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

public enum DesktopLogicalWorkspaceProjectionIssueCode
{
    NoDisplays = 0,
    PrimaryDisplayCountInvalid = 1,
    InvalidDisplayGeometry = 2,
    InvalidIconIdentity = 3,
    UnknownIconDisplay = 4,
    GridPhaseUnavailable = 5,
    DisplayHasNoGridCapacity = 6,
    IconOutsideDisplayGrid = 7,
    SemanticAssignmentOutsideObservation = 8,
    InvalidSemanticCategoryCatalog = 9,
    GridPhaseConflict = 10,
}

public sealed record DesktopLogicalWorkspaceProjectionIssue(
    DesktopLogicalWorkspaceProjectionIssueCode Code,
    string Message,
    string? ItemDisplayName = null,
    string? DisplayRef = null);

/// <summary>
/// 一次本地逻辑工作区投影的结果。公开表面只暴露 Provider-safe Workspace 与结构化问题；
/// StableId、DeviceName 和像素校准保存在 Application 内部 authority sidecar 中。
/// </summary>
public sealed class DesktopLogicalWorkspaceProjectionResult
{
    public DesktopObservedLogicalWorkspace? Workspace { get; }
    public IReadOnlyList<DesktopLogicalWorkspaceProjectionIssue> Issues { get; }
    public bool Succeeded => Workspace is not null && Issues.Count == 0;

    internal DesktopLogicalWorkspaceProjectionAuthority? Authority { get; }

    private DesktopLogicalWorkspaceProjectionResult(
        DesktopObservedLogicalWorkspace? workspace,
        IEnumerable<DesktopLogicalWorkspaceProjectionIssue> issues,
        DesktopLogicalWorkspaceProjectionAuthority? authority)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var issueArray = issues.ToArray();
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("Issues 不能包含 null。", nameof(issues));

        var isSuccess = workspace is not null && authority is not null && issueArray.Length == 0;
        var isFailure = workspace is null && authority is null && issueArray.Length > 0;
        if (!isSuccess && !isFailure)
            throw new ArgumentException("ProjectionResult 必须是完整成功或完整失败状态。");

        Workspace = workspace;
        Issues = Array.AsReadOnly(issueArray);
        Authority = authority;
    }

    internal static DesktopLogicalWorkspaceProjectionResult Success(
        DesktopObservedLogicalWorkspace workspace,
        DesktopLogicalWorkspaceProjectionAuthority authority)
        => new(workspace, [], authority);

    internal static DesktopLogicalWorkspaceProjectionResult Failure(
        IEnumerable<DesktopLogicalWorkspaceProjectionIssue> issues)
        => new(null, issues, null);
}

internal sealed record DesktopLogicalWorkspaceItemBinding(string ItemRef, string StableId);

internal sealed record DesktopLogicalDisplayGridCalibration(
    string DisplayRef,
    string DeviceName,
    PixelRect WorkArea,
    PixelPoint GridOrigin,
    PixelPoint Spacing,
    DesktopLogicalGridSize GridSize);

/// <summary>
/// 仅供本地 Validator / Preview / Execution Planner 使用的 authority sidecar。
/// 该对象永远不能作为 Provider 请求体。
/// </summary>
internal sealed class DesktopLogicalWorkspaceProjectionAuthority
{
    private readonly IReadOnlyDictionary<string, DesktopLogicalWorkspaceItemBinding> _itemsByRef;
    private readonly IReadOnlyDictionary<string, DesktopLogicalWorkspaceItemBinding> _itemsByStableId;
    private readonly IReadOnlyDictionary<string, DesktopLogicalDisplayGridCalibration> _displaysByRef;

    internal Guid ObservationId { get; }
    internal IReadOnlyList<DesktopLogicalWorkspaceItemBinding> ItemBindings { get; }
    internal IReadOnlyList<DesktopLogicalDisplayGridCalibration> DisplayCalibrations { get; }

    internal DesktopLogicalWorkspaceProjectionAuthority(
        Guid observationId,
        IEnumerable<DesktopLogicalWorkspaceItemBinding> itemBindings,
        IEnumerable<DesktopLogicalDisplayGridCalibration> displayCalibrations)
    {
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为 Guid.Empty。", nameof(observationId));
        ArgumentNullException.ThrowIfNull(itemBindings);
        ArgumentNullException.ThrowIfNull(displayCalibrations);

        var itemArray = itemBindings.ToArray();
        var displayArray = displayCalibrations.ToArray();
        if (itemArray.Any(static item => item is null))
            throw new ArgumentException("ItemBindings 不能包含 null。", nameof(itemBindings));
        if (displayArray.Any(static display => display is null))
            throw new ArgumentException("DisplayCalibrations 不能包含 null。", nameof(displayCalibrations));

        _itemsByRef = itemArray.ToDictionary(static item => item.ItemRef, StringComparer.Ordinal);
        _itemsByStableId = itemArray.ToDictionary(static item => item.StableId, StringComparer.Ordinal);
        _displaysByRef = displayArray.ToDictionary(static display => display.DisplayRef, StringComparer.Ordinal);
        ObservationId = observationId;
        ItemBindings = Array.AsReadOnly(itemArray);
        DisplayCalibrations = Array.AsReadOnly(displayArray);
    }

    internal DesktopLogicalWorkspaceItemBinding ResolveItem(string itemRef)
        => _itemsByRef.TryGetValue(itemRef, out var binding)
            ? binding
            : throw new KeyNotFoundException($"未知 ItemRef \"{itemRef}\"。");

    internal bool TryResolveItemByStableId(
        string stableId,
        out DesktopLogicalWorkspaceItemBinding? binding)
        => _itemsByStableId.TryGetValue(stableId, out binding);

    internal DesktopLogicalDisplayGridCalibration ResolveDisplay(string displayRef)
        => _displaysByRef.TryGetValue(displayRef, out var calibration)
            ? calibration
            : throw new KeyNotFoundException($"未知 DisplayRef \"{displayRef}\"。");
}

/// <summary>
/// 把完整 DesktopObservation 确定性投影成 AI 可读的逻辑工作区。
/// 不推断用户意图，不产生目标位置；无法可靠建立网格校准或项目完全没有网格覆盖时 fail-closed。
/// </summary>
public static class DesktopLogicalWorkspaceProjector
{
    private const double MinimumGridConsensusRatio = 0.60d;

    public static DesktopLogicalWorkspaceProjectionResult Project(DesktopObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var issues = new List<DesktopLogicalWorkspaceProjectionIssue>();
        if (observation.Displays.Count == 0)
        {
            issues.Add(new(
                DesktopLogicalWorkspaceProjectionIssueCode.NoDisplays,
                "DesktopObservation 没有显示器，无法建立逻辑工作区。"));
            return DesktopLogicalWorkspaceProjectionResult.Failure(issues);
        }

        var primaryCount = observation.Displays.Count(static display => display.IsPrimary);
        if (primaryCount != 1)
        {
            issues.Add(new(
                DesktopLogicalWorkspaceProjectionIssueCode.PrimaryDisplayCountInvalid,
                $"逻辑工作区要求恰好一块主显示器，当前 {primaryCount} 块。"));
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
                    DesktopLogicalWorkspaceProjectionIssueCode.InvalidDisplayGeometry,
                    "显示器身份、边界或工作区无效，无法建立逻辑网格。"));
            }
        }

        foreach (var icon in observation.Icons)
        {
            if (string.IsNullOrEmpty(icon.StableId) ||
                string.IsNullOrWhiteSpace(icon.DisplayName) ||
                !Enum.IsDefined(icon.ItemKind))
            {
                issues.Add(new(
                    DesktopLogicalWorkspaceProjectionIssueCode.InvalidIconIdentity,
                    "桌面项目缺少本地身份或显示名称。",
                    icon.DisplayName));
            }
        }

        if (issues.Count > 0)
            return DesktopLogicalWorkspaceProjectionResult.Failure(issues);

        var displayEntries = CreateDisplayEntries(observation.Displays);
        var entriesByDeviceName = displayEntries.ToDictionary(
            static entry => entry.Display.DeviceName,
            StringComparer.Ordinal);

        foreach (var icon in observation.Icons)
        {
            if (icon.MonitorDeviceName is null || !entriesByDeviceName.ContainsKey(icon.MonitorDeviceName))
            {
                issues.Add(new(
                    DesktopLogicalWorkspaceProjectionIssueCode.UnknownIconDisplay,
                    $"项目 \"{icon.DisplayName}\" 无法归属到当前显示器拓扑。",
                    icon.DisplayName));
            }
        }

        if (issues.Count > 0)
            return DesktopLogicalWorkspaceProjectionResult.Failure(issues);

        var spacing = observation.PositioningCapabilities.Spacing;
        if (!TryResolveSharedGridPhase(
                observation,
                displayEntries,
                spacing,
                out var gridPhase,
                out var gridPhaseConflict))
        {
            issues.Add(gridPhaseConflict
                ? new(
                    DesktopLogicalWorkspaceProjectionIssueCode.GridPhaseConflict,
                    "多块显示器分别形成可靠但不一致的 Explorer 网格相位，拒绝猜测统一逻辑格位。")
                : new(
                    DesktopLogicalWorkspaceProjectionIssueCode.GridPhaseUnavailable,
                    "当前各显示器均无法形成可靠的 Explorer 网格相位，且全局回退也没有强多数，拒绝猜测逻辑格位。"));
            return DesktopLogicalWorkspaceProjectionResult.Failure(issues);
        }

        var virtualBounds = UnionBounds(observation.Displays);
        var calibrations = new List<DesktopLogicalDisplayGridCalibration>(displayEntries.Count);
        var logicalDisplays = new List<DesktopObservedLogicalDisplay>(displayEntries.Count);
        foreach (var entry in displayEntries)
        {
            var origin = new PixelPoint(
                AlignUpToPhase(entry.Display.WorkArea.Left, spacing.X, gridPhase.X),
                AlignUpToPhase(entry.Display.WorkArea.Top, spacing.Y, gridPhase.Y));
            var columns = CountFullyContainedCells(origin.X, entry.Display.WorkArea.Right, spacing.X);
            var rows = CountFullyContainedCells(origin.Y, entry.Display.WorkArea.Bottom, spacing.Y);
            if (columns <= 0 || rows <= 0)
            {
                issues.Add(new(
                    DesktopLogicalWorkspaceProjectionIssueCode.DisplayHasNoGridCapacity,
                    $"显示器 {entry.DisplayRef} 的工作区无法容纳有效逻辑网格。",
                    DisplayRef: entry.DisplayRef));
                continue;
            }

            var gridSize = new DesktopLogicalGridSize(columns, rows);
            calibrations.Add(new(
                entry.DisplayRef,
                entry.Display.DeviceName,
                entry.Display.WorkArea,
                origin,
                spacing,
                gridSize));
            logicalDisplays.Add(new(
                entry.DisplayRef,
                entry.Display.IsPrimary,
                NormalizeTopologyRect(entry.Display.Bounds, virtualBounds),
                gridSize));
        }

        if (issues.Count > 0)
            return DesktopLogicalWorkspaceProjectionResult.Failure(issues);

        var calibrationsByDeviceName = calibrations.ToDictionary(
            static calibration => calibration.DeviceName,
            StringComparer.Ordinal);
        var logicalItems = new List<DesktopObservedLogicalItem>(observation.Icons.Count);
        var itemBindings = new List<DesktopLogicalWorkspaceItemBinding>(observation.Icons.Count);
        var orderedIcons = observation.Icons
            .OrderBy(static icon => icon.StableId, StringComparer.Ordinal)
            .ToArray();

        for (var index = 0; index < orderedIcons.Length; index++)
        {
            var icon = orderedIcons[index];
            var calibration = calibrationsByDeviceName[icon.MonitorDeviceName!];
            var deltaX = (long)icon.PixelPosition.X - calibration.GridOrigin.X;
            var deltaY = (long)icon.PixelPosition.Y - calibration.GridOrigin.Y;
            var occupiedCells = GetOccupiedCells(icon.PixelPosition, calibration);
            if (occupiedCells.Count == 0)
            {
                issues.Add(new(
                    DesktopLogicalWorkspaceProjectionIssueCode.IconOutsideDisplayGrid,
                    $"项目 \"{icon.DisplayName}\" 超出显示器 {calibration.DisplayRef} 的有效逻辑网格。",
                    icon.DisplayName,
                    calibration.DisplayRef));
                continue;
            }

            var itemRef = $"item-{index + 1:D4}";
            var cell = new DesktopLogicalGridCell(
                NearestCellIndex(deltaX, spacing.X, calibration.GridSize.Columns),
                NearestCellIndex(deltaY, spacing.Y, calibration.GridSize.Rows));
            var isGridAligned = deltaX % spacing.X == 0 &&
                                deltaY % spacing.Y == 0 &&
                                occupiedCells.Count == 1 &&
                                occupiedCells[0] == cell;
            logicalItems.Add(new(
                itemRef,
                icon.DisplayName,
                icon.ItemKind,
                calibration.DisplayRef,
                cell,
                isMovable: !icon.IsSystemItem,
                isGridAligned: isGridAligned,
                occupiedCells: occupiedCells));
            itemBindings.Add(new(itemRef, icon.StableId));
        }

        if (issues.Count > 0)
            return DesktopLogicalWorkspaceProjectionResult.Failure(issues);

        var topologyFingerprint = ComputeTopologyFingerprint(observation.Displays);
        var gridFingerprint = ComputeGridFingerprint(spacing, gridPhase, calibrations);
        var workspace = new DesktopObservedLogicalWorkspace(
            observation.ObservationId,
            topologyFingerprint,
            gridFingerprint,
            logicalDisplays,
            logicalItems);
        var authority = new DesktopLogicalWorkspaceProjectionAuthority(
            observation.ObservationId,
            itemBindings,
            calibrations);
        return DesktopLogicalWorkspaceProjectionResult.Success(workspace, authority);
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
            phase = reliable[0].Phase;
            conflict = reliable.Skip(1).Any(inference => inference.Phase != phase);
            return !conflict;
        }

        var globalFallback = DesktopExplorerGridPhaseResolver.TryInfer(
            observation.Icons.Select(static icon => icon.PixelPosition).ToArray(),
            spacing,
            MinimumGridConsensusRatio);
        if (globalFallback is null)
        {
            phase = default;
            conflict = false;
            return false;
        }

        phase = globalFallback.Phase;
        conflict = false;
        return true;
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
            entries.Add(new(
                $"secondary_{secondaryIndex++}",
                display));
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    private static string ComputeTopologyFingerprint(IReadOnlyList<DisplayDescriptor> displays)
    {
        var canonical = string.Join(
            "\n",
            displays
                .OrderBy(static display => display.DeviceName, StringComparer.Ordinal)
                .Select(static display =>
                    $"{display.DeviceName}|{display.Bounds.Left}|{display.Bounds.Top}|" +
                    $"{display.Bounds.Right}|{display.Bounds.Bottom}|" +
                    $"{display.WorkArea.Left}|{display.WorkArea.Top}|" +
                    $"{display.WorkArea.Right}|{display.WorkArea.Bottom}|" +
                    $"{display.DpiX}|{display.DpiY}|{(display.IsPrimary ? 1 : 0)}"));
        return Hash("topology-v1", canonical);
    }

    private static string ComputeGridFingerprint(
        PixelPoint spacing,
        PixelPoint phase,
        IReadOnlyList<DesktopLogicalDisplayGridCalibration> calibrations)
    {
        var displays = string.Join(
            "\n",
            calibrations
                .OrderBy(static calibration => calibration.DeviceName, StringComparer.Ordinal)
                .Select(static calibration =>
                    $"{calibration.DeviceName}|{calibration.WorkArea.Left}|{calibration.WorkArea.Top}|" +
                    $"{calibration.WorkArea.Right}|{calibration.WorkArea.Bottom}|" +
                    $"{calibration.GridOrigin.X}|{calibration.GridOrigin.Y}|" +
                    $"{calibration.GridSize.Columns}|{calibration.GridSize.Rows}"));
        return Hash("grid-v1", $"{spacing.X}|{spacing.Y}|{phase.X}|{phase.Y}\n{displays}");
    }

    private static string Hash(string version, string canonical)
        => $"{version}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";

    private static DesktopLogicalTopologyRect NormalizeTopologyRect(PixelRect bounds, PixelRect union)
    {
        var width = checked((long)union.Right - union.Left);
        var height = checked((long)union.Bottom - union.Top);
        var left = ScaleFloor((long)bounds.Left - union.Left, width);
        var top = ScaleFloor((long)bounds.Top - union.Top, height);
        var right = ScaleFloor((long)bounds.Right - union.Left, width);
        var bottom = ScaleFloor((long)bounds.Bottom - union.Top, height);
        right = Math.Max(left + 1, right);
        bottom = Math.Max(top + 1, bottom);
        return new DesktopLogicalTopologyRect(left, top, right, bottom);
    }

    private static int ScaleFloor(long offset, long total)
        => checked((int)(offset * DesktopLogicalTopologyRect.Scale / total));

    private static PixelRect UnionBounds(IReadOnlyList<DisplayDescriptor> displays)
        => new(
            displays.Min(static display => display.Bounds.Left),
            displays.Min(static display => display.Bounds.Top),
            displays.Max(static display => display.Bounds.Right),
            displays.Max(static display => display.Bounds.Bottom));

    private static bool IsPositive(PixelRect rect)
        => (long)rect.Right - rect.Left > 0 && (long)rect.Bottom - rect.Top > 0;

    private static bool Contains(PixelRect outer, PixelRect inner)
        => inner.Left >= outer.Left && inner.Top >= outer.Top &&
           inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

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

    private static int CountFullyContainedCells(int origin, int exclusiveEnd, int spacing)
    {
        var available = (long)exclusiveEnd - origin;
        return available <= 0 ? 0 : checked((int)(available / spacing));
    }

    private static IReadOnlyList<DesktopLogicalGridCell> GetOccupiedCells(
        PixelPoint position,
        DesktopLogicalDisplayGridCalibration calibration)
    {
        var firstColumn = FloorDivide((long)position.X - calibration.GridOrigin.X, calibration.Spacing.X);
        var firstRow = FloorDivide((long)position.Y - calibration.GridOrigin.Y, calibration.Spacing.Y);
        var lastColumn = FloorDivide(
            (long)position.X + calibration.Spacing.X - 1 - calibration.GridOrigin.X,
            calibration.Spacing.X);
        var lastRow = FloorDivide(
            (long)position.Y + calibration.Spacing.Y - 1 - calibration.GridOrigin.Y,
            calibration.Spacing.Y);

        firstColumn = Math.Max(0, firstColumn);
        firstRow = Math.Max(0, firstRow);
        lastColumn = Math.Min(calibration.GridSize.Columns - 1L, lastColumn);
        lastRow = Math.Min(calibration.GridSize.Rows - 1L, lastRow);
        if (firstColumn > lastColumn || firstRow > lastRow)
            return [];

        var cells = new List<DesktopLogicalGridCell>();
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
                cells.Add(new(checked((int)column), checked((int)row)));
        }
        return Array.AsReadOnly(cells.ToArray());
    }

    private static int NearestCellIndex(long delta, int spacing, int count)
    {
        var rounded = delta >= 0
            ? (delta + spacing / 2L) / spacing
            : -((-delta + spacing / 2L) / spacing);
        return checked((int)Math.Clamp(rounded, 0, count - 1L));
    }

    private static long FloorDivide(long value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private sealed record DisplayEntry(
        string DisplayRef,
        DisplayDescriptor Display);
}
