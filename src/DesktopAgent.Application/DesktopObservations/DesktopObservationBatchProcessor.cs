namespace DesktopAgent.Application.DesktopObservations;

public enum DesktopObservationTriggeredRunStatus
{
    Completed = 0,
    Failed = 1,
}

/// <summary>
/// 一批 event hints 触发的一次只读诊断运行。
/// Completed 表示 diagnostics pipeline 正常返回；其中仍可能显式得到 Unstable / Unavailable。
/// Failed 只表示发生了 pipeline 之外的逃逸异常。
/// </summary>
public sealed record DesktopObservationTriggeredRun
{
    public Guid RunId { get; }
    public DesktopObservationTriggeredRunStatus Status { get; }
    public DesktopObservationHintBatch TriggerBatch { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public DesktopObservationDiagnosticsResult? DiagnosticsResult { get; }
    public string? EscapedException { get; }

    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;

    public DesktopObservationTriggeredRun(
        Guid runId,
        DesktopObservationTriggeredRunStatus status,
        DesktopObservationHintBatch triggerBatch,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        DesktopObservationDiagnosticsResult? diagnosticsResult,
        string? escapedException)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("RunId 不能为空。", nameof(runId));
        ArgumentNullException.ThrowIfNull(triggerBatch);
        if (completedAtUtc < startedAtUtc)
            throw new ArgumentException("CompletedAtUtc 不得早于 StartedAtUtc。", nameof(completedAtUtc));

        switch (status)
        {
            case DesktopObservationTriggeredRunStatus.Completed
                when diagnosticsResult is null || escapedException is not null:
                throw new ArgumentException(
                    "Completed 必须提供 DiagnosticsResult 且不得携带 EscapedException。",
                    nameof(status));
            case DesktopObservationTriggeredRunStatus.Failed
                when diagnosticsResult is not null || string.IsNullOrWhiteSpace(escapedException):
                throw new ArgumentException(
                    "Failed 必须提供 EscapedException 且不得携带 DiagnosticsResult。",
                    nameof(status));
        }

        RunId = runId;
        Status = status;
        TriggerBatch = triggerBatch;
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
        DiagnosticsResult = diagnosticsResult;
        EscapedException = escapedException;
    }
}

/// <summary>
/// M3-02B 的 single-flight observation processor。
/// 无论上层同时提交多少 ready batches，同一实例一次最多运行一条完整 DiagnosticsRunner。
/// 它不负责计时/合批，不注册 Windows 事件，也不调用 Writer。
/// </summary>
public sealed class DesktopObservationBatchProcessor
{
    private readonly DesktopObservationDiagnosticsRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    public DesktopObservationBatchProcessor(
        DesktopObservationDiagnosticsRunner runner,
        TimeProvider? timeProvider = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DesktopObservationTriggeredRun> ProcessAsync(
        DesktopObservationHintBatch batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await _singleFlight.WaitAsync(ct);
        try
        {
            var runId = Guid.NewGuid();
            var startedAtUtc = _timeProvider.GetUtcNow();

            try
            {
                var result = await _runner.RunOnceAsync(ct);
                var completedAtUtc = Max(_timeProvider.GetUtcNow(), startedAtUtc);

                return new DesktopObservationTriggeredRun(
                    runId,
                    DesktopObservationTriggeredRunStatus.Completed,
                    batch,
                    startedAtUtc,
                    completedAtUtc,
                    result,
                    null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var completedAtUtc = Max(_timeProvider.GetUtcNow(), startedAtUtc);
                return new DesktopObservationTriggeredRun(
                    runId,
                    DesktopObservationTriggeredRunStatus.Failed,
                    batch,
                    startedAtUtc,
                    completedAtUtc,
                    null,
                    ex.ToString());
            }
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private static DateTimeOffset Max(DateTimeOffset value, DateTimeOffset minimum) =>
        value < minimum ? minimum : value;
}
