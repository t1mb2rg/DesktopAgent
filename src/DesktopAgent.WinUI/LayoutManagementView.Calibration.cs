using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    internal bool IsCoordinateOperationInProgress => _directApplyInProgress || _gridProbeRunning;
    internal string CalibrationMaintenanceMessage => LayoutStatusBar.Title + " · " + LayoutStatusBar.Message;

    internal async Task RunRequiredCalibrationAsync(Action<string>? reportProgress = null)
    {
        if (IsCoordinateOperationInProgress)
            throw new InvalidOperationException("已有桌面操作正在运行，完成后再校准。");
        await _directApplyRecoveryStore.InitializeAsync();
        await _workspaceExecutionJournalStore.InitializeAsync();
        await _workspaceDynamicExecutionJournalStore.InitializeAsync();
        if ((await _directApplyRecoveryStore.ListUnresolvedAsync(50)).Any(record => record.GetDisposition().IsUnresolved) ||
            (await _workspaceExecutionJournalStore.ListUnresolvedAsync()).Count > 0 ||
            (await _workspaceDynamicExecutionJournalStore.ListUnresolvedAsync()).Count > 0)
            throw new InvalidOperationException("存在尚未解决的桌面恢复现场，请先查看与处理恢复状态，校准探针尚未启动。");
        if (!await ConfirmGridProbeAsync()) return;
        var baseline = await _observationSource.CaptureAsync();
        if (!baseline.IsCaptured)
            throw new InvalidOperationException("无法完整读取当前桌面，校准探针未启动。");
        // 先登记本次探针使用的环境，保证首次校准完成后不会被首次环境登记再次作废。
        await ProductGridCalibration.Calibration.CheckAsync(baseline.Observation!);
        InvalidateCalibrationEvidence();
        var product = (Microsoft.UI.Xaml.Application.Current as App)?.ProductWindow;
        product?.BeginCalibrationMaintenance();
        try { await RunGridProbeAsync(reportProgress); }
        finally { product?.EndCalibrationMaintenance(); }
    }

    internal void CancelRequiredCalibration() => _gridProbeCancellation?.Cancel();

    internal Task InspectRecoveryForCalibrationAsync() => ReconcileRecoveryAsync();

    internal void InvalidateCalibrationEvidence()
    {
        HideOverlay();
        _desktopZoneEditorWindow?.Close();
        ClearAiLogicalWorkspacePreview();
        ClearAiWorkspaceFrozenPreview();
        InvalidateDirectApplyEvidence("网格校准已失效 · 请重新校准并生成预览");
    }
}
