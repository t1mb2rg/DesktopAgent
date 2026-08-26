namespace DesktopAgent.Core.DesktopLayouts.Workspaces;

/// <summary>
/// 一项已经通过本地严格校验的逻辑终态预览。它只描述起点和终点，
/// 不包含 StableId、物理像素、执行顺序、Writer command 或修复动作。
/// </summary>
public sealed record DesktopLogicalWorkspacePreviewItem
{
    public string ItemRef { get; }
    public string DisplayName { get; }
    public string SourceDisplayRef { get; }
    public DesktopLogicalGridCell SourceCell { get; }
    public bool IsMovable { get; }
    public bool IsGridAligned { get; }
    public DesktopDesiredItemState State { get; }
    public string? TargetDisplayRef { get; }
    public DesktopLogicalGridCell? TargetCell { get; }
    public string? ZoneKey { get; }
    public string? Reason { get; }

    public bool IsCrossDisplayMove =>
        State == DesktopDesiredItemState.Place &&
        !string.Equals(SourceDisplayRef, TargetDisplayRef, StringComparison.Ordinal);

    public bool IsPositionChange =>
        State == DesktopDesiredItemState.Place &&
        (IsCrossDisplayMove || SourceCell != TargetCell);

    internal DesktopLogicalWorkspacePreviewItem(
        DesktopObservedLogicalItem observed,
        DesktopDesiredWorkspaceItem desired)
    {
        ItemRef = observed.ItemRef;
        DisplayName = observed.DisplayName;
        SourceDisplayRef = observed.CurrentDisplayRef;
        SourceCell = observed.CurrentCell;
        IsMovable = observed.IsMovable;
        IsGridAligned = observed.IsGridAligned;
        State = desired.State;
        TargetDisplayRef = desired.TargetDisplayRef;
        TargetCell = desired.TargetCell;
        ZoneKey = desired.ZoneKey;
        Reason = desired.Reason;
    }
}

/// <summary>冻结预览中一块显示器的完整逻辑表面，包括零目标项目的源侧显示器。</summary>
public sealed record DesktopLogicalWorkspacePreviewDisplay
{
    public string DisplayRef { get; }
    public bool IsPrimary { get; }
    public DesktopLogicalTopologyRect TopologyBounds { get; }
    public DesktopLogicalGridSize GridSize { get; }
    public IReadOnlyList<DesktopDesiredWorkspaceZone> Zones { get; }
    public IReadOnlyList<DesktopDesiredWorkspaceReservedRegion> ReservedRegions { get; }
    public int SourceItemCount { get; }
    public int TargetPlacementCount { get; }
    public int InboundCount { get; }
    public int OutboundCount { get; }
    public int KeepCount { get; }
    public int ExcludeCount { get; }
    public int MoveCount { get; }
    public int AlreadyAtTargetCount { get; }

    internal DesktopLogicalWorkspacePreviewDisplay(
        DesktopObservedLogicalDisplay display,
        IEnumerable<DesktopDesiredWorkspaceZone> zones,
        IEnumerable<DesktopDesiredWorkspaceReservedRegion> reservedRegions,
        IReadOnlyList<DesktopLogicalWorkspacePreviewItem> items)
    {
        var zoneArray = zones.ToArray();
        var reservedArray = reservedRegions.ToArray();
        DisplayRef = display.DisplayRef;
        IsPrimary = display.IsPrimary;
        TopologyBounds = display.TopologyBounds;
        GridSize = display.GridSize;
        Zones = Array.AsReadOnly(zoneArray);
        ReservedRegions = Array.AsReadOnly(reservedArray);
        SourceItemCount = items.Count(item =>
            string.Equals(item.SourceDisplayRef, DisplayRef, StringComparison.Ordinal));
        TargetPlacementCount = items.Count(item =>
            item.State == DesktopDesiredItemState.Place &&
            string.Equals(item.TargetDisplayRef, DisplayRef, StringComparison.Ordinal));
        InboundCount = items.Count(item =>
            item.IsCrossDisplayMove &&
            string.Equals(item.TargetDisplayRef, DisplayRef, StringComparison.Ordinal));
        OutboundCount = items.Count(item =>
            item.IsCrossDisplayMove &&
            string.Equals(item.SourceDisplayRef, DisplayRef, StringComparison.Ordinal));
        KeepCount = items.Count(item =>
            item.State == DesktopDesiredItemState.Keep &&
            string.Equals(item.SourceDisplayRef, DisplayRef, StringComparison.Ordinal));
        ExcludeCount = items.Count(item =>
            item.State == DesktopDesiredItemState.Exclude &&
            string.Equals(item.SourceDisplayRef, DisplayRef, StringComparison.Ordinal));
        MoveCount = items.Count(item =>
            item.IsPositionChange &&
            string.Equals(item.TargetDisplayRef, DisplayRef, StringComparison.Ordinal));
        AlreadyAtTargetCount = items.Count(item =>
            item.State == DesktopDesiredItemState.Place &&
            !item.IsPositionChange &&
            string.Equals(item.TargetDisplayRef, DisplayRef, StringComparison.Ordinal));
    }
}

/// <summary>
/// 从同一 Observation 的 Provider-safe Workspace 与 ValidatedPlan 冻结出的只读多屏预览。
/// 这是 Preview 权限对象，不是 Execution authority；它不能被 Writer 消费。
/// </summary>
public sealed record DesktopLogicalWorkspaceFrozenPreview
{
    public Guid PreviewId { get; }
    public Guid PlanId { get; }
    public Guid SourceObservationId { get; }
    public string TopologyFingerprint { get; }
    public string GridFingerprint { get; }
    public IReadOnlyList<DesktopLogicalWorkspacePreviewDisplay> Displays { get; }
    public IReadOnlyList<DesktopLogicalWorkspacePreviewItem> Items { get; }
    public int CrossDisplayAssignmentCount { get; }
    public int MoveCount { get; }
    public int KeepCount { get; }
    public int ExcludeCount { get; }

    internal DesktopLogicalWorkspaceFrozenPreview(
        DesktopObservedLogicalWorkspace workspace,
        DesktopDesiredWorkspacePlan plan,
        IReadOnlyList<DesktopLogicalWorkspacePreviewDisplay> displays,
        IReadOnlyList<DesktopLogicalWorkspacePreviewItem> items)
    {
        PreviewId = Guid.NewGuid();
        PlanId = plan.PlanId;
        SourceObservationId = workspace.ObservationId;
        TopologyFingerprint = workspace.TopologyFingerprint;
        GridFingerprint = workspace.GridFingerprint;
        Displays = Array.AsReadOnly(displays.ToArray());
        Items = Array.AsReadOnly(items.ToArray());
        CrossDisplayAssignmentCount = items.Count(static item => item.IsCrossDisplayMove);
        MoveCount = items.Count(static item => item.IsPositionChange);
        KeepCount = items.Count(static item => item.State == DesktopDesiredItemState.Keep);
        ExcludeCount = items.Count(static item => item.State == DesktopDesiredItemState.Exclude);
    }
}

/// <summary>把严格校验后的逻辑终态确定性投影为只读多屏 Preview。</summary>
public static class DesktopLogicalWorkspaceFrozenPreviewProjector
{
    public static DesktopLogicalWorkspaceFrozenPreview Create(
        DesktopObservedLogicalWorkspace workspace,
        DesktopDesiredWorkspacePlan validatedPlan)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(validatedPlan);

        var validation = DesktopDesiredWorkspacePlanValidator.Validate(workspace, validatedPlan);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "Frozen Preview 只能从当前 Workspace 的有效完整终态创建。",
                nameof(validatedPlan));
        }

        var observedByRef = workspace.Items.ToDictionary(
            static item => item.ItemRef,
            StringComparer.Ordinal);
        var items = validatedPlan.Items
            .Select(desired => new DesktopLogicalWorkspacePreviewItem(
                observedByRef[desired.ItemRef],
                desired))
            .ToArray();
        var displays = workspace.Displays
            .Select(display => new DesktopLogicalWorkspacePreviewDisplay(
                display,
                validatedPlan.Zones.Where(zone => string.Equals(
                    zone.DisplayRef,
                    display.DisplayRef,
                    StringComparison.Ordinal)),
                validatedPlan.ReservedRegions.Where(region => string.Equals(
                    region.DisplayRef,
                    display.DisplayRef,
                    StringComparison.Ordinal)),
                items))
            .ToArray();

        return new DesktopLogicalWorkspaceFrozenPreview(
            workspace,
            validatedPlan,
            displays,
            items);
    }
}
