namespace DesktopAgent.Core.DesktopLayouts.Workspaces;

/// <summary>
/// Provider-safe 的显示器本地逻辑网格尺寸。不包含物理像素、DPI 或 Windows 设备身份。
/// </summary>
public readonly record struct DesktopLogicalGridSize
{
    public int Columns { get; }
    public int Rows { get; }

    public DesktopLogicalGridSize(int columns, int rows)
    {
        if (columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Columns 必须大于 0。");
        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Rows 必须大于 0。");

        Columns = columns;
        Rows = rows;
    }

    public long CellCount => checked((long)Columns * Rows);

    public bool Contains(DesktopLogicalGridCell cell) =>
        cell.Column >= 0 && cell.Column < Columns &&
        cell.Row >= 0 && cell.Row < Rows;

    public bool Contains(DesktopLogicalGridRect rect) =>
        rect.Column >= 0 && rect.Row >= 0 &&
        rect.RightExclusive <= Columns &&
        rect.BottomExclusive <= Rows;
}

/// <summary>显示器本地逻辑网格中的一个格位。</summary>
public readonly record struct DesktopLogicalGridCell
{
    public int Column { get; }
    public int Row { get; }

    public DesktopLogicalGridCell(int column, int row)
    {
        if (column < 0)
            throw new ArgumentOutOfRangeException(nameof(column), "Column 不能小于 0。");
        if (row < 0)
            throw new ArgumentOutOfRangeException(nameof(row), "Row 不能小于 0。");

        Column = column;
        Row = row;
    }
}

/// <summary>对齐到逻辑网格的半开矩形。</summary>
public readonly record struct DesktopLogicalGridRect
{
    public int Column { get; }
    public int Row { get; }
    public int Width { get; }
    public int Height { get; }

    public int RightExclusive => checked(Column + Width);
    public int BottomExclusive => checked(Row + Height);
    public long CellCount => checked((long)Width * Height);

    public DesktopLogicalGridRect(int column, int row, int width, int height)
    {
        if (column < 0)
            throw new ArgumentOutOfRangeException(nameof(column), "Column 不能小于 0。");
        if (row < 0)
            throw new ArgumentOutOfRangeException(nameof(row), "Row 不能小于 0。");
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width 必须大于 0。");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Height 必须大于 0。");

        _ = checked(column + width);
        _ = checked(row + height);

        Column = column;
        Row = row;
        Width = width;
        Height = height;
    }

    public bool Contains(DesktopLogicalGridCell cell) =>
        cell.Column >= Column && cell.Column < RightExclusive &&
        cell.Row >= Row && cell.Row < BottomExclusive;

    public bool Overlaps(DesktopLogicalGridRect other) =>
        Column < other.RightExclusive && RightExclusive > other.Column &&
        Row < other.BottomExclusive && BottomExclusive > other.Row;
}
