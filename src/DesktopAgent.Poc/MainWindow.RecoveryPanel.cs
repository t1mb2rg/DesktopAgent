using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Poc;

public partial class MainWindow
{
    private readonly ObservableCollection<RecoverySessionRow> _recoveryRows = [];
    private bool _recoveryPanelRefreshInProgress;
    private bool _recoveryPanelCanAct;
    private string? _recoveryPanelLastDiagnostic;

    private sealed record RecoverySessionRow(
        Guid SessionId,
        string SessionIdShort,
        string TargetDeviceName,
        string StartedAtLocal,
        string Lifecycle,
        string Handling,
        string Safety,
        string Summary)
    {
        public static RecoverySessionRow From(RecoverySessionRecord record)
        {
            var disposition = record.GetDisposition();
            return new RecoverySessionRow(
                record.SessionId,
                record.SessionId.ToString("D")[..8],
                record.TargetDeviceName,
                record.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                record.Lifecycle.ToString(),
                FormatHandling(disposition.Handling),
                FormatSafety(disposition.SafetyClass),
                FormatRecordSummary(record));
        }

        private static string FormatHandling(RecoverySessionHandling handling) => handling switch
        {
            RecoverySessionHandling.None => "无需处理",
            RecoverySessionHandling.AutomaticReconciliation => "可安全对账",
            RecoverySessionHandling.ManualIntervention => "需人工确认",
            _ => "未知"
        };

        private static string FormatSafety(RecoverySessionSafetyClass safety) => safety switch
        {
            RecoverySessionSafetyClass.None => "无持久化不确定性",
            RecoverySessionSafetyClass.KnownIncomplete => "已知未完整恢复",
            RecoverySessionSafetyClass.PositionUncertain => "位置不确定",
            _ => "未知"
        };
    }

    private async void RecoveryBorder_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (RecoveryBorder.Visibility == Visibility.Visible)
            await RefreshRecoveryPanelAsync();
    }

    private async void RecoveryRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRecoveryPanelAsync();
    }

    private void RecoverySessionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRecoverySelectionState();
    }

    private async void RecoveryRestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutRestoreOperationInProgress
            || !_recoveryPanelCanAct
            || RecoverySessionsGrid.SelectedItem is not RecoverySessionRow selected)
        {
            return;
        }

        RecoverySessionRecord? current;
        try
        {
            var latest = await _recoveryCoordinator.ListUnresolvedAsync();
            current = latest.SingleOrDefault(x => x.SessionId == selected.SessionId);
        }
        catch (Exception ex)
        {
            ShowRecoveryPanelFailure("重新读取恢复会话失败", ex);
            return;
        }

        if (current is null)
        {
            StatusText.Text = $"恢复会话 {selected.SessionId:D} 已不再处于未解决状态，已重新检查列表。";
            await RefreshRecoveryPanelAsync();
            return;
        }

        var disposition = current.GetDisposition();
        var unresolvedCount = _recoveryRows.Count;
        var confirmation = MessageBox.Show(
            $"本次只检查并处理一个恢复会话。\r\n\r\n" +
            $"会话：{current.SessionId:D}\r\n" +
            $"显示器：{current.TargetDeviceName}\r\n" +
            $"持久化状态：{current.Lifecycle}\r\n" +
            $"处置：{RecoverySessionRow.FormatHandling(disposition.Handling)}\r\n" +
            $"安全分类：{RecoverySessionRow.FormatSafety(disposition.SafetyClass)}\r\n\r\n" +
            "恢复器会先重新观察真实桌面；只有当前事实足够确定时才可能执行补偿 Writer。\r\n" +
            "不会执行新的 Forward 步骤，也不会处理列表中的其他会话。" +
            (unresolvedCount > 1 ? $"\r\n\r\n当前另有 {unresolvedCount - 1} 个未解决会话，本次不会处理。" : string.Empty) +
            "\r\n\r\n继续？",
            "确认处理此恢复会话",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
            return;

        _layoutRestoreOperationInProgress = true;
        UpdateRecoverySelectionState();
        UpdateInteractiveState();

        try
        {
            var result = await _recoveryCoordinator.RecoverAsync(current);
            var resultText = FormatRecoveryRunResult(current, result);
            _recoveryPanelLastDiagnostic = BuildRecoveryDiagnostic(current, result);
            StatusText.Text = resultText;
            StatusText.ScrollToHome();

            await RefreshRecoveryPanelAsync();

            if (RecoveryBorder.Visibility == Visibility.Visible)
            {
                RecoveryStatusText.Text =
                    $"刚刚处理会话 {current.SessionId.ToString("D")[..8]}：{FormatRunStatus(result.Status)}。\r\n" +
                    $"仍有 {_recoveryRows.Count} 个未解决会话，请逐条检查。";
            }
        }
        catch (Exception ex)
        {
            ShowRecoveryPanelFailure("恢复过程异常", ex);
        }
        finally
        {
            _layoutRestoreOperationInProgress = false;
            RecoveryRefreshButton.IsEnabled = !_recoveryPanelRefreshInProgress;
            UpdateRecoverySelectionState();
            UpdateInteractiveState();
        }
    }

    private void RecoveryCopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = _recoveryPanelLastDiagnostic;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = RecoverySessionsGrid.SelectedItem is RecoverySessionRow selected
                    ? BuildSelectedSessionDiagnostic(selected)
                    : RecoveryStatusText.Text;
            }

            if (string.IsNullOrWhiteSpace(text))
                return;

            Clipboard.SetText(text);
            RecoveryCopyDiagnosticsButton.Content = "已复制";
        }
        catch (Exception ex)
        {
            RecoveryCopyDiagnosticsButton.Content = "复制失败";
            _recoveryPanelLastDiagnostic = $"复制恢复诊断失败：{ex}";
        }
        finally
        {
            _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() =>
                RecoveryCopyDiagnosticsButton.Content = "复制恢复诊断"));
        }
    }

    private async Task RefreshRecoveryPanelAsync()
    {
        if (_recoveryPanelRefreshInProgress)
            return;

        _recoveryPanelRefreshInProgress = true;
        _recoveryPanelCanAct = false;
        RecoveryRefreshButton.IsEnabled = false;
        RecoveryRestoreButton.IsEnabled = false;
        RecoverySessionsGrid.ItemsSource ??= _recoveryRows;

        var previousSelection = (RecoverySessionsGrid.SelectedItem as RecoverySessionRow)?.SessionId;

        try
        {
            var unresolved = await _recoveryCoordinator.ListUnresolvedAsync();
            _recoveryRows.Clear();
            foreach (var record in unresolved)
                _recoveryRows.Add(RecoverySessionRow.From(record));

            if (_recoveryRows.Count == 0)
            {
                _recoveryPanelCanAct = false;
                _layoutRestoreRequiresAttention = false;
                RecoveryStatusText.Text = "当前没有未解决的恢复会话。";
                RecoverySessionDetailsText.Text = string.Empty;
                RecoveryBorder.Visibility = Visibility.Collapsed;
                return;
            }

            _recoveryPanelCanAct = true;
            _layoutRestoreRequiresAttention = true;
            RecoveryBorder.Visibility = Visibility.Visible;
            RecoveryStatusText.Text =
                $"检测到 {_recoveryRows.Count} 个未解决恢复会话。请选择一条查看详情；恢复操作只作用于当前选中会话。";

            var selected = previousSelection.HasValue
                ? _recoveryRows.FirstOrDefault(x => x.SessionId == previousSelection.Value)
                : null;
            RecoverySessionsGrid.SelectedItem = selected ?? _recoveryRows[0];
            RecoverySessionsGrid.ScrollIntoView(RecoverySessionsGrid.SelectedItem);
            UpdateRecoverySelectionState();
        }
        catch (Exception ex)
        {
            _recoveryPanelCanAct = false;
            _recoveryRows.Clear();
            _layoutRestoreRequiresAttention = true;
            RecoveryBorder.Visibility = Visibility.Visible;
            RecoveryStatusText.Text =
                "恢复数据无法安全读取，真实写入保持锁定。可复制恢复诊断后人工检查数据库。";
            RecoverySessionDetailsText.Text = ex.Message;
            _recoveryPanelLastDiagnostic = $"恢复数据读取异常：\r\n{ex}";
            RecoveryRestoreButton.IsEnabled = false;
        }
        finally
        {
            _recoveryPanelRefreshInProgress = false;
            RecoveryRefreshButton.IsEnabled = !_layoutRestoreOperationInProgress;
            UpdateRecoverySelectionState();
            UpdateInteractiveState();
        }
    }

    private void UpdateRecoverySelectionState()
    {
        var selected = RecoverySessionsGrid.SelectedItem as RecoverySessionRow;
        RecoverySessionDetailsText.Text = selected?.Summary ?? "请选择一个恢复会话。";
        RecoveryRestoreButton.IsEnabled =
            _recoveryPanelCanAct
            && !_layoutRestoreOperationInProgress
            && !_recoveryPanelRefreshInProgress
            && selected is not null;
    }

    private void ShowRecoveryPanelFailure(string heading, Exception ex)
    {
        _recoveryPanelCanAct = false;
        _layoutRestoreRequiresAttention = true;
        RecoveryBorder.Visibility = Visibility.Visible;
        RecoveryStatusText.Text = $"{heading}，真实写入保持锁定。";
        RecoverySessionDetailsText.Text = ex.Message;
        _recoveryPanelLastDiagnostic = $"{heading}：\r\n{ex}";
        StatusText.Text = _recoveryPanelLastDiagnostic;
        StatusText.ScrollToHome();
        RecoveryRestoreButton.IsEnabled = false;
        UpdateInteractiveState();
    }

    private static string FormatRecordSummary(RecoverySessionRecord record)
    {
        var disposition = record.GetDisposition();
        var sb = new StringBuilder();
        sb.AppendLine($"会话：{record.SessionId:D}");
        sb.AppendLine($"目标显示器：{record.TargetDeviceName}");
        sb.AppendLine($"开始时间：{record.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Lifecycle：{record.Lifecycle}");
        sb.AppendLine($"处置：{RecoverySessionRow.FormatHandling(disposition.Handling)}");
        sb.AppendLine($"安全分类：{RecoverySessionRow.FormatSafety(disposition.SafetyClass)}");
        sb.AppendLine($"Forward attempts：{record.ForwardAttempts.Count}；Rollback attempts：{record.RollbackAttempts.Count}");
        sb.AppendLine($"完成 Forward：{record.CompletedForwardStepIndexes.Count}；完成 Rollback：{record.CompletedRollbackSourceStepIndexes.Count}");

        if (record.LastFailedStepIndex is int failedStep)
            sb.AppendLine($"最后失败步骤：#{failedStep}");

        if (!string.IsNullOrWhiteSpace(record.RecoveryFailureType)
            || !string.IsNullOrWhiteSpace(record.RecoveryFailureMessage))
        {
            sb.AppendLine($"恢复失败：{record.RecoveryFailureType ?? "Unknown"} - {record.RecoveryFailureMessage ?? "无详细消息"}");
        }
        else if (!string.IsNullOrWhiteSpace(record.ForwardFailureType)
                 || !string.IsNullOrWhiteSpace(record.ForwardFailureMessage))
        {
            sb.AppendLine($"Forward 失败：{record.ForwardFailureType ?? "Unknown"} - {record.ForwardFailureMessage ?? "无详细消息"}");
        }

        if (record.VerificationResult is not null)
        {
            sb.AppendLine($"最后验证：{record.VerificationResult.Phase} / {record.VerificationResult.Status}；问题 {record.VerificationResult.Issues.Count} 项");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatRecoveryRunResult(
        RecoverySessionRecord source,
        TargetDisplayLayoutExecutionRunResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[M2-04C] 恢复会话处理结果：{source.SessionId:D}");
        sb.AppendLine($"状态：{FormatRunStatus(result.Status)}");
        sb.AppendLine($"成功：{result.IsSuccess}；NoOp：{result.IsNoOp}");
        sb.AppendLine($"需要人工处理：{result.RequiresManualRecovery}；位置不确定：{result.IsPositionUncertain}");
        sb.AppendLine($"本次 Rollback attempts：{result.RollbackAttempts.Count}");

        if (!string.IsNullOrWhiteSpace(result.RecoveryFailureType)
            || !string.IsNullOrWhiteSpace(result.RecoveryFailureMessage))
        {
            sb.AppendLine($"恢复信息：{result.RecoveryFailureType ?? "Unknown"} - {result.RecoveryFailureMessage ?? "无详细消息"}");
        }

        if (result.VerificationResult is not null)
        {
            sb.AppendLine($"验证：{result.VerificationResult.Phase} / {result.VerificationResult.Status}；问题 {result.VerificationResult.Issues.Count} 项");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildRecoveryDiagnostic(
        RecoverySessionRecord source,
        TargetDisplayLayoutExecutionRunResult result) =>
        $"{FormatRecordSummary(source)}\r\n\r\n--- 本次处理结果 ---\r\n{FormatRecoveryRunResult(source, result)}";

    private static string BuildSelectedSessionDiagnostic(RecoverySessionRow selected) =>
        $"Recovery Session {selected.SessionId:D}\r\n{selected.Summary}";

    private static string FormatRunStatus(TargetDisplayLayoutExecutionRunStatus status) => status switch
    {
        TargetDisplayLayoutExecutionRunStatus.Completed => "已确认当前目标布局生效",
        TargetDisplayLayoutExecutionRunStatus.CompletedNoOp => "无需写入，当前事实已安全收敛",
        TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack => "已确认恢复到原始位置",
        TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete => "当前状态已知，但恢复不完整",
        TargetDisplayLayoutExecutionRunStatus.PositionUncertain => "位置状态无法安全证明",
        TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite => "Writer 前失败",
        _ => status.ToString()
    };
}
