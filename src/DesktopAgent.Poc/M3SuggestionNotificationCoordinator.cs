using DesktopAgent.Application.DesktopNotifications;
using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Poc;

public sealed record M3NotificationInteractionHandlingResult(
    DesktopSuggestionNotificationInteraction Interaction,
    bool LifecycleChanged,
    DesktopLayoutSuggestionLifecycleSnapshot Lifecycle);

/// <summary>
/// M3-04B PoC bridge：
/// Suggestion lifecycle → notification scheduler → PoC sink，
/// 并把 Open / Ignore interaction 路由回只读 Suggestion lifecycle。
/// 不接触 Candidate execution、Writer 或 ExecutionCoordinator。
/// </summary>
public sealed class M3SuggestionNotificationCoordinator : IDisposable
{
    private readonly M3SuggestionMonitoringCoordinator _suggestionCoordinator;
    private readonly DesktopLayoutSuggestionLifecycle _lifecycle;
    private readonly DesktopSuggestionNotificationScheduler _scheduler;
    private readonly M3DiagnosticsNotificationSink _sink;
    private int _disposeStarted;

    public event Action<DesktopSuggestionNotificationSchedulerResult>? NotificationReconciled;
    public event Action<M3PocNotificationSurfaceSnapshot>? SurfaceChanged;
    public event Action<Guid>? OpenSuggestionRequested;
    public event Action<M3NotificationInteractionHandlingResult>? InteractionHandled;
    public event Action<string>? Faulted;

    public M3PocNotificationSurfaceSnapshot SurfaceSnapshot => _sink.Snapshot;

    public M3SuggestionNotificationCoordinator(
        M3SuggestionMonitoringCoordinator suggestionCoordinator,
        DesktopLayoutSuggestionLifecycle lifecycle,
        DesktopSuggestionNotificationScheduler scheduler,
        M3DiagnosticsNotificationSink sink)
    {
        _suggestionCoordinator = suggestionCoordinator
            ?? throw new ArgumentNullException(nameof(suggestionCoordinator));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

        _suggestionCoordinator.SuggestionProcessed += OnSuggestionProcessed;
        _scheduler.Reconciled += OnNotificationReconciled;
        _scheduler.Faulted += OnSchedulerFaulted;
        _sink.SurfaceChanged += OnSurfaceChanged;
        _sink.InteractionReceived += OnInteractionReceived;
    }

    public Task<DesktopSuggestionNotificationSchedulerResult> ReconcileLifecycleAsync(
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _scheduler.ReconcileAsync(lifecycle, ct);
    }

    public bool SimulateCurrentInteraction(DesktopSuggestionNotificationActionKind action)
    {
        ThrowIfDisposed();
        return _sink.SimulateCurrentInteraction(action);
    }

    public void SimulateInteraction(
        Guid suggestionId,
        DesktopSuggestionNotificationActionKind action)
    {
        ThrowIfDisposed();
        _sink.SimulateInteraction(suggestionId, action);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _suggestionCoordinator.SuggestionProcessed -= OnSuggestionProcessed;
        _scheduler.Reconciled -= OnNotificationReconciled;
        _scheduler.Faulted -= OnSchedulerFaulted;
        _sink.SurfaceChanged -= OnSurfaceChanged;
        _sink.InteractionReceived -= OnInteractionReceived;
    }

    private void OnSuggestionProcessed(M3SuggestionProcessingReport report) =>
        _ = ReconcileSafelyAsync(report.Lifecycle);

    private void OnNotificationReconciled(
        DesktopSuggestionNotificationSchedulerResult result) =>
        PublishSafely(NotificationReconciled, result);

    private void OnSchedulerFaulted(string fault) =>
        PublishSafely(Faulted, fault);

    private void OnSurfaceChanged(M3PocNotificationSurfaceSnapshot snapshot) =>
        PublishSafely(SurfaceChanged, snapshot);

    private void OnInteractionReceived(DesktopSuggestionNotificationInteraction interaction)
    {
        if (interaction.Action == DesktopSuggestionNotificationActionKind.OpenSuggestion)
        {
            PublishSafely(OpenSuggestionRequested, interaction.SuggestionId);
            return;
        }

        if (interaction.Action == DesktopSuggestionNotificationActionKind.IgnoreSuggestion)
        {
            _ = HandleIgnoreAsync(interaction);
            return;
        }

        PublishSafely(Faulted, $"未知 notification interaction：{interaction.Action}");
    }

    private async Task HandleIgnoreAsync(
        DesktopSuggestionNotificationInteraction interaction)
    {
        try
        {
            // 原子 expected-id gate：旧 notification 的 Ignore 不得误删新 Suggestion。
            var dismiss = _lifecycle.DismissCurrent(interaction.SuggestionId);

            // 即使 interaction 已 stale，也对当前 lifecycle 再做一次通知对账，
            // 以便撤掉可能残留的旧 surface 并展示/延期最新 Suggestion。
            await _scheduler.ReconcileAsync(dismiss.Lifecycle);

            PublishSafely(
                InteractionHandled,
                new M3NotificationInteractionHandlingResult(
                    interaction,
                    dismiss.Dismissed,
                    dismiss.Lifecycle));
        }
        catch (Exception ex)
        {
            PublishSafely(Faulted, ex.ToString());
        }
    }

    private async Task ReconcileSafelyAsync(
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle)
    {
        try
        {
            await _scheduler.ReconcileAsync(lifecycle);
        }
        catch (Exception ex)
        {
            PublishSafely(Faulted, ex.ToString());
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);

    private static void PublishSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
            return;

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // UI/diagnostics observer 不得反向破坏 notification bridge。
            }
        }
    }
}
