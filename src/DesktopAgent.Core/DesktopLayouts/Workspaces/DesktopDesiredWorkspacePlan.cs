namespace DesktopAgent.Core.DesktopLayouts.Workspaces;

public enum DesktopDesiredItemState
{
    Place = 0,
    Keep = 1,
    Exclude = 2,
}

/// <summary>AI 规划的一个显示器本地逻辑 Zone。</summary>
public sealed record DesktopDesiredWorkspaceZone
{
    public string ZoneKey { get; }
    public string Name { get; }
    public string DisplayRef { get; }
    public DesktopLogicalGridRect Bounds { get; }

    public DesktopDesiredWorkspaceZone(
        string zoneKey,
        string name,
        string displayRef,
        DesktopLogicalGridRect bounds)
    {
        if (string.IsNullOrWhiteSpace(zoneKey))
            throw new ArgumentException("ZoneKey 不能为空。", nameof(zoneKey));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name 不能为空。", nameof(name));
        if (string.IsNullOrWhiteSpace(displayRef))
            throw new ArgumentException("DisplayRef 不能为空。", nameof(displayRef));
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException("Bounds 必须是有效的正数网格矩形。", nameof(bounds));

        ZoneKey = zoneKey.Trim();
        Name = name.Trim();
        DisplayRef = displayRef.Trim();
        Bounds = bounds;
    }
}

/// <summary>AI 规划的一个显示器本地保留区域。</summary>
public sealed record DesktopDesiredWorkspaceReservedRegion
{
    public string RegionKey { get; }
    public string Purpose { get; }
    public string DisplayRef { get; }
    public DesktopLogicalGridRect Bounds { get; }

    public DesktopDesiredWorkspaceReservedRegion(
        string regionKey,
        string purpose,
        string displayRef,
        DesktopLogicalGridRect bounds)
    {
        if (string.IsNullOrWhiteSpace(regionKey))
            throw new ArgumentException("RegionKey 不能为空。", nameof(regionKey));
        if (string.IsNullOrWhiteSpace(purpose))
            throw new ArgumentException("Purpose 不能为空。", nameof(purpose));
        if (string.IsNullOrWhiteSpace(displayRef))
            throw new ArgumentException("DisplayRef 不能为空。", nameof(displayRef));
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException("Bounds 必须是有效的正数网格矩形。", nameof(bounds));

        RegionKey = regionKey.Trim();
        Purpose = purpose.Trim();
        DisplayRef = displayRef.Trim();
        Bounds = bounds;
    }
}

/// <summary>AI 对一个请求内 item_ref 给出的显式最终状态。</summary>
public sealed record DesktopDesiredWorkspaceItem
{
    public string ItemRef { get; }
    public DesktopDesiredItemState State { get; }
    public string? TargetDisplayRef { get; }
    public DesktopLogicalGridCell? TargetCell { get; }
    public string? ZoneKey { get; }
    public string? Reason { get; }

    public DesktopDesiredWorkspaceItem(
        string itemRef,
        DesktopDesiredItemState state,
        string? targetDisplayRef = null,
        DesktopLogicalGridCell? targetCell = null,
        string? zoneKey = null,
        string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(itemRef))
            throw new ArgumentException("ItemRef 不能为空。", nameof(itemRef));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (reason is not null && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason 应使用 null 表示省略，不能是空白。", nameof(reason));

        if (state == DesktopDesiredItemState.Place)
        {
            if (string.IsNullOrWhiteSpace(targetDisplayRef))
                throw new ArgumentException("Place 必须提供 TargetDisplayRef。", nameof(targetDisplayRef));
            if (!targetCell.HasValue)
                throw new ArgumentException("Place 必须提供 TargetCell。", nameof(targetCell));
            if (string.IsNullOrWhiteSpace(zoneKey))
                throw new ArgumentException("Place 必须提供 ZoneKey。", nameof(zoneKey));
        }
        else if (targetDisplayRef is not null || targetCell.HasValue || zoneKey is not null)
        {
            throw new ArgumentException("Keep / Exclude 不能携带 TargetDisplayRef、TargetCell 或 ZoneKey。");
        }

        ItemRef = itemRef.Trim();
        State = state;
        TargetDisplayRef = targetDisplayRef?.Trim();
        TargetCell = targetCell;
        ZoneKey = zoneKey?.Trim();
        Reason = reason?.Trim();
    }

    public static DesktopDesiredWorkspaceItem Place(
        string itemRef,
        string targetDisplayRef,
        DesktopLogicalGridCell targetCell,
        string zoneKey,
        string? reason = null)
        => new(itemRef, DesktopDesiredItemState.Place, targetDisplayRef, targetCell, zoneKey, reason);

    public static DesktopDesiredWorkspaceItem Keep(string itemRef, string? reason = null)
        => new(itemRef, DesktopDesiredItemState.Keep, reason: reason);

    public static DesktopDesiredWorkspaceItem Exclude(string itemRef, string? reason = null)
        => new(itemRef, DesktopDesiredItemState.Exclude, reason: reason);
}

/// <summary>
/// AI 提交的完整逻辑工作区终态。该对象不包含 StableId、物理像素、Candidate 或 Writer authority。
/// </summary>
public sealed record DesktopDesiredWorkspacePlan
{
    public const int CurrentSchemaVersion = 1;

    public Guid PlanId { get; }
    public int SchemaVersion { get; }
    public Guid SourceObservationId { get; }
    public string TopologyFingerprint { get; }
    public string GridFingerprint { get; }
    public IReadOnlyList<DesktopDesiredWorkspaceZone> Zones { get; }
    public IReadOnlyList<DesktopDesiredWorkspaceReservedRegion> ReservedRegions { get; }
    public IReadOnlyList<DesktopDesiredWorkspaceItem> Items { get; }

    public DesktopDesiredWorkspacePlan(
        Guid planId,
        Guid sourceObservationId,
        string topologyFingerprint,
        string gridFingerprint,
        IEnumerable<DesktopDesiredWorkspaceZone>? zones = null,
        IEnumerable<DesktopDesiredWorkspaceReservedRegion>? reservedRegions = null,
        IEnumerable<DesktopDesiredWorkspaceItem>? items = null,
        int schemaVersion = CurrentSchemaVersion)
    {
        if (planId == Guid.Empty)
            throw new ArgumentException("PlanId 不能为 Guid.Empty。", nameof(planId));
        if (sourceObservationId == Guid.Empty)
            throw new ArgumentException("SourceObservationId 不能为 Guid.Empty。", nameof(sourceObservationId));
        if (schemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (string.IsNullOrWhiteSpace(topologyFingerprint))
            throw new ArgumentException("TopologyFingerprint 不能为空。", nameof(topologyFingerprint));
        if (string.IsNullOrWhiteSpace(gridFingerprint))
            throw new ArgumentException("GridFingerprint 不能为空。", nameof(gridFingerprint));

        var zoneArray = zones?.ToArray() ?? [];
        var reservedArray = reservedRegions?.ToArray() ?? [];
        var itemArray = items?.ToArray() ?? [];
        if (zoneArray.Any(static zone => zone is null))
            throw new ArgumentException("Zones 不能包含 null。", nameof(zones));
        if (reservedArray.Any(static region => region is null))
            throw new ArgumentException("ReservedRegions 不能包含 null。", nameof(reservedRegions));
        if (itemArray.Any(static item => item is null))
            throw new ArgumentException("Items 不能包含 null。", nameof(items));

        PlanId = planId;
        SchemaVersion = schemaVersion;
        SourceObservationId = sourceObservationId;
        TopologyFingerprint = topologyFingerprint.Trim();
        GridFingerprint = gridFingerprint.Trim();
        Zones = Array.AsReadOnly(zoneArray);
        ReservedRegions = Array.AsReadOnly(reservedArray);
        Items = Array.AsReadOnly(itemArray);
    }
}
