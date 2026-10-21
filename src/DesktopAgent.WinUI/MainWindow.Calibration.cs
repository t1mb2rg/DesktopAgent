using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;
using Microsoft.UI.Xaml;

namespace DesktopAgent.WinUI;

public sealed partial class MainWindow
{
    private readonly StaThreadExecutor _calibrationSta = new();
    private readonly DispatcherTimer _calibrationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _calibrationReady;
    private bool _calibrationCheckRunning;
    private bool _calibrationOperationRunning;

    internal void BeginCalibrationMaintenance()
    {
        _calibrationReady = false;
        _workspaceSession.PauseUnderstanding();
        CancelAiAssistantRequest();
        ShellNavigation.IsEnabled = false;
        CalibrationGate.Visibility = Visibility.Visible;
        CalibrationGateProgress.IsActive = true;
        RequiredCalibrationButton.IsEnabled = false;
        CalibrationRecheckButton.IsEnabled = false;
        CalibrationRecoveryButton.IsEnabled = false;
        CalibrationStopButton.Visibility = Visibility.Visible;
    }

    internal void EndCalibrationMaintenance()
    {
        RequiredCalibrationButton.IsEnabled = true;
        CalibrationRecheckButton.IsEnabled = true;
        CalibrationRecoveryButton.IsEnabled = true;
        CalibrationStopButton.Visibility = Visibility.Collapsed;
        CalibrationMaintenanceStatus.Text = _layoutManagementView?.CalibrationMaintenanceMessage;
        if (!_calibrationOperationRunning) _ = RefreshCalibrationAsync();
    }

    private void CalibrationEnvironmentChanged(bool explorerRestarted)
    {
        // Native WndProc 只投递事件；不在系统消息栈内读取桌面或弹出 UI。
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_exitRequested) return;
            try
            {
                // Explorer/app restart alone does not prove that the measured grid changed.
                // Fresh observation still verifies topology, spacing and persisted evidence.
                await RefreshCalibrationAsync();
            }
            catch (Exception ex)
            {
                _calibrationReady = false;
                ShellNavigation.IsEnabled = false;
                CalibrationGate.Visibility = Visibility.Visible;
                CalibrationGateStatus.Text = $"显示环境核验失败：{ex.Message}";
            }
        });
    }

    private void InitializeCalibrationGate()
    {
        ShellNavigation.IsEnabled = false;
        _calibrationTimer.Tick += CalibrationTimer_Tick;
        Activated += CalibrationWindow_Activated;
        _calibrationTimer.Start();
    }

    private async void CalibrationTimer_Tick(object? sender, object e) => await RefreshCalibrationAsync();

    private async void CalibrationWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            await RefreshCalibrationAsync();
    }

    private async Task RefreshCalibrationAsync()
    {
        if (_exitRequested || _calibrationCheckRunning || _calibrationOperationRunning ||
            _layoutManagementView?.IsCoordinateOperationInProgress == true)
            return;
        _calibrationCheckRunning = true;
        try
        {
            var source = new WindowsDesktopObservationSource(new NativeDisplayTopologyReader(),
                new DesktopShellIconReader(), new DesktopShellIconPositioningCapabilitiesReader(), _calibrationSta);
            var capture = await source.CaptureAsync();
            var result = capture.IsCaptured
                ? await ProductGridCalibration.Calibration.CheckAsync(capture.Observation!)
                : new DesktopGridCalibrationResult(false, "当前桌面读取失败，请检查 Explorer 与显示器后重新检查。", null);
            if (_exitRequested) return;
            if (_calibrationReady && !result.IsReady)
            {
                _workspaceSession.PauseUnderstanding();
                CancelAiAssistantRequest();
                _layoutManagementView?.InvalidateCalibrationEvidence();
            }
            var becameReady = !_calibrationReady && result.IsReady;
            _calibrationReady = result.IsReady;
            CalibrationGateStatus.Text = result.Message;
            CalibrationGate.Visibility = result.IsReady ? Visibility.Collapsed : Visibility.Visible;
            ShellNavigation.IsEnabled = result.IsReady;
            if (becameReady)
            {
                var aiItem = ShellNavigation.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>()
                    .First(item => item.Tag?.ToString() == "ai");
                ShellNavigation.SelectedItem = aiItem;
                ShowSection("ai");
                _ = _workspaceSession.EnsureUnderstandingAsync();
            }
        }
        catch (Exception ex)
        {
            _calibrationReady = false;
            _workspaceSession.PauseUnderstanding();
            CancelAiAssistantRequest();
            _layoutManagementView?.InvalidateCalibrationEvidence();
            ShellNavigation.IsEnabled = false;
            CalibrationGate.Visibility = Visibility.Visible;
            CalibrationGateStatus.Text = $"无法核验校准：{ex.Message}。请重试。";
        }
        finally
        {
            _calibrationCheckRunning = false;
            CalibrationGateProgress.IsActive = _calibrationOperationRunning;
        }
    }

    private async void CalibrationRecheck_Click(object sender, RoutedEventArgs e) => await RefreshCalibrationAsync();

    private async void RequiredCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationOperationRunning || _calibrationCheckRunning) return;
        _calibrationOperationRunning = true;
        CalibrationMaintenanceStatus.Text = string.Empty;
        RequiredCalibrationButton.IsEnabled = false;
        CalibrationStopButton.Visibility = Visibility.Visible;
        CalibrationRecheckButton.IsEnabled = false;
        CalibrationRecoveryButton.IsEnabled = false;
        CalibrationGateProgress.IsActive = true;
        try
        {
            EnsureLayoutManagementView();
            await _layoutManagementView!.RunRequiredCalibrationAsync(message => CalibrationGateStatus.Text = message);
        }
        catch (Exception ex) { CalibrationMaintenanceStatus.Text = $"校准未完成：{ex.Message}"; }
        finally
        {
            _calibrationOperationRunning = false;
            RequiredCalibrationButton.IsEnabled = true;
            CalibrationStopButton.Visibility = Visibility.Collapsed;
            CalibrationRecheckButton.IsEnabled = true;
            CalibrationRecoveryButton.IsEnabled = true;
            await RefreshCalibrationAsync();
        }
    }

    private void CalibrationStop_Click(object sender, RoutedEventArgs e)
    {
        _layoutManagementView?.CancelRequiredCalibration();
        CalibrationGateStatus.Text = "正在停止扫描，等待恢复、清理和核验完成…";
    }

    private async void CalibrationRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationOperationRunning || _calibrationCheckRunning) return;
        _calibrationOperationRunning = true;
        try
        {
            EnsureLayoutManagementView();
            await _layoutManagementView!.InspectRecoveryForCalibrationAsync();
            CalibrationMaintenanceStatus.Text = _layoutManagementView.CalibrationMaintenanceMessage;
        }
        catch (Exception ex) { CalibrationMaintenanceStatus.Text = $"无法读取恢复现场：{ex.Message}"; }
        finally { _calibrationOperationRunning = false; }
    }
}
