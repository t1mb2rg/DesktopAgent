using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Resolution;

/// <summary>
/// 纯 Core、无状态模板解析器。
/// 将归一化分区解析为像素坐标，计算理论容量和有效容量。
/// 不依赖 Shell COM、WPF、SQLite 或显示器 API。
/// </summary>
public static class DesktopLayoutTemplateResolver
{
    /// <summary>
    /// 解析模板到目标显示器工作区。
    /// </summary>
    /// <param name="template">已完成领域验证的模板。</param>
    /// <param name="workArea">目标显示器工作区。</param>
    /// <param name="iconSpacing">当前桌面图标网格间距。</param>
    public static ResolvedDesktopLayoutTemplate Resolve(
        DesktopLayoutTemplate template,
        PixelRect workArea,
        PixelPoint iconSpacing)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));

        // ═══ WorkArea 验证（long 中间运算避免 int 溢出）═══
        long waWidth = (long)workArea.Right - workArea.Left;
        long waHeight = (long)workArea.Bottom - workArea.Top;

        if (waWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea 宽度必须大于 0。WorkArea=({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom})。");
        if (waHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea 高度必须大于 0。WorkArea=({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom})。");
        if (waWidth > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea 宽度 ({waWidth}) 超过 int.MaxValue。WorkArea=({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom})。");
        if (waHeight > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(workArea),
                $"WorkArea 高度 ({waHeight}) 超过 int.MaxValue。WorkArea=({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom})。");

        // ═══ Spacing 验证 ═══
        if (iconSpacing.X <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.X 必须大于 0，实际 {iconSpacing.X}。");
        if (iconSpacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(iconSpacing),
                $"IconSpacing.Y 必须大于 0，实际 {iconSpacing.Y}。");

        // ═══ 解析每个 Zone ═══
        var resolvedZones = new List<ResolvedDesktopLayoutZone>(template.Zones.Count);
        foreach (var zone in template.Zones)
        {
            var resolved = ResolveZone(zone, template.TemplateId, workArea, waWidth, waHeight, iconSpacing);
            resolvedZones.Add(resolved);
        }

        return new ResolvedDesktopLayoutTemplate(template, workArea, iconSpacing, resolvedZones);
    }

    /// <summary>
    /// 统一整数映射：pixel = workAreaStart + floor(normalizedValue * workAreaLength / Scale)。
    /// 所有边界使用完全相同的公式。
    /// </summary>
    private static int MapBoundary(int normalizedValue, int workAreaStart, long workAreaLength)
    {
        long numerator = (long)normalizedValue * workAreaLength;
        long offset = numerator / NormalizedRect.Scale; // floor division for non-negative values
        long pixel = workAreaStart + offset;
        return checked((int)pixel);
    }

    private static ResolvedDesktopLayoutZone ResolveZone(
        DesktopLayoutZone zone,
        Guid templateId,
        PixelRect workArea,
        long waWidth,
        long waHeight,
        PixelPoint iconSpacing)
    {
        var bounds = zone.Bounds;

        int pixelLeft = MapBoundary(bounds.Left, workArea.Left, waWidth);
        int pixelTop = MapBoundary(bounds.Top, workArea.Top, waHeight);
        int pixelRight = MapBoundary(bounds.Right, workArea.Left, waWidth);
        int pixelBottom = MapBoundary(bounds.Bottom, workArea.Top, waHeight);

        // 验证未坍缩
        if (pixelRight <= pixelLeft)
            throw new InvalidOperationException(
                $"TemplateId \"{templateId:D}\" ZoneId \"{zone.ZoneId:D}\" " +
                $"Name=\"{zone.Name}\" 归一化矩形 ({bounds.Left},{bounds.Top})-({bounds.Right},{bounds.Bottom}) " +
                $"映射到 WorkArea ({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom}) 后 " +
                $"宽度为零或负数：Left={pixelLeft}, Right={pixelRight}。");
        if (pixelBottom <= pixelTop)
            throw new InvalidOperationException(
                $"TemplateId \"{templateId:D}\" ZoneId \"{zone.ZoneId:D}\" " +
                $"Name=\"{zone.Name}\" 归一化矩形 ({bounds.Left},{bounds.Top})-({bounds.Right},{bounds.Bottom}) " +
                $"映射到 WorkArea ({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom}) 后 " +
                $"高度为零或负数：Top={pixelTop}, Bottom={pixelBottom}。");

        var pixelBounds = new PixelRect(pixelLeft, pixelTop, pixelRight, pixelBottom);

        // 容量计算
        int columnCount = pixelBounds.Width / iconSpacing.X;
        int rowCount = pixelBounds.Height / iconSpacing.Y;
        long theoreticalCapacity = checked((long)columnCount * rowCount);

        long effectiveCapacity = zone.MaxItemCount.HasValue
            ? Math.Min(theoreticalCapacity, (long)zone.MaxItemCount.Value)
            : theoreticalCapacity;

        return new ResolvedDesktopLayoutZone(
            zone, pixelBounds, columnCount, rowCount,
            theoreticalCapacity, effectiveCapacity);
    }
}
