using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// 将有限的 AI 布局模式确定性转换为 M2 可理解的归一化矩形。
/// 这里才产生几何数字；AI 本身不提供坐标。
/// </summary>
public static class DesktopZoneLayoutPatternGeometry
{
    public const int MaxZoneCount = 8;

    public static IReadOnlyList<NormalizedRect> CreateBounds(
        DesktopZoneLayoutPattern pattern,
        int zoneCount)
    {
        if (!Enum.IsDefined(pattern))
            throw new ArgumentOutOfRangeException(nameof(pattern));
        if (zoneCount is < 1 or > MaxZoneCount)
            throw new ArgumentOutOfRangeException(nameof(zoneCount));

        var bounds = pattern switch
        {
            DesktopZoneLayoutPattern.Grid => CreateGrid(zoneCount),
            DesktopZoneLayoutPattern.Columns => CreateColumns(zoneCount),
            DesktopZoneLayoutPattern.Rows => CreateRows(zoneCount),
            DesktopZoneLayoutPattern.PrimaryLeft => CreatePrimaryVertical(zoneCount, primaryOnStart: true),
            DesktopZoneLayoutPattern.PrimaryRight => CreatePrimaryVertical(zoneCount, primaryOnStart: false),
            DesktopZoneLayoutPattern.PrimaryTop => CreatePrimaryHorizontal(zoneCount, primaryOnStart: true),
            DesktopZoneLayoutPattern.PrimaryBottom => CreatePrimaryHorizontal(zoneCount, primaryOnStart: false),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
        };

        return Array.AsReadOnly(bounds);
    }

    private static NormalizedRect[] CreateColumns(int count)
    {
        var result = new NormalizedRect[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = new NormalizedRect(
                i * NormalizedRect.Scale / count,
                0,
                (i + 1) * NormalizedRect.Scale / count,
                NormalizedRect.Scale);
        }
        return result;
    }

    private static NormalizedRect[] CreateRows(int count)
    {
        var result = new NormalizedRect[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = new NormalizedRect(
                0,
                i * NormalizedRect.Scale / count,
                NormalizedRect.Scale,
                (i + 1) * NormalizedRect.Scale / count);
        }
        return result;
    }

    private static NormalizedRect[] CreateGrid(int count)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling((double)count / columns);
        var result = new NormalizedRect[count];

        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            result[i] = new NormalizedRect(
                column * NormalizedRect.Scale / columns,
                row * NormalizedRect.Scale / rows,
                (column + 1) * NormalizedRect.Scale / columns,
                (row + 1) * NormalizedRect.Scale / rows);
        }

        return result;
    }

    private static NormalizedRect[] CreatePrimaryVertical(int count, bool primaryOnStart)
    {
        if (count == 1)
            return [new NormalizedRect(0, 0, NormalizedRect.Scale, NormalizedRect.Scale)];

        var half = NormalizedRect.Scale / 2;
        var result = new NormalizedRect[count];
        result[0] = primaryOnStart
            ? new NormalizedRect(0, 0, half, NormalizedRect.Scale)
            : new NormalizedRect(half, 0, NormalizedRect.Scale, NormalizedRect.Scale);

        var remaining = count - 1;
        var secondaryLeft = primaryOnStart ? half : 0;
        var secondaryRight = primaryOnStart ? NormalizedRect.Scale : half;
        for (var i = 0; i < remaining; i++)
        {
            result[i + 1] = new NormalizedRect(
                secondaryLeft,
                i * NormalizedRect.Scale / remaining,
                secondaryRight,
                (i + 1) * NormalizedRect.Scale / remaining);
        }

        return result;
    }

    private static NormalizedRect[] CreatePrimaryHorizontal(int count, bool primaryOnStart)
    {
        if (count == 1)
            return [new NormalizedRect(0, 0, NormalizedRect.Scale, NormalizedRect.Scale)];

        var half = NormalizedRect.Scale / 2;
        var result = new NormalizedRect[count];
        result[0] = primaryOnStart
            ? new NormalizedRect(0, 0, NormalizedRect.Scale, half)
            : new NormalizedRect(0, half, NormalizedRect.Scale, NormalizedRect.Scale);

        var remaining = count - 1;
        var secondaryTop = primaryOnStart ? half : 0;
        var secondaryBottom = primaryOnStart ? NormalizedRect.Scale : half;
        for (var i = 0; i < remaining; i++)
        {
            result[i + 1] = new NormalizedRect(
                i * NormalizedRect.Scale / remaining,
                secondaryTop,
                (i + 1) * NormalizedRect.Scale / remaining,
                secondaryBottom);
        }

        return result;
    }
}
