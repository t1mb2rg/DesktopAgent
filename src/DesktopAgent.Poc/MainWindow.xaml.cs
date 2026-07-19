using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Poc;

public partial class MainWindow : Window
{
    private readonly IDisplayTopologyReader _displayTopologyReader;
    private readonly ObservableCollection<DisplayRow> _displayRows = [];

    /// <summary>
    /// 最近一次读取的显示器列表缓存。
    /// 用于在用户单击 DataGrid 行时查找匹配的 DisplayDescriptor，
    /// 避免重复调用 Win32 API。
    /// </summary>
    private IReadOnlyList<DisplayDescriptor> _cachedDisplays = Array.Empty<DisplayDescriptor>();

    /// <summary>
    /// 上一次选择的目标显示器 DeviceName。
    /// 重新读取显示器拓扑时，优先按此名称保留目标。
    /// null 表示尚未选择过。
    /// </summary>
    private string? _previousTargetDeviceName;

    /// <summary>
    /// 当前实际选中的目标显示器描述（来自最新一次读取）。
    /// null 表示没有显示器或尚未读取。
    /// </summary>
    private DisplayDescriptor? _currentTarget;

    /// <summary>
    /// 防止在代码中设置 DataGrid 选中行时触发 SelectionChanged 处理逻辑。
    /// </summary>
    private bool _suppressSelectionChanged;

    public MainWindow(IDisplayTopologyReader displayTopologyReader)
    {
        InitializeComponent();

        _displayTopologyReader = displayTopologyReader;
        DisplaysGrid.ItemsSource = _displayRows;

        Loaded += (_, _) => RefreshDisplays();
    }

    private void RefreshDisplays_Click(object sender, RoutedEventArgs e) =>
        RefreshDisplays();

    private void DisplaysGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        if (DisplaysGrid.SelectedItem is DisplayRow selectedRow)
        {
            // 在缓存的显示器列表中查找匹配的 DeviceName
            var matched = _cachedDisplays.FirstOrDefault(d => d.DeviceName == selectedRow.DeviceName);

            if (matched is not null)
            {
                _currentTarget = matched;
                _previousTargetDeviceName = matched.DeviceName;
                UpdateTargetDisplay();
            }
        }
    }

    private void RefreshDisplays()
    {
        try
        {
            var displays = _displayTopologyReader.GetDisplays();
            _cachedDisplays = displays;

            // 使用 Core 纯逻辑选择目标显示器
            var target = TargetDisplaySelector.SelectTarget(displays, _previousTargetDeviceName);

            _currentTarget = target;
            if (target is not null)
            {
                _previousTargetDeviceName = target.DeviceName;
            }

            // 重建显示行
            _displayRows.Clear();
            foreach (var display in displays)
            {
                var isTarget = target is not null && display.DeviceName == target.DeviceName;
                _displayRows.Add(DisplayRow.From(display, isTarget));
            }

            // 同步 DataGrid 选中行到目标显示器
            SyncDataGridSelection(target);

            UpdateStatusBar(displays.Count);
            UpdateTargetDisplay();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取失败：{exception.Message}";
            TargetText.Text = string.Empty;
        }
    }

    /// <summary>
    /// 将 DataGrid 的选中行同步到当前目标显示器，不触发用户交互逻辑。
    /// </summary>
    private void SyncDataGridSelection(DisplayDescriptor? target)
    {
        _suppressSelectionChanged = true;
        try
        {
            if (target is null)
            {
                DisplaysGrid.SelectedItem = null;
                return;
            }

            foreach (var row in _displayRows)
            {
                if (row.DeviceName == target.DeviceName)
                {
                    DisplaysGrid.SelectedItem = row;
                    DisplaysGrid.ScrollIntoView(row);
                    return;
                }
            }

            DisplaysGrid.SelectedItem = null;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private void UpdateStatusBar(int displayCount)
    {
        StatusText.Text = $"已识别 {displayCount} 块显示器。M1 后续只修改用户选择的目标显示器；其余显示器只读。";
    }

    private void UpdateTargetDisplay()
    {
        if (_currentTarget is null)
        {
            TargetText.Text = "未检测到显示器，无法确定目标。";
            return;
        }

        var d = _currentTarget;
        TargetText.Text = string.Format(
            "当前目标显示器：{0}  |  虚拟坐标 ({1}, {2})  |  {3} × {4} 物理像素  |  DPI {5}×{6}  |  缩放 {7:P0}×{8:P0}",
            d.DeviceName,
            d.Bounds.Left, d.Bounds.Top,
            d.Bounds.Width, d.Bounds.Height,
            d.DpiX, d.DpiY,
            d.ScaleX, d.ScaleY);
    }

    private sealed record DisplayRow(
        string DeviceName,
        bool IsPrimary,
        string BoundsText,
        string WorkAreaText,
        string DpiText,
        string ScaleText,
        string TargetMarker)
    {
        public static DisplayRow From(DisplayDescriptor display, bool isTarget) => new(
            display.DeviceName,
            display.IsPrimary,
            Format(display.Bounds.Left, display.Bounds.Top, display.Bounds.Width, display.Bounds.Height),
            Format(display.WorkArea.Left, display.WorkArea.Top, display.WorkArea.Width, display.WorkArea.Height),
            $"{display.DpiX} × {display.DpiY}",
            $"{display.ScaleX:P0} × {display.ScaleY:P0}",
            isTarget ? "●" : "○");

        private static string Format(int left, int top, int width, int height) =>
            $"({left}, {top})  {width} × {height}";
    }
}
