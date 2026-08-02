using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

public sealed record LayoutPreviewItemRow
{
    public DesktopIconSnapshotItem Icon { get; init; } = null!;
    public int Ordinal { get; init; }
    public string DisplayName { get; init; } = "";
    public string StableIdTruncated { get; init; } = "";
    public string StateText { get; init; } = "";
    public int CurrentX { get; init; }
    public int CurrentY { get; init; }
    public string TargetX { get; set; } = "—";
    public string TargetY { get; set; } = "—";
    public string DeltaX { get; set; } = "—";
    public string DeltaY { get; set; } = "—";
    public string ZoneName { get; set; } = "—";
    public string SlotIndex { get; set; } = "—";
    public string RowIndex { get; set; } = "—";
    public string ColumnIndex { get; set; } = "—";
    public DesktopLayoutPreviewItemState State { get; init; }

    public static LayoutPreviewItemRow From(DesktopLayoutPreviewItem item)
    {
        var row = new LayoutPreviewItemRow
        {
            Icon = item.Icon,
            Ordinal = item.Icon.Ordinal,
            DisplayName = item.Icon.DisplayName,
            StableIdTruncated = Truncate(item.Icon.StableId, 60),
            CurrentX = item.CurrentPosition.X,
            CurrentY = item.CurrentPosition.Y,
            State = item.State,
            StateText = StateToString(item.State)
        };

        if (item.IsAssigned && item.Placement is not null)
        {
            row.TargetX = item.TargetPosition!.Value.X.ToString();
            row.TargetY = item.TargetPosition!.Value.Y.ToString();
            row.DeltaX = item.DeltaX?.ToString() ?? "—";
            row.DeltaY = item.DeltaY?.ToString() ?? "—";
            row.ZoneName = item.Placement.Zone.Zone.Name;
            row.SlotIndex = item.Placement.SlotIndex.ToString();
            row.RowIndex = item.Placement.RowIndex.ToString();
            row.ColumnIndex = item.Placement.ColumnIndex.ToString();
        }

        return row;
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

public sealed record LayoutPreviewOutOfScopeRow
{
    public int Ordinal { get; init; }
    public string DisplayName { get; init; } = "";
    public string StableIdTruncated { get; init; } = "";
    public string MonitorDeviceName { get; init; } = "—";
    public int CurrentX { get; init; }
    public int CurrentY { get; init; }

    public static LayoutPreviewOutOfScopeRow From(DesktopIconSnapshotItem icon) => new()
    {
        Ordinal = icon.Ordinal,
        DisplayName = icon.DisplayName,
        StableIdTruncated = Truncate(icon.StableId, 60),
        MonitorDeviceName = icon.MonitorDeviceName ?? "—",
        CurrentX = icon.PixelPosition.X,
        CurrentY = icon.PixelPosition.Y
    };

    private static string Truncate(string v, int max) => v.Length <= max ? v : v[..(max - 3)] + "...";
}
