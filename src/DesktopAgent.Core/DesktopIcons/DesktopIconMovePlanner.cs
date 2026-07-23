using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 相邻空位规划器。纯函数，不访问 Shell COM、显示器 API、数据库或 WPF。
/// </summary>
public static class DesktopIconMovePlanner
{
    /// <summary>
    /// 为图标规划一个相邻空位。候选方向按固定顺序检查：Right → Left → Down → Up。
    /// 每个候选位置以 spacing 为单元大小进行矩形碰撞检测，不使用坐标完全相等。
    /// </summary>
    /// <param name="originalPosition">图标原始位置。</param>
    /// <param name="workArea">目标显示器的工作区。</param>
    /// <param name="spacing">图标网格间距，X 和 Y 必须大于 0。</param>
    /// <param name="occupiedPositions">
    /// 已占用位置集合。调用者应自行排除待移动图标。
    /// </param>
    /// <returns>规划结果。</returns>
    /// <exception cref="ArgumentOutOfRangeException">spacing 或 workArea 无效。</exception>
    /// <exception cref="ArgumentException">originalPosition 不在 workArea 内。</exception>
    /// <exception cref="InvalidOperationException">所有候选方向均不可用。</exception>
    public static DesktopIconMovePlan PlanAdjacentMove(
        PixelPoint originalPosition,
        PixelRect workArea,
        PixelPoint spacing,
        IReadOnlyCollection<PixelPoint> occupiedPositions)
    {
        ArgumentNullException.ThrowIfNull(occupiedPositions);

        if (spacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "spacing.X 必须大于 0。");
        if (spacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "spacing.Y 必须大于 0。");
        if (workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea), "workArea 的宽高必须大于 0。");
        if (!workArea.Contains(originalPosition))
            throw new ArgumentException("originalPosition 必须位于 workArea 内。", nameof(originalPosition));

        // 将已占用位置转为矩形集合
        var occupiedRects = new List<PixelRect>(occupiedPositions.Count);
        foreach (var pos in occupiedPositions)
        {
            occupiedRects.Add(PixelRectFromPosition(pos, spacing));
        }

        // 候选方向：Right, Left, Down, Up（使用 checked 避免极端整数环绕）
        var candidates = new (string Direction, PixelPoint Position)[]
        {
            ("Right", checked(new PixelPoint(originalPosition.X + spacing.X, originalPosition.Y))),
            ("Left",  checked(new PixelPoint(originalPosition.X - spacing.X, originalPosition.Y))),
            ("Down",  checked(new PixelPoint(originalPosition.X, originalPosition.Y + spacing.Y))),
            ("Up",    checked(new PixelPoint(originalPosition.X, originalPosition.Y - spacing.Y))),
        };

        foreach (var (direction, candidate) in candidates)
        {
            var candidateRect = PixelRectFromPosition(candidate, spacing);

            // 候选必须完整位于 workArea 内
            if (candidateRect.Left < workArea.Left || candidateRect.Top < workArea.Top
                || candidateRect.Right > workArea.Right || candidateRect.Bottom > workArea.Bottom)
                continue;

            // 候选不能与任何已占用位置重叠
            var overlaps = false;
            foreach (var occupied in occupiedRects)
            {
                if (RectsOverlap(candidateRect, occupied))
                {
                    overlaps = true;
                    break;
                }
            }
            if (overlaps) continue;

            return new DesktopIconMovePlan(originalPosition, candidate, direction);
        }

        throw new InvalidOperationException(
            $"无法为图标找到安全相邻空位。\r\n" +
            $"原位置：({originalPosition.X}, {originalPosition.Y})\r\n" +
            $"workArea：({workArea.Left}, {workArea.Top}) - ({workArea.Right}, {workArea.Bottom})\r\n" +
            $"spacing：({spacing.X}, {spacing.Y})\r\n" +
            $"候选方向（Right/Left/Down/Up）均不可用。");
    }

    private static PixelRect PixelRectFromPosition(PixelPoint position, PixelPoint spacing) =>
        checked(new PixelRect(position.X, position.Y, position.X + spacing.X, position.Y + spacing.Y));

    private static bool RectsOverlap(PixelRect a, PixelRect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
