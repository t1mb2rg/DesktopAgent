using DesktopAgent.Application.DesktopNotifications;

namespace DesktopAgent.Poc;

public sealed record M3PocNotificationSurfaceSnapshot(
    long Revision,
    DesktopSuggestionNotificationPayload? CurrentNotification,
    string LastEvent,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// M3-04B 的平台无关 PoC notification surface。
/// 只在内存里记录当前通知并允许模拟 Open / Ignore action；不调用 Windows Toast。
/// </summary>
public sealed class M3DiagnosticsNotificationSink : IDesktopSuggestionNotificationSink
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    private long _revision;
    private DesktopSuggestionNotificationPayload? _current;
    private string _lastEvent = "Ready";
    private DateTimeOffset _updatedAtUtc;

    public event Action<DesktopSuggestionNotificationInteraction>? InteractionReceived;
    public event Action<M3PocNotificationSurfaceSnapshot>? SurfaceChanged;

    public M3DiagnosticsNotificationSink(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _updatedAtUtc = _timeProvider.GetUtcNow();
    }

    public M3PocNotificationSurfaceSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return CreateSnapshot();
        }
    }

    public Task PublishAsync(
        DesktopSuggestionNotificationPayload payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ct.ThrowIfCancellationRequested();

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
        return Task.CompletedTask;
    }

    public Task WithdrawAsync(Guid suggestionId, CancellationToken ct = default)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("suggestionId 不能为空。", nameof(suggestionId));
        ct.ThrowIfCancellationRequested();

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

        return Task.CompletedTask;
    }

    public bool SimulateCurrentInteraction(DesktopSuggestionNotificationActionKind action)
    {
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
        var interaction = new DesktopSuggestionNotificationInteraction(
            suggestionId,
            action);
        PublishSafely(InteractionReceived, interaction);
    }

    private M3PocNotificationSurfaceSnapshot CreateSnapshot() =>
        new(
            _revision,
            _current,
            _lastEvent,
            _updatedAtUtc);

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
                // PoC surface observer 不得反向破坏 notification sink。
            }
        }
    }
}
