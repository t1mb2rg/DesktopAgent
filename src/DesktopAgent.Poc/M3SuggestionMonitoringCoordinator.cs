using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.Poc;

public enum M3SuggestionProcessingOrigin
{
    Manual = 0,
    MonitoringPrime = 1,
    Background = 2,
    ObservationRunFailure = 3,
}

public sealed record M3SuggestionProcessingReport
{
    public M3SuggestionProcessingOrigin Origin { get; }
    public Guid? TriggerRunId { get; }
    public DesktopObservationDiagnosticsResult? Diagnostics { get; }
    public DesktopLayoutSuggestionPipelineResult? PipelineResult { get; }
    public DesktopLayoutSuggestionLifecycleSnapshot Lifecycle { get; }
    public string? Fault { get; }

    public M3SuggestionProcessingReport(
        M3SuggestionProcessingOrigin origin,
        Guid? triggerRunId,
        DesktopObservationDiagnosticsResult? diagnostics,
        DesktopLayoutSuggestionPipelineResult? pipelineResult,
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle,
        string? fault = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        if (origin == M3SuggestionProcessingOrigin.ObservationRunFailure)
        {
            if (diagnostics is not null || pipelineResult is not null || string.IsNullOrWhiteSpace(fault))
            {
                throw new ArgumentException(
                    "ObservationRunFailure 必须只有 Lifecycle + Fault。",
                    nameof(origin));
            }
        }
        else
        {
            if (diagnostics is null || pipelineResult is null || fault is not null)
            {
                throw new ArgumentException(
                    "正常 Suggestion processing report 必须包含 Diagnostics + PipelineResult 且不得携带 Fault。",
                    nameof(origin));
            }
        }

        Origin = origin;
        TriggerRunId = triggerRunId;
        Diagnostics = diagnostics;
        PipelineResult = pipelineResult;
        Lifecycle = lifecycle;
        Fault = fault;
    }
}

public sealed record M3SuggestionMonitoringStartResult(
    M3ReadOnlyMonitoringStartResult Monitoring,
    M3SuggestionProcessingReport? PrimeSuggestion);

/// <summary>
/// PoC 组合层：把只读 monitoring session 的 diagnostics 按顺序喂给 Application Suggestion pipeline。
/// 不执行 Candidate，不持有 Writer。
/// </summary>
public sealed class M3SuggestionMonitoringCoordinator : IDisposable
{
    private readonly DesktopObservationDiagnosticsRunner _runner;
    private readonly M3ReadOnlyMonitoringSession _monitoringSession;
    private readonly DesktopLayoutSuggestionPipeline _suggestionPipeline;
    private readonly IDesktopLayoutTemplateStore _templateStore;
    private readonly IDesktopLayoutIconRuleStore _ruleStore;
    private readonly IDisplayTopologyReader _displayTopologyReader;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _storeInitializationGate = new(1, 1);
    private readonly object _backgroundGate = new();
    private readonly Queue<BackgroundSuggestionWork> _startupBufferedWork = new();

    private Task _backgroundTail = Task.CompletedTask;
    private bool _startupBuffering;
    private bool _storesInitialized;
    private int _disposeStarted;

    public event Action<DesktopObservationTriggeredRun>? ObservationRunCompleted;
    public event Action<string>? MonitoringFaulted;
    public event Action<M3SuggestionProcessingReport>? SuggestionProcessed;

    public bool IsMonitoring => _monitoringSession.IsRunning;
    public DesktopLayoutSuggestionBindingSnapshot BindingSnapshot =>
        _suggestionPipeline.BindingSnapshot;
    public DesktopLayoutSuggestionLifecycleSnapshot SuggestionSnapshot =>
        _suggestionPipeline.LifecycleSnapshot;

    public M3SuggestionMonitoringCoordinator(
        DesktopObservationDiagnosticsRunner runner,
        M3ReadOnlyMonitoringSession monitoringSession,
        DesktopLayoutSuggestionPipeline suggestionPipeline,
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        IDisplayTopologyReader displayTopologyReader)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _monitoringSession = monitoringSession ?? throw new ArgumentNullException(nameof(monitoringSession));
        _suggestionPipeline = suggestionPipeline ?? throw new ArgumentNullException(nameof(suggestionPipeline));
        _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _displayTopologyReader = displayTopologyReader ?? throw new ArgumentNullException(nameof(displayTopologyReader));

        _monitoringSession.RunCompleted += OnBackgroundRunCompleted;
        _monitoringSession.PumpFaulted += OnMonitoringFaulted;
    }

    public async Task<IReadOnlyList<DesktopLayoutTemplateSummary>> ListTemplatesAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureStoresInitializedAsync(ct);
        return await _templateStore.ListAsync(limit, ct);
    }

    public IReadOnlyList<DisplayDescriptor> ReadDisplays()
    {
        ThrowIfDisposed();
        return _displayTopologyReader.GetDisplays();
    }

    public DesktopLayoutSuggestionBindingSnapshot SetBinding(
        DesktopLayoutSuggestionBinding? binding)
    {
        ThrowIfDisposed();
        return _suggestionPipeline.SetBinding(binding);
    }

    public DesktopLayoutSuggestionLifecycleSnapshot DismissCurrentSuggestion()
    {
        ThrowIfDisposed();
        return _suggestionPipeline.DismissCurrent();
    }

    public async Task<M3SuggestionProcessingReport> RunManualAsync(
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_monitoringSession.IsRunning)
        {
            throw new InvalidOperationException(
                "后台 monitoring 运行时不得绕过其 single-flight 执行独立 manual observation。" );
        }

        await EnsureStoresInitializedAsync(ct);
        var diagnostics = await _runner.RunOnceAsync(ct);
        return await ProcessDiagnosticsAsync(
            diagnostics,
            M3SuggestionProcessingOrigin.Manual,
            null,
            ct);
    }

    public DesktopLayoutSuggestionLifecycleSnapshot ResetBaseline()
    {
        ThrowIfDisposed();
        if (_monitoringSession.IsRunning)
        {
            throw new InvalidOperationException(
                "后台 monitoring 运行时不得直接 Reset baseline。请先停止监控。" );
        }

        _runner.ResetBaseline();
        return _suggestionPipeline.MarkBaselineReset();
    }

    public async Task<M3SuggestionMonitoringStartResult> StartMonitoringAsync(
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            await EnsureStoresInitializedAsync(ct);

            lock (_backgroundGate)
            {
                _startupBuffering = true;
                _startupBufferedWork.Clear();
            }

            var releaseBufferedWork = false;
            try
            {
                var monitoringResult = await _monitoringSession.StartAsync(ct);
                M3SuggestionProcessingReport? primeReport = null;

                if (monitoringResult.BaselinePrime is not null)
                {
                    primeReport = await ProcessDiagnosticsAsync(
                        monitoringResult.BaselinePrime,
                        M3SuggestionProcessingOrigin.MonitoringPrime,
                        null,
                        ct);
                }

                releaseBufferedWork = true;
                return new M3SuggestionMonitoringStartResult(
                    monitoringResult,
                    primeReport);
            }
            catch
            {
                // session.StartAsync 可能已经成功启动 pump/periodic，而 prime Suggestion processing
                // 随后被取消或失败。此时必须 fail-close 整个 monitoring session，不能留下后台幽灵任务。
                if (_monitoringSession.IsRunning)
                {
                    try
                    {
                        await _monitoringSession.StopAsync();
                    }
                    catch
                    {
                        // 保留原始 start/prime 异常作为主异常。
                    }
                }

                throw;
            }
            finally
            {
                if (releaseBufferedWork)
                    ReleaseStartupBuffer();
                else
                    DiscardStartupBuffer();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopMonitoringAsync()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
            return;

        await _lifecycleGate.WaitAsync();
        try
        {
            await _monitoringSession.StopAsync();
            await SnapshotBackgroundTailAsync();
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

        _monitoringSession.RunCompleted -= OnBackgroundRunCompleted;
        _monitoringSession.PumpFaulted -= OnMonitoringFaulted;

        try
        {
            _monitoringSession.StopAsync().GetAwaiter().GetResult();
            SnapshotBackgroundTailAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _lifecycleGate.Dispose();
        _storeInitializationGate.Dispose();
    }

    private async Task<M3SuggestionProcessingReport> ProcessDiagnosticsAsync(
        DesktopObservationDiagnosticsResult diagnostics,
        M3SuggestionProcessingOrigin origin,
        Guid? triggerRunId,
        CancellationToken ct)
    {
        await EnsureStoresInitializedAsync(ct);
        var pipelineResult = await _suggestionPipeline.ProcessAsync(diagnostics, ct);
        var report = new M3SuggestionProcessingReport(
            origin,
            triggerRunId,
            diagnostics,
            pipelineResult,
            pipelineResult.Lifecycle);
        PublishSafely(SuggestionProcessed, report);
        return report;
    }

    private void OnBackgroundRunCompleted(DesktopObservationTriggeredRun run)
    {
        PublishSafely(ObservationRunCompleted, run);
        var work = BackgroundSuggestionWork.ForRun(run);

        lock (_backgroundGate)
        {
            if (_startupBuffering)
            {
                _startupBufferedWork.Enqueue(work);
                return;
            }

            QueueBackgroundWorkLocked(work);
        }
    }

    private void OnMonitoringFaulted(string fault)
    {
        PublishSafely(MonitoringFaulted, fault);
        var work = BackgroundSuggestionWork.ForFault(fault);

        lock (_backgroundGate)
        {
            if (_startupBuffering)
            {
                _startupBufferedWork.Enqueue(work);
                return;
            }

            QueueBackgroundWorkLocked(work);
        }
    }

    private void ReleaseStartupBuffer()
    {
        lock (_backgroundGate)
        {
            _startupBuffering = false;
            while (_startupBufferedWork.Count > 0)
                QueueBackgroundWorkLocked(_startupBufferedWork.Dequeue());
        }
    }

    private void DiscardStartupBuffer()
    {
        lock (_backgroundGate)
        {
            _startupBuffering = false;
            _startupBufferedWork.Clear();
        }
    }

    private void QueueBackgroundWorkLocked(BackgroundSuggestionWork work)
    {
        _backgroundTail = ProcessAfterAsync(_backgroundTail, work);
    }

    private async Task ProcessAfterAsync(
        Task previous,
        BackgroundSuggestionWork work)
    {
        try
        {
            await previous;
        }
        catch
        {
            // 前一项不应阻塞后续。每一项自身会把异常收敛为 fault report。
        }

        try
        {
            if (work.MonitoringFault is not null)
            {
                PublishObservationFailure(null, work.MonitoringFault);
                return;
            }

            var run = work.Run!;
            if (run.Status == DesktopObservationTriggeredRunStatus.Failed)
            {
                PublishObservationFailure(
                    run.RunId,
                    run.EscapedException ?? "Unknown observation run failure.");
                return;
            }

            await ProcessDiagnosticsAsync(
                run.DiagnosticsResult!,
                M3SuggestionProcessingOrigin.Background,
                run.RunId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            PublishObservationFailure(work.Run?.RunId, ex.ToString());
        }
    }

    private void PublishObservationFailure(Guid? runId, string fault)
    {
        var lifecycle = _suggestionPipeline.MarkObservationRunFailed();
        PublishSafely(
            SuggestionProcessed,
            new M3SuggestionProcessingReport(
                M3SuggestionProcessingOrigin.ObservationRunFailure,
                runId,
                null,
                null,
                lifecycle,
                fault));
    }

    private async Task EnsureStoresInitializedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _storesInitialized))
            return;

        await _storeInitializationGate.WaitAsync(ct);
        try
        {
            if (_storesInitialized)
                return;

            await _templateStore.InitializeAsync(ct);
            await _ruleStore.InitializeAsync(ct);
            Volatile.Write(ref _storesInitialized, true);
        }
        finally
        {
            _storeInitializationGate.Release();
        }
    }

    private Task SnapshotBackgroundTailAsync()
    {
        lock (_backgroundGate)
            return _backgroundTail;
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
                // UI/telemetry observer 不得反向破坏只读监控或 Suggestion pipeline。
            }
        }
    }

    private sealed record BackgroundSuggestionWork(
        DesktopObservationTriggeredRun? Run,
        string? MonitoringFault)
    {
        public static BackgroundSuggestionWork ForRun(DesktopObservationTriggeredRun run)
        {
            ArgumentNullException.ThrowIfNull(run);
            return new BackgroundSuggestionWork(run, null);
        }

        public static BackgroundSuggestionWork ForFault(string fault)
        {
            if (string.IsNullOrWhiteSpace(fault))
                throw new ArgumentException("fault 不能为空。", nameof(fault));
            return new BackgroundSuggestionWork(null, fault);
        }
    }
}
