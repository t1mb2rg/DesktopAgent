using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Application.DesktopLayouts.Templates.Editing;

/// <summary>
/// Windows 物理显示器身份与 AI 可见语义 display_ref 之间的本地映射。
/// DeviceName 永远留在本地，Provider 只看到 primary / secondary_N。
/// </summary>
public sealed class DesktopSemanticDisplayMap
{
    public sealed record Entry(string DisplayRef, DisplayDescriptor Display, int CurrentItemCount);

    private readonly IReadOnlyDictionary<string, Entry> _byDeviceName;
    private readonly IReadOnlyDictionary<string, Entry> _byDisplayRef;

    public IReadOnlyList<Entry> Entries { get; }

    private DesktopSemanticDisplayMap(IReadOnlyList<Entry> entries)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        _byDeviceName = Entries.ToDictionary(
            static entry => entry.Display.DeviceName,
            StringComparer.Ordinal);
        _byDisplayRef = Entries.ToDictionary(
            static entry => entry.DisplayRef,
            StringComparer.Ordinal);
    }

    public static DesktopSemanticDisplayMap Create(DesktopObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Displays.Count == 0)
            throw new ArgumentException("DesktopObservation 至少需要一块显示器。", nameof(observation));

        var itemCounts = observation.Icons
            .Where(static icon => icon.MonitorDeviceName is not null)
            .GroupBy(static icon => icon.MonitorDeviceName!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

        var primary = observation.Displays.Where(static display => display.IsPrimary).ToArray();
        if (primary.Length != 1)
            throw new InvalidOperationException($"语义显示器映射要求恰好一块主显示器，当前 {primary.Length} 块。");

        var entries = new List<Entry>(observation.Displays.Count)
        {
            new(
                "primary",
                primary[0],
                itemCounts.TryGetValue(primary[0].DeviceName, out var primaryCount) ? primaryCount : 0),
        };

        var secondaryIndex = 1;
        foreach (var display in observation.Displays
                     .Where(static display => !display.IsPrimary)
                     .OrderBy(static display => display.Bounds.Left)
                     .ThenBy(static display => display.Bounds.Top)
                     .ThenBy(static display => display.DeviceName, StringComparer.Ordinal))
        {
            entries.Add(new Entry(
                $"secondary_{secondaryIndex++}",
                display,
                itemCounts.TryGetValue(display.DeviceName, out var count) ? count : 0));
        }

        return new DesktopSemanticDisplayMap(entries);
    }

    public Entry ResolveDisplayRef(string displayRef)
    {
        if (string.IsNullOrWhiteSpace(displayRef))
            throw new ArgumentException("DisplayRef 不能为空。", nameof(displayRef));
        if (!_byDisplayRef.TryGetValue(displayRef, out var entry))
            throw new KeyNotFoundException($"未知语义显示器引用 \"{displayRef}\"。");
        return entry;
    }

    public Entry ResolveDeviceName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("DeviceName 不能为空。", nameof(deviceName));
        if (!_byDeviceName.TryGetValue(deviceName, out var entry))
            throw new KeyNotFoundException($"当前 Observation 中不存在显示器 \"{deviceName}\"。");
        return entry;
    }

    public bool TryResolveDeviceName(string? deviceName, out Entry? entry)
    {
        if (deviceName is null)
        {
            entry = null;
            return false;
        }
        return _byDeviceName.TryGetValue(deviceName, out entry);
    }

    public IReadOnlyList<DesktopSemanticDisplayContext> ToProviderContexts()
        => Entries
            .Select(static entry => new DesktopSemanticDisplayContext(
                entry.DisplayRef,
                entry.Display.IsPrimary,
                entry.CurrentItemCount))
            .ToArray();
}
