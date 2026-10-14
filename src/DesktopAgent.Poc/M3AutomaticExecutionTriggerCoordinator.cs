using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.WindowsInterop.DesktopAutomation;

namespace DesktopAgent.Poc;

public sealed record M3AutomaticExecutionTriggerKey(
    Guid SuggestionId,
    long LifecycleRevision,
    long RuntimeRevision,
    DesktopAutomationMode EffectiveMode,
    bool IsPaused,
    bool IsCooldownActive,
    bool IsFuseTripped);

/// <summary>
/// PoC 自动执行触发泵。
/// SuggestionProcessed 只是“值得尝试一次 current B05 cycle”的提示，绝不直接触发 Writer。
///
/// 重复的同一 lifecycle/runtime 状态会折叠；如果 cooldown / timed pause 自然到期，
/// IsCooldownActive / IsPaused 状态变化会形成新 TriggerKey，可在下一次 SuggestionProcessed 时再评估一次。
/// </summary>
public sealed class M3AutomaticExecutionTriggerCoordinator : IDisposable
{
    private readonly M3SuggestionMonitoringCoordinator? _suggestionCoordinator;
    private readonly Func<DesktopLayoutSuggestionLifecycleSnapshot> _getLifecycle;
    private readonly Func<DesktopAutomationRuntimeSnapshot> _getRuntime;
    private readonly Func<CancellationToken, Task<DesktopAutomationExecutionCycleResult>> _runCycle;

    private readonly object _gate = new();
    private M3AutomaticExecutionTriggerKey? _pendingKey;
    private M3AutomaticExecutionTriggerKey? _lastAttemptedKey;
    private Task _pumpTask = Task.CompletedTask;
    private bool _pumpRunning;
    private bool _started;
    private bool _accepting;
    private int _disposeStarted;

    public event Action<DesktopAutomationExecutionCycleResult>? CycleCompleted;
    public event Action<string>? TriggerFaulted;

    public bool IsStarted => _started;
    public bool IsCyclePumpRunning
    {
        get
        {
            lock (_gate)
                return _pumpRunning;
        }
    }

    public M3AutomaticExecutionTriggerCoordinator(
        M3SuggestionMonitoringCoordinator suggestionCoordinator,
        DesktopAutomationRuntimePolicy runtimePolicy,
        DesktopAutomationExecutionCycleCoordinator cycleCoordinator)
        : this(
            suggestionCoordinator,
            () => suggestionCoordinator.SuggestionSnapshot,
            () => runtimePolicy.Snapshot,
            ct => cycleCoordinator.RunCurrentAsync(ct))
    {
        ArgumentNullException.ThrowIfNull(suggestionCoordinator);
        ArgumentNullException.ThrowIfNull(runtimePolicy);
        ArgumentNullException.ThrowIfNull(cycleCoordinator);
    }

    internal M3AutomaticExecutionTriggerCoordinator(
        Func<DesktopLayoutSuggestionLifecycleSnapshot> getLifecycle,
        Func<DesktopAutomationRuntimeSnapshot> getRuntime,
        Func<CancellationToken, Task<DesktopAutomationExecutionCycleResult>> runCycle)
        : this(null, getLifecycle, getRuntime, runCycle)
    {
    }

    private M3AutomaticExecutionTriggerCoordinator(
        M3SuggestionMonitoringCoordinator? suggestionCoordinator,
        Func<DesktopLayoutSuggestionLifecycleSnapshot> getLifecycle,
        Func<DesktopAutomationRuntimeSnapshot> getRuntime,
        Func<CancellationToken, Task<DesktopAutomationExecutionCycleResult>> runCycle)
    {
        _suggestionCoordinator = suggestionCoordinator;
        _getLifecycle = getLifecycle ?? throw new ArgumentNullException(nameof(getLifecycle));
        _getRuntime = getRuntime ?? throw new ArgumentNullException(nameof(getRuntime));
        _runCycle = runCycle ?? throw new ArgumentNullException(nameof(runCycle));
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
            return;

        if (_suggestionCoordinator is not null)
            _suggestionCoordinator.SuggestionProcessed += OnSuggestionProcessed;

        lock (_gate)
        {
            _accepting = true;
            _started = true;
        }

        // 如果 trigger 比 Suggestion 晚启动，不漏掉当前已经 Active 的建议。
        RequestCurrent();
    }

    public void RequestCurrent()
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            if (!_started || !_accepting)
                return;

            var key = BuildCurrentKey();
            if (key is null)
                return;

            if (Equals(_pendingKey, key) || Equals(_lastAttemptedKey, key))
                return;

            _pendingKey = key;
            if (!_pumpRunning)
            {
                _pumpRunning = true;
                // 在同一把 lock 里同时建立 running 标记和可等待 Task。
                // Task.Run 返回后 StopAsync 不会再看到“running=true 但 tail 还是 Completed”的半状态。
                _pumpTask = Task.Run(PumpAsync);
            }
        }
    }

    /// <summary>
    /// 无人值守点火：RequestedMode 为 Automatic 时请求一次当前建议。
    /// 这是“从托盘把模式切到 Automatic”的等价输入，用于本次进程一开始就处于 Automatic、
    /// 因此没有人去点托盘菜单的场景；重复调用由 TriggerKey 折叠，不会重复执行同一份建议。
    /// 安全边界不在这里：RequestedMode=Automatic 只是请求，真正的门是
    /// ValidatedOpen gate + 逐屏实测证据 + 站定授权，全部由 preflight/安全门审判。
    /// </summary>
    public void RequestCurrentIfAutomatic()
    {
        ThrowIfDisposed();

        if (_getRuntime().RequestedMode != DesktopAutomationMode.Automatic)
            return;

        RequestCurrent();
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _disposeStarted) != 0 && !_started)
            return;

        if (_suggestionCoordinator is not null)
            _suggestionCoordinator.SuggestionProcessed -= OnSuggestionProcessed;

        Task tail;
        lock (_gate)
        {
            _accepting = false;
            _started = false;
            _pendingKey = null;
            tail = _pumpTask;
        }

        try
        {
            await tail.ConfigureAwait(false);
        }
        catch
        {
            // Pump 自己会通过 TriggerFaulted 报告；Stop 只负责 drain。
        }
    }

    private void OnSuggestionProcessed(M3SuggestionProcessingReport report)
    {
        _ = report;
        RequestCurrent();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                M3AutomaticExecutionTriggerKey? queuedKey;
                lock (_gate)
                {
                    queuedKey = _pendingKey;
                    _pendingKey = null;
                    if (queuedKey is null)
                        return;
                }

                // Queue 后到真正执行前，lifecycle/runtime 可能已经变化。
                // 以当前 key 为准；B05 内部仍会再次读取全部 current state。
                var currentKey = BuildCurrentKey();
                if (currentKey is null)
                    continue;

                lock (_gate)
                {
                    if (Equals(_lastAttemptedKey, currentKey))
                        continue;
                    _lastAttemptedKey = currentKey;
                }

                try
                {
                    var result = await _runCycle(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        CycleCompleted?.Invoke(result);
                    }
                    catch (Exception subscriberEx)
                    {
                        TriggerFaulted?.Invoke(
                            $"Automatic cycle completion subscriber 发生异常：{subscriberEx}");
                    }
                }
                catch (Exception ex)
                {
                    // B05 正常会把非 cancellation 逃逸转为 FaultedAndPaused。
                    // 这里仍兜底，避免 event pump 因意外实现异常永久死亡。
                    TriggerFaulted?.Invoke($"Automatic execution cycle 逃逸出 trigger pump：{ex}");
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _pumpRunning = false;

                // cycle 运行期间若出现了更新的 TriggerKey，只保留最新一份 pending。
                // 直接在同一 lock 下挂下一条 task，避免 StopAsync 穿过半状态。
                if (_accepting && _pendingKey is not null)
                {
                    _pumpRunning = true;
                    _pumpTask = Task.Run(PumpAsync);
                }
            }
        }
    }

    private M3AutomaticExecutionTriggerKey? BuildCurrentKey()
    {
        var lifecycle = _getLifecycle();
        if (lifecycle.State != DesktopLayoutSuggestionPresentationState.Active
            || lifecycle.CurrentSuggestion is null)
        {
            return null;
        }

        var runtime = _getRuntime();
        return new M3AutomaticExecutionTriggerKey(
            lifecycle.CurrentSuggestion.SuggestionId,
            lifecycle.Revision,
            runtime.Revision,
            runtime.EffectiveMode,
            runtime.IsPaused,
            runtime.IsCooldownActive,
            runtime.IsFuseTripped);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        StopAsync().GetAwaiter().GetResult();
    }
}
