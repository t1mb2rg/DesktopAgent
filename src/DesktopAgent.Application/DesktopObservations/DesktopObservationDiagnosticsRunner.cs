using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopObservations;

public enum DesktopObservationDiagnosticsStatus
{
    BaselineEstablished = 0,
    NoChanges = 1,
    ChangesDetected = 2,
    Unstable = 3,
    Unavailable = 4,
}

/// <summary>
/// 一次显式 M3 诊断运行的结果。
/// 只允许 Stable observation 推进会话内 baseline；Unstable / Unavailable 不修改 baseline。
/// </summary>
public sealed record DesktopObservationDiagnosticsResult
{
    public DesktopObservationDiagnosticsStatus Status { get; }
    public StableDesktopObservationResult ObservationResult { get; }
    public DesktopStateBaselineUpdateResult? BaselineUpdate { get; }

    public DesktopStateChangeSet? ChangeSet => BaselineUpdate?.ChangeSet;
    public bool HasChanges => Status == DesktopObservationDiagnosticsStatus.ChangesDetected;

    public DesktopObservationDiagnosticsResult(
        DesktopObservationDiagnosticsStatus status,
        StableDesktopObservationResult observationResult,
        DesktopStateBaselineUpdateResult? baselineUpdate)
    {
        ArgumentNullException.ThrowIfNull(observationResult);

        switch (status)
        {
            case DesktopObservationDiagnosticsStatus.BaselineEstablished
                when observationResult.Status != StableDesktopObservationStatus.Stable
                     || baselineUpdate?.Status != DesktopStateBaselineUpdateStatus.BaselineEstablished:
                throw new ArgumentException(
                    "BaselineEstablished 必须来自 Stable observation 且建立新 baseline。",
                    nameof(status));

            case DesktopObservationDiagnosticsStatus.NoChanges
                when observationResult.Status != StableDesktopObservationStatus.Stable
                     || baselineUpdate?.Status != DesktopStateBaselineUpdateStatus.Compared
                     || baselineUpdate.ChangeSet?.HasChanges != false:
                throw new ArgumentException(
                    "NoChanges 必须来自 Stable observation 且 ChangeSet 为空。",
                    nameof(status));

            case DesktopObservationDiagnosticsStatus.ChangesDetected
                when observationResult.Status != StableDesktopObservationStatus.Stable
                     || baselineUpdate?.Status != DesktopStateBaselineUpdateStatus.Compared
                     || baselineUpdate.ChangeSet?.HasChanges != true:
                throw new ArgumentException(
                    "ChangesDetected 必须来自 Stable observation 且 ChangeSet 非空。",
                    nameof(status));

            case DesktopObservationDiagnosticsStatus.Unstable
                when observationResult.Status != StableDesktopObservationStatus.Unstable
                     || baselineUpdate is not null:
                throw new ArgumentException(
                    "Unstable 不得推进 baseline。",
                    nameof(status));

            case DesktopObservationDiagnosticsStatus.Unavailable
                when observationResult.Status != StableDesktopObservationStatus.Unavailable
                     || baselineUpdate is not null:
                throw new ArgumentException(
                    "Unavailable 不得推进 baseline。",
                    nameof(status));
        }

        Status = status;
        ObservationResult = observationResult;
        BaselineUpdate = baselineUpdate;
    }
}

/// <summary>
/// M3-01D 的显式单轮诊断用例。
/// 只负责 StableObservation -> baseline -> state diff 编排，不读取历史、不持久化 baseline、不调用 Writer。
/// </summary>
public sealed class DesktopObservationDiagnosticsRunner
{
    private readonly IStableDesktopObservationSource _stableObservationSource;
    private readonly DesktopStateBaselineTracker _baselineTracker;

    public DesktopObservationDiagnosticsRunner(
        IStableDesktopObservationSource stableObservationSource,
        DesktopStateBaselineTracker baselineTracker)
    {
        _stableObservationSource = stableObservationSource
            ?? throw new ArgumentNullException(nameof(stableObservationSource));
        _baselineTracker = baselineTracker
            ?? throw new ArgumentNullException(nameof(baselineTracker));
    }

    public async Task<DesktopObservationDiagnosticsResult> RunOnceAsync(
        CancellationToken ct = default)
    {
        var stable = await _stableObservationSource.CaptureStableAsync(ct);

        if (stable.Status == StableDesktopObservationStatus.Unavailable)
        {
            return new DesktopObservationDiagnosticsResult(
                DesktopObservationDiagnosticsStatus.Unavailable,
                stable,
                null);
        }

        if (stable.Status == StableDesktopObservationStatus.Unstable)
        {
            return new DesktopObservationDiagnosticsResult(
                DesktopObservationDiagnosticsStatus.Unstable,
                stable,
                null);
        }

        var baseline = _baselineTracker.AcceptStableObservation(stable.StableObservation!);
        if (baseline.Status == DesktopStateBaselineUpdateStatus.BaselineEstablished)
        {
            return new DesktopObservationDiagnosticsResult(
                DesktopObservationDiagnosticsStatus.BaselineEstablished,
                stable,
                baseline);
        }

        return new DesktopObservationDiagnosticsResult(
            baseline.HasChanges
                ? DesktopObservationDiagnosticsStatus.ChangesDetected
                : DesktopObservationDiagnosticsStatus.NoChanges,
            stable,
            baseline);
    }

    public void ResetBaseline() => _baselineTracker.Reset();
}
