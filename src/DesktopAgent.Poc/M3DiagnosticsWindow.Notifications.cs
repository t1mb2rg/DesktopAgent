using System.Windows;
using System.Windows.Threading;
using DesktopAgent.Application.DesktopNotifications;

namespace DesktopAgent.Poc;

public partial class M3DiagnosticsWindow
{
    private M3SuggestionNotificationCoordinator? _notificationCoordinator;

    public M3DiagnosticsWindow(
        M3SuggestionMonitoringCoordinator coordinator,
        M3SuggestionNotificationCoordinator notificationCoordinator)
        : this(coordinator)
    {
        _notificationCoordinator = notificationCoordinator
            ?? throw new ArgumentNullException(nameof(notificationCoordinator));

        _notificationCoordinator.SurfaceChanged += OnNotificationSurfaceChanged;
        _notificationCoordinator.NotificationReconciled += OnNotificationReconciled;
        _notificationCoordinator.OpenSuggestionRequested += OnNotificationOpenRequested;
        _notificationCoordinator.InteractionHandled += OnNotificationInteractionHandled;
        _notificationCoordinator.Faulted += OnNotificationFaulted;

        Loaded += OnNotificationLoaded;
        Closed += OnNotificationClosed;

        // 03D 的这些 handler 已由 XAML 在 InitializeComponent 中注册。
        // 04B 只追加“当前 routed event 结束后重新对账”的监听，不改 03D handler 本体。
        ResetBaselineButton.Click += OnSuggestionLifecycleUiMutation;
        ApplySuggestionBindingButton.Click += OnSuggestionLifecycleUiMutation;
        ClearSuggestionBindingButton.Click += OnSuggestionLifecycleUiMutation;
        DismissSuggestionButton.Click += OnSuggestionLifecycleUiMutation;
    }

    private void OnNotificationLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RenderNotification(_notificationCoordinator!.SurfaceSnapshot);
    }

    private void SimulateNotificationOpen_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_notificationCoordinator?.SimulateCurrentInteraction(
                DesktopSuggestionNotificationActionKind.OpenSuggestion) != true)
        {
            NotificationSchedulerStatusText.Text = "PoC Notification：当前没有可交互通知。";
        }
    }

    private void SimulateNotificationIgnore_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_notificationCoordinator?.SimulateCurrentInteraction(
                DesktopSuggestionNotificationActionKind.IgnoreSuggestion) != true)
        {
            NotificationSchedulerStatusText.Text = "PoC Notification：当前没有可交互通知。";
        }
    }

    private void OnSuggestionLifecycleUiMutation(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        // 等原始 03D click handler 完成后，再读取同一 singleton lifecycle 的最新 snapshot。
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _ = ReconcileNotificationAfterUiMutationAsync()));
    }

    private async Task ReconcileNotificationAfterUiMutationAsync()
    {
        if (_notificationCoordinator is null)
            return;

        try
        {
            await _notificationCoordinator.ReconcileLifecycleAsync(
                _coordinator.SuggestionSnapshot);
        }
        catch (Exception ex)
        {
            OnNotificationFaulted(ex.ToString());
        }
    }

    private void OnNotificationSurfaceChanged(M3PocNotificationSurfaceSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RenderNotification(snapshot);
            AppendDiagnostics(
                $"=== PoC notification surface @ {DateTimeOffset.Now:O} ===\r\n"
                + $"Revision: {snapshot.Revision}\r\n"
                + $"LastEvent: {snapshot.LastEvent}\r\n"
                + $"CurrentSuggestionId: {snapshot.CurrentNotification?.SuggestionId.ToString("D") ?? "<none>"}\r\n");
        }));
    }

    private void OnNotificationReconciled(
        DesktopSuggestionNotificationSchedulerResult result)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NotificationSchedulerStatusText.Text = result.Status switch
            {
                DesktopSuggestionNotificationSchedulerStatus.DeferredScheduled =>
                    $"Notification scheduler：冷却中，到 {result.DeferredUntilUtc:O} 重新检查最新 Suggestion。",
                DesktopSuggestionNotificationSchedulerStatus.Superseded =>
                    $"Notification scheduler：Lifecycle Revision {result.LifecycleRevision} 已被更新状态取代。",
                _ when result.DispatchResult?.Status == DesktopSuggestionNotificationDispatchStatus.SinkFailed =>
                    $"Notification scheduler：sink 失败 ({result.DispatchResult.FailedOperation})，后续 lifecycle 可重试。",
                _ =>
                    $"Notification scheduler：{result.DispatchResult?.Status} / Lifecycle Revision {result.LifecycleRevision}。",
            };

            AppendDiagnostics(FormatNotificationSchedulerResult(result));
        }));
    }

    private void OnNotificationOpenRequested(Guid suggestionId)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NotificationSchedulerStatusText.Text =
                $"PoC OpenSuggestion：请求打开 Suggestion {suggestionId:D}。这里只激活诊断窗，不执行整理。";
            if (!IsVisible)
                Show();
            Activate();
        }));
    }

    private void OnNotificationInteractionHandled(
        M3NotificationInteractionHandlingResult result)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RenderSuggestion(result.Lifecycle);
            NotificationSchedulerStatusText.Text = result.LifecycleChanged
                ? $"PoC IgnoreSuggestion：已忽略 {result.Interaction.SuggestionId:D}。"
                : $"PoC IgnoreSuggestion：{result.Interaction.SuggestionId:D} 已过期，当前 Suggestion 未被修改。";

            AppendDiagnostics(
                $"=== PoC notification interaction @ {DateTimeOffset.Now:O} ===\r\n"
                + $"Action: {result.Interaction.Action}\r\n"
                + $"SuggestionId: {result.Interaction.SuggestionId:D}\r\n"
                + $"LifecycleChanged: {result.LifecycleChanged}\r\n"
                + $"Lifecycle: {result.Lifecycle.State} / Revision={result.Lifecycle.Revision}\r\n");
            UpdateButtons();
        }));
    }

    private void OnNotificationFaulted(string fault)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NotificationSchedulerStatusText.Text = "PoC Notification：调度/交互发生异常，请查看诊断。";
            AppendDiagnostics(
                $"=== PoC notification fault @ {DateTimeOffset.Now:O} ===\r\n{fault}\r\n");
        }));
    }

    private void OnNotificationClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;

        ResetBaselineButton.Click -= OnSuggestionLifecycleUiMutation;
        ApplySuggestionBindingButton.Click -= OnSuggestionLifecycleUiMutation;
        ClearSuggestionBindingButton.Click -= OnSuggestionLifecycleUiMutation;
        DismissSuggestionButton.Click -= OnSuggestionLifecycleUiMutation;

        Loaded -= OnNotificationLoaded;
        Closed -= OnNotificationClosed;

        if (_notificationCoordinator is null)
            return;

        _notificationCoordinator.SurfaceChanged -= OnNotificationSurfaceChanged;
        _notificationCoordinator.NotificationReconciled -= OnNotificationReconciled;
        _notificationCoordinator.OpenSuggestionRequested -= OnNotificationOpenRequested;
        _notificationCoordinator.InteractionHandled -= OnNotificationInteractionHandled;
        _notificationCoordinator.Faulted -= OnNotificationFaulted;
    }

    private void RenderNotification(M3PocNotificationSurfaceSnapshot snapshot)
    {
        if (snapshot.CurrentNotification is null)
        {
            NotificationStatusText.Text =
                $"PoC Notification：无当前通知。Surface Revision={snapshot.Revision}，Last={snapshot.LastEvent}。";
            SimulateNotificationOpenButton.IsEnabled = false;
            SimulateNotificationIgnoreButton.IsEnabled = false;
            return;
        }

        var payload = snapshot.CurrentNotification;
        NotificationStatusText.Text =
            $"PoC Notification：Suggestion={payload.SuggestionId:D}  |  Target={payload.TargetDeviceName}  |  "
            + $"Moves={payload.MoveRequiredCount}  |  Steps={payload.ExecutionStepCount}  |  "
            + $"SourceObservation={payload.SourceObservationId:D}";
        SimulateNotificationOpenButton.IsEnabled = true;
        SimulateNotificationIgnoreButton.IsEnabled = true;
    }

    private static string FormatNotificationSchedulerResult(
        DesktopSuggestionNotificationSchedulerResult result)
    {
        var dispatch = result.DispatchResult;
        return
            $"=== notification reconcile @ {DateTimeOffset.Now:O} ===\r\n"
            + $"SchedulerStatus: {result.Status}\r\n"
            + $"LifecycleRevision: {result.LifecycleRevision}\r\n"
            + $"DispatchStatus: {dispatch?.Status.ToString() ?? "<none>"}\r\n"
            + $"Suppression: {dispatch?.Plan.SuppressionReason?.ToString() ?? "<none>"}\r\n"
            + $"DeferredUntilUtc: {result.DeferredUntilUtc?.ToString("O") ?? "<none>"}\r\n"
            + $"FailedOperation: {dispatch?.FailedOperation?.ToString() ?? "<none>"}\r\n";
    }
}
