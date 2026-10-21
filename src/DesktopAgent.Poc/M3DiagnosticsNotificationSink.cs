using DesktopAgent.Application.DesktopNotifications;

namespace DesktopAgent.Poc;

public sealed record M3PocNotificationSurfaceSnapshot(
    long Revision,
    DesktopSuggestionNotificationPayload? CurrentNotification,
    string LastEvent,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Notification diagnostics mirror。
/// 04B 可独立作为纯内存 PoC sink；04C 可包装真实平台 sink。
/// 只有 inner sink 成功后才更新镜像，因此 diagnostics 不会领先于真实平台副作用。
/// </summary>
public sealed class M3DiagnosticsNotificationSink : IDesktopSuggestionNotificationSink, IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly IDesktopSuggestionNotificationSink? _inner;

    private long _revision;
    private DesktopSuggestionNotificationPayload? _current;
    private string _lastEvent = "Ready";
    private DateTimeOffset _updatedAtUtc;
    private int _disposeStarted;

    public event Action<DesktopSuggestionNotificationInteraction>? InteractionReceived;
    public event Action<M3PocNotificationSurfaceSnapshot>? SurfaceChanged;

    public M3DiagnosticsNotificationSink(TimeProvider? timeProvider = null)
        : this(null, timeProvider)
    {
    }

    public M3DiagnosticsNotificationSink(
        IDesktopSuggestionNotificationSink? inner,
        TimeProvider? timeProvider = null)
    {
        _inner = inner;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _updatedAtUtc = _timeProvider.GetUtcNow();

        if (_inner is not null)
            _inner.InteractionReceived += OnInnerInteractionReceived;
    }

    public M3PocNotificationSurfaceSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return CreateSnapshot();
        }
    }

    public async Task PublishAsync(
        DesktopSuggestionNotificationPayload payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (_inner is not null)
            await _inner.PublishAsync(payload, ct);

        M3PocNotificationSurfaceSnapshot snapshot;
        lock (_gate)
        {
            _current = payload;
            _revision++;
            _lastEvent = $"Published {payload.SuggestionId:D}";
            _updatedAtUtc = _timeProvider.GetUtcNow();
            snapshot = CreateSnapshot();
        }

        PublishSafely(SurfaceChanged, snapshot);
    }

    public async Task WithdrawAsync(Guid suggestionId, CancellationToken ct = default)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("suggestionId 不能为空。", nameof(suggestionId));
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (_inner is not null)
            await _inner.WithdrawAsync(suggestionId, ct);

        M3PocNotificationSurfaceSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_current?.SuggestionId == suggestionId)
            {
                _current = null;
                _revision++;
                _lastEvent = $"Withdrawn {suggestionId:D}";
                _updatedAtUtc = _timeProvider.GetUtcNow();
                snapshot = CreateSnapshot();
            }
        }

        if (snapshot is not null)
            PublishSafely(SurfaceChanged, snapshot);
    }

    public bool SimulateCurrentInteraction(DesktopSuggestionNotificationActionKind action)
    {
        ThrowIfDisposed();

        DesktopSuggestionNotificationPayload? payload;
        lock (_gate)
            payload = _current;

        if (payload is null)
            return false;

        SimulateInteraction(payload.SuggestionId, action);
        return true;
    }

    /// <summary>
    /// 允许测试/PoC 模拟一个可能晚到的旧 notification action。
    /// stale-safe 与否由上层按 SuggestionId 原子校验。
    /// </summary>
    public void SimulateInteraction(
        Guid suggestionId,
        DesktopSuggestionNotificationActionKind action)
    {
        ThrowIfDisposed();
        var interaction = new DesktopSuggestionNotificationInteraction(
            suggestionId,
            action);
        PublishSafely(InteractionReceived, interaction);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        if (_inner is not null)
            _inner.InteractionReceived -= OnInnerInteractionReceived;
    }

    private void OnInnerInteractionReceived(
        DesktopSuggestionNotificationInteraction interaction) =>
        PublishSafely(InteractionReceived, interaction);

    private M3PocNotificationSurfaceSnapshot CreateSnapshot() =>
        new(
            _revision,
            _current,
            _lastEvent,
            _updatedAtUtc);

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
                // Diagnostics observer 不得反向破坏真实 notification sink。
            }
        }
    }
}
