using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.Displays;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Poc;

public partial class MainWindow : Window
{
    private readonly IDisplayTopologyReader _displayTopologyReader;
    private readonly IDesktopIconReader _desktopIconReader;
    private readonly StaThreadExecutor _staExecutor;
    private readonly DesktopLayoutSnapshotFactory _snapshotFactory;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;

    // ─── 显示器相关 ───
    private readonly ObservableCollection<DisplayRow> _displayRows = [];
    private IReadOnlyList<DisplayDescriptor> _cachedDisplays = Array.Empty<DisplayDescriptor>();
    private string? _previousTargetDeviceName;
    private DisplayDescriptor? _currentTarget;
    private bool _suppressSelectionChanged;

    // ─── 图标相关 ───
    private readonly ObservableCollection<IconRow> _iconRows = [];
    private DesktopIconReadResult? _lastReadResult;

    // ─── 快照相关 ───
    private readonly ObservableCollection<SnapshotRow> _snapshotRows = [];
    private bool _snapshotStoreReady;
    private bool _snapshotOperationInProgress;

    public MainWindow(
        IDisplayTopologyReader displayTopologyReader,
        IDesktopIconReader desktopIconReader,
        StaThreadExecutor staExecutor,
        DesktopLayoutSnapshotFactory snapshotFactory,
        IDesktopLayoutSnapshotStore snapshotStore)
    {
        InitializeComponent();

        _displayTopologyReader = displayTopologyReader;
        _desktopIconReader = desktopIconReader;
        _staExecutor = staExecutor;
        _snapshotFactory = snapshotFactory;
        _snapshotStore = snapshotStore;

        DisplaysGrid.ItemsSource = _displayRows;
        IconsGrid.ItemsSource = _iconRows;
        SnapshotsGrid.ItemsSource = _snapshotRows;

        Loaded += OnLoaded;
    }

    // ═══════════════════════════════════════════════════
    //  初始化
    // ═══════════════════════════════════════════════════

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 1. 读取显示器拓扑
        RefreshDisplays();

        var dbPath = DesktopAgentDataPaths.DefaultDatabasePath;

        // ── 阶段 1：初始化 SQLite ──
        try
        {
            await _snapshotStore.InitializeAsync();
            _snapshotStoreReady = true;
            RefreshSnapshotsButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _snapshotStoreReady = false;
            SaveSnapshotButton.IsEnabled = false;
            RefreshSnapshotsButton.IsEnabled = false;

            StatusText.Text = $"[M1-03B] SQLite 初始化失败！\r\n数据库路径：{dbPath}\r\n异常：{ex}\r\n\r\n图标读取功能仍可使用，但保存和刷新快照已禁用。";
            StatusText.ScrollToHome();

            SnapshotStatsText.Text = $"数据库初始化失败（路径：{dbPath}）";
            return;
        }

        // ── 阶段 2：加载快照列表 ──
        try
        {
            await RefreshSnapshotsAsync();

            StatusText.Text = $"[M1-03B] SQLite 已初始化。\r\n数据库路径：{dbPath}\r\n当前快照数量：{_snapshotRows.Count}\r\n请先读取桌面图标，然后保存快照。";
            StatusText.ScrollToHome();
        }
        catch (Exception ex)
        {
            // 列表加载失败不能改 _snapshotStoreReady，数据库已成功初始化
            StatusText.Text = $"[M1-03B] SQLite 已初始化，但快照列表加载失败。\r\n数据库路径：{dbPath}\r\n异常：{ex}\r\n\r\n可点击「刷新列表」重试。";
            StatusText.ScrollToHome();

            SnapshotStatsText.Text = $"数据库已初始化，列表加载失败（路径：{dbPath}）";
            return;
        }

        UpdateSnapshotStats();
    }

    // ═══════════════════════════════════════════════════
    //  显示器拓扑
    // ═══════════════════════════════════════════════════

    private void RefreshDisplays_Click(object sender, RoutedEventArgs e)
    {
        // 显示器拓扑变化后，旧读取结果可能已过期
        InvalidateLastIconRead("显示器拓扑已刷新，请重新读取桌面图标。");
        RefreshDisplays();
    }

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

                // 仅切换目标显示器，不使读取结果失效，重新生成图标行
                RebuildIconRowsFromLastRead();
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
    //  读取结果生命周期
    // ═══════════════════════════════════════════════════

    private void InvalidateLastIconRead(string reason)
    {
        _lastReadResult = null;
        _iconRows.Clear();
        IconStatsText.Text = reason;
        UpdateSnapshotButtonState();
    }

    private void RebuildIconRowsFromLastRead()
    {
        if (_lastReadResult is null) return;

        var targetDeviceName = _currentTarget?.DeviceName;
        _iconRows.Clear();
        foreach (var icon in _lastReadResult.Icons)
        {
            var isOnTarget = targetDeviceName is not null
                && icon.MonitorDeviceName == targetDeviceName;
            _iconRows.Add(IconRow.From(icon, isOnTarget));
        }
    }

    private void UpdateSnapshotButtonState()
    {
        var canSave = _snapshotStoreReady
            && !_snapshotOperationInProgress
            && _lastReadResult is not null
            && _lastReadResult.FailedItemCount == 0;

        SaveSnapshotButton.IsEnabled = canSave;
    }

    // ═══════════════════════════════════════════════════
    //  诊断复制
    // ═══════════════════════════════════════════════════

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = StatusText.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            Clipboard.SetText(text);
            CopyDiagnosticsButton.Content = "已复制";
        }
        catch (Exception ex)
        {
            CopyDiagnosticsButton.Content = "复制失败";
            Debug.WriteLine($"[M1-03B] 复制诊断失败：{ex.Message}");
        }
        finally
        {
            _ = Task.Delay(1500).ContinueWith(_ =>
                Dispatcher.Invoke(() => CopyDiagnosticsButton.Content = "复制诊断"));
        }
    }

    // ═══════════════════════════════════════════════════
    //  桌面图标读取
    // ═══════════════════════════════════════════════════

    private async void ReadDesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        ReadIconsButton.IsEnabled = false;
        RefreshDisplaysButton.IsEnabled = false;
        DisplaysGrid.IsEnabled = false;

        // 开始新一次读取时清空旧结果
        _lastReadResult = null;
        SaveSnapshotButton.IsEnabled = false;

        IconStatsText.Text = "正在读取桌面图标…";
        StatusText.Text = "正在通过 Shell COM 读取桌面图标，请稍候…";

        try
        {
            var displays = _cachedDisplays;

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

                return new DesktopIconReadResult(
                    assigned, result.FailedItemCount, result.ConnectionMethod,
                    result.ReadTime, result.PrimaryPathException);
            });

            // 缓存成功读取结果，并通过 RebuildIconRowsFromLastRead 构建图标列表
            _lastReadResult = readResult;
            RebuildIconRowsFromLastRead();

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

            if (readResult.FailedItemCount > 0)
            {
                var errorInfo = $"；{readResult.FailedItemCount} 个读取失败";

                IconStatsText.Text = string.Format(
                    "成功 {0} 项{1}  |  连接：{2}{3}  |  耗时 {4:F1} 秒  |  {5}",
                    readResult.Icons.Count,
                    errorInfo,
                    connectionLabel,
                    fallbackNote,
                    readResult.ReadTime.TotalSeconds,
                    DateTime.Now.ToString("HH:mm:ss"));

                StatusText.Text = $"读取完成（{readResult.Icons.Count} 成功，{readResult.FailedItemCount} 失败）。连接方式：{connectionLabel}。\r\n本次读取不完整，不能保存为可恢复快照，请重新读取。";
                StatusText.ScrollToHome();
            }
            else
            {
                IconStatsText.Text = string.Format(
                    "成功 {0} 项  |  连接：{1}{2}  |  耗时 {3:F1} 秒  |  {4}",
                    readResult.Icons.Count,
                    connectionLabel,
                    fallbackNote,
                    readResult.ReadTime.TotalSeconds,
                    DateTime.Now.ToString("HH:mm:ss"));

                StatusText.Text = $"读取完成：共 {readResult.Icons.Count} 个桌面图标。连接方式：{connectionLabel}。";
                StatusText.ScrollToHome();
            }

            UpdateSnapshotButtonState();
        }
        catch (Exception ex)
        {
            _lastReadResult = null;
            _iconRows.Clear();

            IconStatsText.Text = "读取失败（详见状态栏）";

            var detail = DesktopShellIconReader.FormatException(ex);
            StatusText.Text = $"桌面图标读取失败。诊断：\r\n{detail}";
            StatusText.ScrollToHome();
            Debug.WriteLine($"[M1-03B] 读取失败：\r\n{detail}");

            SaveSnapshotButton.IsEnabled = false;
        }
        finally
        {
            ReadIconsButton.IsEnabled = true;
            RefreshDisplaysButton.IsEnabled = true;
            DisplaysGrid.IsEnabled = true;
        }
    }

    // ═══════════════════════════════════════════════════
    //  保存快照
    // ═══════════════════════════════════════════════════

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshotOperationInProgress) return;

        // 防卫性检查
        if (!_snapshotStoreReady)
        {
            StatusText.Text = "[M1-03B] 数据库未初始化，无法保存快照。";
            return;
        }

        if (_lastReadResult is null)
        {
            StatusText.Text = "[M1-03B] 没有可用的读取结果，请先读取桌面图标。";
            return;
        }

        if (_lastReadResult.FailedItemCount > 0)
        {
            StatusText.Text = $"[M1-03B] 本次读取不完整（{_lastReadResult.FailedItemCount} 个失败），不能保存为可恢复快照。";
            return;
        }

        // 捕获当前变量，避免操作过程中状态变化
        var readResult = _lastReadResult;
        var currentTargetDeviceName = _currentTarget?.DeviceName;

        _snapshotOperationInProgress = true;
        SaveSnapshotButton.IsEnabled = false;
        RefreshSnapshotsButton.IsEnabled = false;

        try
        {
            var snapshot = _snapshotFactory.Create(
                readResult.Icons,
                currentTargetDeviceName,
                readResult.ConnectionMethod);

            // ── 阶段 1：保存 ──
            try
            {
                await _snapshotStore.SaveAsync(snapshot);
            }
            catch (Exception saveEx)
            {
                StatusText.Text = $"[M1-03B] 保存快照失败：\r\n{saveEx}";
                StatusText.ScrollToHome();
                Debug.WriteLine($"[M1-03B] 保存快照失败：{saveEx}");
                return;
            }

            // ── 阶段 2：刷新列表 ──
            var dbPath = DesktopAgentDataPaths.DefaultDatabasePath;
            try
            {
                await RefreshSnapshotsAsync();
                UpdateSnapshotStats();

                StatusText.Text = string.Format(
                    "[M1-03B] 快照已保存。\r\nSnapshotId：{0:D}\r\n图标数量：{1}\r\n目标显示器：{2}\r\nConnectionMethod：{3}\r\n数据库路径：{4}",
                    snapshot.SnapshotId,
                    snapshot.Icons.Count,
                    currentTargetDeviceName ?? "—",
                    readResult.ConnectionMethod,
                    dbPath);
                StatusText.ScrollToHome();
            }
            catch (Exception refreshEx)
            {
                StatusText.Text = string.Format(
                    "[M1-03B] 快照已保存，但列表刷新失败。\r\n\r\nSnapshotId：{0:D}\r\n图标数量：{1}\r\n数据库路径：{2}\r\n\r\n刷新异常：{3}\r\n\r\n可点击「刷新列表」重试。",
                    snapshot.SnapshotId,
                    snapshot.Icons.Count,
                    dbPath,
                    refreshEx);
                StatusText.ScrollToHome();
                Debug.WriteLine($"[M1-03B] 保存后刷新列表失败：{refreshEx}");
            }
        }
        finally
        {
            _snapshotOperationInProgress = false;
            RefreshSnapshotsButton.IsEnabled = _snapshotStoreReady;
            UpdateSnapshotButtonState();
        }
    }

    // ═══════════════════════════════════════════════════
    //  快照列表
    // ═══════════════════════════════════════════════════

    private async void RefreshSnapshots_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshotOperationInProgress) return;

        if (!_snapshotStoreReady)
        {
            StatusText.Text = "[M1-03B] 数据库未初始化，无法刷新快照列表。";
            return;
        }

        _snapshotOperationInProgress = true;
        SaveSnapshotButton.IsEnabled = false;
        RefreshSnapshotsButton.IsEnabled = false;

        try
        {
            await RefreshSnapshotsAsync();
            UpdateSnapshotStats();
            StatusText.Text = $"[M1-03B] 快照列表已刷新，共 {_snapshotRows.Count} 条。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"[M1-03B] 刷新快照列表失败：\r\n{ex}";
            Debug.WriteLine($"[M1-03B] 刷新快照列表失败：{ex}");
        }
        finally
        {
            _snapshotOperationInProgress = false;
            RefreshSnapshotsButton.IsEnabled = _snapshotStoreReady;
            UpdateSnapshotButtonState();
        }
    }

    private async Task RefreshSnapshotsAsync()
    {
        var list = await _snapshotStore.ListAsync(limit: 100);

        // 原子更新：先生成完整临时列表，再提交
        var rows = new List<SnapshotRow>(list.Count);
        foreach (var summary in list)
        {
            rows.Add(SnapshotRow.From(summary));
        }

        _snapshotRows.Clear();
        foreach (var row in rows)
        {
            _snapshotRows.Add(row);
        }
    }

    private void UpdateSnapshotStats()
    {
        var dbPath = DesktopAgentDataPaths.DefaultDatabasePath;
        if (_snapshotStoreReady)
        {
            SnapshotStatsText.Text = string.Format(
                "数据库已初始化  |  已加载 {0} 条快照  |  路径：{1}",
                _snapshotRows.Count, dbPath);
        }
        else
        {
            SnapshotStatsText.Text = "数据库初始化失败";
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

    private sealed record SnapshotRow(
        string CreatedAtLocal,
        string SnapshotId,
        int IconCount,
        string TargetMonitorDeviceName,
        string ConnectionMethod,
        int FormatVersion)
    {
        public static SnapshotRow From(DesktopLayoutSnapshotSummary summary) => new(
            summary.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            summary.SnapshotId.ToString("D"),
            summary.IconCount,
            summary.TargetMonitorDeviceName ?? "—",
            summary.ConnectionMethod.ToString(),
            summary.FormatVersion);
    }
}
