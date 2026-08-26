using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces;

/// <summary>
/// 一块显示器对 AI 暴露的逻辑描述。DisplayRef 是请求内语义引用，不是真实设备名。
/// </summary>
public sealed record DesktopObservedLogicalDisplay
{
    public string DisplayRef { get; }
    public bool IsPrimary { get; }
    public DesktopLogicalGridSize GridSize { get; }

    public DesktopObservedLogicalDisplay(
        string displayRef,
        bool isPrimary,
        DesktopLogicalGridSize gridSize)
    {
        if (string.IsNullOrWhiteSpace(displayRef))
            throw new ArgumentException("DisplayRef 不能为空。", nameof(displayRef));
        if (gridSize.Columns <= 0 || gridSize.Rows <= 0)
            throw new ArgumentException("GridSize 必须是有效的正数网格。", nameof(gridSize));

        DisplayRef = displayRef.Trim();
        IsPrimary = isPrimary;
        GridSize = gridSize;
    }
}

/// <summary>
/// 一个桌面项目对 AI 暴露的当前逻辑事实。ItemRef 是请求内临时引用，不是 StableId。
/// </summary>
public sealed record DesktopObservedLogicalItem
{
    public string ItemRef { get; }
    public string DisplayName { get; }
    public ItemKind ItemKind { get; }
    public string CurrentDisplayRef { get; }
    public DesktopLogicalGridCell CurrentCell { get; }
    public bool IsMovable { get; }
    public string? CategoryKey { get; }

    public DesktopObservedLogicalItem(
        string itemRef,
        string displayName,
        ItemKind itemKind,
        string currentDisplayRef,
        DesktopLogicalGridCell currentCell,
        bool isMovable,
        string? categoryKey = null)
    {
        if (string.IsNullOrWhiteSpace(itemRef))
            throw new ArgumentException("ItemRef 不能为空。", nameof(itemRef));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("DisplayName 不能为空。", nameof(displayName));
        if (string.IsNullOrWhiteSpace(currentDisplayRef))
            throw new ArgumentException("CurrentDisplayRef 不能为空。", nameof(currentDisplayRef));
        if (!Enum.IsDefined(itemKind))
            throw new ArgumentOutOfRangeException(nameof(itemKind));
        if (categoryKey is not null && string.IsNullOrWhiteSpace(categoryKey))
            throw new ArgumentException("CategoryKey 应使用 null 表示未知，不能是空白。", nameof(categoryKey));

        ItemRef = itemRef.Trim();
        DisplayName = displayName.Trim();
        ItemKind = itemKind;
        CurrentDisplayRef = currentDisplayRef.Trim();
        CurrentCell = currentCell;
        IsMovable = isMovable;
        CategoryKey = categoryKey?.Trim();
    }
}

/// <summary>
/// 从一次 fresh DesktopObservation 投影出的 Provider-safe 逻辑工作区。
/// 只包含当前事实，不包含目标位置、Candidate 或 Writer authority。
/// </summary>
public sealed record DesktopObservedLogicalWorkspace
{
    public Guid ObservationId { get; }
    public string TopologyFingerprint { get; }
    public string GridFingerprint { get; }
    public IReadOnlyList<DesktopObservedLogicalDisplay> Displays { get; }
    public IReadOnlyList<DesktopObservedLogicalItem> Items { get; }

    public DesktopObservedLogicalWorkspace(
        Guid observationId,
        string topologyFingerprint,
        string gridFingerprint,
        IEnumerable<DesktopObservedLogicalDisplay> displays,
        IEnumerable<DesktopObservedLogicalItem> items)
    {
        if (observationId == Guid.Empty)
            throw new ArgumentException("ObservationId 不能为 Guid.Empty。", nameof(observationId));
        if (string.IsNullOrWhiteSpace(topologyFingerprint))
            throw new ArgumentException("TopologyFingerprint 不能为空。", nameof(topologyFingerprint));
        if (string.IsNullOrWhiteSpace(gridFingerprint))
            throw new ArgumentException("GridFingerprint 不能为空。", nameof(gridFingerprint));
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(items);

        var displayArray = displays.ToArray();
        var itemArray = items.ToArray();
        if (displayArray.Length == 0)
            throw new ArgumentException("Displays 至少需要一块显示器。", nameof(displays));
        if (displayArray.Any(static display => display is null))
            throw new ArgumentException("Displays 不能包含 null。", nameof(displays));
        if (itemArray.Any(static item => item is null))
            throw new ArgumentException("Items 不能包含 null。", nameof(items));

        var duplicateDisplayRefs = displayArray
            .GroupBy(static display => display.DisplayRef, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicateDisplayRefs.Length > 0)
            throw new ArgumentException(
                $"Displays 存在重复 DisplayRef：{string.Join(", ", duplicateDisplayRefs)}。",
                nameof(displays));
        if (displayArray.Count(static display => display.IsPrimary) != 1)
            throw new ArgumentException("Displays 必须恰好包含一块主显示器。", nameof(displays));

        var duplicateItemRefs = itemArray
            .GroupBy(static item => item.ItemRef, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicateItemRefs.Length > 0)
            throw new ArgumentException(
                $"Items 存在重复 ItemRef：{string.Join(", ", duplicateItemRefs)}。",
                nameof(items));

        var displaysByRef = displayArray.ToDictionary(
            static display => display.DisplayRef,
            StringComparer.Ordinal);
        foreach (var item in itemArray)
        {
            if (!displaysByRef.TryGetValue(item.CurrentDisplayRef, out var display))
                throw new ArgumentException(
                    $"ItemRef \"{item.ItemRef}\" 引用了未知 CurrentDisplayRef \"{item.CurrentDisplayRef}\"。",
                    nameof(items));
            if (!display.GridSize.Contains(item.CurrentCell))
                throw new ArgumentException(
                    $"ItemRef \"{item.ItemRef}\" 的 CurrentCell 超出显示器 \"{item.CurrentDisplayRef}\" 网格。",
                    nameof(items));
        }

        ObservationId = observationId;
        TopologyFingerprint = topologyFingerprint.Trim();
        GridFingerprint = gridFingerprint.Trim();
        Displays = Array.AsReadOnly(displayArray);
        Items = Array.AsReadOnly(itemArray);
    }
}
