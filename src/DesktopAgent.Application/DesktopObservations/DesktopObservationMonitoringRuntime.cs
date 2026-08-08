namespace DesktopAgent.Application.DesktopObservations;

public enum DesktopObservationMonitoringState
{
    Stopped = 0,
    Running = 1,
    Degraded = 2,
}

public sealed record DesktopObservationMonitoringStatus
{
    public DesktopObservationMonitoringState State { get; }
    public DateTimeOffset ChangedAtUtc { get; }
    public IReadOnlyList<DesktopObservationHintSourceStartResult> Sources { get; }
    public IReadOnlyList<string> Issues { get; }

    public bool IsRunning => State is DesktopObservationMonitoringState.Running
        or DesktopObservationMonitoringState.Degraded;

    public DesktopObservationMonitoringStatus(
        DesktopObservationMonitoringState state,
        DateTimeOffset changedAtUtc,
        IReadOnlyList<DesktopObservationHintSourceStartResult> sources,
        IReadOnlyList<string> issues)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(issues);

        State = state;
        ChangedAtUtc = changedAtUtc.ToUniversalTime();
        Sources = Array.AsReadOnly(sources.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
    }
}

/// <summary>
/// M3-A 只读后台监控生命周期。
/// 负责启动/停止 hint sources 与 observation pump，并提供 diagnostics control/event surface。
/// 不依赖 WPF、WindowsInterop 或 Writer。
/// </summary>
public sealed class DesktopObservationMonitoringRuntime
{
    public const string StartupHintSource = "M3MonitoringStartup";
    public const string ManualHintSource = "M3DiagnosticsManual";

    private readonly object _gate = new();
    private readonly DesktopObservationBackgroundPump _pump;
    private readonly DesktopStateBaselineTracker _baselineTracker;
    private readonly IReadOnlyList<IDesktopObservationHintSource> _hintSources;
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _lifetimeCts;
    private Task? _pumpTask;
    private bool _hasStarted;
    private bool _isRunning;
    private DesktopObservationMonitoringStatus _status;

    public event Action<DesktopObservationTriggeredRun>? RunCompleted;
    public event Action<DesktopObservationMonitoringStatus>? StatusChanged;

    public DesktopObservationTriggeredRun? LastRun => _pump.LastRun;

    public DesktopObservationMonitoringStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public DesktopObservationMonitoringRuntime(
        DesktopObservationBackgroundPump pump,
        DesktopStateBaselineTracker baselineTracker,
        IEnumerable<IDesktopObservationHintSource> hintSources,
        TimeProvider? timeProvider = null)
    {
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
        _baselineTracker = baselineTracker ?? throw new ArgumentNullException(nameof(baselineTracker));
        ArgumentNullException.ThrowIfNull(hintSources);
        _hintSources = Array.AsReadOnly(hintSources.ToArray());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _status = new DesktopObservationMonitoringStatus(
            DesktopObservationMonitoringState.Stopped,
            _timeProvider.GetUtcNow(),
            Array.Empty<DesktopObservationHintSourceStartResult>(),
            Array.Empty<string>());

        _pump.RunCompleted += OnPumpRunCompleted;
    }

    public DesktopObservationMonitoringStatus Start()
    {
        CancellationTokenSource lifetimeCts;

        lock (_gate)
        {
            if (_isRunning)
                return _status;
            if (_hasStarted)
                throw new InvalidOperationException(
                    "DesktopObservationMonitoringRuntime 当前为单次 Host 生命周期对象，停止后不得重新启动。");

            _hasStarted = true;
            _isRunning = true;
            lifetimeCts = new CancellationTokenSource();
            _lifetimeCts = lifetimeCts;
            _pumpTask = _pump.RunAsync(lifetimeCts.Token);
        }

        var sourceResults = new List<DesktopObservationHintSourceStartResult>();
        var runtimeIssues = new List<string>();

        foreach (var source in _hintSources)
        {
            try
            {
                sourceResults.Add(source.Start());
            }
            catch (Exception ex)
            {
                runtimeIssues.Add(
                    $"Hint source “{source.Source}”启动失败：{ex.GetType().Name}: {ex.Message}");
                sourceResults.Add(new DesktopObservationHintSourceStartResult(
                    source.Source,
                    false,
                    0,
                    Array.Empty<string>(),
                    new[] { $"{ex.GetType().Name}: {ex.Message}" }));
            }
        }

        var activeSourceCount = sourceResults.Count(result => result.IsRunning);
        if (activeSourceCount == 0)
            runtimeIssues.Add("没有真实 hint source 成功运行；后台仍可通过 Manual hint 进行 fresh observation。");

        if (!_pump.TryPublishHint(DesktopObservationHintKind.Manual, StartupHintSource))
            runtimeIssues.Add("启动 baseline hint 未能提交给 observation pump。");

        var hasDegradation = runtimeIssues.Count != 0
            || sourceResults.Any(result => !result.IsRunning || result.Issues.Count != 0);
        var state = activeSourceCount > 0 && !hasDegradation
            ? DesktopObservationMonitoringState.Running
            : DesktopObservationMonitoringState.Degraded;
        var status = new DesktopObservationMonitoringStatus(
            state,
            _timeProvider.GetUtcNow(),
            sourceResults,
            runtimeIssues);
        SetStatus(status);
        return status;
    }

    public bool RequestManualObservation() =>
        IsRunning() && _pump.TryPublishHint(DesktopObservationHintKind.Manual, ManualHintSource);

    public void ResetBaseline() => _baselineTracker.Reset();

    public async Task StopAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? lifetimeCts;
        Task? pumpTask;

        lock (_gate)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            lifetimeCts = _lifetimeCts;
            pumpTask = _pumpTask;
        }

        var stopIssues = new List<string>();
        foreach (var source in _hintSources)
        {
            try
            {
                source.Stop();
            }
            catch (Exception ex)
            {
                stopIssues.Add(
                    $"Hint source “{source.Source}”停止失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        lifetimeCts?.Cancel();

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (pumpTask.IsCanceled)
            {
            }
            catch (Exception ex)
            {
                stopIssues.Add($"Observation pump 停止时异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        lifetimeCts?.Dispose();

        lock (_gate)
        {
            _lifetimeCts = null;
            _pumpTask = null;
        }

        SetStatus(new DesktopObservationMonitoringStatus(
            DesktopObservationMonitoringState.Stopped,
            _timeProvider.GetUtcNow(),
            Array.Empty<DesktopObservationHintSourceStartResult>(),
            stopIssues));
    }

    private bool IsRunning()
    {
        lock (_gate)
            return _isRunning;
    }

    private void OnPumpRunCompleted(DesktopObservationTriggeredRun run)
    {
        PublishSafely(RunCompleted, run);
    }

    private void SetStatus(DesktopObservationMonitoringStatus status)
    {
        lock (_gate)
            _status = status;

        PublishSafely(StatusChanged, status);
    }

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
                // Diagnostics/UI observer 不能终止后台只读监控。
            }
        }
    }
}
