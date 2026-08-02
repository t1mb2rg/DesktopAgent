using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

/// <summary>
/// 预览图标行视图模型 — 不可变 sealed class。
/// 所有属性 get-only，只能通过静态工厂方法 From 构造。
/// </summary>
public sealed class LayoutPreviewItemRow
{
    public DesktopIconSnapshotItem Icon { get; }
    public int Ordinal { get; }
    public string DisplayName { get; }
    public string StableIdTruncated { get; }
    public string StateText { get; }
    public int CurrentX { get; }
    public int CurrentY { get; }
    public string TargetX { get; }
    public string TargetY { get; }
    public string DeltaX { get; }
    public string DeltaY { get; }
    public string ZoneName { get; }
    public string SlotIndex { get; }
    public string RowIndex { get; }
    public string ColumnIndex { get; }
    public DesktopLayoutPreviewItemState State { get; }

    private LayoutPreviewItemRow(
        DesktopIconSnapshotItem icon,
        int ordinal, string displayName, string stableIdTruncated,
        string stateText, int currentX, int currentY,
        string targetX, string targetY, string deltaX, string deltaY,
        string zoneName, string slotIndex, string rowIndex, string columnIndex,
        DesktopLayoutPreviewItemState state)
    {
        Icon = icon ?? throw new ArgumentNullException(nameof(icon));
        Ordinal = ordinal;
        DisplayName = displayName ?? "";
        StableIdTruncated = stableIdTruncated ?? "";
        StateText = stateText ?? "";
        CurrentX = currentX;
        CurrentY = currentY;
        TargetX = targetX;
        TargetY = targetY;
        DeltaX = deltaX;
        DeltaY = deltaY;
        ZoneName = zoneName;
        SlotIndex = slotIndex;
        RowIndex = rowIndex;
        ColumnIndex = columnIndex;
        State = state;
    }

    public static LayoutPreviewItemRow From(DesktopLayoutPreviewItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));

        string tx = "—", ty = "—", dx = "—", dy = "—";
        string zn = "—", si = "—", ri = "—", ci = "—";

        if (item.IsAssigned && item.Placement is not null)
        {
            tx = item.TargetPosition!.Value.X.ToString();
            ty = item.TargetPosition!.Value.Y.ToString();
            dx = item.DeltaX?.ToString() ?? "—";
            dy = item.DeltaY?.ToString() ?? "—";
            zn = item.Placement.Zone.Zone.Name;
            si = item.Placement.SlotIndex.ToString();
            ri = item.Placement.RowIndex.ToString();
            ci = item.Placement.ColumnIndex.ToString();
        }

        return new LayoutPreviewItemRow(
            item.Icon,
            item.Icon.Ordinal,
            item.Icon.DisplayName,
            Truncate(item.Icon.StableId, 60),
            StateToString(item.State),
            item.CurrentPosition.X,
            item.CurrentPosition.Y,
            tx, ty, dx, dy, zn, si, ri, ci,
            item.State);
    }

    private static string StateToString(DesktopLayoutPreviewItemState state) => state switch
    {
        DesktopLayoutPreviewItemState.AlreadyAtTarget => "已就位",
        DesktopLayoutPreviewItemState.MoveRequired => "需要移动",
        DesktopLayoutPreviewItemState.Unassigned => "未分配",
        _ => state.ToString()
    };

    private static string Truncate(string v, int max) => v.Length <= max ? v : v[..(max - 3)] + "...";
}

/// <summary>
/// OutOfScope 图标行视图模型 — 不可变 sealed class。
/// 所有属性 get-only，只能通过静态工厂方法 From 构造。
/// </summary>
public sealed class LayoutPreviewOutOfScopeRow
{
    public int Ordinal { get; }
    public string DisplayName { get; }
    public string StableIdTruncated { get; }
    public string MonitorDeviceName { get; }
    public int CurrentX { get; }
    public int CurrentY { get; }

    private LayoutPreviewOutOfScopeRow(
        int ordinal, string displayName, string stableIdTruncated,
        string monitorDeviceName, int currentX, int currentY)
    {
        Ordinal = ordinal;
        DisplayName = displayName ?? "";
        StableIdTruncated = stableIdTruncated ?? "";
        MonitorDeviceName = monitorDeviceName ?? "—";
        CurrentX = currentX;
        CurrentY = currentY;
    }

    public static LayoutPreviewOutOfScopeRow From(DesktopIconSnapshotItem icon)
    {
        if (icon is null) throw new ArgumentNullException(nameof(icon));
        return new LayoutPreviewOutOfScopeRow(
            icon.Ordinal,
            icon.DisplayName,
            Truncate(icon.StableId, 60),
            icon.MonitorDeviceName ?? "—",
            icon.PixelPosition.X,
            icon.PixelPosition.Y);
    }

    private static string Truncate(string v, int max) => v.Length <= max ? v : v[..(max - 3)] + "...";
}
