using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.Productization;

/// <summary>
/// Radar Mark 的离散密度档位。Empty cell 不进入 snapshot。
/// </summary>
public enum DesktopRadarDensityLevel
{
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// 一个低分辨率 Radar Cell 的 presentation-only 密度摘要。
/// 不代表单个桌面项目，也不携带真实图标坐标或身份。
/// </summary>
public sealed record DesktopRadarDensityCell
{
    public int Column { get; }
    public int Row { get; }
    public DesktopRadarDensityLevel Density { get; }

    public DesktopRadarDensityCell(
        int column,
        int row,
        DesktopRadarDensityLevel density)
    {
        if (column < 0)
            throw new ArgumentOutOfRangeException(nameof(column));
        if (row < 0)
            throw new ArgumentOutOfRangeException(nameof(row));
        if (!Enum.IsDefined(density))
            throw new ArgumentOutOfRangeException(nameof(density));

        Column = column;
        Row = row;
        Density = density;
    }
}

/// <summary>
/// 一块显示器在 Desktop Radar 中的低分辨率空间摘要。
/// Presentation-only. Must not be used as execution evidence.
/// </summary>
public sealed record DesktopRadarDisplaySnapshot
{
    public string DisplayKey { get; }
    public PixelRect Bounds { get; }
    public bool IsPrimary { get; }
    public int ItemCount { get; }
    public int GridColumns { get; }
    public int GridRows { get; }
    public IReadOnlyList<DesktopRadarDensityCell> DensityCells { get; }

    public DesktopRadarDisplaySnapshot(
        string displayKey,
        PixelRect bounds,
        bool isPrimary,
        int itemCount,
        int gridColumns,
        int gridRows,
        IReadOnlyList<DesktopRadarDensityCell> densityCells)
    {
        if (string.IsNullOrWhiteSpace(displayKey))
            throw new ArgumentException("DisplayKey 不能为空。", nameof(displayKey));
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException("Display Bounds 必须具有正宽高。", nameof(bounds));
        if (itemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount));
        if (gridColumns <= 0)
            throw new ArgumentOutOfRangeException(nameof(gridColumns));
        if (gridRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(gridRows));
        ArgumentNullException.ThrowIfNull(densityCells);

        var cells = densityCells.ToArray();
        foreach (var cell in cells)
        {
            if (cell.Column >= gridColumns || cell.Row >= gridRows)
            {
                throw new ArgumentException(
                    $"Density cell ({cell.Column}, {cell.Row}) 超出 {gridColumns}x{gridRows} grid。",
                    nameof(densityCells));
            }
        }

        var duplicateCells = cells
            .GroupBy(cell => (cell.Column, cell.Row))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateCells.Length > 0)
            throw new ArgumentException("DensityCells 不允许包含重复 cell。", nameof(densityCells));

        if (cells.Length > itemCount)
        {
            throw new ArgumentException(
                "非空 Density Cell 数量不能大于该 Display 的项目总数。",
                nameof(densityCells));
        }

        DisplayKey = displayKey;
        Bounds = bounds;
        IsPrimary = isPrimary;
        ItemCount = itemCount;
        GridColumns = gridColumns;
        GridRows = gridRows;
        DensityCells = Array.AsReadOnly(cells);
    }
}

/// <summary>
/// 从 Fresh DesktopObservation 压缩得到的 Desktop Radar 展示快照。
///
/// Presentation-only. Must not be used as execution evidence.
/// 不保存文件名、路径、PIDL、稳定 item identity、真实图标坐标或 Writer payload。
/// </summary>
public sealed record DesktopRadarSnapshot
{
    public Guid ObservationId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string DisplayTopologyFingerprint { get; }
    public Guid? LayoutTemplateId { get; }
    public int? LayoutVersion { get; }
    public IReadOnlyList<DesktopRadarDisplaySnapshot> Displays { get; }
    public int TotalItemCount { get; }

    public DesktopRadarSnapshot(
        Guid observationId,
        DateTimeOffset observedAtUtc,
        string displayTopologyFingerprint,
        Guid? layoutTemplateId,
        int? layoutVersion,
        IReadOnlyList<DesktopRadarDisplaySnapshot> displays)
    {
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为 Guid.Empty。", nameof(observationId));
        if (string.IsNullOrWhiteSpace(displayTopologyFingerprint))
        {
            throw new ArgumentException(
                "DisplayTopologyFingerprint 不能为空。",
                nameof(displayTopologyFingerprint));
        }
        if (layoutTemplateId.HasValue != layoutVersion.HasValue)
        {
            throw new ArgumentException(
                "LayoutTemplateId 与 LayoutVersion 必须同时存在或同时为空。",
                nameof(layoutVersion));
        }
        if (layoutTemplateId == Guid.Empty)
            throw new ArgumentException("LayoutTemplateId 不能为 Guid.Empty。", nameof(layoutTemplateId));
        if (layoutVersion is <= 0)
            throw new ArgumentOutOfRangeException(nameof(layoutVersion));
        ArgumentNullException.ThrowIfNull(displays);

        var displayArray = displays.ToArray();
        if (displayArray.Length == 0)
            throw new ArgumentException("Radar Snapshot 至少需要一块 Display。", nameof(displays));

        var duplicateDisplayKeys = displayArray
            .GroupBy(display => display.DisplayKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateDisplayKeys.Length > 0)
            throw new ArgumentException("Radar Snapshot 不允许重复 DisplayKey。", nameof(displays));

        ObservationId = observationId;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        DisplayTopologyFingerprint = displayTopologyFingerprint;
        LayoutTemplateId = layoutTemplateId;
        LayoutVersion = layoutVersion;
        Displays = Array.AsReadOnly(displayArray);
        TotalItemCount = displayArray.Sum(display => display.ItemCount);
    }
}
