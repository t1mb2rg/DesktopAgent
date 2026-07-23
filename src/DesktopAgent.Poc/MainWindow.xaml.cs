using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Poc;

public partial class MainWindow : Window
{
    private readonly IDisplayTopologyReader _displayTopologyReader;
    private readonly IDesktopIconReader _desktopIconReader;
    private readonly IDesktopIconPositionWriter _desktopIconPositionWriter;
    private readonly StaThreadExecutor _staExecutor;
    private readonly DesktopLayoutSnapshotFactory _snapshotFactory;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;

    private readonly ObservableCollection<DisplayRow> _displayRows = [];
    private IReadOnlyList<DisplayDescriptor> _cachedDisplays = Array.Empty<DisplayDescriptor>();
    private string? _previousTargetDeviceName;
    private DisplayDescriptor? _currentTarget;
    private bool _suppressSelectionChanged;

    private readonly ObservableCollection<IconRow> _iconRows = [];
    private DesktopIconReadResult? _lastReadResult;

    private readonly ObservableCollection<SnapshotRow> _snapshotRows = [];
    private bool _snapshotStoreReady;
    private bool _snapshotOperationInProgress;
    private bool _iconReadInProgress;
    private bool _moveOperationInProgress;

    private MoveRecoverySession? _activeMoveSession;

    private bool IsAnyOperationInProgress =>
        _iconReadInProgress || _snapshotOperationInProgress || _moveOperationInProgress;

    public MainWindow(
        IDisplayTopologyReader displayTopologyReader,
        IDesktopIconReader desktopIconReader,
        IDesktopIconPositionWriter desktopIconPositionWriter,
        StaThreadExecutor staExecutor,
        DesktopLayoutSnapshotFactory snapshotFactory,
        IDesktopLayoutSnapshotStore snapshotStore)
    {
        InitializeComponent();

        _displayTopologyReader = displayTopologyReader;
        _desktopIconReader = desktopIconReader;
        _desktopIconPositionWriter = desktopIconPositionWriter;
        _staExecutor = staExecutor;
        _snapshotFactory = snapshotFactory;
        _snapshotStore = snapshotStore;

        DisplaysGrid.ItemsSource = _displayRows;
        IconsGrid.ItemsSource = _iconRows;
        SnapshotsGrid.ItemsSource = _snapshotRows;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ═══════════════════════════════════════════════════
    //  MoveRecoverySession
    // ═══════════════════════════════════════════════════

    private sealed record MoveRecoverySession(
        Guid SafetySnapshotId,
        string StableId,
        string DisplayName,
        string MonitorDeviceName,
        PixelPoint OriginalPosition,
        PixelPoint RequestedPosition,
        PixelPoint LastKnownPosition,
        DateTime StartedAtUtc);

    // ═══════════════════════════════════════════════════
    //  统一交互状态
    // ═══════════════════════════════════════════════════

    private void UpdateInteractiveState()
    {
        var busy = IsAnyOperationInProgress;
        var hasSession = _activeMoveSession is not null;

        RefreshDisplaysButton.IsEnabled = !busy && !hasSession;
        DisplaysGrid.IsEnabled = !busy && !hasSession;
        ReadIconsButton.IsEnabled = !busy && !hasSession;
        IconsGrid.IsEnabled = !busy && !hasSession;

        // 快照保存按钮
        var canSaveSnapshot = !busy && !hasSession && _snapshotStoreReady
            && _lastReadResult is not null && _lastReadResult.FailedItemCount == 0;
        SaveSnapshotButton.IsEnabled = canSaveSnapshot;

        // 快照刷新按钮
        RefreshSnapshotsButton.IsEnabled = !busy && !hasSession && _snapshotStoreReady;

        // 移动确认框
        MoveConsentCheckBox.IsEnabled = !busy && !hasSession;

        // 移动按钮
        var canMove = !busy && !hasSession && _snapshotStoreReady
            && MoveConsentCheckBox.IsChecked == true
            && _lastReadResult is not null && _lastReadResult.FailedItemCount == 0
            && _currentTarget is not null
            && IconsGrid.SelectedItem is IconRow selectedIconRow
            && selectedIconRow.IsFileSystemItem && !selectedIconRow.IsSystemItem
            && selectedIconRow.IsOnTarget
            && selectedIconRow.MonitorDeviceName is not null
            && string.Equals(selectedIconRow.MonitorDeviceName, _currentTarget.DeviceName, StringComparison.Ordinal);
        MoveSelectedIconButton.IsEnabled = canMove;

        // 恢复按钮
        RestoreOriginalPositionButton.IsEnabled = hasSession && !busy;

        if (hasSession && !busy)
        {
            var s = _activeMoveSession!;
            MoveSessionText.Text = string.Format(
                "⚠ 活跃恢复会话 — 图标: {0}  |  原位置: ({1},{2})  |  当前位置: ({3},{4})  |  安全快照: {5:D}",
                s.DisplayName, s.OriginalPosition.X, s.OriginalPosition.Y,
                s.LastKnownPosition.X, s.LastKnownPosition.Y, s.SafetySnapshotId);
        }
    }

    // ═══════════════════════════════════════════════════
    //  初始化
    // ═══════════════════════════════════════════════════

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshDisplays();

        var dbPath = DesktopAgentDataPaths.DefaultDatabasePath;

        try
        {
            await _snapshotStore.InitializeAsync();
            _snapshotStoreReady = true;
        }
        catch (Exception ex)
        {
            _snapshotStoreReady = false;
            StatusText.Text = $"[M1-04B] SQLite 初始化失败！\r\n数据库路径：{dbPath}\r\n异常：{ex}\r\n\r\n图标读取功能仍可使用，但保存和刷新快照已禁用。";
            StatusText.ScrollToHome();
            SnapshotStatsText.Text = $"数据库初始化失败（路径：{dbPath}）";
            UpdateInteractiveState();
            return;
        }

        try
        {
            await RefreshSnapshotsAsync();
            StatusText.Text = $"[M1-04B] SQLite 已初始化。\r\n数据库路径：{dbPath}\r\n当前快照数量：{_snapshotRows.Count}\r\n请先读取桌面图标。";
            StatusText.ScrollToHome();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"[M1-04B] SQLite 已初始化，但快照列表加载失败。\r\n数据库路径：{dbPath}\r\n异常：{ex}\r\n\r\n可点击「刷新列表」重试。";
            StatusText.ScrollToHome();
            SnapshotStatsText.Text = $"数据库已初始化，列表加载失败（路径：{dbPath}）";
            UpdateInteractiveState();
            return;
        }

        UpdateSnapshotStats();
        UpdateInteractiveState();
    }

    // ═══════════════════════════════════════════════════
    //  显示器拓扑
    // ═══════════════════════════════════════════════════

    private void RefreshDisplays_Click(object sender, RoutedEventArgs e)
    {
        if (_activeMoveSession is not null)
        {
            StatusText.Text = "[M1-04B] 有活跃恢复会话，请先恢复原位置后再刷新显示器拓扑。";
            return;
        }
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
                RebuildIconRowsFromLastRead();
                UpdateInteractiveState();
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
        catch (Exception exception) { TargetText.Text = $"读取失败：{exception.Message}"; }
    }

    private void SyncDataGridSelection(DisplayDescriptor? target)
    {
        _suppressSelectionChanged = true;
        try
        {
            if (target is null) { DisplaysGrid.SelectedItem = null; return; }
            foreach (var row in _displayRows)
                if (row.DeviceName == target.DeviceName)
                { DisplaysGrid.SelectedItem = row; DisplaysGrid.ScrollIntoView(row); return; }
            DisplaysGrid.SelectedItem = null;
        }
        finally { _suppressSelectionChanged = false; }
    }

    private void UpdateTargetDisplay()
    {
        if (_currentTarget is null) { TargetText.Text = "未检测到显示器。"; return; }
        var d = _currentTarget;
        TargetText.Text = string.Format(
            "当前目标显示器：{0}  |  虚拟坐标 ({1}, {2})  |  {3} × {4}  |  DPI {5}×{6}  |  缩放 {7:P0}×{8:P0}",
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
        UpdateInteractiveState();
    }

    private void RebuildIconRowsFromLastRead()
    {
        if (_lastReadResult is null) return;
        var targetDeviceName = _currentTarget?.DeviceName;
        _iconRows.Clear();
        foreach (var icon in _lastReadResult.Icons)
        {
            var isOnTarget = targetDeviceName is not null && icon.MonitorDeviceName == targetDeviceName;
            _iconRows.Add(IconRow.From(icon, isOnTarget));
        }
    }

    private static DesktopIconReadResult AssignMonitorOwnership(
        DesktopIconReadResult rawResult, IReadOnlyList<DisplayDescriptor> displays)
    {
        var assigned = new List<DesktopIconDescriptor>(rawResult.Icons.Count);
        foreach (var icon in rawResult.Icons)
        {
            var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays);
            assigned.Add(icon with { MonitorDeviceName = monitor?.DeviceName });
        }
        return new DesktopIconReadResult(
            assigned, rawResult.FailedItemCount, rawResult.ConnectionMethod,
            rawResult.ReadTime, rawResult.PrimaryPathException);
    }

    private bool TryUpdateCachedIconPosition(string stableId, PixelPoint actualPosition)
    {
        if (_lastReadResult is null) return false;
        var icons = new List<DesktopIconDescriptor>(_lastReadResult.Icons.Count);
        DesktopIconDescriptor? match = null;
        var matchCount = 0;
        foreach (var icon in _lastReadResult.Icons)
        {
            if (string.Equals(icon.StableId, stableId, StringComparison.Ordinal))
            { matchCount++; match = icon; }
            icons.Add(icon);
        }
        if (matchCount != 1 || match is null) return false;

        var idx = icons.IndexOf(match);
        var monitor = DisplayAssignmentService.AssignMonitor(actualPosition, _cachedDisplays);
        icons[idx] = match with { PixelPosition = actualPosition, MonitorDeviceName = monitor?.DeviceName };

        _lastReadResult = new DesktopIconReadResult(
            icons, _lastReadResult.FailedItemCount, _lastReadResult.ConnectionMethod,
            _lastReadResult.ReadTime, _lastReadResult.PrimaryPathException);
        RebuildIconRowsFromLastRead();
        return true;
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
        catch (Exception ex) { CopyDiagnosticsButton.Content = "复制失败"; Debug.WriteLine($"[M1-04B] 复制诊断失败：{ex.Message}"); }
        finally { _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => CopyDiagnosticsButton.Content = "复制诊断")); }
    }

    // ═══════════════════════════════════════════════════
    //  桌面图标读取
    // ═══════════════════════════════════════════════════

    private async void ReadDesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        if (_activeMoveSession is not null)
        {
            StatusText.Text = "[M1-04B] 有活跃恢复会话，请先恢复原位置后再重新读取。";
            return;
        }

        _iconReadInProgress = true;
        UpdateInteractiveState();

        _lastReadResult = null;
        IconStatsText.Text = "正在读取桌面图标…";
        StatusText.Text = "正在通过 Shell COM 读取桌面图标，请稍候…";

        try
        {
            var displays = _cachedDisplays;
            var rawResult = await _staExecutor.RunAsync(() => _desktopIconReader.ReadDesktopIcons());
            var readResult = AssignMonitorOwnership(rawResult, displays);

            _lastReadResult = readResult;
            RebuildIconRowsFromLastRead();

            var connectionLabel = readResult.ConnectionMethod switch
            {
                ConnectionMethod.FindWindowSW => "FindWindowSW",
                ConnectionMethod.EnumerationFallback => "EnumFallback",
                _ => readResult.ConnectionMethod.ToString(),
            };
            var fallbackNote = readResult.PrimaryPathException is not null ? $"  |  主路径失败：{readResult.PrimaryPathException.Message}" : "";

            if (readResult.FailedItemCount > 0)
            {
                var errorInfo = $"；{readResult.FailedItemCount} 个读取失败";
                IconStatsText.Text = string.Format("成功 {0} 项{1}  |  连接：{2}{3}  |  耗时 {4:F1} 秒  |  {5}",
                    readResult.Icons.Count, errorInfo, connectionLabel, fallbackNote,
                    readResult.ReadTime.TotalSeconds, DateTime.Now.ToString("HH:mm:ss"));
                StatusText.Text = $"读取完成（{readResult.Icons.Count} 成功，{readResult.FailedItemCount} 失败）。\r\n本次读取不完整，不能保存为可恢复快照，请重新读取。";
            }
            else
            {
                IconStatsText.Text = string.Format("成功 {0} 项  |  连接：{1}{2}  |  耗时 {3:F1} 秒  |  {4}",
                    readResult.Icons.Count, connectionLabel, fallbackNote,
                    readResult.ReadTime.TotalSeconds, DateTime.Now.ToString("HH:mm:ss"));
                StatusText.Text = $"读取完成：共 {readResult.Icons.Count} 个桌面图标。连接方式：{connectionLabel}。";
            }
            StatusText.ScrollToHome();
        }
        catch (Exception ex)
        {
            _lastReadResult = null;
            _iconRows.Clear();
            IconStatsText.Text = "读取失败（详见状态栏）";
            StatusText.Text = $"桌面图标读取失败。诊断：\r\n{DesktopShellIconReader.FormatException(ex)}";
            StatusText.ScrollToHome();
        }
        finally
        {
            _iconReadInProgress = false;
            UpdateInteractiveState();
        }
    }

    // ═══════════════════════════════════════════════════
    //  保存快照
    // ═══════════════════════════════════════════════════

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
        if (_activeMoveSession is not null) { StatusText.Text = "[M1-04B] 请先恢复原位置。"; return; }
        if (!_snapshotStoreReady) { StatusText.Text = "数据库未初始化。"; return; }
        if (_lastReadResult is null) { StatusText.Text = "请先读取桌面图标。"; return; }
        if (_lastReadResult.FailedItemCount > 0) { StatusText.Text = $"读取不完整（{_lastReadResult.FailedItemCount} 个失败）。"; return; }

        var readResult = _lastReadResult;
        var currentTargetDeviceName = _currentTarget?.DeviceName;

        _snapshotOperationInProgress = true;
        UpdateInteractiveState();

        try
        {
            var snapshot = _snapshotFactory.Create(readResult.Icons, currentTargetDeviceName, readResult.ConnectionMethod);
            try { await _snapshotStore.SaveAsync(snapshot); }
            catch (Exception saveEx) { StatusText.Text = $"保存快照失败：\r\n{saveEx}"; StatusText.ScrollToHome(); return; }

            try
            {
                await RefreshSnapshotsAsync();
                UpdateSnapshotStats();
                StatusText.Text = string.Format("快照已保存。\r\nSnapshotId：{0:D}\r\n图标数量：{1}\r\n目标显示器：{2}",
                    snapshot.SnapshotId, snapshot.Icons.Count, currentTargetDeviceName ?? "—");
                StatusText.ScrollToHome();
            }
            catch (Exception refreshEx)
            {
                StatusText.Text = string.Format("快照已保存，但列表刷新失败。\r\nSnapshotId：{0:D}\r\n图标数量：{1}\r\n刷新异常：{2}",
                    snapshot.SnapshotId, snapshot.Icons.Count, refreshEx);
                StatusText.ScrollToHome();
            }
        }
        finally { _snapshotOperationInProgress = false; UpdateInteractiveState(); }
    }

    // ═══════════════════════════════════════════════════
    //  快照列表
    // ═══════════════════════════════════════════════════

    private async void RefreshSnapshots_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
        if (_activeMoveSession is not null) { StatusText.Text = "[M1-04B] 请先恢复原位置。"; return; }
        if (!_snapshotStoreReady) { StatusText.Text = "数据库未初始化。"; return; }

        _snapshotOperationInProgress = true;
        UpdateInteractiveState();

        try { await RefreshSnapshotsAsync(); UpdateSnapshotStats(); StatusText.Text = $"快照列表已刷新，共 {_snapshotRows.Count} 条。"; }
        catch (Exception ex) { StatusText.Text = $"刷新快照列表失败：\r\n{ex}"; }
        finally { _snapshotOperationInProgress = false; UpdateInteractiveState(); }
    }

    private async Task RefreshSnapshotsAsync()
    {
        var list = await _snapshotStore.ListAsync(limit: 100);
        var rows = new List<SnapshotRow>(list.Count);
        foreach (var s in list) rows.Add(SnapshotRow.From(s));
        _snapshotRows.Clear();
        foreach (var r in rows) _snapshotRows.Add(r);
    }

    private void UpdateSnapshotStats()
    {
        var dbPath = DesktopAgentDataPaths.DefaultDatabasePath;
        SnapshotStatsText.Text = _snapshotStoreReady
            ? $"数据库已初始化  |  已加载 {_snapshotRows.Count} 条快照  |  路径：{dbPath}"
            : "数据库初始化失败";
    }

    private void IconsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInteractiveState();
    }

    private void MoveConsentChanged(object sender, RoutedEventArgs e)
    {
        UpdateInteractiveState();
    }

    // ═══════════════════════════════════════════════════
    //  移动到相邻空位
    // ═══════════════════════════════════════════════════

    private async void MoveSelectedIcon_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
        if (_activeMoveSession is not null) { StatusText.Text = "请先恢复原位置。"; return; }
        if (!_snapshotStoreReady) { StatusText.Text = "数据库未初始化。"; return; }
        if (MoveConsentCheckBox.IsChecked != true) { StatusText.Text = "请先勾选移动确认框。"; return; }
        if (_lastReadResult is null || _lastReadResult.FailedItemCount > 0) { StatusText.Text = "请先完整读取桌面图标。"; return; }
        if (_currentTarget is null) { StatusText.Text = "未选择目标显示器。"; return; }
        if (IconsGrid.SelectedItem is not IconRow selectedRow) { StatusText.Text = "请先在图标列表中选择一个图标。"; return; }
        if (!selectedRow.IsFileSystemItem || selectedRow.IsSystemItem || !selectedRow.IsOnTarget) { StatusText.Text = "只允许当前目标显示器上的文件系统图标。"; return; }
        if (selectedRow.MonitorDeviceName is null || !string.Equals(selectedRow.MonitorDeviceName, _currentTarget.DeviceName, StringComparison.Ordinal)) { StatusText.Text = "选中图标不属于当前目标显示器。"; return; }

        var selectedStableId = selectedRow.StableId;
        var targetDeviceName = _currentTarget.DeviceName;
        var displaysSnapshot = _cachedDisplays.ToList();

        _moveOperationInProgress = true;
        UpdateInteractiveState();
        StatusText.Text = "[M1-04B] 正在重新读取桌面和检查定位能力…";

        try
        {
            // ── 重新读取能力和完整桌面 ──
            var (capabilities, freshRawResult) = await _staExecutor.RunAsync(() =>
            {
                var cap = _desktopIconPositionWriter.GetCapabilities();
                var raw = _desktopIconReader.ReadDesktopIcons();
                return (cap, raw);
            });
            var freshAssignedReadResult = AssignMonitorOwnership(freshRawResult, displaysSnapshot);

            // ── Preparation Service ──
            var targetDisplay = displaysSnapshot.FirstOrDefault(
                d => string.Equals(d.DeviceName, targetDeviceName, StringComparison.Ordinal));
            if (targetDisplay is null) throw new InvalidOperationException("目标显示器在显示器列表中不再存在。");

            var preparation = DesktopIconMovePreparationService.Prepare(
                selectedStableId, freshAssignedReadResult, targetDisplay, capabilities);

            // ── 最终确认框 ──
            var confirmResult = MessageBox.Show(
                string.Format("即将真实移动一个桌面图标。\r\n\r\n图标：{0}\r\nStableId：{1}\r\n目标显示器：{2}\r\n原位置：({3}, {4})\r\n目标位置：({5}, {6})\r\n方向：{7}\r\n图标间距：({8}, {9})\r\n\r\n将先保存并验证完整安全快照。移动后必须点击「恢复原位置」还原。",
                    preparation.SelectedIcon.DisplayName, selectedStableId, targetDeviceName,
                    preparation.SelectedIcon.PixelPosition.X, preparation.SelectedIcon.PixelPosition.Y,
                    preparation.Plan.TargetPosition.X, preparation.Plan.TargetPosition.Y,
                    preparation.Plan.Direction,
                    capabilities.Spacing.X, capabilities.Spacing.Y),
                "确认移动桌面图标", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (confirmResult != MessageBoxResult.Yes)
            {
                StatusText.Text = "[M1-04B] 用户取消移动操作。";
                return;
            }

            // ── 保存安全快照 ──
            StatusText.Text = "[M1-04B] 正在保存安全快照…";
            var snapshot = _snapshotFactory.Create(
                freshAssignedReadResult.Icons, targetDeviceName, freshAssignedReadResult.ConnectionMethod);
            await _snapshotStore.SaveAsync(snapshot);

            // ── 回读验证 ──
            var verified = await _snapshotStore.GetAsync(snapshot.SnapshotId);
            if (verified is null || verified.SnapshotId != snapshot.SnapshotId)
                throw new InvalidOperationException("安全快照回读失败：返回为 null 或 SnapshotId 不匹配。");
            if (verified.Icons.Count != freshAssignedReadResult.Icons.Count)
                throw new InvalidOperationException($"安全快照回读图标数量不一致：预期 {freshAssignedReadResult.Icons.Count}，实际 {verified.Icons.Count}。");
            var stableIdMatchCount = 0;
            foreach (var item in verified.Icons)
                if (string.Equals(item.StableId, selectedStableId, StringComparison.Ordinal)) stableIdMatchCount++;
            if (stableIdMatchCount != 1)
                throw new InvalidOperationException($"安全快照中 StableId 匹配 {stableIdMatchCount} 次。");
            var verifiedItem = verified.Icons.First(item => string.Equals(item.StableId, selectedStableId, StringComparison.Ordinal));
            if (verifiedItem.PixelPosition != preparation.SelectedIcon.PixelPosition)
                throw new InvalidOperationException($"安全快照位置不一致：快照中 ({verifiedItem.PixelPosition.X},{verifiedItem.PixelPosition.Y})，内存中 ({preparation.SelectedIcon.PixelPosition.X},{preparation.SelectedIcon.PixelPosition.Y})。");

            // 刷新快照列表（失败也终止移动）
            try { await RefreshSnapshotsAsync(); UpdateSnapshotStats(); }
            catch (Exception refreshEx) { throw new InvalidOperationException("安全快照已保存但列表刷新失败，为保证零真实写入策略终止移动。", refreshEx); }

            // ── 创建恢复会话（必须在 Writer 调用之前）──
            _activeMoveSession = new MoveRecoverySession(
                snapshot.SnapshotId,
                preparation.SelectedIcon.StableId,
                preparation.SelectedIcon.DisplayName,
                targetDeviceName,
                preparation.SelectedIcon.PixelPosition,
                preparation.Plan.TargetPosition,
                preparation.SelectedIcon.PixelPosition,
                DateTime.UtcNow);

            // ── 调用真实 Writer ──
            StatusText.Text = "[M1-04B] 正在移动图标…";
            var result = await _staExecutor.RunAsync(() =>
                _desktopIconPositionWriter.MoveDesktopIcon(
                    _activeMoveSession!.StableId,
                    _activeMoveSession.RequestedPosition));

            // ── 处理结果 ──
            _activeMoveSession = _activeMoveSession! with { LastKnownPosition = result!.ActualPosition };

            if (result.ActualPosition == _activeMoveSession.OriginalPosition)
            {
                _activeMoveSession = null;
                StatusText.Text = "[M1-04B] 图标未发生移动（实际位置等于原位置）。";
                StatusText.ScrollToHome();
                MoveSessionText.Text = "图标位置未变化，无需恢复。";
            }
            else
            {
                TryUpdateCachedIconPosition(_activeMoveSession!.StableId, result.ActualPosition);
                StatusText.Text = string.Format(
                    "[M1-04B] 图标已移动。\r\nSafetySnapshotId：{0:D}\r\n原位置：({1},{2})\r\n请求位置：({3},{4})\r\n实际位置：({5},{6})\r\nIsExact：{7}\r\n\r\n请点击「恢复原位置」还原图标。",
                    _activeMoveSession.SafetySnapshotId,
                    _activeMoveSession.OriginalPosition.X, _activeMoveSession.OriginalPosition.Y,
                    _activeMoveSession.RequestedPosition.X, _activeMoveSession.RequestedPosition.Y,
                    _activeMoveSession.LastKnownPosition.X, _activeMoveSession.LastKnownPosition.Y,
                    result.IsExact);
                StatusText.ScrollToHome();
            }
        }
        catch (Exception ex)
        {
            if (_activeMoveSession is null)
            {
                StatusText.Text = $"[M1-04B] 移动已中止，未进入 Writer 阶段。\r\n{ex}";
                StatusText.ScrollToHome();
            }
            else
            {
                StatusText.Text = string.Format(
                    "[M1-04B] 移动调用失败或结果不确定，恢复会话已保留。\r\nSafetySnapshotId：{0:D}\r\nStableId：{1}\r\n原位置：({2},{3})\r\n异常：{4}",
                    _activeMoveSession.SafetySnapshotId, _activeMoveSession.StableId,
                    _activeMoveSession.OriginalPosition.X, _activeMoveSession.OriginalPosition.Y, ex);
                StatusText.ScrollToHome();
            }
        }
        finally { _moveOperationInProgress = false; UpdateInteractiveState(); }
    }

    // ═══════════════════════════════════════════════════
    //  恢复原位置
    // ═══════════════════════════════════════════════════

    private async void RestoreOriginalPosition_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
        if (_activeMoveSession is null) return;

        var session = _activeMoveSession;
        _moveOperationInProgress = true;
        UpdateInteractiveState();
        StatusText.Text = "[M1-04B] 正在恢复原位置…";

        try
        {
            var result = await _staExecutor.RunAsync(() =>
                _desktopIconPositionWriter.MoveDesktopIcon(
                    session.StableId, session.OriginalPosition));

            _activeMoveSession = _activeMoveSession! with { LastKnownPosition = result!.ActualPosition };

            if (result.ActualPosition == session.OriginalPosition)
            {
                TryUpdateCachedIconPosition(session.StableId, result.ActualPosition);
                _activeMoveSession = null;
                MoveConsentCheckBox.IsChecked = false;
                MoveSessionText.Text = "恢复成功，当前没有待恢复的移动会话。";
                StatusText.Text = string.Format(
                    "[M1-04B] 恢复完成。\r\nSafetySnapshotId：{0:D}\r\n原位置：({1},{2})\r\n恢复实际位置：({3},{4})",
                    session.SafetySnapshotId,
                    session.OriginalPosition.X, session.OriginalPosition.Y,
                    result.ActualPosition.X, result.ActualPosition.Y);
            }
            else
            {
                StatusText.Text = string.Format(
                    "[M1-04B] 恢复未达到原位置。\r\n原位置：({0},{1})\r\n实际位置：({2},{3})\r\nIsExact：{4}\r\n可再次点击恢复。",
                    session.OriginalPosition.X, session.OriginalPosition.Y,
                    result.ActualPosition.X, result.ActualPosition.Y, result.IsExact);
            }
            StatusText.ScrollToHome();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"[M1-04B] 恢复调用失败（可重试）。\r\n异常：{ex}";
            StatusText.ScrollToHome();
        }
        finally { _moveOperationInProgress = false; UpdateInteractiveState(); }
    }

    // ═══════════════════════════════════════════════════
    //  窗口关闭保护
    // ═══════════════════════════════════════════════════

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsAnyOperationInProgress)
        {
            MessageBox.Show("有操作正在进行中，请等待完成后再关闭窗口。", "无法关闭", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }

        if (_activeMoveSession is null) return;

        var result = MessageBox.Show(
            string.Format("有一个图标尚未确认恢复！\r\n\r\n图标：{0}\r\n原位置：({1},{2})\r\n最后已知位置：({3},{4})\r\n安全快照：{5:D}\r\n\r\n仍然关闭窗口？（图标将保持在当前位置，不会自动恢复）",
                _activeMoveSession.DisplayName,
                _activeMoveSession.OriginalPosition.X, _activeMoveSession.OriginalPosition.Y,
                _activeMoveSession.LastKnownPosition.X, _activeMoveSession.LastKnownPosition.Y,
                _activeMoveSession.SafetySnapshotId),
            "未恢复的移动会话", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) e.Cancel = true;
    }

    // ═══════════════════════════════════════════════════
    //  行模型
    // ═══════════════════════════════════════════════════

    private sealed record DisplayRow(
        string DeviceName, bool IsPrimary, string BoundsText, string WorkAreaText,
        string DpiText, string ScaleText, string TargetMarker)
    {
        public static DisplayRow From(DisplayDescriptor d, bool isTarget) => new(
            d.DeviceName, d.IsPrimary,
            Format(d.Bounds.Left, d.Bounds.Top, d.Bounds.Width, d.Bounds.Height),
            Format(d.WorkArea.Left, d.WorkArea.Top, d.WorkArea.Width, d.WorkArea.Height),
            $"{d.DpiX} × {d.DpiY}", $"{d.ScaleX:P0} × {d.ScaleY:P0}", isTarget ? "●" : "○");
        private static string Format(int l, int t, int w, int h) => $"({l}, {t})  {w} × {h}";
    }

    private sealed record IconRow(
        string StableId, string DisplayName, string StableIdTruncated, string ItemKind,
        int PixelX, int PixelY, string MonitorDisplay, string TargetScreenMarker,
        bool IsFileSystemItem, bool IsSystemItem, bool IsOnTarget, string? MonitorDeviceName)
    {
        public static IconRow From(DesktopIconDescriptor icon, bool isOnTarget) => new(
            icon.StableId, icon.DisplayName, Truncate(icon.StableId, 60),
            icon.ItemKind.ToString(), icon.PixelPosition.X, icon.PixelPosition.Y,
            icon.MonitorDeviceName ?? "—", isOnTarget ? "★" : "",
            icon.IsFileSystemItem, icon.IsSystemItem, isOnTarget, icon.MonitorDeviceName);
        private static string Truncate(string v, int max) => v.Length <= max ? v : v[..(max - 3)] + "...";
    }

    private sealed record SnapshotRow(
        string CreatedAtLocal, string SnapshotId, int IconCount,
        string TargetMonitorDeviceName, string ConnectionMethod, int FormatVersion)
    {
        public static SnapshotRow From(DesktopLayoutSnapshotSummary s) => new(
            s.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            s.SnapshotId.ToString("D"), s.IconCount, s.TargetMonitorDeviceName ?? "—",
            s.ConnectionMethod.ToString(), s.FormatVersion);
    }
}
