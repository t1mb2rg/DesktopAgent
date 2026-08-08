using System.Threading.Channels;

namespace DesktopAgent.Application.DesktopObservations;

public interface IDesktopObservationPumpClock
{
    DateTimeOffset GetUtcNow();
    Task DelayUntilAsync(DateTimeOffset dueAtUtc, CancellationToken ct);
}

public sealed class TimeProviderDesktopObservationPumpClock : IDesktopObservationPumpClock
{
    private readonly TimeProvider _timeProvider;

    public TimeProviderDesktopObservationPumpClock(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    public Task DelayUntilAsync(DateTimeOffset dueAtUtc, CancellationToken ct)
    {
        var delay = dueAtUtc.ToUniversalTime() - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;

        return Task.Delay(delay, _timeProvider, ct);
    }
}

/// <summary>
/// M3-02C 的只读后台 pump。
/// PublishHint 在调用线程立即进入 thread-safe batcher；pump 只处理 ready batch 和 due timer。
/// 因此事件风暴不会先堆积成大量 raw event queue。
/// </summary>
public sealed class DesktopObservationBackgroundPump
{
    private readonly DesktopObservationHintBatcher _batcher;
    private readonly DesktopObservationBatchProcessor _processor;
    private readonly IDesktopObservationPumpClock _clock;
    private readonly Channel<byte> _wakeSignals;
    private readonly Channel<DesktopObservationHintBatch> _readyBatches;

    private int _runStarted;
    private int _acceptingHints = 1;
    private DesktopObservationTriggeredRun? _lastRun;

    /// <summary>
    /// 在 pump 工作线程触发。订阅方不得假设运行在 WPF Dispatcher。
    /// 单个订阅方异常会被隔离，不会终止 pump。
    /// </summary>
    public event Action<DesktopObservationTriggeredRun>? RunCompleted;

    public DesktopObservationTriggeredRun? LastRun => Volatile.Read(ref _lastRun);

    public DesktopObservationBackgroundPump(
        DesktopObservationHintBatcher batcher,
        DesktopObservationBatchProcessor processor,
        IDesktopObservationPumpClock? clock = null)
    {
        _batcher = batcher ?? throw new ArgumentNullException(nameof(batcher));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _clock = clock ?? new TimeProviderDesktopObservationPumpClock();

        _wakeSignals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });

        _readyBatches = Channel.CreateUnbounded<DesktopObservationHintBatch>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    public bool TryPublishHint(DesktopObservationHintKind kind, string source) =>
        TryPublishHint(new DesktopObservationHint(kind, source, _clock.GetUtcNow()));

    public bool TryPublishHint(DesktopObservationHint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);
        if (Volatile.Read(ref _acceptingHints) == 0)
            return false;

        var registration = _batcher.RegisterHint(hint);
        if (registration.ReadyBatch is not null)
            _readyBatches.Writer.TryWrite(registration.ReadyBatch);

        // 只需要唤醒一次让 pump 重新检查 ReadyBatch / DueAt，重复 signal 可以安全折叠。
        _wakeSignals.Writer.TryWrite(1);
        return true;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            throw new InvalidOperationException("DesktopObservationBackgroundPump 只能启动一次。");

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                DrainWakeSignals();

                // 已经跨过 batch 边界的 ready batch 优先，不再重新参与 debounce。
                if (_readyBatches.Reader.TryRead(out var queuedReady))
                {
                    await ProcessReadyBatchAsync(queuedReady, ct);
                    continue;
                }

                var now = _clock.GetUtcNow();
                var dueAt = _batcher.NextDueAtUtc;
                if (dueAt is not null && now >= dueAt.Value)
                {
                    var ready = _batcher.TryTakeReady(now);
                    if (ready is not null)
                    {
                        await ProcessReadyBatchAsync(ready, ct);
                        continue;
                    }
                }

                if (dueAt is null)
                {
                    await _wakeSignals.Reader.ReadAsync(ct);
                    continue;
                }

                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delayTask = _clock.DelayUntilAsync(dueAt.Value, delayCts.Token);
                var wakeTask = _wakeSignals.Reader.ReadAsync(ct).AsTask();

                var completed = await Task.WhenAny(delayTask, wakeTask);
                if (ReferenceEquals(completed, wakeTask))
                {
                    // 新 hint 已经在 PublishHint 调用线程完成 batcher 更新。
                    // 取消旧 DueAt wait，下一轮按新的 batch 状态重新计算。
                    delayCts.Cancel();
                    await IgnoreExpectedDelayCancellationAsync(delayTask, delayCts.Token);
                    await wakeTask;
                    continue;
                }

                await delayTask;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _acceptingHints, 0);
            _wakeSignals.Writer.TryComplete();
            _readyBatches.Writer.TryComplete();
        }
    }

    private async Task ProcessReadyBatchAsync(
        DesktopObservationHintBatch batch,
        CancellationToken ct)
    {
        var run = await _processor.ProcessAsync(batch, ct);
        Volatile.Write(ref _lastRun, run);
        PublishRunCompleted(run);
    }

    private void PublishRunCompleted(DesktopObservationTriggeredRun run)
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
                // Observer 只是输出/通知边界，不能杀死只读监控 pump。
                // 后续正式 telemetry 可在这里增加 observer-failure 计数。
            }
        }
    }

    private void DrainWakeSignals()
    {
        while (_wakeSignals.Reader.TryRead(out _))
        {
        }
    }

    private static async Task IgnoreExpectedDelayCancellationAsync(
        Task delayTask,
        CancellationToken delayToken)
    {
        try
        {
            await delayTask;
        }
        catch (OperationCanceledException) when (delayToken.IsCancellationRequested)
        {
        }
    }
}
