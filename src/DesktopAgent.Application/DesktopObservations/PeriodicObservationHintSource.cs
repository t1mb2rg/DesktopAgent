namespace DesktopAgent.Application.DesktopObservations;

public sealed record PeriodicObservationHintSettings
{
    public TimeSpan Interval { get; }

    public PeriodicObservationHintSettings(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Periodic interval 必须大于 0。" );

        Interval = interval;
    }
}

public interface IPeriodicObservationHintClock
{
    Task DelayAsync(TimeSpan interval, CancellationToken ct);
}

public sealed class TimeProviderPeriodicObservationHintClock
    : IPeriodicObservationHintClock
{
    private readonly TimeProvider _timeProvider;

    public TimeProviderPeriodicObservationHintClock(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task DelayAsync(TimeSpan interval, CancellationToken ct) =>
        Task.Delay(interval, _timeProvider, ct);
}

/// <summary>
/// M3 的低频兜底 hint source。
/// 它只负责周期性表达“即使没有可靠 Windows event，也值得重新 fresh-observe 一次”。
/// 不读取 Shell、不产生 DesktopObservation、不接触 baseline / Writer。
/// </summary>
public sealed class PeriodicObservationHintSource
{
    public const string ProviderName = "PeriodicObservationFallback";

    private readonly PeriodicObservationHintSettings _settings;
    private readonly IPeriodicObservationHintClock _clock;

    public PeriodicObservationHintSource(
        PeriodicObservationHintSettings settings,
        IPeriodicObservationHintClock? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? new TimeProviderPeriodicObservationHintClock();
    }

    public async Task RunAsync(
        Action<DesktopObservationHintKind, string> publishHint,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(publishHint);

        while (true)
        {
            await _clock.DelayAsync(_settings.Interval, ct);
            ct.ThrowIfCancellationRequested();

            try
            {
                publishHint(
                    DesktopObservationHintKind.PeriodicFallback,
                    ProviderName);
            }
            catch
            {
                // Hint sink/pump 的瞬时异常不能永久杀死 periodic fallback。
                // 下一 tick 仍继续提供 eventual re-observation 机会。
            }
        }
    }
}
