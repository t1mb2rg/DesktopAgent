using System.Text;
using System.Windows;
using System.Windows.Controls;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Poc;

public partial class M3DiagnosticsWindow : Window
{
    private readonly M3SuggestionMonitoringCoordinator _coordinator;
    private bool _running;
    private bool _monitoringOperation;
    private bool _optionsLoading;
    private int _runNumber;

    private IReadOnlyList<TemplateBindingRow> _templateRows = Array.Empty<TemplateBindingRow>();
    private IReadOnlyList<DisplayBindingRow> _displayRows = Array.Empty<DisplayBindingRow>();

    public M3DiagnosticsWindow(M3SuggestionMonitoringCoordinator coordinator)
    {
        InitializeComponent();
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        _coordinator.ObservationRunCompleted += OnBackgroundRunCompleted;
        _coordinator.MonitoringFaulted += OnMonitoringFaulted;
        _coordinator.SuggestionProcessed += OnSuggestionProcessed;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderBinding(_coordinator.BindingSnapshot);
        RenderSuggestion(_coordinator.SuggestionSnapshot);
        await RefreshSuggestionOptionsAsync();
        UpdateButtons();
    }

    private async void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _monitoringOperation || _coordinator.IsMonitoring)
            return;

        _running = true;
        UpdateButtons();
        SummaryText.Text = "正在执行两次 fresh Observation，并继续经过 Suggestion pipeline…";

        try
        {
            var report = await _coordinator.RunManualAsync();
            _runNumber++;
            SummaryText.Text = DescribeDiagnosticsResult(report.Diagnostics!);
            AppendDiagnostics(FormatResult(_runNumber, report.Diagnostics!, "manual"));
            RenderSuggestion(report.Lifecycle);
        }
        catch (Exception ex)
        {
            SummaryText.Text = "诊断 / Suggestion 运行发生未处理异常。应用仍保持运行。";
            AppendDiagnostics(
                $"=== M3 manual escaped exception @ {DateTimeOffset.Now:O} ===\r\n{ex}\r\n");
        }
        finally
        {
            _running = false;
            UpdateButtons();
        }
    }

    private void ResetBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _monitoringOperation || _coordinator.IsMonitoring)
            return;

        try
        {
            var lifecycle = _coordinator.ResetBaseline();
            SummaryText.Text = "Baseline 已重置。旧 Suggestion 已立即失去证据链；下一次 stable observation 会重建 baseline。";
            RenderSuggestion(lifecycle);
            AppendDiagnostics(
                $"=== baseline reset @ {DateTimeOffset.Now:O} ===\r\n"
                + $"SuggestionLifecycle: {lifecycle.State} / Revision={lifecycle.Revision}\r\n"
                + "No persisted history was used. Next stable observation will establish a new in-memory baseline.\r\n");
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"重置 baseline 失败：{ex.Message}";
        }
    }

    private async void StartMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _monitoringOperation || _coordinator.IsMonitoring)
            return;

        _monitoringOperation = true;
        UpdateButtons();
        MonitoringStatusText.Text = "后台监听：正在启动 watcher、建立 stable baseline，并按顺序初始化 Suggestion pipeline…";

        try
        {
            var coordinated = await _coordinator.StartMonitoringAsync();
            var result = coordinated.Monitoring;
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
                    $"后台监听：运行中（FileSystem watchers: {result.FileSystemSource!.ActiveWatcherCount}；PeriodicFallback: 15s）",
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

            if (result.BaselinePrime is not null)
                SummaryText.Text = DescribeDiagnosticsResult(result.BaselinePrime);

            if (coordinated.PrimeSuggestion is not null)
                RenderSuggestion(coordinated.PrimeSuggestion.Lifecycle);
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
        if (_running || _monitoringOperation || !_coordinator.IsMonitoring)
            return;

        _monitoringOperation = true;
        UpdateButtons();
        MonitoringStatusText.Text = "后台监听：正在停止，并等待已产生的 Suggestion processing 收口…";

        try
        {
            await _coordinator.StopMonitoringAsync();
            MonitoringStatusText.Text = "后台监听：已停止。可重新启动；session baseline 与当前 Suggestion 状态保留。";
            AppendDiagnostics(
                $"=== monitoring stopped @ {DateTimeOffset.Now:O} ===\r\n"
                + "Watchers / periodic / pump stopped. Completed suggestion-processing tail was drained.\r\n");
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

    private async void RefreshSuggestionOptions_Click(object sender, RoutedEventArgs e) =>
        await RefreshSuggestionOptionsAsync();

    private void SuggestionTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateSuggestionBindingEditorState();
    }

    private void ApplySuggestionBinding_Click(object sender, RoutedEventArgs e)
    {
        if (SuggestionTemplateComboBox.SelectedItem is not TemplateBindingRow templateRow)
        {
            SuggestionBindingStatusText.Text = "Suggestion Binding：请先选择模板。";
            return;
        }

        string? selectedTarget = null;
        if (templateRow.Binding.Kind == TargetDisplayBindingKind.SelectedTarget)
        {
            if (SuggestionTargetComboBox.SelectedItem is not DisplayBindingRow displayRow)
            {
                SuggestionBindingStatusText.Text = "Suggestion Binding：该模板使用 SelectedTarget，请明确选择目标显示器。";
                return;
            }

            selectedTarget = displayRow.DeviceName;
        }

        var snapshot = _coordinator.SetBinding(
            new DesktopLayoutSuggestionBinding(templateRow.TemplateId, selectedTarget));
        RenderBinding(snapshot);
        RenderSuggestion(_coordinator.SuggestionSnapshot);
        AppendDiagnostics(
            $"=== suggestion binding changed @ {DateTimeOffset.Now:O} ===\r\n"
            + $"Revision: {snapshot.Revision}\r\n"
            + $"TemplateId: {snapshot.Binding!.TemplateId:D}\r\n"
            + $"SelectedTargetDeviceName: {snapshot.Binding.SelectedTargetDeviceName ?? "<not used>"}\r\n");
        UpdateButtons();
    }

    private void ClearSuggestionBinding_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _coordinator.SetBinding(null);
        RenderBinding(snapshot);
        RenderSuggestion(_coordinator.SuggestionSnapshot);
        AppendDiagnostics(
            $"=== suggestion binding cleared @ {DateTimeOffset.Now:O} ===\r\n"
            + $"Revision: {snapshot.Revision}\r\n");
        UpdateButtons();
    }

    private void DismissSuggestion_Click(object sender, RoutedEventArgs e)
    {
        var lifecycle = _coordinator.DismissCurrentSuggestion();
        RenderSuggestion(lifecycle);
        AppendDiagnostics(
            $"=== suggestion dismissed @ {DateTimeOffset.Now:O} ===\r\n"
            + $"Lifecycle: {lifecycle.State} / Revision={lifecycle.Revision}\r\n");
        UpdateButtons();
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
                    $"后台监听：运行中，最近一批 {run.TriggerBatch.TotalHintCount} 个 hint 已完成 Observation。";
            }
            else
            {
                SummaryText.Text = "后台 observation pipeline 发生逃逸异常，当前 Suggestion 会进入 Suspended。";
                MonitoringStatusText.Text = "后台监听：pump 仍在运行，但最近一次 batch 处理失败。";
            }

            UpdateButtons();
        }));
    }

    private void OnSuggestionProcessed(M3SuggestionProcessingReport report)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RenderSuggestion(report.Lifecycle);
            AppendDiagnostics(FormatSuggestionProcessing(report));
            UpdateButtons();
        }));
    }

    private void OnMonitoringFaulted(string fault)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MonitoringStatusText.Text = "后台监听：pump / periodic 已异常停止，可再次点击启动创建新会话。";
            AppendDiagnostics(
                $"=== background monitoring fault @ {DateTimeOffset.Now:O} ===\r\n{fault}\r\n");
            UpdateButtons();
        }));
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _coordinator.ObservationRunCompleted -= OnBackgroundRunCompleted;
        _coordinator.MonitoringFaulted -= OnMonitoringFaulted;
        _coordinator.SuggestionProcessed -= OnSuggestionProcessed;
        Loaded -= OnLoaded;
        Closed -= OnClosed;

        try
        {
            await _coordinator.StopMonitoringAsync();
        }
        catch
        {
            // Window 已关闭。Host.Dispose 仍会作为最终资源回收兜底。
        }
    }

    private async Task RefreshSuggestionOptionsAsync()
    {
        if (_optionsLoading)
            return;

        _optionsLoading = true;
        UpdateButtons();
        try
        {
            var currentBinding = _coordinator.BindingSnapshot.Binding;
            var summaries = await _coordinator.ListTemplatesAsync();
            _templateRows = summaries
                .Select(summary => new TemplateBindingRow(
                    summary.TemplateId,
                    summary.Name,
                    summary.TargetDisplayBinding,
                    $"{summary.Name}  [{DescribeTargetBinding(summary.TargetDisplayBinding)}]"))
                .ToArray();
            SuggestionTemplateComboBox.ItemsSource = _templateRows;
            SuggestionTemplateComboBox.DisplayMemberPath = nameof(TemplateBindingRow.DisplayText);

            try
            {
                _displayRows = _coordinator.ReadDisplays()
                    .Select(display => new DisplayBindingRow(
                        display.DeviceName,
                        $"{display.DeviceName}{(display.IsPrimary ? " (主屏)" : "")}  {display.Bounds}"))
                    .ToArray();
            }
            catch (Exception ex)
            {
                _displayRows = Array.Empty<DisplayBindingRow>();
                AppendDiagnostics(
                    $"=== suggestion display options read failed @ {DateTimeOffset.Now:O} ===\r\n{ex}\r\n");
            }

            SuggestionTargetComboBox.ItemsSource = _displayRows;
            SuggestionTargetComboBox.DisplayMemberPath = nameof(DisplayBindingRow.DisplayText);

            if (currentBinding is not null)
            {
                SuggestionTemplateComboBox.SelectedItem = _templateRows.FirstOrDefault(
                    row => row.TemplateId == currentBinding.TemplateId);
                if (currentBinding.SelectedTargetDeviceName is not null)
                {
                    SuggestionTargetComboBox.SelectedItem = _displayRows.FirstOrDefault(
                        row => string.Equals(
                            row.DeviceName,
                            currentBinding.SelectedTargetDeviceName,
                            StringComparison.Ordinal));
                }
            }
            else if (_templateRows.Count > 0 && SuggestionTemplateComboBox.SelectedItem is null)
            {
                // 仅为了方便浏览默认高亮第一项，不会自动 SetBinding。
                SuggestionTemplateComboBox.SelectedIndex = 0;
            }

            UpdateSuggestionBindingEditorState();
            RenderBinding(_coordinator.BindingSnapshot);
        }
        catch (Exception ex)
        {
            SuggestionBindingStatusText.Text = "Suggestion Binding：模板/规则 store 初始化或列表读取失败。";
            AppendDiagnostics(
                $"=== suggestion options refresh failed @ {DateTimeOffset.Now:O} ===\r\n{ex}\r\n");
        }
        finally
        {
            _optionsLoading = false;
            UpdateButtons();
        }
    }

    private void UpdateSuggestionBindingEditorState()
    {
        var row = SuggestionTemplateComboBox.SelectedItem as TemplateBindingRow;
        var needsSelectedTarget = row?.Binding.Kind == TargetDisplayBindingKind.SelectedTarget;
        SuggestionTargetComboBox.IsEnabled = !_optionsLoading && needsSelectedTarget;

        if (row is null)
            return;

        SuggestionBindingStatusText.Text = row.Binding.Kind switch
        {
            TargetDisplayBindingKind.SelectedTarget =>
                "编辑中：模板要求 SelectedTarget；应用绑定前必须明确选一块当前显示器。",
            TargetDisplayBindingKind.PrimaryDisplay =>
                "编辑中：模板绑定主显示器；SelectedTarget 不参与。点击“应用绑定”后才正式生效。",
            TargetDisplayBindingKind.ExactDeviceName =>
                $"编辑中：模板固定到 {row.Binding.DeviceName}；SelectedTarget 不参与。点击“应用绑定”后才正式生效。",
            _ => row.Binding.Kind.ToString(),
        };
    }

    private void RenderBinding(DesktopLayoutSuggestionBindingSnapshot snapshot)
    {
        if (snapshot.Binding is null)
        {
            SuggestionBindingStatusText.Text =
                $"Suggestion Binding：未配置（Revision {snapshot.Revision}）。后台不会猜测模板。";
            return;
        }

        var row = _templateRows.FirstOrDefault(item => item.TemplateId == snapshot.Binding.TemplateId);
        var name = row?.Name ?? snapshot.Binding.TemplateId.ToString("D");
        SuggestionBindingStatusText.Text =
            $"Suggestion Binding：{name}  |  Revision {snapshot.Revision}"
            + (snapshot.Binding.SelectedTargetDeviceName is null
                ? string.Empty
                : $"  |  SelectedTarget={snapshot.Binding.SelectedTargetDeviceName}");
    }

    private void RenderSuggestion(DesktopLayoutSuggestionLifecycleSnapshot snapshot)
    {
        if (snapshot.CurrentSuggestion is null)
        {
            var retired = snapshot.LastRetiredSuggestion is null
                ? string.Empty
                : $" 最近退休：{snapshot.LastRetiredSuggestion.Reason}。";
            SuggestionStatusText.Text =
                $"当前 Suggestion：无。Lifecycle Revision={snapshot.Revision}.{retired}";
            DismissSuggestionButton.IsEnabled = false;
            return;
        }

        var suggestion = snapshot.CurrentSuggestion;
        var prefix = snapshot.State == DesktopLayoutSuggestionPresentationState.Active
            ? "当前 Suggestion：Active"
            : $"当前 Suggestion：Suspended ({snapshot.SuspensionReason})";
        SuggestionStatusText.Text =
            $"{prefix}  |  Template={suggestion.TemplateId:D}  |  Target={suggestion.TargetDeviceName}  |  "
            + $"Moves={suggestion.MoveRequiredCount}  |  Steps={suggestion.ExecutionStepCount}  |  "
            + $"Staging={suggestion.UsesTemporaryStaging}  |  SourceObservation={suggestion.SourceObservationId:D}  |  "
            + "仅建议证据，不可直接执行。";
        DismissSuggestionButton.IsEnabled = true;
    }

    private void UpdateButtons()
    {
        var monitoringRunning = _coordinator.IsMonitoring;
        var blockedByMonitoring = monitoringRunning || _monitoringOperation;

        RunOnceButton.IsEnabled = !_running && !blockedByMonitoring;
        ResetBaselineButton.IsEnabled = !_running && !blockedByMonitoring;
        CopyButton.IsEnabled = !_running && !_monitoringOperation;
        StartMonitoringButton.IsEnabled = !_running && !_monitoringOperation && !monitoringRunning;
        StopMonitoringButton.IsEnabled = !_running && !_monitoringOperation && monitoringRunning;

        RefreshSuggestionOptionsButton.IsEnabled = !_optionsLoading && !_monitoringOperation;
        ApplySuggestionBindingButton.IsEnabled = !_optionsLoading
            && SuggestionTemplateComboBox.SelectedItem is TemplateBindingRow;
        ClearSuggestionBindingButton.IsEnabled = !_optionsLoading
            && _coordinator.BindingSnapshot.IsConfigured;
        if (SuggestionTemplateComboBox.SelectedItem is TemplateBindingRow selected)
        {
            SuggestionTargetComboBox.IsEnabled = !_optionsLoading
                && selected.Binding.Kind == TargetDisplayBindingKind.SelectedTarget;
        }

        DismissSuggestionButton.IsEnabled =
            _coordinator.SuggestionSnapshot.HasCurrentSuggestion;
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
                "Baseline 已建立。后续真实变化会进入 Suggestion pipeline。",
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

    private static string FormatSuggestionProcessing(M3SuggestionProcessingReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== suggestion processing [{report.Origin}] @ {DateTimeOffset.Now:O} ===");
        if (report.TriggerRunId is not null)
            sb.AppendLine($"TriggerRunId: {report.TriggerRunId:D}");

        if (report.Fault is not null)
        {
            sb.AppendLine("Status: ObservationRunFailure");
            sb.AppendLine($"Lifecycle: {report.Lifecycle.State} / Revision={report.Lifecycle.Revision}");
            sb.AppendLine($"SuspensionReason: {report.Lifecycle.SuspensionReason}");
            sb.AppendLine("Fault:");
            sb.AppendLine(report.Fault);
            return sb.ToString();
        }

        var pipeline = report.PipelineResult!;
        sb.AppendLine($"PipelineStatus: {pipeline.Status}");
        sb.AppendLine($"BindingRevision: {pipeline.BindingSnapshot.Revision}");
        sb.AppendLine($"BindingConfigured: {pipeline.BindingSnapshot.IsConfigured}");
        sb.AppendLine($"Lifecycle: {pipeline.Lifecycle.State} / Revision={pipeline.Lifecycle.Revision}");
        if (pipeline.Lifecycle.SuspensionReason is not null)
            sb.AppendLine($"SuspensionReason: {pipeline.Lifecycle.SuspensionReason}");

        if (pipeline.BuildResult is { } build)
        {
            sb.AppendLine($"BuildStatus: {build.Status}");
            foreach (var issue in build.Issues)
            {
                sb.Append("  BuildIssue: ").Append(issue.Code);
                if (issue.StableId is not null)
                    sb.Append(" StableId=").Append(issue.StableId);
                sb.Append(": ").AppendLine(issue.Message);
            }
        }

        if (pipeline.CurrentSuggestion is { } suggestion)
        {
            sb.AppendLine($"SuggestionId: {suggestion.SuggestionId:D}");
            sb.AppendLine($"TemplateId: {suggestion.TemplateId:D}");
            sb.AppendLine($"TargetDevice: {suggestion.TargetDeviceName}");
            sb.AppendLine($"Moves: {suggestion.MoveRequiredCount}");
            sb.AppendLine($"ExecutionSteps: {suggestion.ExecutionStepCount}");
            sb.AppendLine($"UsesTemporaryStaging: {suggestion.UsesTemporaryStaging}");
            sb.AppendLine($"SourceObservationId: {suggestion.SourceObservationId:D}");
        }

        if (pipeline.EscapedException is not null)
        {
            sb.AppendLine("BuilderEscapedException:");
            sb.AppendLine(pipeline.EscapedException);
        }

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

    private static string DescribeTargetBinding(TargetDisplayBinding binding) =>
        binding.Kind switch
        {
            TargetDisplayBindingKind.SelectedTarget => "SelectedTarget",
            TargetDisplayBindingKind.PrimaryDisplay => "PrimaryDisplay",
            TargetDisplayBindingKind.ExactDeviceName => $"Exact:{binding.DeviceName}",
            _ => binding.Kind.ToString(),
        };

    private static string FormatDisplay(DisplayDescriptor display) =>
        $"{display.DeviceName} Bounds={display.Bounds} WorkArea={display.WorkArea} DPI={display.DpiX}x{display.DpiY} Primary={display.IsPrimary}";

    private static string FormatIcon(DesktopIconDescriptor icon) =>
        $"Name=\"{icon.DisplayName}\" Kind={icon.ItemKind} Pos=({icon.PixelPosition.X},{icon.PixelPosition.Y}) Monitor={icon.MonitorDeviceName ?? "<null>"}";

    private sealed record TemplateBindingRow(
        Guid TemplateId,
        string Name,
        TargetDisplayBinding Binding,
        string DisplayText);

    private sealed record DisplayBindingRow(
        string DeviceName,
        string DisplayText);
}
