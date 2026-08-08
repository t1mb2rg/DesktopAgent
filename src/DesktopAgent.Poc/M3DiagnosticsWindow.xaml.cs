using System.Text;
using System.Windows;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Poc;

public partial class M3DiagnosticsWindow : Window
{
    private readonly DesktopObservationMonitoringRuntime _runtime;
    private readonly HashSet<Guid> _seenRunIds = [];
    private int _runNumber;
    private bool _subscribed;

    public M3DiagnosticsWindow(DesktopObservationMonitoringRuntime runtime)
    {
        InitializeComponent();
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _runtime.RunCompleted += Runtime_RunCompleted;
            _runtime.StatusChanged += Runtime_StatusChanged;
            _subscribed = true;
        }

        RenderStatus(_runtime.Status, appendDiagnostics: true);
        if (_runtime.LastRun is { } lastRun)
            AppendTriggeredRun(lastRun);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_subscribed)
            return;

        _runtime.RunCompleted -= Runtime_RunCompleted;
        _runtime.StatusChanged -= Runtime_StatusChanged;
        _subscribed = false;
    }

    private void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime.RequestManualObservation())
        {
            SummaryText.Text =
                "已提交 Manual hint。将经过 bounded debounce 后进入同一条 single-flight observation 管线。";
        }
        else
        {
            SummaryText.Text = "后台监控当前未运行，Manual hint 未提交。";
        }
    }

    private void ResetBaseline_Click(object sender, RoutedEventArgs e)
    {
        _runtime.ResetBaseline();
        SummaryText.Text =
            "Baseline 已重置。下一次成功 stable observation 会重新建立当前会话 baseline。";
        AppendDiagnostics(
            $"=== baseline reset @ {DateTimeOffset.Now:O} ===\r\n"
            + "History was not used. Next stable observation will establish a fresh in-memory baseline.\r\n");
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

    private void Runtime_RunCompleted(DesktopObservationTriggeredRun run)
    {
        _ = Dispatcher.BeginInvoke(new Action(() => AppendTriggeredRun(run)));
    }

    private void Runtime_StatusChanged(DesktopObservationMonitoringStatus status)
    {
        _ = Dispatcher.BeginInvoke(new Action(() => RenderStatus(status, appendDiagnostics: true)));
    }

    private void RenderStatus(
        DesktopObservationMonitoringStatus status,
        bool appendDiagnostics)
    {
        var sourceSummary = status.Sources.Count == 0
            ? "no active source diagnostics"
            : string.Join(
                "; ",
                status.Sources.Select(source =>
                    $"{source.Source}: {(source.IsRunning ? "running" : "stopped")} / endpoints={source.ActiveEndpointCount}"));

        MonitoringText.Text =
            $"后台监控状态：{status.State}  |  {sourceSummary}";
        RunOnceButton.IsEnabled = status.IsRunning;
        ResetBaselineButton.IsEnabled = status.IsRunning;

        if (!appendDiagnostics)
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"=== monitoring status @ {status.ChangedAtUtc:O} ===");
        sb.AppendLine($"State: {status.State}");
        foreach (var source in status.Sources)
        {
            sb.AppendLine(
                $"Source: {source.Source}  Running={source.IsRunning}  ActiveEndpoints={source.ActiveEndpointCount}");
            foreach (var endpoint in source.ActiveEndpoints)
                sb.AppendLine($"  Endpoint: {endpoint}");
            foreach (var issue in source.Issues)
                sb.AppendLine($"  Issue: {issue}");
        }
        foreach (var issue in status.Issues)
            sb.AppendLine($"RuntimeIssue: {issue}");

        AppendDiagnostics(sb.ToString());
    }

    private void AppendTriggeredRun(DesktopObservationTriggeredRun run)
    {
        if (!_seenRunIds.Add(run.RunId))
            return;

        _runNumber++;
        var text = FormatTriggeredRun(_runNumber, run);
        AppendDiagnostics(text);

        if (run.Status == DesktopObservationTriggeredRunStatus.Failed)
        {
            SummaryText.Text = "后台 observation run 发生逃逸异常；Runtime 仍保持运行并可处理后续 hint。";
            return;
        }

        var result = run.DiagnosticsResult!;
        SummaryText.Text = result.Status switch
        {
            DesktopObservationDiagnosticsStatus.BaselineEstablished =>
                "后台已从 fresh stable observation 建立 baseline。",
            DesktopObservationDiagnosticsStatus.NoChanges =>
                "Stable：与上一份 stable baseline 相比没有事实变化。",
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
    }

    private void AppendDiagnostics(string text)
    {
        if (!string.IsNullOrWhiteSpace(DiagnosticsText.Text))
            DiagnosticsText.AppendText("\r\n");

        DiagnosticsText.AppendText(text.TrimEnd() + "\r\n");
        DiagnosticsText.ScrollToEnd();
    }

    private static string FormatTriggeredRun(
        int runNumber,
        DesktopObservationTriggeredRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== M3 background run #{runNumber} / {run.RunId:D} ===");
        sb.AppendLine($"RunStatus: {run.Status}");
        sb.AppendLine($"StartedAtUtc: {run.StartedAtUtc:O}");
        sb.AppendLine($"CompletedAtUtc: {run.CompletedAtUtc:O}");
        sb.AppendLine($"Duration: {run.Duration.TotalMilliseconds:F1} ms");
        sb.AppendLine(
            $"TriggerBatch: hints={run.TriggerBatch.TotalHintCount}  "
            + $"first={run.TriggerBatch.FirstHintAtUtc:O}  "
            + $"last={run.TriggerBatch.LastHintAtUtc:O}  due={run.TriggerBatch.DueAtUtc:O}");
        sb.AppendLine($"TriggerSources: {string.Join(", ", run.TriggerBatch.Sources)}");
        sb.AppendLine(
            "TriggerKinds: "
            + string.Join(
                ", ",
                run.TriggerBatch.HintCounts
                    .OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}={pair.Value}")));

        if (run.Status == DesktopObservationTriggeredRunStatus.Failed)
        {
            sb.AppendLine("EscapedException:");
            sb.AppendLine(run.EscapedException);
            return sb.ToString();
        }

        AppendDiagnosticsResult(sb, run.DiagnosticsResult!);
        return sb.ToString();
    }

    private static void AppendDiagnosticsResult(
        StringBuilder sb,
        DesktopObservationDiagnosticsResult result)
    {
        sb.AppendLine($"DiagnosticsStatus: {result.Status}");

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
