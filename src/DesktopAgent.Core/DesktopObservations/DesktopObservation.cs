using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Core.DesktopObservations;

/// <summary>
/// 一次成功、完整、可作为当前事实依据的桌面观察。
/// 不包含 COM/PIDL/HWND/WPF/SQLite 类型；集合在构造时防御性复制。
/// </summary>
public sealed record DesktopObservation
{
    public Guid ObservationId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public IReadOnlyList<DisplayDescriptor> Displays { get; }
    public DesktopIconPositioningCapabilities PositioningCapabilities { get; }
    public IReadOnlyList<DesktopIconDescriptor> Icons { get; }
    public ConnectionMethod IconReadConnectionMethod { get; }
    public TimeSpan IconReadTime { get; }

    public DesktopObservation(
        Guid observationId,
        DateTimeOffset observedAtUtc,
        IReadOnlyList<DisplayDescriptor> displays,
        DesktopIconPositioningCapabilities positioningCapabilities,
        IReadOnlyList<DesktopIconDescriptor> icons,
        ConnectionMethod iconReadConnectionMethod,
        TimeSpan iconReadTime)
    {
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为 Guid.Empty。", nameof(observationId));
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(positioningCapabilities);
        ArgumentNullException.ThrowIfNull(icons);
        if (iconReadTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(iconReadTime));

        var displayArray = displays.ToArray();
        var duplicateDisplayNames = displayArray
            .GroupBy(display => display.DeviceName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (duplicateDisplayNames.Length > 0)
        {
            throw new ArgumentException(
                $"DesktopObservation 中存在重复显示器 DeviceName：{string.Join(", ", duplicateDisplayNames)}。",
                nameof(displays));
        }

        var iconArray = icons.ToArray();
        var duplicateStableIds = iconArray
            .GroupBy(icon => icon.StableId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicateStableIds.Length > 0)
        {
            throw new ArgumentException(
                $"DesktopObservation 中存在重复 StableId：{string.Join(", ", duplicateStableIds)}。",
                nameof(icons));
        }

        ObservationId = observationId;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        Displays = Array.AsReadOnly(displayArray);
        PositioningCapabilities = positioningCapabilities;
        Icons = Array.AsReadOnly(iconArray);
        IconReadConnectionMethod = iconReadConnectionMethod;
        IconReadTime = iconReadTime;
    }
}
