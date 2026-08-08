using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.WindowsInterop.DesktopAutomation;

public enum DesktopAutomationExecutionCycleStatus
{
    CycleAlreadyRunning = 0,
    PreflightStopped = 1,
    FreshHandoffStopped = 2,
    AutomaticSafetyGateStopped = 3,
    ExecutionStageReturned = 4,
    FaultedAndPaused = 5,
}

public sealed record DesktopAutomationExecutionCycleResult
{
    public DesktopAutomationExecutionCycleStatus Status { get; }
    public DesktopAutomationRuntimeSnapshot RuntimeAtStart { get; }
    public DesktopLayoutSuggestionLifecycleSnapshot LifecycleAtStart { get; }
    public DesktopAutomationPreflightDecision? Preflight { get; }
    public DesktopAutomationFreshExecutionHandoffResult? Handoff { get; }
    public DesktopAutomationAutomaticSafetyGateResult? SafetyGate { get; }
    public DesktopAutomationExecutionOutcomeFeedbackResult? Outcome { get; }
    public DesktopAutomationRuntimeSnapshot RuntimeAfter { get; }
    public string? Fault { get; }

    public bool ReachedCoordinatorStage => Outcome is not null;

    public DesktopAutomationExecutionCycleResult(
        DesktopAutomationExecutionCycleStatus status,
        DesktopAutomationRuntimeSnapshot runtimeAtStart,
        DesktopLayoutSuggestionLifecycleSnapshot lifecycleAtStart,
        DesktopAutomationRuntimeSnapshot runtimeAfter,
        DesktopAutomationPreflightDecision? preflight = null,
        DesktopAutomationFreshExecutionHandoffResult? handoff = null,
        DesktopAutomationAutomaticSafetyGateResult? safetyGate = null,
        DesktopAutomationExecutionOutcomeFeedbackResult? outcome = null,
        string? fault = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeAtStart);
        ArgumentNullException.ThrowIfNull(lifecycleAtStart);
        ArgumentNullException.ThrowIfNull(runtimeAfter);

        if (status == DesktopAutomationExecutionCycleStatus.PreflightStopped && preflight is null)
            throw new ArgumentException("PreflightStopped 必须携带 Preflight。", nameof(preflight));
        if (status == DesktopAutomationExecutionCycleStatus.FreshHandoffStopped
            && (preflight is null || handoff is null))
        {
            throw new ArgumentException(
                "FreshHandoffStopped 必须携带 Preflight + Handoff。",
                nameof(handoff));
        }
        if (status == DesktopAutomationExecutionCycleStatus.AutomaticSafetyGateStopped
            && (preflight is null || handoff is null || safetyGate is null))
        {
            throw new ArgumentException(
                "AutomaticSafetyGateStopped 必须携带前三阶段结果。",
                nameof(safetyGate));
        }
        if (status == DesktopAutomationExecutionCycleStatus.ExecutionStageReturned
            && (preflight is null || handoff is null || safetyGate is null || outcome is null))
        {
            throw new ArgumentException(
                "ExecutionStageReturned 必须携带完整 cycle 结果。",
                nameof(outcome));
        }
        if (status == DesktopAutomationExecutionCycleStatus.FaultedAndPaused
            && string.IsNullOrWhiteSpace(fault))
        {
            throw new ArgumentException("FaultedAndPaused 必须携带 Fault。", nameof(fault));
        }
        if (status != DesktopAutomationExecutionCycleStatus.FaultedAndPaused && fault is not null)
            throw new ArgumentException("只有 FaultedAndPaused 可以携带 Fault。", nameof(fault));

        Status = status;
        RuntimeAtStart = runtimeAtStart;
        LifecycleAtStart = lifecycleAtStart;
        Preflight = preflight;
        Handoff = handoff;
        SafetyGate = safetyGate;
        Outcome = outcome;
        RuntimeAfter = runtimeAfter;
        Fault = fault;
    }
}

/// <summary>
/// M3-B05 一次性 automatic execution cycle。
///
/// 固定串联：05C → B01 → B02 → B04(B03)。
/// 任一阶段未 Ready 就原地停止，不跨级补票；cycle 本身不直接依赖 Writer。
/// </summary>
public sealed class DesktopAutomationExecutionCycleCoordinator : IDisposable
{
    private const string EscapedFaultPauseReason =
        "Automatic execution cycle 发生逃逸异常；执行历史可能不完整，需要人工检查后再恢复 Automatic。";

    private readonly DesktopAutomationRuntimePolicy _runtimePolicy;
    private readonly DesktopLayoutSuggestionLifecycle _lifecycle;
    private readonly Func<DesktopAutomationRuntimeSnapshot,
        DesktopLayoutSuggestionLifecycleSnapshot,
        DesktopAutomationPreflightDecision> _evaluatePreflight;
    private readonly Func<DesktopAutomationPreflightDecision, CancellationToken,
        Task<DesktopAutomationFreshExecutionHandoffResult>> _prepareHandoff;
    private readonly Func<DesktopAutomationFreshExecutionHandoffResult, CancellationToken,
        Task<DesktopAutomationAutomaticSafetyGateResult>> _evaluateSafetyGate;
    private readonly Func<DesktopAutomationAutomaticSafetyGateResult, CancellationToken,
        Task<DesktopAutomationExecutionOutcomeFeedbackResult>> _executeAndRecord;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private bool _disposed;

    public DesktopAutomationExecutionCycleCoordinator(
        DesktopAutomationRuntimePolicy runtimePolicy,
        DesktopLayoutSuggestionLifecycle lifecycle,
        DesktopAutomationPreflightDecisionPolicy preflightPolicy,
        DesktopAutomationFreshExecutionHandoff freshHandoff,
        DesktopAutomationAutomaticSafetyGate automaticSafetyGate,
        DesktopAutomationExecutionOutcomeCoordinator outcomeCoordinator)
        : this(
            runtimePolicy,
            lifecycle,
            (runtime, suggestionLifecycle) =>
                preflightPolicy.Evaluate(runtime, suggestionLifecycle),
            (preflight, ct) => freshHandoff.PrepareAsync(preflight, ct),
            (handoff, ct) => automaticSafetyGate.EvaluateAsync(handoff, ct),
            (safetyGate, ct) => outcomeCoordinator.ExecuteAndRecordAsync(safetyGate, ct))
    {
        ArgumentNullException.ThrowIfNull(preflightPolicy);
        ArgumentNullException.ThrowIfNull(freshHandoff);
        ArgumentNullException.ThrowIfNull(automaticSafetyGate);
        ArgumentNullException.ThrowIfNull(outcomeCoordinator);
    }

    internal DesktopAutomationExecutionCycleCoordinator(
        DesktopAutomationRuntimePolicy runtimePolicy,
        DesktopLayoutSuggestionLifecycle lifecycle,
        Func<DesktopAutomationRuntimeSnapshot,
            DesktopLayoutSuggestionLifecycleSnapshot,
            DesktopAutomationPreflightDecision> evaluatePreflight,
        Func<DesktopAutomationPreflightDecision, CancellationToken,
            Task<DesktopAutomationFreshExecutionHandoffResult>> prepareHandoff,
        Func<DesktopAutomationFreshExecutionHandoffResult, CancellationToken,
            Task<DesktopAutomationAutomaticSafetyGateResult>> evaluateSafetyGate,
        Func<DesktopAutomationAutomaticSafetyGateResult, CancellationToken,
            Task<DesktopAutomationExecutionOutcomeFeedbackResult>> executeAndRecord)
    {
        _runtimePolicy = runtimePolicy ?? throw new ArgumentNullException(nameof(runtimePolicy));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _evaluatePreflight = evaluatePreflight ?? throw new ArgumentNullException(nameof(evaluatePreflight));
        _prepareHandoff = prepareHandoff ?? throw new ArgumentNullException(nameof(prepareHandoff));
        _evaluateSafetyGate = evaluateSafetyGate ?? throw new ArgumentNullException(nameof(evaluateSafetyGate));
        _executeAndRecord = executeAndRecord ?? throw new ArgumentNullException(nameof(executeAndRecord));
    }

    public async Task<DesktopAutomationExecutionCycleResult> RunCurrentAsync(
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (!await _singleFlight.WaitAsync(0, ct))
        {
            var busyRuntime = _runtimePolicy.Snapshot;
            return new DesktopAutomationExecutionCycleResult(
                DesktopAutomationExecutionCycleStatus.CycleAlreadyRunning,
                busyRuntime,
                _lifecycle.Snapshot,
                busyRuntime);
        }

        var runtimeAtStart = _runtimePolicy.Snapshot;
        var lifecycleAtStart = _lifecycle.Snapshot;
        DesktopAutomationPreflightDecision? preflight = null;
        DesktopAutomationFreshExecutionHandoffResult? handoff = null;
        DesktopAutomationAutomaticSafetyGateResult? safetyGate = null;

        try
        {
            try
            {
                preflight = _evaluatePreflight(runtimeAtStart, lifecycleAtStart);
                if (!preflight.MayProceedToFreshSafetyCheck)
                {
                    return new DesktopAutomationExecutionCycleResult(
                        DesktopAutomationExecutionCycleStatus.PreflightStopped,
                        runtimeAtStart,
                        lifecycleAtStart,
                        _runtimePolicy.Snapshot,
                        preflight: preflight);
                }

                handoff = await _prepareHandoff(preflight, ct);
                if (!handoff.MayProceedToAutomaticSafetyGate)
                {
                    return new DesktopAutomationExecutionCycleResult(
                        DesktopAutomationExecutionCycleStatus.FreshHandoffStopped,
                        runtimeAtStart,
                        lifecycleAtStart,
                        _runtimePolicy.Snapshot,
                        preflight,
                        handoff);
                }

                safetyGate = await _evaluateSafetyGate(handoff, ct);
                if (!safetyGate.MayEnterCoordinatorPreWriteGate)
                {
                    return new DesktopAutomationExecutionCycleResult(
                        DesktopAutomationExecutionCycleStatus.AutomaticSafetyGateStopped,
                        runtimeAtStart,
                        lifecycleAtStart,
                        _runtimePolicy.Snapshot,
                        preflight,
                        handoff,
                        safetyGate);
                }

                var outcome = await _executeAndRecord(safetyGate, ct);
                return new DesktopAutomationExecutionCycleResult(
                    DesktopAutomationExecutionCycleStatus.ExecutionStageReturned,
                    runtimeAtStart,
                    lifecycleAtStart,
                    outcome.RuntimeAfter,
                    preflight,
                    handoff,
                    safetyGate,
                    outcome);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var paused = _runtimePolicy.Pause(EscapedFaultPauseReason);
                return new DesktopAutomationExecutionCycleResult(
                    DesktopAutomationExecutionCycleStatus.FaultedAndPaused,
                    runtimeAtStart,
                    lifecycleAtStart,
                    paused,
                    preflight,
                    handoff,
                    safetyGate,
                    fault: $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _singleFlight.Dispose();
    }
}
