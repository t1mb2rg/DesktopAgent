using System.Windows;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

namespace DesktopAgent.Poc;

public partial class MainWindow
{
    private CancellationTokenSource? _gridProbeCancellation;

    private void GridProbeConsentChanged(object sender, RoutedEventArgs e)
        => UpdateInteractiveState();

    private async void RunGridProbe_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartGridProbe())
            return;
        if (GridProbeConsentCheckBox.IsChecked != true)
            return;

        var displayCount = _cachedDisplays.Count;
        var confirmation = MessageBox.Show(
            $"即将运行 Explorer Grid Physical Probe v1。\n\n" +
            $"当前显示器：{displayCount} 块\n" +
            $"将创建：{displayCount} 个唯一命名的临时 .url 快捷方式（一屏一个）\n" +
            "每屏动作：中心 seed + 一条完整横线 + 一条完整纵线 + 四角\n\n" +
            "空闲落点通过现有 primitive Writer 主动验证；已被精确占用的格子只读记录为被动样本，不会移动既有图标。" +
            "每次主动移动都会立即读回位置，再通过稳定双采样取得 Explorer 最终 settled 位置；报告会同时保留两者。" +
            "任意拓扑变化、既有图标变化、越界、spacing/phase 异常都会立即停止。结束时会尽力恢复测试图标原位置并删除临时快捷方式。\n\n" +
            "运行期间请勿拖动图标、增删桌面文件、切换自动排列/网格设置或改变显示器拓扑。是否继续？",
            "确认运行 Explorer Grid Physical Probe v1",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        await ExecuteGridProbeAsync();
    }

    /// <summary>
    /// 无人值守入口：命令行 <c>--run-grid-probe</c> 就是这次物理写入的显式确认（等价于勾选同意复选框并在
    /// 确认框上选 Yes，因此不再弹确认框）。返回 <c>null</c> 表示被前置安全检查拒绝，运行没有启动。
    /// </summary>
    internal async Task<ExplorerGridProbeRunStatus?> RunGridProbeUnattendedAsync()
        => CanStartGridProbe() ? await ExecuteGridProbeAsync() : null;

    /// <summary>
    /// 无人值守入口轮询用：应用刚启动时可能仍在读显示器/桌面图标（或存在人工恢复锁），此刻不允许启动探针。
    /// </summary>
    internal bool CanStartGridProbeNow
        => !IsAnyOperationInProgress && _activeMoveSession is null && !_layoutRestoreRequiresAttention;

    private bool CanStartGridProbe()
    {
        if (IsAnyOperationInProgress || _activeMoveSession is not null)
            return false;
        if (!_layoutRestoreRequiresAttention)
            return true;

        StatusText.Text = "存在人工恢复锁，Explorer Grid Physical Probe 拒绝启动。";
        return false;
    }

    /// <summary>报告落盘位置：产品与诊断共用同一目录，便于无人值守复核直接读文件。</summary>
    private static string GridProbeLogDirectory =>
        ExplorerGridProbeSession.LogDirectoryFor(DesktopAgentDataPaths.DefaultDatabasePath);

    private async Task<ExplorerGridProbeRunStatus?> ExecuteGridProbeAsync()
    {
        _gridProbeOperationInProgress = true;
        _gridProbeCancellation = new CancellationTokenSource();
        UpdateInteractiveState();
        StatusText.Text = "Explorer Grid Physical Probe v1 正在启动…";
        GridProbeStatusText.Text = "运行中：正在冻结 baseline 并准备专用测试快捷方式。";

        var progress = new Progress<string>(message =>
        {
            GridProbeStatusText.Text = message;
            StatusText.AppendText($"{DateTimeOffset.Now:HH:mm:ss.fff}  {message}\r\n");
            StatusText.ScrollToEnd();
        });

        ExplorerGridProbeRunStatus? status = null;
        try
        {
            var report = await _explorerGridPhysicalProbeRunner.RunAsync(
                progress,
                _gridProbeCancellation.Token);
            status = report.Status;

            var recorded = await ExplorerGridProbeSession.RecordFactsAsync(
                _explorerGridProbeFactRecorder,
                report,
                _gridProbeCancellation.Token);

            var reportText = report.Format() + ExplorerGridProbeSession.FormatFactSummary(recorded);
            StatusText.Text = reportText;
            ExplorerGridProbeSession.PersistReport(reportText, GridProbeLogDirectory);
            StatusText.ScrollToHome();
            GridProbeStatusText.Text = report.Status switch
            {
                ExplorerGridProbeRunStatus.Passed =>
                    "PASS：每屏 observed spacing/phase 恒定，所有显示器共享 spacing/phase；专用快捷方式已清理，既有图标未变化。"
                    + (recorded.Facts.Count > 0
                        ? $" 已写入 {recorded.Facts.Count} 条 Measured 网格事实。"
                        : " 未写入任何网格事实，原因见报告。"),
                ExplorerGridProbeRunStatus.Partial =>
                    "PARTIAL：有显示器拿到了合格实测样本（已逐屏写入 Measured 事实），"
                    + "另有显示器明确测不到（原因见报告）；没有事实的屏上不得执行桌面布局。"
                    + (recorded.Facts.Count > 0
                        ? $" 已写入 {recorded.Facts.Count} 条 Measured 网格事实。"
                        : " 未写入任何网格事实，原因见报告。"),
                ExplorerGridProbeRunStatus.Cancelled =>
                    "已停止：请在下方报告中确认恢复、快捷方式删除和既有图标最终核验结果。",
                ExplorerGridProbeRunStatus.Blocked =>
                    "BLOCKED：安全空位或主动样本不足；未形成网格 PASS/FAIL 结论，请查看报告。",
                _ =>
                    "FAIL-CLOSED：扫描已停止。请在下方报告中查看首个异常与恢复/清理结果。",
            };
        }
        catch (Exception exception)
        {
            var exceptionText = $"Explorer Grid Physical Probe 出现逃逸异常：\r\n{exception}";
            StatusText.Text = exceptionText;
            ExplorerGridProbeSession.PersistReport(exceptionText, GridProbeLogDirectory);
            GridProbeStatusText.Text = "FAIL-CLOSED：发生逃逸异常；请人工确认临时快捷方式与桌面位置。";
        }
        finally
        {
            _gridProbeCancellation.Dispose();
            _gridProbeCancellation = null;
            _gridProbeOperationInProgress = false;
            GridProbeConsentCheckBox.IsChecked = false;
            InvalidateLastIconRead("Physical Probe 已结束，请重新读取桌面图标。");
            RefreshDisplays();
            UpdateInteractiveState();
        }

        return status;
    }

    private void CancelGridProbe_Click(object sender, RoutedEventArgs e)
    {
        CancelGridProbeButton.IsEnabled = false;
        GridProbeStatusText.Text = "正在停止扫描；完成安全恢复与专用快捷方式清理后才会结束。";
        _gridProbeCancellation?.Cancel();
    }
}
