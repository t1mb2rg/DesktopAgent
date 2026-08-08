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
    private bool _running;
    private int _runNumber;

    public M3DiagnosticsWindow(DesktopObservationDiagnosticsRunner runner)
    {
        InitializeComponent();
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    private async void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
            return;

        _running = true;
        UpdateButtons();
        SummaryText.Text = "正在执行两次 fresh Observation 并判断稳定性…";

        try
        {
            var result = await _runner.RunOnceAsync();
            _runNumber++;
            var text = FormatResult(_runNumber, result);

            SummaryText.Text = result.Status switch
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

            AppendDiagnostics(text);
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
        if (_running)
            return;

        _runner.ResetBaseline();
        SummaryText.Text = "Baseline 已重置。下一次成功 stable observation 将重新建立 baseline。";
        AppendDiagnostics(
            $"=== baseline reset @ {DateTimeOffset.Now:O} ===\r\n"
            + "No persisted history was used. Next stable observation will establish a new in-memory baseline.\r\n");
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

    private void UpdateButtons()
    {
        RunOnceButton.IsEnabled = !_running;
        ResetBaselineButton.IsEnabled = !_running;
        CopyButton.IsEnabled = !_running;
    }

    private void AppendDiagnostics(string text)
    {
        if (!string.IsNullOrWhiteSpace(DiagnosticsText.Text))
            DiagnosticsText.AppendText("\r\n");

        DiagnosticsText.AppendText(text.TrimEnd() + "\r\n");
        DiagnosticsText.ScrollToEnd();
    }

    private static string FormatResult(
        int runNumber,
        DesktopObservationDiagnosticsResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== M3 diagnostics run #{runNumber} @ {DateTimeOffset.Now:O} ===");
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
