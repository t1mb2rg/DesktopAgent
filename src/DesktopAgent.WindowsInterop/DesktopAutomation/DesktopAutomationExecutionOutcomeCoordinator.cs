using DesktopAgent.Application.DesktopAutomation;

namespace DesktopAgent.WindowsInterop.DesktopAutomation;

public enum DesktopAutomationExecutionOutcomeFeedbackStatus
{
    BridgeStoppedBeforeCoordinator = 0,
    SuccessRecorded = 1,
    FailureRecorded = 2,
}

public sealed record DesktopAutomationExecutionOutcomeFeedbackResult
{
    public DesktopAutomationExecutionOutcomeFeedbackStatus Status { get; }
    public DesktopAutomationCoordinatorExecutionBridgeResult BridgeResult { get; }
    public DesktopAutomationRuntimeSnapshot RuntimeBefore { get; }
    public DesktopAutomationRuntimeSnapshot RuntimeAfter { get; }

    public bool FeedbackApplied =>
        Status is DesktopAutomationExecutionOutcomeFeedbackStatus.SuccessRecorded
            or DesktopAutomationExecutionOutcomeFeedbackStatus.FailureRecorded;

    public DesktopAutomationExecutionOutcomeFeedbackResult(
        DesktopAutomationExecutionOutcomeFeedbackStatus status,
        DesktopAutomationCoordinatorExecutionBridgeResult bridgeResult,
        DesktopAutomationRuntimeSnapshot runtimeBefore,
        DesktopAutomationRuntimeSnapshot runtimeAfter)
    {
        ArgumentNullException.ThrowIfNull(bridgeResult);
        ArgumentNullException.ThrowIfNull(runtimeBefore);
        ArgumentNullException.ThrowIfNull(runtimeAfter);

        if (status == DesktopAutomationExecutionOutcomeFeedbackStatus.BridgeStoppedBeforeCoordinator
            && bridgeResult.CoordinatorWasInvoked)
        {
            throw new ArgumentException(
                "BridgeStoppedBeforeCoordinator 不得携带 Coordinator run。",
                nameof(status));
        }
        if (status != DesktopAutomationExecutionOutcomeFeedbackStatus.BridgeStoppedBeforeCoordinator
            && !bridgeResult.CoordinatorWasInvoked)
        {
            throw new ArgumentException(
                "Success/Failure feedback 必须来自真实 Coordinator run。",
                nameof(status));
        }

        Status = status;
        BridgeResult = bridgeResult;
        RuntimeBefore = runtimeBefore;
        RuntimeAfter = runtimeAfter;
    }
}

/// <summary>
/// M3-B04 把 B03 执行与 Runtime outcome feedback 放进同一次调用链。
///
/// 不提供 public Apply(result) API，避免同一 B03 result 被重复应用、重复累计 Fuse。
/// 只有本次 ExecuteAndRecordAsync 内部真正得到 Coordinator run 时才记录一次 outcome。
/// </summary>
public sealed class DesktopAutomationExecutionOutcomeCoordinator
{
    private readonly Func<DesktopAutomationAutomaticSafetyGateResult, CancellationToken,
        Task<DesktopAutomationCoordinatorExecutionBridgeResult>> _executeBridge;
    private readonly DesktopAutomationRuntimePolicy _runtimePolicy;

    public DesktopAutomationExecutionOutcomeCoordinator(
        DesktopAutomationCoordinatorExecutionBridge bridge,
        DesktopAutomationRuntimePolicy runtimePolicy)
        : this(
            (safetyGate, ct) => bridge.ExecuteAsync(safetyGate, ct),
            runtimePolicy)
    {
        ArgumentNullException.ThrowIfNull(bridge);
    }

    internal DesktopAutomationExecutionOutcomeCoordinator(
        Func<DesktopAutomationAutomaticSafetyGateResult, CancellationToken,
            Task<DesktopAutomationCoordinatorExecutionBridgeResult>> executeBridge,
        DesktopAutomationRuntimePolicy runtimePolicy)
    {
        _executeBridge = executeBridge ?? throw new ArgumentNullException(nameof(executeBridge));
        _runtimePolicy = runtimePolicy ?? throw new ArgumentNullException(nameof(runtimePolicy));
    }

    public async Task<DesktopAutomationExecutionOutcomeFeedbackResult> ExecuteAndRecordAsync(
        DesktopAutomationAutomaticSafetyGateResult safetyGate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(safetyGate);
        ct.ThrowIfCancellationRequested();

        var before = _runtimePolicy.Snapshot;
        var bridgeResult = await _executeBridge(safetyGate, ct);

        if (!bridgeResult.CoordinatorWasInvoked || bridgeResult.CoordinatorRun is null)
        {
            return new DesktopAutomationExecutionOutcomeFeedbackResult(
                DesktopAutomationExecutionOutcomeFeedbackStatus.BridgeStoppedBeforeCoordinator,
                bridgeResult,
                before,
                _runtimePolicy.Snapshot);
        }

        var after = bridgeResult.CoordinatorRun.IsSuccess
            ? _runtimePolicy.RecordExecutionSuccess()
            : _runtimePolicy.RecordExecutionFailure();
        var status = bridgeResult.CoordinatorRun.IsSuccess
            ? DesktopAutomationExecutionOutcomeFeedbackStatus.SuccessRecorded
            : DesktopAutomationExecutionOutcomeFeedbackStatus.FailureRecorded;

        return new DesktopAutomationExecutionOutcomeFeedbackResult(
            status,
            bridgeResult,
            before,
            after);
    }
}
