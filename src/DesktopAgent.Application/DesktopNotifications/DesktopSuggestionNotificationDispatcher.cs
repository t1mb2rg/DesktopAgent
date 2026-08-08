using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Application.DesktopNotifications;

public enum DesktopSuggestionNotificationDispatchStatus
{
    Completed = 0,
    NoSinkWork = 1,
    Deferred = 2,
    SinkFailed = 3,
}

public enum DesktopSuggestionNotificationSinkOperation
{
    Withdraw = 0,
    Publish = 1,
}

public sealed record DesktopSuggestionNotificationDispatchResult
{
    public DesktopSuggestionNotificationDispatchStatus Status { get; }
    public DesktopSuggestionNotificationPlan Plan { get; }
    public DesktopSuggestionNotificationPolicyState PolicyState { get; }
    public DesktopSuggestionNotificationSinkOperation? FailedOperation { get; }
    public string? EscapedException { get; }

    public DateTimeOffset? NextEligibleAtUtc => Plan.DeferredUntilUtc;

    public DesktopSuggestionNotificationDispatchResult(
        DesktopSuggestionNotificationDispatchStatus status,
        DesktopSuggestionNotificationPlan plan,
        DesktopSuggestionNotificationPolicyState policyState,
        DesktopSuggestionNotificationSinkOperation? failedOperation = null,
        string? escapedException = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(policyState);

        if (status == DesktopSuggestionNotificationDispatchStatus.SinkFailed)
        {
            if (failedOperation is null || string.IsNullOrWhiteSpace(escapedException))
            {
                throw new ArgumentException(
                    "SinkFailed 必须携带 FailedOperation 与 EscapedException。",
                    nameof(status));
            }
        }
        else if (failedOperation is not null || escapedException is not null)
        {
            throw new ArgumentException(
                "只有 SinkFailed 可以携带 sink failure details。",
                nameof(status));
        }

        Status = status;
        Plan = plan;
        PolicyState = policyState;
        FailedOperation = failedOperation;
        EscapedException = escapedException;
    }
}

/// <summary>
/// M3-04A notification 对账器。
/// 同一实例串行执行 notification sink 操作，确保 Withdraw → Publish 顺序稳定。
/// 不接触 Diff、Shell、Candidate、Writer 或 ExecutionCoordinator。
/// </summary>
public sealed class DesktopSuggestionNotificationDispatcher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DesktopSuggestionNotificationPolicy _policy;
    private readonly IDesktopSuggestionNotificationSink _sink;
    private readonly TimeProvider _timeProvider;

    public DesktopSuggestionNotificationDispatcher(
        DesktopSuggestionNotificationPolicy policy,
        IDesktopSuggestionNotificationSink sink,
        TimeProvider? timeProvider = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DesktopSuggestionNotificationDispatchResult> ReconcileAsync(
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        await _gate.WaitAsync(ct);
        try
        {
            var plan = _policy.Evaluate(lifecycle, _timeProvider.GetUtcNow());

            if (plan.WithdrawSuggestionId is Guid withdrawId)
            {
                try
                {
                    await _sink.WithdrawAsync(withdrawId, ct);
                    _policy.AcknowledgeWithdrawn(withdrawId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 撤旧失败时不继续发布新通知，避免同时留下两个互相矛盾的 notification。
                    return Failed(
                        plan,
                        DesktopSuggestionNotificationSinkOperation.Withdraw,
                        ex);
                }
            }

            if (plan.PublishPayload is not null)
            {
                try
                {
                    await _sink.PublishAsync(plan.PublishPayload, ct);
                    _policy.AcknowledgePublished(
                        plan.PublishPayload,
                        _timeProvider.GetUtcNow());
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Failed(
                        plan,
                        DesktopSuggestionNotificationSinkOperation.Publish,
                        ex);
                }
            }

            var status = plan.IsDeferred
                ? DesktopSuggestionNotificationDispatchStatus.Deferred
                : plan.HasSinkWork
                    ? DesktopSuggestionNotificationDispatchStatus.Completed
                    : DesktopSuggestionNotificationDispatchStatus.NoSinkWork;

            return new DesktopSuggestionNotificationDispatchResult(
                status,
                plan,
                _policy.State);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DesktopSuggestionNotificationDispatchResult Failed(
        DesktopSuggestionNotificationPlan plan,
        DesktopSuggestionNotificationSinkOperation operation,
        Exception ex) =>
        new(
            DesktopSuggestionNotificationDispatchStatus.SinkFailed,
            plan,
            _policy.State,
            operation,
            ex.ToString());
}
