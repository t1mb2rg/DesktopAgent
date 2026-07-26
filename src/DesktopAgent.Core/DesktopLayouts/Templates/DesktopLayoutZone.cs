namespace DesktopAgent.Core.DesktopLayouts.Templates;

/// <summary>
/// 桌面布局模板中的一个分区。使用归一化坐标描述相对于目标显示器工作区的位置。
/// 不含 WPF、颜色、画刷或 UI 状态。
/// </summary>
public sealed record DesktopLayoutZone
{
    public Guid ZoneId { get; }
    public string Name { get; }
    public NormalizedRect Bounds { get; }
    public int SortOrder { get; }
    public ZonePlacementFlow PlacementFlow { get; }
    public int? MaxItemCount { get; }

    public DesktopLayoutZone(
        Guid zoneId,
        string name,
        NormalizedRect bounds,
        int sortOrder,
        ZonePlacementFlow placementFlow,
        int? maxItemCount)
    {
        if (zoneId == Guid.Empty)
            throw new ArgumentException(
                "ZoneId 不能为 Guid.Empty。", nameof(zoneId));
        if (name is null)
            throw new ArgumentNullException(nameof(name));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Name 不能为 null、空字符串或空白。", nameof(name));
        if (bounds is null)
            throw new ArgumentNullException(nameof(bounds));
        if (sortOrder < 0)
            throw new ArgumentOutOfRangeException(nameof(sortOrder), sortOrder,
                "SortOrder 不能为负数。");
        if (!Enum.IsDefined(placementFlow))
            throw new ArgumentOutOfRangeException(nameof(placementFlow), placementFlow,
                $"ZonePlacementFlow 值 {placementFlow} 未定义。");
        if (maxItemCount.HasValue && maxItemCount.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxItemCount), maxItemCount,
                "MaxItemCount 为 null 或大于 0。");

        ZoneId = zoneId;
        Name = name;
        Bounds = bounds;
        SortOrder = sortOrder;
        PlacementFlow = placementFlow;
        MaxItemCount = maxItemCount;
    }
}
