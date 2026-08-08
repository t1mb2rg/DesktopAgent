using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.WindowsInterop.DesktopObservations;

namespace DesktopAgent.Poc;

public enum M3ReadOnlyMonitoringStartStatus
{
    Started = 0,
    AlreadyRunning = 1,
    HintSourceUnavailable = 2,
    BaselineUnstable = 3,
    BaselineUnavailable = 4,
}

public sealed record M3ReadOnlyMonitoringStartResult(
    M3ReadOnlyMonitoringStartStatus Status,
    DesktopFileSystemHintSourceStartResult? FileSystemSource,
    DesktopObservationDiagnosticsResult? BaselinePrime)
{
    public bool IsStarted => Status is
        M3ReadOnlyMonitoringStartStatus.Started
        or M3ReadOnlyMonitoringStartStatus.AlreadyRunning;
}

/// <summary>
/// PoC 组合层的最小 hint-source seam。
/// 只暴露 Start/Stop/Dispose；事件 payload 仍由具体 source 在边界内丢弃。
/// </summary>
public interface IM3ReadOnlyMonitoringHintSource : IDisposable
{
    DesktopFileSystemHintSourceStartResult Start();
    void Stop();
}

public interface IM3ReadOnlyMonitoringHintSourceFactory
{
    IM3ReadOnlyMonitoringHintSource Create(
        Action<DesktopObservationHintKind, string> publishHint);
}

public sealed class DesktopFileSystemMonitoringHintSourceFactory
    : IM3ReadOnlyMonitoringHintSourceFactory
{
    public IM3ReadOnlyMonitoringHintSource Create(
        Action<DesktopObservationHintKind, string> publishHint) =>
        new Adapter(publishHint);

    private sealed class Adapter : IM3ReadOnlyMonitoringHintSource
    {
        private readonly DesktopFileSystemObservationHintSource _inner;

        public Adapter(Action<DesktopObservationHintKind, string> publishHint)
        {
            _inner = new DesktopFileSystemObservationHintSource(publishHint);
        }

        public DesktopFileSystemHintSourceStartResult Start() => _inner.Start();

        public void Stop() => _inner.Stop();

        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>
/// M3 PoC 的显式只读监控会话。
/// 每次 Start 都创建新的 batcher / processor / pump / file-system hint source；
/// periodic hint source 本身无会话状态，可在每次 Start 中启动一条新的可取消 loop。
/// </summary>
public sealed class M3ReadOnlyMonitoringSession : IDisposable
{
    private readonly DesktopObservationDiagnosticsRunner _runner;
    private readonly DesktopObservationHintBatchSettings _hintBatchSettings;
    private readonly IM3ReadOnlyMonitoringHintSourceFactory _hintSourceFactory;
    private readonly IPeriodicObservationHintSource _periodicHintSource;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private DesktopObservationBackgroundPump? _pump;
    private IM3ReadOnlyMonitoringHintSource? _fileSystemSource;
    private CancellationTokenSource? _sessionCts;
    private Task? _sessionLifetimeTask;
    private int _isRunning;
    private int _disposeStarted;
    private string? _lastPumpFault;

    public event Action<DesktopObservationTriggeredRun>? RunCompleted;
    public event Action<string>? PumpFaulted;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    /// <summary>
    /// 兼容 02E 既有 diagnostics 命名；02G 后该值表示 pump 或 periodic component 的最近一次监控 fault。
    /// </summary>
    public string? LastPumpFault => Volatile.Read(ref _lastPumpFault);

    public M3ReadOnlyMonitoringSession(
        DesktopObservationDiagnosticsRunner runner,
        DesktopObservationHintBatchSettings hintBatchSettings,
        IM3ReadOnlyMonitoringHintSourceFactory hintSourceFactory,
        IPeriodicObservationHintSource periodicHintSource)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _hintBatchSettings = hintBatchSettings
            ?? throw new ArgumentNullException(nameof(hintBatchSettings));
        _hintSourceFactory = hintSourceFactory
            ?? throw new ArgumentNullException(nameof(hintSourceFactory));
        _periodicHintSource = periodicHintSource
            ?? throw new ArgumentNullException(nameof(periodicHintSource));
    }

    public async Task<M3ReadOnlyMonitoringStartResult> StartAsync(
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);

        await _lifecycleGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0,
                this);

            if (IsRunning)
            {
                return new M3ReadOnlyMonitoringStartResult(
                    M3ReadOnlyMonitoringStartStatus.AlreadyRunning,
                    null,
                    null);
            }

            await CleanupPreviousAttemptAsync();
            Volatile.Write(ref _lastPumpFault, null);

            var batcher = new DesktopObservationHintBatcher(_hintBatchSettings);
            var processor = new DesktopObservationBatchProcessor(_runner);
            var pump = new DesktopObservationBackgroundPump(batcher, processor);
            var publishHint = new Action<DesktopObservationHintKind, string>(
                (kind, provider) => pump.TryPublishHint(kind, provider));
            var source = _hintSourceFactory.Create(publishHint);

            DesktopFileSystemHintSourceStartResult sourceResult;
            try
            {
                sourceResult = source.Start();
            }
            catch
            {
                source.Dispose();
                throw;
            }

            if (!sourceResult.IsRunning)
            {
                source.Dispose();
                return new M3ReadOnlyMonitoringStartResult(
                    M3ReadOnlyMonitoringStartStatus.HintSourceUnavailable,
                    sourceResult,
                    null);
            }

            DesktopObservationDiagnosticsResult prime;
            try
            {
                // File-system source 先启动，但 pump / periodic 暂不运行。
                // prime 期间到达的 file-system hint 会先进入 batcher，待 baseline 成功后再处理。
                prime = await _runner.RunOnceAsync(ct);
            }
            catch
            {
                source.Stop();
                source.Dispose();
                batcher.Reset();
                throw;
            }

            if (prime.Status == DesktopObservationDiagnosticsStatus.Unstable)
            {
                source.Stop();
                source.Dispose();
                batcher.Reset();
                return new M3ReadOnlyMonitoringStartResult(
                    M3ReadOnlyMonitoringStartStatus.BaselineUnstable,
                    sourceResult,
                    prime);
            }

            if (prime.Status == DesktopObservationDiagnosticsStatus.Unavailable)
            {
                source.Stop();
                source.Dispose();
                batcher.Reset();
                return new M3ReadOnlyMonitoringStartResult(
                    M3ReadOnlyMonitoringStartStatus.BaselineUnavailable,
                    sourceResult,
                    prime);
            }

            var cts = new CancellationTokenSource();
            pump.RunCompleted += ForwardRunCompleted;

            _pump = pump;
            _fileSystemSource = source;
            _sessionCts = cts;
            Interlocked.Exchange(ref _isRunning, 1);

            // Periodic source 在 baseline prime 后才启动，所以不会产生无意义的“启动即 tick”。
            var rawPumpTask = pump.RunAsync(cts.Token);
            var rawPeriodicTask = _periodicHintSource.RunAsync(publishHint, cts.Token);
            _sessionLifetimeTask = ObserveMonitoringLifetimeAsync(
                rawPumpTask,
                rawPeriodicTask,
                source,
                cts);

            return new M3ReadOnlyMonitoringStartResult(
                M3ReadOnlyMonitoringStartStatus.Started,
                sourceResult,
                prime);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            return;

        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _lifecycleGate.Wait();
        try
        {
            StopCoreAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private async Task ObserveMonitoringLifetimeAsync(
        Task rawPumpTask,
        Task rawPeriodicTask,
        IM3ReadOnlyMonitoringHintSource source,
        CancellationTokenSource cts)
    {
        string? fault = null;

        try
        {
            var firstCompleted = await Task.WhenAny(rawPumpTask, rawPeriodicTask);

            if (!cts.IsCancellationRequested)
            {
                var componentName = ReferenceEquals(firstCompleted, rawPumpTask)
                    ? "BackgroundPump"
                    : "PeriodicFallback";
                fault = await DescribeUnexpectedComponentExitAsync(
                    firstCompleted,
                    componentName,
                    cts.Token);

                // 任一长期组件非预期结束，整套 monitoring session 一起收口，
                // 不允许悄悄失去 periodic convergence 或 single-flight pump。
                cts.Cancel();
                TryStopSource(source);
            }

            fault ??= await ObserveComponentShutdownAsync(
                rawPumpTask,
                "BackgroundPump",
                cts.Token);
            fault ??= await ObserveComponentShutdownAsync(
                rawPeriodicTask,
                "PeriodicFallback",
                cts.Token);

            if (fault is not null)
            {
                Volatile.Write(ref _lastPumpFault, fault);
                PublishPumpFaulted(fault);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private async Task StopCoreAsync()
    {
        var source = _fileSystemSource;
        var pump = _pump;
        var cts = _sessionCts;
        var lifetime = _sessionLifetimeTask;

        if (source is not null)
            TryStopSource(source);

        cts?.Cancel();

        if (lifetime is not null)
            await lifetime;

        if (pump is not null)
            pump.RunCompleted -= ForwardRunCompleted;

        source?.Dispose();
        cts?.Dispose();

        _pump = null;
        _fileSystemSource = null;
        _sessionCts = null;
        _sessionLifetimeTask = null;
        Interlocked.Exchange(ref _isRunning, 0);
    }

    private async Task CleanupPreviousAttemptAsync()
    {
        if (_pump is null
            && _fileSystemSource is null
            && _sessionCts is null
            && _sessionLifetimeTask is null)
        {
            return;
        }

        await StopCoreAsync();
    }

    private void ForwardRunCompleted(DesktopObservationTriggeredRun run)
    {
        var handlers = RunCompleted;
        if (handlers is null)
            return;

        foreach (Action<DesktopObservationTriggeredRun> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(run);
            }
            catch
            {
                // UI/telemetry observer 不得反向杀死监控会话。
            }
        }
    }

    private void PublishPumpFaulted(string fault)
    {
        var handlers = PumpFaulted;
        if (handlers is null)
            return;

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(fault);
            }
            catch
            {
            }
        }
    }

    private static void TryStopSource(IM3ReadOnlyMonitoringHintSource source)
    {
        try
        {
            source.Stop();
        }
        catch
        {
        }
    }

    private static async Task<string> DescribeUnexpectedComponentExitAsync(
        Task task,
        string componentName,
        CancellationToken sessionToken)
    {
        try
        {
            await task;
            return $"{componentName} unexpectedly completed while monitoring session was still active.";
        }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            return $"{componentName} was unexpectedly cancelled while monitoring session was still active.";
        }
        catch (Exception ex)
        {
            return $"{componentName} faulted unexpectedly.\n{ex}";
        }
    }

    private static async Task<string?> ObserveComponentShutdownAsync(
        Task task,
        string componentName,
        CancellationToken sessionToken)
    {
        try
        {
            await task;
            return null;
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return $"{componentName} faulted while monitoring session was stopping.\n{ex}";
        }
    }
}
