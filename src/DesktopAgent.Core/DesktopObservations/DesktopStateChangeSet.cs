using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopObservations;

[Flags]
public enum DesktopItemFactChange
{
    None = 0,
    DisplayName = 1 << 0,
    ParsingName = 1 << 1,
    ItemKind = 1 << 2,
    Position = 1 << 3,
    MonitorAssignment = 1 << 4,
    SystemItemFlag = 1 << 5,
    FileSystemItemFlag = 1 << 6,
}

public enum DesktopItemStateChangeKind
{
    Added = 0,
    Removed = 1,
    Changed = 2,
}

public sealed record DesktopItemStateChange
{
    public DesktopItemStateChangeKind Kind { get; }
    public string StableId { get; }
    public DesktopIconDescriptor? Previous { get; }
    public DesktopIconDescriptor? Current { get; }
    public DesktopItemFactChange FactChanges { get; }

    public bool IsMoved =>
        FactChanges.HasFlag(DesktopItemFactChange.Position)
        || FactChanges.HasFlag(DesktopItemFactChange.MonitorAssignment);

    public DesktopItemStateChange(
        DesktopItemStateChangeKind kind,
        string stableId,
        DesktopIconDescriptor? previous,
        DesktopIconDescriptor? current,
        DesktopItemFactChange factChanges)
    {
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));

        switch (kind)
        {
            case DesktopItemStateChangeKind.Added when previous is not null || current is null:
                throw new ArgumentException("Added 必须只有 Current。", nameof(kind));
            case DesktopItemStateChangeKind.Removed when previous is null || current is not null:
                throw new ArgumentException("Removed 必须只有 Previous。", nameof(kind));
            case DesktopItemStateChangeKind.Changed when previous is null || current is null:
                throw new ArgumentException("Changed 必须同时提供 Previous 与 Current。", nameof(kind));
            case DesktopItemStateChangeKind.Changed when factChanges == DesktopItemFactChange.None:
                throw new ArgumentException("Changed 必须至少包含一个 FactChange。", nameof(factChanges));
            case DesktopItemStateChangeKind.Added or DesktopItemStateChangeKind.Removed
                when factChanges != DesktopItemFactChange.None:
                throw new ArgumentException("Added / Removed 不使用 FactChanges。", nameof(factChanges));
        }

        if (previous is not null
            && !string.Equals(previous.StableId, stableId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Previous.StableId 与 StableId 不一致。", nameof(previous));
        }

        if (current is not null
            && !string.Equals(current.StableId, stableId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Current.StableId 与 StableId 不一致。", nameof(current));
        }

        Kind = kind;
        StableId = stableId;
        Previous = previous;
        Current = current;
        FactChanges = factChanges;
    }
}

public sealed record DesktopTopologyChange
{
    public IReadOnlyList<DisplayDescriptor> PreviousDisplays { get; }
    public IReadOnlyList<DisplayDescriptor> CurrentDisplays { get; }

    public DesktopTopologyChange(
        IReadOnlyList<DisplayDescriptor> previousDisplays,
        IReadOnlyList<DisplayDescriptor> currentDisplays)
    {
        ArgumentNullException.ThrowIfNull(previousDisplays);
        ArgumentNullException.ThrowIfNull(currentDisplays);

        PreviousDisplays = Array.AsReadOnly(previousDisplays.ToArray());
        CurrentDisplays = Array.AsReadOnly(currentDisplays.ToArray());
    }
}

public sealed record DesktopPositioningCapabilitiesChange(
    bool PreviousAutoArrangeEnabled,
    bool CurrentAutoArrangeEnabled,
    PixelPoint PreviousSpacing,
    PixelPoint CurrentSpacing)
{
    public bool AutoArrangeChanged =>
        PreviousAutoArrangeEnabled != CurrentAutoArrangeEnabled;

    public bool IconSpacingChanged => PreviousSpacing != CurrentSpacing;

    public bool HasChanges => AutoArrangeChanged || IconSpacingChanged;
}

/// <summary>
/// 两份稳定 DesktopObservation 之间的事实变化。
/// 与 M2 Preview Diff（当前布局 vs 模板目标）语义完全不同：这里仅描述“真实桌面刚刚发生了什么”。
/// </summary>
public sealed record DesktopStateChangeSet
{
    public Guid PreviousObservationId { get; }
    public Guid CurrentObservationId { get; }
    public DesktopTopologyChange? TopologyChange { get; }
    public DesktopPositioningCapabilitiesChange? PositioningCapabilitiesChange { get; }
    public IReadOnlyList<DesktopItemStateChange> ItemChanges { get; }

    public bool HasChanges =>
        TopologyChange is not null
        || PositioningCapabilitiesChange is not null
        || ItemChanges.Count != 0;

    public DesktopStateChangeSet(
        Guid previousObservationId,
        Guid currentObservationId,
        DesktopTopologyChange? topologyChange,
        DesktopPositioningCapabilitiesChange? positioningCapabilitiesChange,
        IReadOnlyList<DesktopItemStateChange> itemChanges)
    {
        if (previousObservationId == Guid.Empty)
            throw new ArgumentException("PreviousObservationId 不能为空。", nameof(previousObservationId));
        if (currentObservationId == Guid.Empty)
            throw new ArgumentException("CurrentObservationId 不能为空。", nameof(currentObservationId));
        ArgumentNullException.ThrowIfNull(itemChanges);
        if (positioningCapabilitiesChange is { HasChanges: false })
            throw new ArgumentException("无变化的 capabilities 不应进入 ChangeSet。", nameof(positioningCapabilitiesChange));

        PreviousObservationId = previousObservationId;
        CurrentObservationId = currentObservationId;
        TopologyChange = topologyChange;
        PositioningCapabilitiesChange = positioningCapabilitiesChange;
        ItemChanges = Array.AsReadOnly(itemChanges.ToArray());
    }
}

public static class DesktopStateDiffer
{
    public static DesktopStateChangeSet Compare(
        DesktopObservation previous,
        DesktopObservation current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var topologyChange = DisplayTopologyComparer.AreEquivalent(previous.Displays, current.Displays)
            ? null
            : new DesktopTopologyChange(previous.Displays, current.Displays);

        DesktopPositioningCapabilitiesChange? capabilitiesChange = null;
        var candidateCapabilitiesChange = new DesktopPositioningCapabilitiesChange(
            previous.PositioningCapabilities.IsAutoArrangeEnabled,
            current.PositioningCapabilities.IsAutoArrangeEnabled,
            previous.PositioningCapabilities.Spacing,
            current.PositioningCapabilities.Spacing);
        if (candidateCapabilitiesChange.HasChanges)
            capabilitiesChange = candidateCapabilitiesChange;

        var previousById = previous.Icons.ToDictionary(icon => icon.StableId, StringComparer.Ordinal);
        var currentById = current.Icons.ToDictionary(icon => icon.StableId, StringComparer.Ordinal);
        var stableIds = previousById.Keys
            .Union(currentById.Keys, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);

        var itemChanges = new List<DesktopItemStateChange>();
        foreach (var stableId in stableIds)
        {
            var hasPrevious = previousById.TryGetValue(stableId, out var previousIcon);
            var hasCurrent = currentById.TryGetValue(stableId, out var currentIcon);

            if (!hasPrevious)
            {
                itemChanges.Add(new DesktopItemStateChange(
                    DesktopItemStateChangeKind.Added,
                    stableId,
                    null,
                    currentIcon,
                    DesktopItemFactChange.None));
                continue;
            }

            if (!hasCurrent)
            {
                itemChanges.Add(new DesktopItemStateChange(
                    DesktopItemStateChangeKind.Removed,
                    stableId,
                    previousIcon,
                    null,
                    DesktopItemFactChange.None));
                continue;
            }

            var factChanges = GetFactChanges(previousIcon!, currentIcon!);
            if (factChanges == DesktopItemFactChange.None)
                continue;

            itemChanges.Add(new DesktopItemStateChange(
                DesktopItemStateChangeKind.Changed,
                stableId,
                previousIcon,
                currentIcon,
                factChanges));
        }

        return new DesktopStateChangeSet(
            previous.ObservationId,
            current.ObservationId,
            topologyChange,
            capabilitiesChange,
            itemChanges);
    }

    private static DesktopItemFactChange GetFactChanges(
        DesktopIconDescriptor previous,
        DesktopIconDescriptor current)
    {
        var changes = DesktopItemFactChange.None;

        if (!string.Equals(previous.DisplayName, current.DisplayName, StringComparison.Ordinal))
            changes |= DesktopItemFactChange.DisplayName;
        if (!string.Equals(previous.ParsingName, current.ParsingName, StringComparison.Ordinal))
            changes |= DesktopItemFactChange.ParsingName;
        if (previous.ItemKind != current.ItemKind)
            changes |= DesktopItemFactChange.ItemKind;
        if (previous.PixelPosition != current.PixelPosition)
            changes |= DesktopItemFactChange.Position;
        if (!string.Equals(previous.MonitorDeviceName, current.MonitorDeviceName, StringComparison.Ordinal))
            changes |= DesktopItemFactChange.MonitorAssignment;
        if (previous.IsSystemItem != current.IsSystemItem)
            changes |= DesktopItemFactChange.SystemItemFlag;
        if (previous.IsFileSystemItem != current.IsFileSystemItem)
            changes |= DesktopItemFactChange.FileSystemItemFlag;

        return changes;
    }
}
