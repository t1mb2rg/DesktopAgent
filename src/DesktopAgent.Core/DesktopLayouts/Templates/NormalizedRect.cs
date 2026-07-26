namespace DesktopAgent.Core.DesktopLayouts.Templates;

/// <summary>
/// 归一化坐标中的半开矩形 [Left, Right) × [Top, Bottom)。
/// 坐标范围为 0..10000 整数，用于表示桌面分区相对于目标显示器工作区的比例位置。
/// 使用 sealed record（引用类型），避免 default(T) 绕过四参数构造不变量。
/// </summary>
public sealed record NormalizedRect
{
    public const int Scale = 10_000;

    public int Left { get; }
    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public NormalizedRect(int left, int top, int right, int bottom)
    {
        if (left < 0)
            throw new ArgumentOutOfRangeException(nameof(left), left,
                $"Left 不能为负数，实际值 {left}。");
        if (top < 0)
            throw new ArgumentOutOfRangeException(nameof(top), top,
                $"Top 不能为负数，实际值 {top}。");
        if (right > Scale)
            throw new ArgumentOutOfRangeException(nameof(right), right,
                $"Right 不能超过 {Scale}，实际值 {right}。");
        if (bottom > Scale)
            throw new ArgumentOutOfRangeException(nameof(bottom), bottom,
                $"Bottom 不能超过 {Scale}，实际值 {bottom}。");
        if (right <= left)
            throw new ArgumentException(
                $"Right ({right}) 必须大于 Left ({left})，零宽或反向边界无效。");
        if (bottom <= top)
            throw new ArgumentException(
                $"Bottom ({bottom}) 必须大于 Top ({top})，零高或反向边界无效。");

        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    /// <summary>半开边界包含检查。[Left, Right) × [Top, Bottom)。</summary>
    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>半开矩形重叠检测：相邻边界不视为重叠。</summary>
    public bool Overlaps(NormalizedRect other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Left < other.Right && Right > other.Left
            && Top < other.Bottom && Bottom > other.Top;
    }

    /// <summary>other 是否完整包含在 this 内。</summary>
    public bool FullyContains(NormalizedRect other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Left >= Left && other.Top >= Top
            && other.Right <= Right && other.Bottom <= Bottom;
    }
}
