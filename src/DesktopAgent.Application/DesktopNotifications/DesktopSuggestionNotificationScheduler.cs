using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Application.DesktopNotifications;

public enum DesktopSuggestionNotificationSchedulerStatus
{
    Reconciled = 0,
    DeferredScheduled = 1,
    Superseded = 2,
}

public sealed record DesktopSuggestionNotificationSchedulerResult
{
    public DesktopSuggestionNotificationSchedulerStatus Status { get; }
    public long LifecycleRevision { get; }
    public DesktopSuggestionNotificationDispatchResult? DispatchResult { get; }
    public DateTimeOffset? DeferredUntilUtc { get; }

    public DesktopSuggestionNotificationSchedulerResult(
        DesktopSuggestionNotificationSchedulerStatus status,
        long lifecycleRevision,
        DesktopSuggestionNotificationDispatchResult? dispatchResult,
        DateTimeOffset? deferredUntilUtc)
    {
        if (lifecycleRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(lifecycleRevision));

        if (status == DesktopSuggestionNotificationSchedulerStatus.Superseded)
        {
            if (dispatchResult is not null || deferredUntilUtc is not null)
            {
                throw new ArgumentException(
                    "Superseded scheduler result 不得携带 dispatch/deferred 结果。",
                    nameof(status));
            }
        }
        else if (dispatchResult is null)
        {
            throw new ArgumentException(
                "非 Superseded scheduler result 必须携带 DispatchResult。",
                nameof(dispatchResult));
        }

        if (status == DesktopSuggestionNotificationSchedulerStatus.DeferredScheduled
            && deferredUntilUtc is null)
        {
            throw new ArgumentException(
                "DeferredScheduled 必须携带 DeferredUntilUtc。",
                nameof(deferredUntilUtc));
        }

        if (status != DesktopSuggestionNotificationSchedulerStatus.DeferredScheduled
            && deferredUntilUtc is not null)
        {
            throw new ArgumentException(
                "只有 DeferredScheduled 可以携带 DeferredUntilUtc。",
                nameof(deferredUntilUtc));
        }

        Status = status;
        LifecycleRevision = lifecycleRevision;
        DispatchResult = dispatchResult;
        DeferredUntilUtc = deferredUntilUtc?.ToUniversalTime();
    }
}

internal delegate Task DesktopSuggestionNotificationDelayAsync(
    TimeSpan delay,
    TimeProvider timeProvider,
    CancellationToken ct);

/// <summary>
/// M3-04B deferred notification scheduler。
/// 只保存最新 Suggestion lifecycle；到点后重新 Evaluate 最新 lifecycle，
/// 不保存/重放旧 pending payload。
/// </summary>
public sealed class DesktopSuggestionNotificationScheduler : IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly DesktopSuggestionNotificationDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly DesktopSuggestionNotificationDelayAsync _delayAsync;

    private DesktopLayoutSuggestionLifecycleSnapshot? _latestLifecycle;
    private long _generation;
    private CancellationTokenSource? _deferredDelayCts;
    private Task _deferredTask = Task.CompletedTask;
    private int _disposeStarted;

    public event Action<DesktopSuggestionNotificationSchedulerResult>? Reconciled;
    public event Action<string>? Faulted;

    public DesktopLayoutSuggestionLifecycleSnapshot? LatestLifecycle
    {
        get
        {
            lock (_stateGate)
                return _latestLifecycle;
        }
    }

    public DesktopSuggestionNotificationScheduler(
        DesktopSuggestionNotificationDispatcher dispatcher,
        TimeProvider? timeProvider = null)
        : this(
            dispatcher,
            timeProvider ?? TimeProvider.System,
            static (delay, provider, ct) => Task.Delay(delay, provider, ct))
    {
    }

    internal DesktopSuggestionNotificationScheduler(
        DesktopSuggestionNotificationDispatcher dispatcher,
        TimeProvider timeProvider,
        DesktopSuggestionNotificationDelayAsync delayAsync)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public async Task<DesktopSuggestionNotificationSchedulerResult> ReconcileAsync(
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        CancellationTokenSource? previousDelay;
        long generation;
        lock (_stateGate)
        {
            ThrowIfDisposed();
            _latestLifecycle = lifecycle;
            generation = ++_generation;
            previousDelay = _deferredDelayCts;
            _deferredDelayCts = null;
        }

        previousDelay?.Cancel();

        return await ReconcileGenerationAsync(
            generation,
            lifecycle,
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        CancellationTokenSource? delayCts;
        Task deferredTask;
        lock (_stateGate)
        {
            _generation++;
            _latestLifecycle = null;
            delayCts = _deferredDelayCts;
            _deferredDelayCts = null;
            deferredTask = _deferredTask;
        }

        delayCts?.Cancel();

        try
        {
            await deferredTask;
        }
        catch
        {
            // RunDeferredAsync 会收敛自身异常；这里只做最终资源回收兜底。
        }

        await _reconcileGate.WaitAsync();
        _reconcileGate.Release();
        _reconcileGate.Dispose();
    }

    private async Task<DesktopSuggestionNotificationSchedulerResult> ReconcileGenerationAsync(
        long generation,
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle,
        CancellationToken ct)
    {
        await _reconcileGate.WaitAsync(ct);
        try
        {
            if (!IsCurrentGeneration(generation))
            {
                var superseded = new DesktopSuggestionNotificationSchedulerResult(
                    DesktopSuggestionNotificationSchedulerStatus.Superseded,
                    lifecycle.Revision,
                    null,
                    null);
                PublishSafely(Reconciled, superseded);
                return superseded;
            }

            var dispatch = await _dispatcher.ReconcileAsync(lifecycle, ct);

            DesktopSuggestionNotificationSchedulerResult result;
            if (dispatch.Status == DesktopSuggestionNotificationDispatchStatus.Deferred
                && dispatch.NextEligibleAtUtc is DateTimeOffset dueUtc
                && IsCurrentGeneration(generation))
            {
                ScheduleDeferred(generation, dueUtc);
                result = new DesktopSuggestionNotificationSchedulerResult(
                    DesktopSuggestionNotificationSchedulerStatus.DeferredScheduled,
                    lifecycle.Revision,
                    dispatch,
                    dueUtc);
            }
            else
            {
                result = new DesktopSuggestionNotificationSchedulerResult(
                    DesktopSuggestionNotificationSchedulerStatus.Reconciled,
                    lifecycle.Revision,
                    dispatch,
                    null);
            }

            PublishSafely(Reconciled, result);
            return result;
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private void ScheduleDeferred(long generation, DateTimeOffset dueUtc)
    {
        CancellationTokenSource? previous;
        var cts = new CancellationTokenSource();

        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposeStarted) != 0
                || _generation != generation)
            {
                cts.Dispose();
                return;
            }

            previous = _deferredDelayCts;
            _deferredDelayCts = cts;
            _deferredTask = RunDeferredAsync(generation, dueUtc, cts);
        }

        previous?.Cancel();
    }

    private async Task RunDeferredAsync(
        long generation,
        DateTimeOffset dueUtc,
        CancellationTokenSource cts)
    {
        try
        {
            var delay = dueUtc.ToUniversalTime() - _timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
                await _delayAsync(delay, _timeProvider, cts.Token);

            DesktopLayoutSuggestionLifecycleSnapshot? latest;
            lock (_stateGate)
            {
                if (Volatile.Read(ref _disposeStarted) != 0
                    || _generation != generation
                    || !ReferenceEquals(_deferredDelayCts, cts))
                {
                    return;
                }

                latest = _latestLifecycle;
                _deferredDelayCts = null;
            }

            if (latest is null)
                return;

            // delay 只控制“何时重新问一次”；一旦到点并通过 generation gate，
            // dispatcher 自身操作不再绑定旧 timer token，避免 Publish 已发生但 ack 被取消的半状态。
            await ReconcileGenerationAsync(
                generation,
                latest,
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PublishSafely(Faulted, ex.ToString());
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_deferredDelayCts, cts))
                    _deferredDelayCts = null;
            }

            cts.Dispose();
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_stateGate)
        {
            return Volatile.Read(ref _disposeStarted) == 0
                && _generation == generation;
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
                // diagnostics/UI observer 不得反向破坏 notification scheduler。
            }
        }
    }
}
