using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Poc.LayoutTemplates.Editor;

/// <summary>
/// Zone 编辑草稿。允许属性暂时无效，保存时 fail-closed。
/// 实现 INotifyPropertyChanged 供 WPF 绑定。
/// </summary>
public sealed class LayoutZoneEditorDraft : INotifyPropertyChanged
{
    public Guid ZoneId { get; }

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    private int _left;
    public int Left
    {
        get => _left;
        set { if (_left != value) { _left = value; OnPropertyChanged(); OnPropertyChanged(nameof(BoundsText)); } }
    }

    private int _top;
    public int Top
    {
        get => _top;
        set { if (_top != value) { _top = value; OnPropertyChanged(); OnPropertyChanged(nameof(BoundsText)); } }
    }

    private int _right;
    public int Right
    {
        get => _right;
        set { if (_right != value) { _right = value; OnPropertyChanged(); OnPropertyChanged(nameof(BoundsText)); } }
    }

    private int _bottom;
    public int Bottom
    {
        get => _bottom;
        set { if (_bottom != value) { _bottom = value; OnPropertyChanged(); OnPropertyChanged(nameof(BoundsText)); } }
    }

    public string BoundsText => $"[{Left},{Top},{Right},{Bottom})";

    private int _sortOrder;
    public int SortOrder
    {
        get => _sortOrder;
        set { if (_sortOrder != value) { _sortOrder = value; OnPropertyChanged(); } }
    }

    private ZonePlacementFlow _placementFlow;
    public ZonePlacementFlow PlacementFlow
    {
        get => _placementFlow;
        set { if (_placementFlow != value) { _placementFlow = value; OnPropertyChanged(); } }
    }

    private int? _maxItemCount;
    public int? MaxItemCount
    {
        get => _maxItemCount;
        set { if (_maxItemCount != value) { _maxItemCount = value; OnPropertyChanged(); } }
    }

    public LayoutZoneEditorDraft(
        Guid zoneId, string name, int left, int top, int right, int bottom,
        int sortOrder, ZonePlacementFlow placementFlow, int? maxItemCount)
    {
        ZoneId = zoneId;
        _name = name;
        _left = left;
        _top = top;
        _right = right;
        _bottom = bottom;
        _sortOrder = sortOrder;
        _placementFlow = placementFlow;
        _maxItemCount = maxItemCount;
    }

    public static LayoutZoneEditorDraft FromZone(DesktopLayoutZone zone) => new(
        zone.ZoneId, zone.Name,
        zone.Bounds.Left, zone.Bounds.Top, zone.Bounds.Right, zone.Bounds.Bottom,
        zone.SortOrder, zone.PlacementFlow, zone.MaxItemCount);

    public bool TryGetBounds(out NormalizedRect? bounds, out string? error)
    {
        try
        {
            // 先验证范围
            if (Left < 0 || Left > NormalizedZoneEditorGeometry.NormalizedMax)
            {
                bounds = null; error = $"Left ({Left}) 不在 [0, {NormalizedZoneEditorGeometry.NormalizedMax}] 范围内。"; return false;
            }
            if (Top < 0 || Top > NormalizedZoneEditorGeometry.NormalizedMax)
            {
                bounds = null; error = $"Top ({Top}) 不在 [0, {NormalizedZoneEditorGeometry.NormalizedMax}] 范围内。"; return false;
            }
            if (Right < 0 || Right > NormalizedZoneEditorGeometry.NormalizedMax)
            {
                bounds = null; error = $"Right ({Right}) 不在 [0, {NormalizedZoneEditorGeometry.NormalizedMax}] 范围内。"; return false;
            }
            if (Bottom < 0 || Bottom > NormalizedZoneEditorGeometry.NormalizedMax)
            {
                bounds = null; error = $"Bottom ({Bottom}) 不在 [0, {NormalizedZoneEditorGeometry.NormalizedMax}] 范围内。"; return false;
            }

            bounds = new NormalizedRect(Left, Top, Right, Bottom);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            bounds = null;
            error = $"Bounds 无效：{ex.Message}";
            return false;
        }
    }

    public DesktopLayoutZone Build()
    {
        if (!TryGetBounds(out var bounds, out var error) || bounds is null)
            throw new InvalidOperationException(
                $"Zone \"{Name}\" (ZoneId={ZoneId:D}) Bounds 无效：{error}");

        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException(
                $"Zone (ZoneId={ZoneId:D}) Name 不能为空白。");

        if (SortOrder < 0)
            throw new InvalidOperationException(
                $"Zone \"{Name}\" SortOrder ({SortOrder}) 不能为负数。");

        if (MaxItemCount.HasValue && MaxItemCount.Value <= 0)
            throw new InvalidOperationException(
                $"Zone \"{Name}\" MaxItemCount ({MaxItemCount}) 时必须 > 0。");

        if (PlacementFlow != ZonePlacementFlow.ColumnMajor
            && PlacementFlow != ZonePlacementFlow.RowMajor)
            throw new InvalidOperationException(
                $"Zone \"{Name}\" PlacementFlow ({PlacementFlow}) 无效。");

        return new DesktopLayoutZone(
            ZoneId, Name, bounds, SortOrder, PlacementFlow, MaxItemCount);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
