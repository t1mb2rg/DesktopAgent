using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// Explorer 原生可写槽位。Column / Row 只属于本地 Windows 几何层，
/// 不是 Semantic Understanding 或 Workspace Blueprint 的逻辑 Cell。
/// </summary>
public sealed record ExplorerNativeSlot
{
    public string SlotId { get; }
    public int Column { get; }
    public int Row { get; }
    public PixelPoint PixelPosition { get; }

    public ExplorerNativeSlot(
        string slotId,
        int column,
        int row,
        PixelPoint pixelPosition)
    {
        if (string.IsNullOrWhiteSpace(slotId))
            throw new ArgumentException("SlotId 不能为空。", nameof(slotId));
        if (column < 0)
            throw new ArgumentOutOfRangeException(nameof(column));
        if (row < 0)
            throw new ArgumentOutOfRangeException(nameof(row));

        SlotId = slotId.Trim();
        Column = column;
        Row = row;
        PixelPosition = pixelPosition;
    }
}

/// <summary>
/// 当前 Windows 事实中的不可移动障碍。BlockedSlotIds 只描述它实际覆盖的
/// Explorer-native slots，不把障碍强行投影成某个 CurrentCell。
/// </summary>
public sealed record ExplorerFixedObstacle
{
    private readonly IReadOnlyList<string> _blockedSlotIds;

    public string StableId { get; }
    public string DisplayName { get; }
    public PixelPoint PixelPosition { get; }
    public IReadOnlyList<string> BlockedSlotIds => _blockedSlotIds;

    public ExplorerFixedObstacle(
        string stableId,
        string displayName,
        PixelPoint pixelPosition,
        IEnumerable<string>? blockedSlotIds = null)
    {
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("DisplayName 不能为空。", nameof(displayName));

        var blockedArray = blockedSlotIds?.ToArray() ?? [];
        if (blockedArray.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("BlockedSlotIds 不能包含空值。", nameof(blockedSlotIds));
        if (blockedArray.Distinct(StringComparer.Ordinal).Count() != blockedArray.Length)
            throw new ArgumentException("BlockedSlotIds 不能重复。", nameof(blockedSlotIds));

        StableId = stableId;
        DisplayName = displayName.Trim();
        PixelPosition = pixelPosition;
        _blockedSlotIds = Array.AsReadOnly(blockedArray);
    }
}

/// <summary>
/// 一块显示器在当前 Explorer 环境中的本地施工几何。
/// DeviceName、Pixel、spacing、phase 与 native slots 都属于本地 authority，
/// 不得发送给 Workspace Designer Provider。
/// </summary>
public sealed record ExplorerDisplayGeometry
{
    private readonly IReadOnlyList<ExplorerNativeSlot> _slots;
    private readonly IReadOnlyList<ExplorerFixedObstacle> _fixedObstacles;

    public string DisplayRef { get; }
    public string DeviceName { get; }
    public PixelRect Bounds { get; }
    public PixelRect WorkArea { get; }
    public bool IsPrimary { get; }
    public uint DpiX { get; }
    public uint DpiY { get; }
    public PixelPoint Spacing { get; }
    public PixelPoint Phase { get; }
    public PixelPoint GridOrigin { get; }
    public int Columns { get; }
    public int Rows { get; }
    public IReadOnlyList<ExplorerNativeSlot> Slots => _slots;
    public IReadOnlyList<ExplorerFixedObstacle> FixedObstacles => _fixedObstacles;

    public ExplorerDisplayGeometry(
        string displayRef,
        string deviceName,
        PixelRect bounds,
        PixelRect workArea,
        bool isPrimary,
        uint dpiX,
        uint dpiY,
        PixelPoint spacing,
        PixelPoint phase,
        PixelPoint gridOrigin,
        int columns,
        int rows,
        IEnumerable<ExplorerNativeSlot> slots,
        IEnumerable<ExplorerFixedObstacle>? fixedObstacles = null)
    {
        DisplayRef = RequireText(displayRef, nameof(displayRef));
        DeviceName = RequireText(deviceName, nameof(deviceName));
        if (!IsPositive(bounds))
            throw new ArgumentException("Bounds 必须具有正面积。", nameof(bounds));
        if (!IsPositive(workArea) || !Contains(bounds, workArea))
            throw new ArgumentException("WorkArea 必须具有正面积且位于 Bounds 内。", nameof(workArea));
        if (dpiX == 0 || dpiY == 0)
            throw new ArgumentOutOfRangeException(nameof(dpiX), "DPI 必须大于 0。");
        if (spacing.X <= 0 || spacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing 必须为正数。 ");
        if (phase.X < 0 || phase.X >= spacing.X || phase.Y < 0 || phase.Y >= spacing.Y)
            throw new ArgumentOutOfRangeException(nameof(phase), "Phase 必须位于各自 spacing 的余数范围内。 ");
        if (PositiveModulo(gridOrigin.X, spacing.X) != phase.X ||
            PositiveModulo(gridOrigin.Y, spacing.Y) != phase.Y)
        {
            throw new ArgumentException("GridOrigin 必须与 Explorer phase 对齐。", nameof(gridOrigin));
        }
        if (columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows));
        ArgumentNullException.ThrowIfNull(slots);

        var slotArray = slots.ToArray();
        var obstacleArray = fixedObstacles?.ToArray() ?? [];
        RejectNullElements(slotArray, nameof(slots));
        RejectNullElements(obstacleArray, nameof(fixedObstacles));

        if ((long)columns * rows != slotArray.Length)
            throw new ArgumentException("Slots 数量必须等于 Columns × Rows。", nameof(slots));
        EnsureUnique(slotArray.Select(static slot => slot.SlotId), nameof(slots));
        EnsureUnique(obstacleArray.Select(static obstacle => obstacle.StableId), nameof(fixedObstacles));

        var slotIds = slotArray.Select(static slot => slot.SlotId).ToHashSet(StringComparer.Ordinal);
        foreach (var slot in slotArray)
        {
            if (slot.Column >= columns || slot.Row >= rows)
                throw new ArgumentException("Slot 的 Column / Row 超出显示器 native slot surface。", nameof(slots));
            if (!workArea.Contains(slot.PixelPosition))
                throw new ArgumentException("Slot PixelPosition 必须位于 WorkArea 内。", nameof(slots));
            if (PositiveModulo(slot.PixelPosition.X, spacing.X) != phase.X ||
                PositiveModulo(slot.PixelPosition.Y, spacing.Y) != phase.Y)
            {
                throw new ArgumentException("所有 Slot 必须与 Explorer phase 对齐。", nameof(slots));
            }
        }

        foreach (var obstacle in obstacleArray)
        {
            if (obstacle.BlockedSlotIds.Any(slotId => !slotIds.Contains(slotId)))
                throw new ArgumentException("FixedObstacle 引用了当前显示器之外的 SlotId。", nameof(fixedObstacles));
        }

        Bounds = bounds;
        WorkArea = workArea;
        IsPrimary = isPrimary;
        DpiX = dpiX;
        DpiY = dpiY;
        Spacing = spacing;
        Phase = phase;
        GridOrigin = gridOrigin;
        Columns = columns;
        Rows = rows;
        _slots = Array.AsReadOnly(slotArray);
        _fixedObstacles = Array.AsReadOnly(obstacleArray);
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("值不能为空。", parameterName);
        return value.Trim();
    }

    private static bool IsPositive(PixelRect rect)
        => (long)rect.Right - rect.Left > 0 && (long)rect.Bottom - rect.Top > 0;

    private static bool Contains(PixelRect outer, PixelRect inner)
        => inner.Left >= outer.Left && inner.Top >= outer.Top &&
           inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static int PositiveModulo(int value, int modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }

    private static void RejectNullElements<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        if (values.Any(static value => value is null))
            throw new ArgumentException("集合不能包含 null。", parameterName);
    }

    private static void EnsureUnique(IEnumerable<string> values, string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
                throw new ArgumentException($"集合包含重复键：{value}。", parameterName);
        }
    }
}

/// <summary>
/// 绑定某次 DesktopObservation 的 Explorer-native 几何快照。
/// GeometryFingerprint 只覆盖会影响施工面的 Windows 几何事实：显示拓扑、WorkArea、
/// spacing/phase、native slots 与 fixed obstacles；可移动图标当前位置不属于 geometry identity。
/// </summary>
public sealed record ExplorerWorkspaceGeometry
{
    private readonly IReadOnlyList<ExplorerDisplayGeometry> _displays;

    public Guid ObservationId { get; }
    public string GeometryFingerprint { get; }
    public bool IsAutoArrangeEnabled { get; }
    public PixelPoint Spacing { get; }
    public IReadOnlyList<ExplorerDisplayGeometry> Displays => _displays;

    public ExplorerWorkspaceGeometry(
        Guid observationId,
        string geometryFingerprint,
        bool isAutoArrangeEnabled,
        PixelPoint spacing,
        IEnumerable<ExplorerDisplayGeometry> displays)
    {
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为 Guid.Empty。", nameof(observationId));
        if (string.IsNullOrWhiteSpace(geometryFingerprint))
            throw new ArgumentException("GeometryFingerprint 不能为空。", nameof(geometryFingerprint));
        if (spacing.X <= 0 || spacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing));
        ArgumentNullException.ThrowIfNull(displays);

        var displayArray = displays.ToArray();
        RejectNullElements(displayArray, nameof(displays));
        if (displayArray.Length == 0)
            throw new ArgumentException("ExplorerWorkspaceGeometry 至少需要一块显示器。", nameof(displays));
        if (displayArray.Count(static display => display.IsPrimary) != 1)
            throw new ArgumentException("ExplorerWorkspaceGeometry 必须且只能有一块主显示器。", nameof(displays));
        EnsureUnique(displayArray.Select(static display => display.DisplayRef), nameof(displays));
        EnsureUnique(displayArray.Select(static display => display.DeviceName), nameof(displays));
        if (displayArray.Any(display => display.Spacing != spacing))
            throw new ArgumentException("当前几何快照中的显示器必须共享同一 Explorer spacing。", nameof(displays));

        ObservationId = observationId;
        GeometryFingerprint = geometryFingerprint.Trim();
        IsAutoArrangeEnabled = isAutoArrangeEnabled;
        Spacing = spacing;
        _displays = Array.AsReadOnly(displayArray);
    }

    public ExplorerDisplayGeometry ResolveDisplay(string displayRef)
        => _displays.FirstOrDefault(display =>
               string.Equals(display.DisplayRef, displayRef, StringComparison.Ordinal))
           ?? throw new KeyNotFoundException($"未知 DisplayRef \"{displayRef}\"。");

    private static void RejectNullElements<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        if (values.Any(static value => value is null))
            throw new ArgumentException("集合不能包含 null。", parameterName);
    }

    private static void EnsureUnique(IEnumerable<string> values, string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
                throw new ArgumentException($"集合包含重复键：{value}。", parameterName);
        }
    }
}
