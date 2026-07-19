using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Displays;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Poc;

public partial class MainWindow : Window
{
    private readonly IDisplayTopologyReader _displayTopologyReader;
    private readonly IDesktopIconReader _desktopIconReader;
    private readonly StaThreadExecutor _staExecutor;

    // ─── 显示器相关 ───
    private readonly ObservableCollection<DisplayRow> _displayRows = [];
    private IReadOnlyList<DisplayDescriptor> _cachedDisplays = Array.Empty<DisplayDescriptor>();
    private string? _previousTargetDeviceName;
    private DisplayDescriptor? _currentTarget;
    private bool _suppressSelectionChanged;

    // ─── 图标相关 ───
    private readonly ObservableCollection<IconRow> _iconRows = [];

    public MainWindow(
        IDisplayTopologyReader displayTopologyReader,
        IDesktopIconReader desktopIconReader,
        StaThreadExecutor staExecutor)
    {
        InitializeComponent();

        _displayTopologyReader = displayTopologyReader;
        _desktopIconReader = desktopIconReader;
        _staExecutor = staExecutor;

        DisplaysGrid.ItemsSource = _displayRows;
        IconsGrid.ItemsSource = _iconRows;

        Loaded += (_, _) => RefreshDisplays();
    }

    // ═══════════════════════════════════════════════════
    //  显示器拓扑
    // ═══════════════════════════════════════════════════

    private void RefreshDisplays_Click(object sender, RoutedEventArgs e) =>
        RefreshDisplays();

    private void DisplaysGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;

        if (DisplaysGrid.SelectedItem is DisplayRow selectedRow)
        {
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

            var target = TargetDisplaySelector.SelectTarget(displays, _previousTargetDeviceName);
            _currentTarget = target;
            if (target is not null) _previousTargetDeviceName = target.DeviceName;

            _displayRows.Clear();
            foreach (var display in displays)
            {
                var isTarget = target is not null && display.DeviceName == target.DeviceName;
                _displayRows.Add(DisplayRow.From(display, isTarget));
            }

            SyncDataGridSelection(target);
            UpdateTargetDisplay();
        }
        catch (Exception exception)
        {
            TargetText.Text = $"读取失败：{exception.Message}";
        }
    }

    private void SyncDataGridSelection(DisplayDescriptor? target)
    {
        _suppressSelectionChanged = true;
        try
        {
            if (target is null) { DisplaysGrid.SelectedItem = null; return; }
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
        finally { _suppressSelectionChanged = false; }
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
            d.DeviceName, d.Bounds.Left, d.Bounds.Top, d.Bounds.Width, d.Bounds.Height,
            d.DpiX, d.DpiY, d.ScaleX, d.ScaleY);
    }

    // ═══════════════════════════════════════════════════
    //  桌面图标读取
    // ═══════════════════════════════════════════════════

    private async void ReadDesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        ReadIconsButton.IsEnabled = false;
        IconStatsText.Text = "正在读取桌面图标…";
        StatusText.Text = "正在通过 Shell COM 读取桌面图标，请稍候…";

        try
        {
            var displays = _cachedDisplays;
            var targetDeviceName = _currentTarget?.DeviceName;

            // 在专用 STA 线程中执行 Shell COM 操作
            var readResult = await _staExecutor.RunAsync(() =>
            {
                var result = _desktopIconReader.ReadDesktopIcons();

                // 为每个图标分配显示器归属
                var assigned = new List<DesktopIconDescriptor>(result.Icons.Count);
                foreach (var icon in result.Icons)
                {
                    var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays);
                    assigned.Add(icon with { MonitorDeviceName = monitor?.DeviceName });
                }

                // 返回带分配后图标的结果
                return new DesktopIconReadResult(
                    assigned, result.FailedItemCount, result.ConnectionMethod,
                    result.ReadTime, result.PrimaryPathException);
            });

            // 更新图标列表
            _iconRows.Clear();
            foreach (var icon in readResult.Icons)
            {
                var isOnTarget = targetDeviceName is not null
                    && icon.MonitorDeviceName == targetDeviceName;
                _iconRows.Add(IconRow.From(icon, isOnTarget));
            }

            // 构建连接方式诊断文本
            var connectionLabel = readResult.ConnectionMethod switch
            {
                ConnectionMethod.FindWindowSW => "FindWindowSW",
                ConnectionMethod.EnumerationFallback => "EnumFallback",
                _ => readResult.ConnectionMethod.ToString(),
            };

            var fallbackNote = readResult.PrimaryPathException is not null
                ? $"  |  主路径失败：{readResult.PrimaryPathException.Message}"
                : string.Empty;

            var errorInfo = readResult.FailedItemCount > 0
                ? $"；{readResult.FailedItemCount} 个读取失败"
                : string.Empty;

            IconStatsText.Text = string.Format(
                "成功 {0} 项{1}  |  连接：{2}{3}  |  耗时 {4:F1} 秒  |  {5}",
                readResult.Icons.Count,
                errorInfo,
                connectionLabel,
                fallbackNote,
                readResult.ReadTime.TotalSeconds,
                DateTime.Now.ToString("HH:mm:ss"));

            StatusText.Text = readResult.FailedItemCount > 0
                ? $"读取完成（{readResult.Icons.Count} 成功，{readResult.FailedItemCount} 失败）。连接方式：{connectionLabel}。"
                : $"读取完成：共 {readResult.Icons.Count} 个桌面图标。连接方式：{connectionLabel}。";
        }
        catch (Exception ex)
        {
            IconStatsText.Text = "读取失败（详见状态栏）";

            // 使用 DesktopShellIconReader 的详细格式化
            var detail = DesktopAgent.WindowsInterop.DesktopIcons.DesktopShellIconReader.FormatException(ex);
            StatusText.Text = $"桌面图标读取失败。诊断：\r\n{detail}";
            Debug.WriteLine($"[M1-02] 读取失败：\r\n{detail}");

            _iconRows.Clear();
        }
        finally
        {
            ReadIconsButton.IsEnabled = true;
        }
    }

    // ═══════════════════════════════════════════════════
    //  行模型
    // ═══════════════════════════════════════════════════

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

    private sealed record IconRow(
        string DisplayName,
        string StableIdTruncated,
        string ItemKind,
        string X,
        string Y,
        string MonitorDisplay,
        string TargetScreenMarker,
        bool IsFileSystemItem,
        bool IsSystemItem)
    {
        public static IconRow From(DesktopIconDescriptor icon, bool isOnTarget) => new(
            icon.DisplayName,
            Truncate(icon.StableId, 60),
            icon.ItemKind.ToString(),
            icon.PixelPosition.X.ToString(),
            icon.PixelPosition.Y.ToString(),
            icon.MonitorDeviceName ?? "—",
            isOnTarget ? "★" : "",
            icon.IsFileSystemItem,
            icon.IsSystemItem);

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }
}
