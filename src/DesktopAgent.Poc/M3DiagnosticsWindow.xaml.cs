using System.Text;
using System.Windows;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Poc;

public partial class M3DiagnosticsWindow : Window
{
    private readonly DesktopObservationDiagnosticsRunner _runner;
    private readonly M3ReadOnlyMonitoringSession _monitoringSession;
    private bool _running;
    private bool _monitoringOperation;
    private int _runNumber;

    public M3DiagnosticsWindow(
        DesktopObservationDiagnosticsRunner runner,
        M3ReadOnlyMonitoringSession monitoringSession)
    {
        InitializeComponent();
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _monitoringSession = monitoringSession
            ?? throw new ArgumentNullException(nameof(monitoringSession));

        _monitoringSession.RunCompleted += OnBackgroundRunCompleted;
        _monitoringSession.PumpFaulted += OnPumpFaulted;
        Closed += OnClosed;
    }

    private async void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _monitoringOperation || _monitoringSession.IsRunning)
            return;

        _running = true;
        UpdateButtons();
        SummaryText.Text = "正在执行两次 fresh Observation 并判断稳定性…";

        try
        {
            var result = await _runner.RunOnceAsync();
            _runNumber++;
            SummaryText.Text = DescribeDiagnosticsResult(result);
            AppendDiagnostics(FormatResult(_runNumber, result, "manual"));
        }
        catch (Exception ex)
        {
            SummaryText.Text = "诊断运行发生未处理异常。应用仍保持运行。";
            AppendDiagnostics(
                $"=== M3 diagnostics escaped exception @ {DateTimeOffset.Now:O} ===\r\n{ex}\r\n");
        }
        finally
        {
            _running = false;
            UpdateButtons();
        }
    }

    private void ResetBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _monitoringOperation || _monitoringSession.IsRunning)
            return;

        _runner.ResetBaseline();
        SummaryText.Text = "Baseline 已重置。下一次成功 stable observation 将重新建立 baseline。";
        AppendDiagnostics(
            $"=== baseline reset @ {DateTimeOffset.Now:O} ===\r\n"
            + "No persisted history was used. Next stable observation will establish a new in-memory baseline.\r\n");
    }

    private async void StartMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _monitoringOperation || _monitoringSession.IsRunning)
            return;

        _monitoringOperation = true;
        UpdateButtons();
        MonitoringStatusText.Text = "后台监听：正在启动 watcher，并建立 stable baseline…";

        try
        {
            var result = await _monitoringSession.StartAsync();
            AppendDiagnostics(FormatMonitoringStart(result));

            if (result.BaselinePrime is not null)
            {
                _runNumber++;
                AppendDiagnostics(
                    FormatResult(
                        _runNumber,
                        result.BaselinePrime,
                        "monitoring baseline prime"));
            }

            MonitoringStatusText.Text = result.Status switch
            {
                M3ReadOnlyMonitoringStartStatus.Started =>
                    $"后台监听：运行中（FileSystem watchers: {result.FileSystemSource!.ActiveWatcherCount}）",
                M3ReadOnlyMonitoringStartStatus.AlreadyRunning =>
                    "后台监听：已经在运行。",
                M3ReadOnlyMonitoringStartStatus.HintSourceUnavailable =>
                    "后台监听：FileSystem hint source 无法启动。",
                M3ReadOnlyMonitoringStartStatus.BaselineUnstable =>
                    "后台监听：未启动。Baseline prime 为 Unstable，请稍后重试。",
                M3ReadOnlyMonitoringStartStatus.BaselineUnavailable =>
                    "后台监听：未启动。Baseline prime 为 Unavailable，请查看诊断。",
                _ => result.Status.ToString(),
            };

            if (result.IsStarted && result.BaselinePrime is not null)
                SummaryText.Text = DescribeDiagnosticsResult(result.BaselinePrime);
        }
        catch (Exception ex)
        {
            MonitoringStatusText.Text = "后台监听：启动失败。应用仍保持运行。";
            AppendDiagnostics(
                $"=== monitoring start escaped exception @ {DateTimeOffset.Now:O} ===\r\n{ex}\r\n");
        }
        finally
        {
            _monitoringOperation = false;
            UpdateButtons();
        }
    }

    private async void StopMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _monitoringOperation || !_monitoringSession.IsRunning)
            return;

        _monitoringOperation = true;
        UpdateButtons();
        MonitoringStatusText.Text = "后台监听：正在停止…";

        try
        {
            await _monitoringSession.StopAsync();
            MonitoringStatusText.Text = "后台监听：已停止。可重新启动；session baseline 保留。";
            AppendDiagnostics(
                $"=== monitoring stopped @ {DateTimeOffset.Now:O} ===\r\n"
                + "FileSystem watchers stopped. Pump cancelled. In-memory baseline was intentionally retained.\r\n");
        }
        catch (Exception ex)
        {
            MonitoringStatusText.Text = "后台监听：停止时发生异常，请查看诊断。";
            AppendDiagnostics(
                $"=== monitoring stop escaped exception @ {DateTimeOffset.Now:O} ===\r\n{ex}\r\n");
        }
        finally
        {
            _monitoringOperation = false;
            UpdateButtons();
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsText.Text ?? string.Empty);
            SummaryText.Text = "诊断文本已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void OnBackgroundRunCompleted(DesktopObservationTriggeredRun run)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _runNumber++;
            AppendDiagnostics(FormatTriggeredRun(_runNumber, run));

            if (run.Status == DesktopObservationTriggeredRunStatus.Completed)
            {
                var result = run.DiagnosticsResult!;
                SummaryText.Text = "后台：" + DescribeDiagnosticsResult(result);
                MonitoringStatusText.Text =
                    $"后台监听：运行中，最近一批 {run.TriggerBatch.TotalHintCount} 个 hint 已处理。";
            }
            else
            {
                SummaryText.Text = "后台 observation pipeline 发生逃逸异常。";
                MonitoringStatusText.Text = "后台监听：pump 仍在运行，但最近一次 batch 处理失败。";
            }

            UpdateButtons();
        }));
    }

    private void OnPumpFaulted(string fault)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MonitoringStatusText.Text = "后台监听：pump 已异常停止，可再次点击启动以创建新会话。";
            AppendDiagnostics(
                $"=== background pump fault @ {DateTimeOffset.Now:O} ===\r\n{fault}\r\n");
            UpdateButtons();
        }));
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _monitoringSession.RunCompleted -= OnBackgroundRunCompleted;
        _monitoringSession.PumpFaulted -= OnPumpFaulted;
        Closed -= OnClosed;

        try
        {
            await _monitoringSession.StopAsync();
        }
        catch
        {
            // Window 已关闭。Host.Dispose 仍会作为最终资源回收兜底。
        }
    }

    private void UpdateButtons()
    {
        var monitoringRunning = _monitoringSession.IsRunning;
        var blockedByMonitoring = monitoringRunning || _monitoringOperation;

        RunOnceButton.IsEnabled = !_running && !blockedByMonitoring;
        ResetBaselineButton.IsEnabled = !_running && !blockedByMonitoring;
        CopyButton.IsEnabled = !_running && !_monitoringOperation;
        StartMonitoringButton.IsEnabled = !_running && !_monitoringOperation && !monitoringRunning;
        StopMonitoringButton.IsEnabled = !_running && !_monitoringOperation && monitoringRunning;
    }

    private void AppendDiagnostics(string text)
    {
        if (!string.IsNullOrWhiteSpace(DiagnosticsText.Text))
            DiagnosticsText.AppendText("\r\n");

        DiagnosticsText.AppendText(text.TrimEnd() + "\r\n");
        DiagnosticsText.ScrollToEnd();
    }

    private static string DescribeDiagnosticsResult(
        DesktopObservationDiagnosticsResult result) =>
        result.Status switch
        {
            DesktopObservationDiagnosticsStatus.BaselineEstablished =>
                "Baseline 已建立。移动/新增/删除桌面项目后再次观察即可看到 Diff。",
            DesktopObservationDiagnosticsStatus.NoChanges =>
                "Stable：与上一次 stable baseline 相比没有事实变化。",
            DesktopObservationDiagnosticsStatus.ChangesDetected =>
                $"Stable：检测到 {result.ChangeSet!.ItemChanges.Count} 个项目变化"
                + (result.ChangeSet.TopologyChange is not null ? "，并包含 topology 变化" : "")
                + "。",
            DesktopObservationDiagnosticsStatus.Unstable =>
                "Unstable：两次完整观察不一致，baseline 未推进。",
            DesktopObservationDiagnosticsStatus.Unavailable =>
                "Unavailable：无法建立完整事实，baseline 未推进。",
            _ => result.Status.ToString(),
        };

    private static string FormatMonitoringStart(M3ReadOnlyMonitoringStartResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== monitoring start @ {DateTimeOffset.Now:O} ===");
        sb.AppendLine($"Status: {result.Status}");

        if (result.FileSystemSource is { } source)
        {
            sb.AppendLine($"FileSystemSource.IsRunning: {source.IsRunning}");
            sb.AppendLine($"FileSystemSource.ActiveWatcherCount: {source.ActiveWatcherCount}");
            foreach (var directory in source.WatchedDirectories)
                sb.AppendLine($"  WatchedDirectory: {directory}");
            foreach (var issue in source.Issues)
                sb.AppendLine($"  SourceIssue: {issue}");
        }

        if (result.BaselinePrime is not null)
            sb.AppendLine($"BaselinePrime: {result.BaselinePrime.Status}");

        return sb.ToString();
    }

    private static string FormatTriggeredRun(
        int runNumber,
        DesktopObservationTriggeredRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== background trigger #{runNumber} @ {DateTimeOffset.Now:O} ===");
        sb.AppendLine($"TriggeredRunId: {run.RunId:D}");
        sb.AppendLine($"TriggeredRunStatus: {run.Status}");
        sb.AppendLine($"Duration: {run.Duration.TotalMilliseconds:F1} ms");
        sb.AppendLine($"HintCount: {run.TriggerBatch.TotalHintCount}");
        sb.AppendLine($"HintWindow: {run.TriggerBatch.FirstHintAtUtc:O} -> {run.TriggerBatch.LastHintAtUtc:O}");
        sb.AppendLine(
            "HintKinds: "
            + string.Join(
                ", ",
                run.TriggerBatch.HintCounts
                    .OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}={pair.Value}")));
        sb.AppendLine("HintSources: " + string.Join(", ", run.TriggerBatch.Sources));

        if (run.Status == DesktopObservationTriggeredRunStatus.Failed)
        {
            sb.AppendLine("EscapedException:");
            sb.AppendLine(run.EscapedException);
            return sb.ToString();
        }

        sb.AppendLine();
        sb.Append(FormatResult(runNumber, run.DiagnosticsResult!, "background"));
        return sb.ToString();
    }

    private static string FormatResult(
        int runNumber,
        DesktopObservationDiagnosticsResult result,
        string origin)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== M3 diagnostics run #{runNumber} [{origin}] @ {DateTimeOffset.Now:O} ===");
        sb.AppendLine($"Status: {result.Status}");

        if (result.ObservationResult.StableObservation is { } observation)
        {
            sb.AppendLine($"ObservationId: {observation.ObservationId:D}");
            sb.AppendLine($"ObservedAtUtc: {observation.ObservedAtUtc:O}");
            sb.AppendLine($"Displays: {observation.Displays.Count}");
            sb.AppendLine($"Icons: {observation.Icons.Count}");
            sb.AppendLine(
                $"AutoArrange: {observation.PositioningCapabilities.IsAutoArrangeEnabled}  "
                + $"IconSpacing: ({observation.PositioningCapabilities.Spacing.X},{observation.PositioningCapabilities.Spacing.Y})");
            sb.AppendLine($"ShellRead: {observation.IconReadConnectionMethod} / {observation.IconReadTime.TotalMilliseconds:F1} ms");
        }

        switch (result.Status)
        {
            case DesktopObservationDiagnosticsStatus.BaselineEstablished:
                sb.AppendLine("Baseline: established from this fresh stable observation.");
                break;

            case DesktopObservationDiagnosticsStatus.NoChanges:
                AppendBaselineIds(sb, result.BaselineUpdate!);
                sb.AppendLine("Diff: empty");
                break;

            case DesktopObservationDiagnosticsStatus.ChangesDetected:
                AppendBaselineIds(sb, result.BaselineUpdate!);
                AppendChangeSet(sb, result.ChangeSet!);
                break;

            case DesktopObservationDiagnosticsStatus.Unstable:
                sb.AppendLine("Baseline: unchanged");
                sb.AppendLine("Stability issues:");
                foreach (var issue in result.ObservationResult.StabilityIssues)
                {
                    sb.Append("  - ").Append(issue.Code);
                    if (issue.StableId is not null)
                        sb.Append("  StableId=").Append(issue.StableId);
                    sb.Append(": ").AppendLine(issue.Message);
                }
                break;

            case DesktopObservationDiagnosticsStatus.Unavailable:
                sb.AppendLine("Baseline: unchanged");
                sb.AppendLine("Capture failures:");
                foreach (var failure in result.ObservationResult.CaptureFailures)
                {
                    sb.Append("  - ").Append(failure.Phase)
                        .Append(" / ").Append(failure.Issue.Code);
                    if (failure.Issue.StableId is not null)
                        sb.Append("  StableId=").Append(failure.Issue.StableId);
                    sb.Append(": ").AppendLine(failure.Issue.Message);
                }
                break;
        }

        return sb.ToString();
    }

    private static void AppendBaselineIds(
        StringBuilder sb,
        DesktopStateBaselineUpdateResult baseline)
    {
        sb.AppendLine($"PreviousObservationId: {baseline.PreviousObservation!.ObservationId:D}");
        sb.AppendLine($"CurrentObservationId: {baseline.CurrentObservation.ObservationId:D}");
    }

    private static void AppendChangeSet(StringBuilder sb, DesktopStateChangeSet changeSet)
    {
        sb.AppendLine("Diff:");

        if (changeSet.TopologyChange is { } topology)
        {
            sb.AppendLine("  TopologyChanged: true");
            sb.AppendLine(
                "    Previous: "
                + string.Join(", ", topology.PreviousDisplays.Select(FormatDisplay)));
            sb.AppendLine(
                "    Current:  "
                + string.Join(", ", topology.CurrentDisplays.Select(FormatDisplay)));
        }

        if (changeSet.PositioningCapabilitiesChange is { } capabilities)
        {
            sb.AppendLine("  PositioningCapabilitiesChanged: true");
            if (capabilities.AutoArrangeChanged)
            {
                sb.AppendLine(
                    $"    AutoArrange: {capabilities.PreviousAutoArrangeEnabled} -> {capabilities.CurrentAutoArrangeEnabled}");
            }

            if (capabilities.IconSpacingChanged)
            {
                sb.AppendLine(
                    $"    IconSpacing: ({capabilities.PreviousSpacing.X},{capabilities.PreviousSpacing.Y})"
                    + $" -> ({capabilities.CurrentSpacing.X},{capabilities.CurrentSpacing.Y})");
            }
        }

        sb.AppendLine($"  ItemChanges: {changeSet.ItemChanges.Count}");
        foreach (var item in changeSet.ItemChanges)
        {
            sb.Append("    - ").Append(item.Kind)
                .Append("  StableId=").AppendLine(item.StableId);

            switch (item.Kind)
            {
                case DesktopItemStateChangeKind.Added:
                    sb.AppendLine($"      Current: {FormatIcon(item.Current!)}");
                    break;
                case DesktopItemStateChangeKind.Removed:
                    sb.AppendLine($"      Previous: {FormatIcon(item.Previous!)}");
                    break;
                case DesktopItemStateChangeKind.Changed:
                    sb.AppendLine($"      FactChanges: {item.FactChanges}");
                    sb.AppendLine($"      Previous: {FormatIcon(item.Previous!)}");
                    sb.AppendLine($"      Current:  {FormatIcon(item.Current!)}");
                    break;
            }
        }

        if (!changeSet.HasChanges)
            sb.AppendLine("  (empty)");
    }

    private static string FormatDisplay(DisplayDescriptor display) =>
        $"{display.DeviceName} Bounds={display.Bounds} WorkArea={display.WorkArea} DPI={display.DpiX}x{display.DpiY} Primary={display.IsPrimary}";

    private static string FormatIcon(DesktopIconDescriptor icon) =>
        $"Name=\"{icon.DisplayName}\" Kind={icon.ItemKind} Pos=({icon.PixelPosition.X},{icon.PixelPosition.Y}) Monitor={icon.MonitorDeviceName ?? "<null>"}";
}
