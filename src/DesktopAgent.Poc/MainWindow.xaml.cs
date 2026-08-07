using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Poc.LayoutTemplates.Editor;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Poc;

public partial class MainWindow : Window
{
    private readonly IDisplayTopologyReader _displayTopologyReader;
    private readonly IDesktopIconReader _desktopIconReader;
    private readonly IDesktopIconPositionWriter _desktopIconPositionWriter;
    private readonly StaThreadExecutor _staExecutor;
    private readonly DesktopLayoutSnapshotFactory _snapshotFactory;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;
    private readonly IDesktopLayoutSafetySnapshotFactory _safetySnapshotFactory;
    private readonly TargetDisplayLayoutExecutionCoordinator _layoutExecutionCoordinator;
    private readonly IDesktopLayoutTemplateStore _templateStore;
    private readonly IDesktopLayoutIconRuleStore _ruleStore;
    private readonly IRecoverySessionStore _recoverySessionStore;
    private readonly PersistedRecoveryCoordinator _recoveryCoordinator;

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

    // M1-05C-B
    private LayoutRestorePreviewSession? _layoutRestorePreview;
    private bool _layoutRestoreOperationInProgress;
    private bool _layoutRestoreRequiresAttention;
    private TargetDisplayLayoutExecutionRunResult? _lastLayoutExecutionResult;
    /// <summary>布局恢复执行时逃逸异常诊断（_lastLayoutExecutionResult 为 null 时使用）。</summary>
    private string? _layoutRestoreEscapedException;

    private bool IsAnyOperationInProgress =>
        _iconReadInProgress || _snapshotOperationInProgress || _moveOperationInProgress
        || _layoutRestoreOperationInProgress;

    public MainWindow(
        IDisplayTopologyReader displayTopologyReader,
        IDesktopIconReader desktopIconReader,
        IDesktopIconPositionWriter desktopIconPositionWriter,
        StaThreadExecutor staExecutor,
        DesktopLayoutSnapshotFactory snapshotFactory,
        IDesktopLayoutSnapshotStore snapshotStore,
        IDesktopLayoutSafetySnapshotFactory safetySnapshotFactory,
        TargetDisplayLayoutExecutionCoordinator layoutExecutionCoordinator,
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        IRecoverySessionStore recoverySessionStore,
        PersistedRecoveryCoordinator recoveryCoordinator)
    {
        InitializeComponent();

        _displayTopologyReader = displayTopologyReader;
        _desktopIconReader = desktopIconReader;
        _desktopIconPositionWriter = desktopIconPositionWriter;
        _staExecutor = staExecutor;
        _snapshotFactory = snapshotFactory;
        _snapshotStore = snapshotStore;
        _safetySnapshotFactory = safetySnapshotFactory;
        _layoutExecutionCoordinator = layoutExecutionCoordinator;
        _templateStore = templateStore;
        _ruleStore = ruleStore;
        _recoverySessionStore = recoverySessionStore;
        _recoveryCoordinator = recoveryCoordinator;

        DisplaysGrid.ItemsSource = _displayRows;
        IconsGrid.ItemsSource = _iconRows;
        SnapshotsGrid.ItemsSource = _snapshotRows;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private sealed record MoveRecoverySession(
        Guid SafetySnapshotId,
        string StableId,
        string DisplayName,
        string MonitorDeviceName,
        PixelPoint OriginalPosition,
        PixelPoint RequestedPosition,
        PixelPoint LastKnownPosition,
        DateTime StartedAtUtc,
        bool IsPositionUncertain = false);

    private void UpdateInteractiveState()
    {
        var busy = IsAnyOperationInProgress;
        var hasSession = _activeMoveSession is not null;

        RefreshDisplaysButton.IsEnabled = !busy && !hasSession;
        DisplaysGrid.IsEnabled = !busy && !hasSession;
        ReadIconsButton.IsEnabled = !busy && !hasSession;
        IconsGrid.IsEnabled = !busy && !hasSession;

        var canSaveSnapshot = !busy && !hasSession && _snapshotStoreReady
            && _lastReadResult is not null && _lastReadResult.FailedItemCount == 0;
        SaveSnapshotButton.IsEnabled = canSaveSnapshot;

        RefreshSnapshotsButton.IsEnabled = !busy && !hasSession && _snapshotStoreReady;
        MoveConsentCheckBox.IsEnabled = !busy && !hasSession && !_layoutRestoreRequiresAttention;

        var canMove = !busy && !hasSession && !_layoutRestoreRequiresAttention && _snapshotStoreReady
            && MoveConsentCheckBox.IsChecked == true
            && _lastReadResult is not null && _lastReadResult.FailedItemCount == 0
            && _currentTarget is not null
            && IconsGrid.SelectedItem is IconRow selectedIconRow
            && selectedIconRow.IsFileSystemItem && !selectedIconRow.IsSystemItem
            && selectedIconRow.IsOnTarget
            && selectedIconRow.MonitorDeviceName is not null
            && string.Equals(selectedIconRow.MonitorDeviceName, _currentTarget.DeviceName, StringComparison.Ordinal);
        MoveSelectedIconButton.IsEnabled = canMove;

        RestoreOriginalPositionButton.IsEnabled = hasSession && !busy;

        var canPreviewLayout = !busy && !hasSession && !_layoutRestoreRequiresAttention
            && _snapshotStoreReady && _currentTarget is not null
            && SnapshotsGrid.SelectedItem is SnapshotRow;
        PreviewLayoutRestoreButton.IsEnabled = canPreviewLayout;

        var canExecuteLayout = !busy && !hasSession && !_layoutRestoreRequiresAttention
            && _layoutRestorePreview?.GateResult.IsAllowed == true
            && LayoutRestoreConsentCheckBox.IsChecked == true
            && SnapshotsGrid.SelectedItem is SnapshotRow selectedSnapRow
            && selectedSnapRow.SnapshotId == _layoutRestorePreview.SnapshotId
            && _currentTarget?.DeviceName == _layoutRestorePreview.TargetDeviceName;
        ExecuteLayoutRestoreButton.IsEnabled = canExecuteLayout;

        OpenTemplateEditorButton.IsEnabled = !busy && !hasSession && !_layoutRestoreRequiresAttention;
        OpenLayoutPreviewButton.IsEnabled = !busy && !hasSession && !_layoutRestoreRequiresAttention;

        if (hasSession && !busy)
        {
            var s = _activeMoveSession!;
            var posLabel = s.IsPositionUncertain
                ? $"位置状态：不确定；最后确认位置：({s.LastKnownPosition.X},{s.LastKnownPosition.Y})"
                : $"最后确认位置：({s.LastKnownPosition.X},{s.LastKnownPosition.Y})";
            MoveSessionText.Text = string.Format(
                "⚠ 活跃恢复会话 — 图标: {0}  |  原位置: ({1},{2})  |  {3}  |  安全快照: {4:D}",
                s.DisplayName, s.OriginalPosition.X, s.OriginalPosition.Y,
                posLabel, s.SafetySnapshotId);
        }
    }

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

        await CheckRecoveryStateAsync();

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

    private void RefreshDisplays_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
        if (_activeMoveSession is not null)
        {
            StatusText.Text = "[M1-04B] 有活跃恢复会话，请先恢复原位置后再刷新显示器拓扑。";
            return;
        }
        InvalidateLayoutRestorePreview("显示器拓扑即将刷新。");
        InvalidateLastIconRead("显示器拓扑已刷新，请重新读取桌面图标。");
        RefreshDisplays();
    }

    private void DisplaysGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (DisplaysGrid.SelectedItem is DisplayRow selectedRow)
        {
            var matched = _cachedDisplays.FirstOrDefault(
                d => string.Equals(d.DeviceName, selectedRow.DeviceName, StringComparison.Ordinal));
            if (matched is not null)
            {
                var deviceChanged = _currentTarget is null
                    || !string.Equals(_currentTarget.DeviceName, matched.DeviceName, StringComparison.Ordinal);
                _currentTarget = matched;
                _previousTargetDeviceName = matched.DeviceName;
                UpdateTargetDisplay();
                RebuildIconRowsFromLastRead();
                if (deviceChanged)
                    InvalidateLayoutRestorePreview("目标显示器已变化。");
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
                var isTarget = target is not null
                    && string.Equals(display.DeviceName, target.DeviceName, StringComparison.Ordinal);
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
                if (string.Equals(row.DeviceName, target.DeviceName, StringComparison.Ordinal))
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
            var isOnTarget = targetDeviceName is not null
                && string.Equals(icon.MonitorDeviceName, targetDeviceName, StringComparison.Ordinal);
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

        var matchCount = 0;
        var matchIndex = -1;
        for (var i = 0; i < _lastReadResult.Icons.Count; i++)
        {
            if (string.Equals(_lastReadResult.Icons[i].StableId, stableId, StringComparison.Ordinal))
            { matchCount++; matchIndex = i; }
        }
        if (matchCount != 1 || matchIndex < 0) return false;

        var icons = _lastReadResult.Icons.ToList();
        var monitor = DisplayAssignmentService.AssignMonitor(actualPosition, _cachedDisplays);
        icons[matchIndex] = icons[matchIndex] with
        {
            PixelPosition = actualPosition,
            MonitorDeviceName = monitor?.DeviceName
        };

        _lastReadResult = new DesktopIconReadResult(
            icons, _lastReadResult.FailedItemCount, _lastReadResult.ConnectionMethod,
            _lastReadResult.ReadTime, _lastReadResult.PrimaryPathException);
        RebuildIconRowsFromLastRead();
        return true;
    }

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

    private async void ReadDesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
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
            var fallbackNote = readResult.PrimaryPathException is not null
                ? $"  |  主路径失败：{readResult.PrimaryPathException.Message}" : "";

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
        finally { _iconReadInProgress = false; UpdateInteractiveState(); }
    }

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
                InvalidateLayoutRestorePreview("新快照已保存。");
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

    private async void RefreshSnapshots_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
        if (_activeMoveSession is not null) { StatusText.Text = "[M1-04B] 请先恢复原位置。"; return; }
        if (!_snapshotStoreReady) { StatusText.Text = "数据库未初始化。"; return; }

        InvalidateLayoutRestorePreview("快照列表即将刷新。");
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

    private void IconsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInteractiveState();
    private void MoveConsentChanged(object sender, RoutedEventArgs e) => UpdateInteractiveState();

    private async void MoveSelectedIcon_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
        if (_activeMoveSession is not null) { StatusText.Text = "请先恢复原位置。"; return; }
        if (_layoutRestoreRequiresAttention)
        {
            StatusText.Text = "[M1-04B] 布局恢复需要人工处理，禁止继续真实写入。请重新启动 PoC。";
            return;
        }
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
            var (capabilities, freshRawResult) = await _staExecutor.RunAsync(() =>
            {
                var cap = _desktopIconPositionWriter.GetCapabilities();
                var raw = _desktopIconReader.ReadDesktopIcons();
                return (cap, raw);
            });
            var freshAssignedReadResult = AssignMonitorOwnership(freshRawResult, displaysSnapshot);

            var targetDisplay = UniqueDisplaySelector.FindByDeviceName(
                displaysSnapshot, targetDeviceName);

            var preparation = DesktopIconMovePreparationService.Prepare(
                selectedStableId, freshAssignedReadResult, targetDisplay, capabilities);

            var confirmResult = MessageBox.Show(
                string.Format("即将真实移动一个桌面图标。\r\n\r\n图标：{0}\r\nStableId：{1}\r\n目标显示器：{2}\r\n原位置：({3}, {4})\r\n目标位置：({5}, {6})\r\n方向：{7}\r\n图标间距：({8}, {9})\r\n\r\n将先保存并验证完整安全快照。移动后必须点击「恢复原位置」还原。",
                    preparation.SelectedIcon.DisplayName, selectedStableId, targetDeviceName,
                    preparation.SelectedIcon.PixelPosition.X, preparation.SelectedIcon.PixelPosition.Y,
                    preparation.Plan.TargetPosition.X, preparation.Plan.TargetPosition.Y,
                    preparation.Plan.Direction,
                    capabilities.Spacing.X, capabilities.Spacing.Y),
                "确认移动桌面图标", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (confirmResult != MessageBoxResult.Yes)
            { StatusText.Text = "[M1-04B] 用户取消移动操作。"; return; }

            StatusText.Text = "[M1-04B] 正在保存安全快照…";
            var snapshot = _snapshotFactory.Create(
                freshAssignedReadResult.Icons, targetDeviceName, freshAssignedReadResult.ConnectionMethod);
            await _snapshotStore.SaveAsync(snapshot);

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
                throw new InvalidOperationException($"安全快照位置不一致。");

            try { await RefreshSnapshotsAsync(); UpdateSnapshotStats(); }
            catch (Exception refreshEx) { throw new InvalidOperationException("安全快照已保存但列表刷新失败。", refreshEx); }

            _activeMoveSession = new MoveRecoverySession(
                snapshot.SnapshotId,
                preparation.SelectedIcon.StableId,
                preparation.SelectedIcon.DisplayName,
                targetDeviceName,
                preparation.SelectedIcon.PixelPosition,
                preparation.Plan.TargetPosition,
                preparation.SelectedIcon.PixelPosition,
                DateTime.UtcNow,
                IsPositionUncertain: false);

            StatusText.Text = "[M1-04B] 正在移动图标…";
            var result = await _staExecutor.RunAsync(() =>
                _desktopIconPositionWriter.MoveDesktopIcon(
                    _activeMoveSession!.StableId,
                    _activeMoveSession.RequestedPosition));

            _activeMoveSession = _activeMoveSession! with
            {
                LastKnownPosition = result!.ActualPosition,
                IsPositionUncertain = false
            };

            if (result.ActualPosition == _activeMoveSession.OriginalPosition)
            {
                _activeMoveSession = null;
                StatusText.Text = "[M1-04B] 图标未发生移动（实际位置等于原位置）。";
                StatusText.ScrollToHome();
                MoveSessionText.Text = "图标位置未变化，无需恢复。";
            }
            else
            {
                var msg = string.Format(
                    "[M1-04B] 图标已移动。\r\nSafetySnapshotId：{0:D}\r\n原位置：({1},{2})\r\n请求位置：({3},{4})\r\n实际位置：({5},{6})\r\nIsExact：{7}\r\n\r\n请点击「恢复原位置」还原图标。",
                    _activeMoveSession.SafetySnapshotId,
                    _activeMoveSession.OriginalPosition.X, _activeMoveSession.OriginalPosition.Y,
                    _activeMoveSession.RequestedPosition.X, _activeMoveSession.RequestedPosition.Y,
                    _activeMoveSession.LastKnownPosition.X, _activeMoveSession.LastKnownPosition.Y,
                    result.IsExact);

                var cacheOk = TryUpdateCachedIconPosition(_activeMoveSession!.StableId, result.ActualPosition);
                if (!cacheOk)
                    msg += "\r\n\r\n警告：桌面实际位置已返回，但 PoC 缓存未更新，请恢复后重新读取桌面图标。";

                StatusText.Text = msg;
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
                _activeMoveSession = _activeMoveSession with { IsPositionUncertain = true };
                StatusText.Text = string.Format(
                    "[M1-04B] 移动调用失败或结果不确定，恢复会话已保留。\r\nSafetySnapshotId：{0:D}\r\nStableId：{1}\r\n原位置：({2},{3})\r\n位置状态：不确定\r\n异常：{4}",
                    _activeMoveSession.SafetySnapshotId, _activeMoveSession.StableId,
                    _activeMoveSession.OriginalPosition.X, _activeMoveSession.OriginalPosition.Y, ex);
                StatusText.ScrollToHome();
            }
        }
        finally { _moveOperationInProgress = false; UpdateInteractiveState(); }
    }

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

            _activeMoveSession = _activeMoveSession! with
            {
                LastKnownPosition = result!.ActualPosition,
                IsPositionUncertain = false
            };

            if (result.ActualPosition == session.OriginalPosition)
            {
                var msg = string.Format(
                    "[M1-04B] 恢复完成。\r\nSafetySnapshotId：{0:D}\r\n原位置：({1},{2})\r\n恢复实际位置：({3},{4})",
                    session.SafetySnapshotId,
                    session.OriginalPosition.X, session.OriginalPosition.Y,
                    result.ActualPosition.X, result.ActualPosition.Y);

                var cacheOk = TryUpdateCachedIconPosition(session.StableId, result.ActualPosition);
                if (!cacheOk)
                    msg += "\r\n\r\n警告：PoC 缓存未更新，请重新读取桌面图标。";

                _activeMoveSession = null;
                MoveConsentCheckBox.IsChecked = false;
                MoveSessionText.Text = "恢复成功，当前没有待恢复的移动会话。";
                StatusText.Text = msg;
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
            if (_activeMoveSession is not null)
                _activeMoveSession = _activeMoveSession with { IsPositionUncertain = true };

            StatusText.Text = $"[M1-04B] 恢复调用失败（可重试）。\r\n位置状态：不确定\r\n异常：{ex}";
            StatusText.ScrollToHome();
        }
        finally { _moveOperationInProgress = false; UpdateInteractiveState(); }
    }

    private void OpenTemplateEditor_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress)
        {
            StatusText.Text = "[MainWindow] 有操作正在进行中，无法打开模板编辑器。";
            return;
        }
        if (_activeMoveSession is not null)
        {
            StatusText.Text = "[MainWindow] 有活跃移动会话，请先恢复原位置。";
            return;
        }
        if (_layoutRestoreRequiresAttention)
        {
            StatusText.Text = "[MainWindow] 布局恢复需要人工处理，无法打开模板编辑器。";
            return;
        }

        var knownDeviceNames = _cachedDisplays
            .Select(d => d.DeviceName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var editor = new LayoutTemplateEditorWindow(_templateStore, _ruleStore, knownDeviceNames)
        {
            Owner = this
        };
        editor.ShowDialog();
    }

    private void OpenLayoutPreview_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress || _activeMoveSession is not null || _layoutRestoreRequiresAttention)
            return;

        var window = new LayoutTemplates.Preview.LayoutPreviewWindow(
            _templateStore,
            _ruleStore,
            _displayTopologyReader,
            _desktopIconReader,
            _desktopIconPositionWriter,
            _staExecutor,
            _snapshotFactory,
            _layoutExecutionCoordinator,
            _snapshotStore,
            _safetySnapshotFactory)
        {
            Owner = this
        };
        window.ShowDialog();

        if (window.RequiresManualRecovery)
        {
            _layoutRestoreRequiresAttention = true;
            _lastLayoutExecutionResult = window.LastExecutionResult;
            _layoutRestoreEscapedException = window.EscapedException;

            StatusText.Text = "[MainWindow] 布局预览窗口返回人工处理锁：所有旧写入入口已禁用。";
            LayoutRestorePreviewText.Text = "布局恢复已禁用（来自布局预览的批量执行人工处理锁）。";
        }
        else
        {
            _lastLayoutExecutionResult = null;
            _layoutRestoreEscapedException = null;
        }
        UpdateInteractiveState();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsAnyOperationInProgress)
        {
            MessageBox.Show("有操作正在进行中，请等待完成后再关闭窗口。", "无法关闭",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }

        if (_layoutRestoreRequiresAttention)
        {
            string msg;
            if (_lastLayoutExecutionResult is not null)
            {
                var r = _lastLayoutExecutionResult;
                msg = $"布局恢复需要人工处理！\n\n" +
                    $"Status: {r.Status}\nSafetySnapshotId: {r.SafetySnapshotId}\n" +
                    $"SessionId: {r.Session?.SessionId}\nFailedStepIndex: {r.FailedStepIndex}\n" +
                    $"ForwardFailure: {r.ForwardFailureType}: {r.ForwardFailureMessage}\n" +
                    $"RecoveryFailure: {r.RecoveryFailureType}: {r.RecoveryFailureMessage}\n\n" +
                    $"不会自动恢复。仍然关闭窗口？";
            }
            else
            {
                msg = $"布局恢复需要人工处理！\n\n" +
                    $"执行结果不可用（逃逸异常）。\n" +
                    $"异常: {_layoutRestoreEscapedException ?? "—"}\n\n" +
                    $"不会自动恢复。仍然关闭窗口？";
            }
            var dr = MessageBox.Show(msg, "需要人工处理 — 布局恢复",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (dr != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }

        if (_activeMoveSession is null) return;

        var s = _activeMoveSession;
        var posInfo = s.IsPositionUncertain
            ? $"位置状态：不确定；最后确认位置：({s.LastKnownPosition.X},{s.LastKnownPosition.Y})"
            : $"最后已知位置：({s.LastKnownPosition.X},{s.LastKnownPosition.Y})";

        var result = MessageBox.Show(
            string.Format("有一个图标尚未确认恢复！\r\n\r\n图标：{0}\r\n原位置：({1},{2})\r\n{3}\r\n安全快照：{4:D}\r\n\r\n仍然关闭窗口？（图标将保持在当前位置，不会自动恢复）",
                s.DisplayName,
                s.OriginalPosition.X, s.OriginalPosition.Y,
                posInfo, s.SafetySnapshotId),
            "未恢复的移动会话", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) e.Cancel = true;
    }

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
        string CreatedAtLocal, Guid SnapshotId, int IconCount,
        string TargetMonitorDeviceName, string ConnectionMethod, int FormatVersion)
    {
        public static SnapshotRow From(DesktopLayoutSnapshotSummary s) => new(
            s.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            s.SnapshotId, s.IconCount, s.TargetMonitorDeviceName ?? "—",
            s.ConnectionMethod.ToString(), s.FormatVersion);
    }

    private sealed record LayoutRestorePreviewSession(
        Guid SnapshotId,
        string TargetDeviceName,
        DateTimeOffset CreatedAtUtc,
        TargetDisplayLayoutRestorePlan SourceRestorePlan,
        TargetDisplayLayoutExecutionPlan PreviewExecutionPlan,
        TargetDisplayLayoutExecutionScopeGate.GateResult GateResult);

    private async Task CheckRecoveryStateAsync()
    {
        try
        {
            await _recoverySessionStore.InitializeAsync();
            var unresolved = await _recoveryCoordinator.ListUnresolvedAsync();
            if (unresolved.Count > 0)
            {
                _layoutRestoreRequiresAttention = true;
                var s = unresolved[0];
                RecoveryStatusText.Text =
                    $"会话: {s.SessionId.ToString("D")[..8]}...\r\n" +
                    $"显示器: {s.TargetDeviceName}\r\n" +
                    $"开始: {s.StartedAtUtc:yyyy-MM-dd HH:mm}\r\n" +
                    $"状态: {s.Lifecycle}\r\n" +
                    $"Forward完成: {s.CompletedForwardStepIndexes.Count}\r\n" +
                    $"Rollback完成: {s.CompletedRollbackSourceStepIndexes.Count}\r\n" +
                    $"位置不确定: {s.GetDisposition().IsPositionUncertain}";
                RecoveryBorder.Visibility = System.Windows.Visibility.Visible;
            }
        }
        catch
        {
            _layoutRestoreRequiresAttention = true;
            RecoveryStatusText.Text = "恢复数据损坏 — 需要人工处理。";
            RecoveryBorder.Visibility = System.Windows.Visibility.Visible;
            RecoveryRestoreButton.IsEnabled = false;
        }
        UpdateInteractiveState();
    }

    private async void RecoveryRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutRestoreOperationInProgress) return;
        var result = System.Windows.MessageBox.Show(
            "将使用持久化的恢复会话执行保守回滚。\r\n不会执行新的 Forward 步骤。\r\n继续？",
            "跨重启恢复确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        RecoveryRestoreButton.IsEnabled = false;
        _layoutRestoreOperationInProgress = true;
        try
        {
            var unresolved = await _recoveryCoordinator.ListUnresolvedAsync();
            if (unresolved.Count == 0)
            {
                _layoutRestoreRequiresAttention = false;
                RecoveryBorder.Visibility = System.Windows.Visibility.Collapsed;
                UpdateInteractiveState();
                return;
            }

            foreach (var session in unresolved)
            {
                await _recoveryCoordinator.RecoverAsync(session);
            }

            var after = await _recoveryCoordinator.ListUnresolvedAsync();
            if (after.Count == 0)
            {
                _layoutRestoreRequiresAttention = false;
                RecoveryBorder.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                RecoveryStatusText.Text = $"仍有 {after.Count} 个未解决会话。";
            }
        }
        catch
        {
            _layoutRestoreRequiresAttention = true;
            RecoveryStatusText.Text = "恢复过程异常 — 需要人工处理。";
        }
        finally
        {
            _layoutRestoreOperationInProgress = false;
            RecoveryRestoreButton.IsEnabled = true;
            UpdateInteractiveState();
        }
    }

    private void InvalidateLayoutRestorePreview(string reason)
    {
        _layoutRestorePreview = null;
        LayoutRestorePreviewText.Text = $"预览已失效：{reason}";
        LayoutRestoreConsentCheckBox.IsChecked = false;
        ExecuteLayoutRestoreButton.IsEnabled = false;
        UpdateInteractiveState();
    }

    private void SnapshotsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        InvalidateLayoutRestorePreview("快照选择已变化。");

    private void LayoutRestoreConsentChanged(object sender, RoutedEventArgs e) =>
        UpdateInteractiveState();

    private async void PreviewLayoutRestore_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
        if (_activeMoveSession is not null)
        {
            StatusText.Text = "有活跃 M1-04B 移动会话，请先恢复。";
            return;
        }
        if (_layoutRestoreRequiresAttention)
        {
            StatusText.Text = "布局恢复需要人工处理，请重新启动 PoC。";
            return;
        }
        if (!_snapshotStoreReady) { StatusText.Text = "数据库未初始化。"; return; }
        if (SnapshotsGrid.SelectedItem is not SnapshotRow row)
        {
            StatusText.Text = "未选择快照。";
            return;
        }
        if (_currentTarget is null)
        {
            StatusText.Text = "未选择目标显示器。";
            return;
        }

        var snapshotId = row.SnapshotId;
        var targetDeviceName = _currentTarget.DeviceName;

        InvalidateLayoutRestorePreview("开始新预览。");
        _layoutRestoreOperationInProgress = true;
        UpdateInteractiveState();

        var sb = new System.Text.StringBuilder();
        try
        {
            sb.AppendLine($"SnapshotId: {snapshotId:D}");
            sb.AppendLine($"TargetDeviceName: {targetDeviceName}");

            var snapshot = await _snapshotStore.GetAsync(snapshotId);
            if (snapshot is null)
            {
                sb.AppendLine("快照不存在。");
                StatusText.Text = sb.ToString();
                return;
            }

            var displays = _displayTopologyReader.GetDisplays();
            var targetDisplay = UniqueDisplaySelector.FindByDeviceName(displays, targetDeviceName);

            var capabilities = await _staExecutor.RunAsync(() =>
                _desktopIconPositionWriter.GetCapabilities());
            var rawRead = await _staExecutor.RunAsync(() => _desktopIconReader.ReadDesktopIcons());
            var assignedIcons = rawRead.Icons.Select(icon =>
            {
                var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays);
                return icon with { MonitorDeviceName = monitor?.DeviceName };
            }).ToList();
            var currentRead = new DesktopIconReadResult(assignedIcons, rawRead.FailedItemCount,
                rawRead.ConnectionMethod, rawRead.ReadTime, rawRead.PrimaryPathException);

            var rp = TargetDisplayLayoutRestorePlanner.CreatePlan(
                snapshot, currentRead, displays, capabilities, targetDeviceName);
            var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(
                rp, currentRead, displays, capabilities);

            var gate = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(rp, ep);

            _layoutRestorePreview = new LayoutRestorePreviewSession(
                snapshotId, targetDeviceName, snapshot.CreatedAtUtc, rp, ep, gate);

            sb.AppendLine($"\n--- RestorePlan ---");
            sb.AppendLine($"SnapshotTargetIconCount: {rp.SnapshotTargetIconCount}");
            sb.AppendLine($"AlreadyAtTargetCount: {rp.AlreadyAtTargetCount}");
            sb.AppendLine($"MoveRequiredCount: {rp.MoveRequiredCount}");
            sb.AppendLine($"MissingCount: {rp.MissingCount}");
            sb.AppendLine($"BlockedCount: {rp.BlockedCount}");
            sb.AppendLine($"HasBlockingErrors: {rp.HasBlockingErrors}");
            sb.AppendLine($"RequiresTemporaryStaging: {rp.RequiresTemporaryStaging}");
            sb.AppendLine($"IsNoOp: {rp.IsNoOp}");

            sb.AppendLine($"\n--- ExecutionPlan ---");
            sb.AppendLine($"IsExecutable: {ep.IsExecutable}");
            sb.AppendLine($"IsNoOp: {ep.IsNoOp}");
            sb.AppendLine($"UsesTemporaryStaging: {ep.UsesTemporaryStaging}");
            sb.AppendLine($"Steps.Count: {ep.Steps.Count}");

            sb.AppendLine($"\n--- Gate ---");
            sb.AppendLine($"IsAllowed: {gate.IsAllowed}");
            foreach (var reason in gate.RejectionReasons)
                sb.AppendLine($"  拒绝: {reason}");

            sb.AppendLine($"\n--- Items ---");
            foreach (var item in rp.Items)
                sb.AppendLine($"  {item.DisplayName} {item.StableId} [{item.State}] " +
                    $"CurPos=({item.CurrentPosition?.X},{item.CurrentPosition?.Y}) " +
                    $"Target=({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y}) " +
                    $"Mon={item.CurrentMonitorDeviceName ?? "—"} Block={item.BlockReason ?? "—"}");

            sb.AppendLine($"\n--- Steps ---");
            foreach (var step in ep.Steps)
                sb.AppendLine($"  [{step.StepIndex}] {step.Kind} {step.StableId} " +
                    $"({step.FromPosition.X},{step.FromPosition.Y})→({step.ToPosition.X},{step.ToPosition.Y}) " +
                    $"Mon={step.ExpectedMonitorDeviceName}");

            LayoutRestorePreviewText.Text = gate.IsAllowed ? "预览通过。单图标、单步骤、无中转。" : "预览未通过闸门。";
            StatusText.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"预览异常: {ex.GetType().Name}: {ex.Message}");
            StatusText.Text = sb.ToString();
            LayoutRestorePreviewText.Text = "预览失败。";
            _layoutRestorePreview = null;
        }
        finally
        {
            _layoutRestoreOperationInProgress = false;
            UpdateInteractiveState();
        }
    }

    private async void ExecuteLayoutRestore_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyOperationInProgress) return;
        if (_activeMoveSession is not null)
        {
            StatusText.Text = "有活跃 M1-04B 移动会话，请先恢复。";
            return;
        }
        if (_layoutRestoreRequiresAttention)
        {
            StatusText.Text = "布局恢复需要人工处理，请重新启动 PoC。";
            return;
        }
        if (!_snapshotStoreReady) { StatusText.Text = "数据库未初始化。"; return; }
        if (LayoutRestoreConsentCheckBox.IsChecked != true)
        {
            StatusText.Text = "请先勾选同意框。";
            return;
        }
        if (_layoutRestorePreview is null)
        {
            StatusText.Text = "没有有效预览，请先预览。";
            return;
        }
        if (SnapshotsGrid.SelectedItem is not SnapshotRow selectedRow
            || selectedRow.SnapshotId != _layoutRestorePreview.SnapshotId)
        {
            StatusText.Text = "所选快照与预览不匹配，请重新预览。";
            InvalidateLayoutRestorePreview("快照不匹配。");
            return;
        }
        if (_currentTarget is null
            || !string.Equals(_currentTarget.DeviceName, _layoutRestorePreview.TargetDeviceName, StringComparison.Ordinal))
        {
            StatusText.Text = "目标显示器已变化，请重新预览。";
            InvalidateLayoutRestorePreview("目标显示器不匹配。");
            return;
        }

        var reGate = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(
            _layoutRestorePreview.SourceRestorePlan, _layoutRestorePreview.PreviewExecutionPlan);
        if (!reGate.IsAllowed)
        {
            StatusText.Text = $"执行前闸门被拒绝:\n{string.Join("\n", reGate.RejectionReasons)}";
            InvalidateLayoutRestorePreview("预览闸门复检失败。");
            return;
        }

        var preview = _layoutRestorePreview;

        TargetDisplayLayoutRestorePlanItem moveItem;
        TargetDisplayLayoutExecutionStep step;
        {
            var moveItems = preview.SourceRestorePlan.Items
                .Where(it => it.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
                .ToList();
            if (moveItems.Count != 1)
            {
                StatusText.Text = $"MoveRequired item 数量 = {moveItems.Count}，预期 1。请重新预览。";
                InvalidateLayoutRestorePreview("MoveRequired 数量异常。");
                return;
            }
            moveItem = moveItems[0];
            step = preview.PreviewExecutionPlan.Steps[0];
        }

        var confirmMsg =
            $"确认执行受控布局恢复？\n\n" +
            $"SnapshotId: {preview.SnapshotId}\n" +
            $"图标: {moveItem.DisplayName} ({moveItem.StableId})\n" +
            $"From: ({step.FromPosition.X}, {step.FromPosition.Y})\n" +
            $"To: ({step.ToPosition.X}, {step.ToPosition.Y})\n" +
            $"目标显示器: {preview.TargetDeviceName}\n\n" +
            $"Coordinator 会再次读取桌面、重新规划并保存新的完整安全快照。\n" +
            $"该操作不可撤销。";

        var dlgResult = MessageBox.Show(confirmMsg, "二次确认 — 受控布局恢复",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (dlgResult != MessageBoxResult.Yes)
        {
            StatusText.Text += "\n用户取消执行。";
            return;
        }

        InvalidateLayoutRestorePreview("正在执行，旧预览已失效。");
        _layoutRestoreOperationInProgress = true;
        UpdateInteractiveState();

        var sb = new System.Text.StringBuilder();
        try
        {
            var result = await _layoutExecutionCoordinator.ExecuteWithScopeGateAsync(
                preview.SourceRestorePlan,
                TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove,
                CancellationToken.None);
            _lastLayoutExecutionResult = result;
            _layoutRestoreEscapedException = null;

            sb.AppendLine($"=== 执行结果 ===");
            sb.AppendLine($"Status: {result.Status}");
            sb.AppendLine($"IsSuccess: {result.IsSuccess}");
            sb.AppendLine($"IsNoOp: {result.IsNoOp}");
            sb.AppendLine($"SafetySnapshotId: {result.SafetySnapshotId}");
            sb.AppendLine($"SessionId: {result.Session?.SessionId}");
            sb.AppendLine($"FailedStepIndex: {result.FailedStepIndex}");
            sb.AppendLine($"RequiresManualRecovery: {result.RequiresManualRecovery}");
            sb.AppendLine($"IsPositionUncertain: {result.IsPositionUncertain}");
            sb.AppendLine($"ForwardFailureType: {result.ForwardFailureType}");
            sb.AppendLine($"ForwardFailureMessage: {result.ForwardFailureMessage}");
            sb.AppendLine($"RecoveryFailureType: {result.RecoveryFailureType}");
            sb.AppendLine($"RecoveryFailureMessage: {result.RecoveryFailureMessage}");

            sb.AppendLine($"\n--- ForwardAttempts ({result.ForwardAttempts.Count}) ---");
            foreach (var a in result.ForwardAttempts)
                AppendAttempt(sb, a);

            sb.AppendLine($"\n--- RollbackAttempts ({result.RollbackAttempts.Count}) ---");
            foreach (var a in result.RollbackAttempts)
                AppendAttempt(sb, a);

            switch (result.Status)
            {
                case TargetDisplayLayoutExecutionRunStatus.Completed:
                case TargetDisplayLayoutExecutionRunStatus.CompletedNoOp:
                    LayoutRestorePreviewText.Text = result.IsNoOp ? "NoOp 完成。" : "执行成功。";
                    LayoutRestoreConsentCheckBox.IsChecked = false;
                    _layoutRestoreRequiresAttention = false;
                    break;
                case TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite:
                    if (result.ForwardFailureType == "ExecutionScopeRejected")
                    {
                        sb.AppendLine("\n桌面状态在预览后发生变化，最新执行计划已超出");
                        sb.AppendLine("单图标、单步骤、无中转范围；Writer 未开始，请重新预览。");
                    }
                    else
                    {
                        sb.AppendLine("\nWriter 未开始，没有副作用。");
                    }
                    _layoutRestoreRequiresAttention = false;
                    break;
                case TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack:
                    sb.AppendLine("\n正向失败但已确认回滚，没有残留副作用。");
                    _layoutRestoreRequiresAttention = false;
                    break;
                case TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete:
                case TargetDisplayLayoutExecutionRunStatus.PositionUncertain:
                    _layoutRestoreRequiresAttention = true;
                    sb.AppendLine("\n⚠ 需要人工处理。请关闭并重新启动 PoC。");
                    break;
            }

            if (result.ForwardAttempts.Count > 0 || result.RollbackAttempts.Count > 0)
            {
                _lastReadResult = null;
                _iconRows.Clear();
                IconStatsText.Text = "桌面图标已变更。请重新读取。";
            }

            try { await RefreshSnapshotsAsync(); UpdateSnapshotStats(); }
            catch (Exception ex) { sb.AppendLine($"刷新快照列表失败: {ex.Message}"); }

            StatusText.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            _layoutRestoreRequiresAttention = true;
            _layoutRestoreEscapedException = $"{ex.GetType().Name}: {ex.Message}";
            _lastLayoutExecutionResult = null;

            sb.AppendLine($"执行逃逸异常: {_layoutRestoreEscapedException}");
            sb.AppendLine("\n⚠ 无法确定异常发生在 Writer 前还是后。");
            sb.AppendLine("已进入人工处理锁，请关闭并重新启动 PoC。");
            StatusText.Text = sb.ToString();
        }
        finally
        {
            _layoutRestoreOperationInProgress = false;
            UpdateInteractiveState();
        }
    }

    private static void AppendAttempt(
        System.Text.StringBuilder sb, TargetDisplayLayoutExecutionAttemptResult a)
    {
        sb.Append($"  [{a.AttemptIndex}] srcStep={a.SourceStepIndex} {a.Phase} {a.Outcome} " +
            $"CM={a.ConnectionMethod} " +
            $"ExpSid={a.ExpectedStableId} RptSid={a.ReportedStableId ?? "—"} " +
            $"ExpPrev=({a.ExpectedPreviousPosition?.X},{a.ExpectedPreviousPosition?.Y}) " +
            $"RptPrev=({a.ReportedPreviousPosition?.X},{a.ReportedPreviousPosition?.Y}) " +
            $"Req=({a.RequestedPosition.X},{a.RequestedPosition.Y}) " +
            $"Actual=({a.ActualPosition?.X},{a.ActualPosition?.Y}) " +
            $"IsExact={a.IsExact} Trusted={a.HasTrustedActualPosition} " +
            $"ExcType={a.ExceptionType ?? "—"} ErrMsg={a.ErrorMessage ?? "—"} " +
            $"PriPathExcType={a.PrimaryPathExceptionType ?? "—"} " +
            $"PriPathExcMsg={a.PrimaryPathExceptionMessage ?? "—"}");
        sb.AppendLine();
    }
}
