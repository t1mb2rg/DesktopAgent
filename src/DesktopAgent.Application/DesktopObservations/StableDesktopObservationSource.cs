using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopObservations;

public sealed record DesktopObservationStabilitySettings
{
    public TimeSpan SampleInterval { get; }

    public DesktopObservationStabilitySettings(TimeSpan sampleInterval)
    {
        if (sampleInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        SampleInterval = sampleInterval;
    }
}

public enum DesktopObservationSamplePhase
{
    First = 0,
    Second = 1,
}

public sealed record DesktopObservationCaptureFailure(
    DesktopObservationSamplePhase Phase,
    DesktopObservationCaptureIssue Issue);

public enum StableDesktopObservationStatus
{
    Stable = 0,
    Unstable = 1,
    Unavailable = 2,
}

/// <summary>
/// 连续两次完整 capture 的稳定观察结果。
/// Stable 时使用第二次 fresh observation；Unavailable 表示至少一次 capture 本身无法建立完整事实；
/// Unstable 表示两次 capture 都完整，但当前事实仍在变化。
/// </summary>
public sealed record StableDesktopObservationResult
{
    public StableDesktopObservationStatus Status { get; }
    public DesktopObservation? StableObservation { get; }
    public IReadOnlyList<DesktopObservationCaptureFailure> CaptureFailures { get; }
    public IReadOnlyList<DesktopObservationStabilityIssue> StabilityIssues { get; }

    public bool IsStable => Status == StableDesktopObservationStatus.Stable;

    public StableDesktopObservationResult(
        StableDesktopObservationStatus status,
        DesktopObservation? stableObservation,
        IReadOnlyList<DesktopObservationCaptureFailure> captureFailures,
        IReadOnlyList<DesktopObservationStabilityIssue> stabilityIssues)
    {
        ArgumentNullException.ThrowIfNull(captureFailures);
        ArgumentNullException.ThrowIfNull(stabilityIssues);

        switch (status)
        {
            case StableDesktopObservationStatus.Stable when stableObservation is null:
                throw new ArgumentException("Stable 状态必须提供 StableObservation。", nameof(stableObservation));
            case StableDesktopObservationStatus.Stable when captureFailures.Count != 0 || stabilityIssues.Count != 0:
                throw new ArgumentException("Stable 状态不得携带 failure/issue。", nameof(status));
            case StableDesktopObservationStatus.Unstable when stableObservation is not null:
                throw new ArgumentException("Unstable 状态不得提供 StableObservation。", nameof(stableObservation));
            case StableDesktopObservationStatus.Unstable when stabilityIssues.Count == 0:
                throw new ArgumentException("Unstable 状态至少需要一个 StabilityIssue。", nameof(stabilityIssues));
            case StableDesktopObservationStatus.Unstable when captureFailures.Count != 0:
                throw new ArgumentException("Unstable 状态不得携带 capture failure。", nameof(captureFailures));
            case StableDesktopObservationStatus.Unavailable when stableObservation is not null:
                throw new ArgumentException("Unavailable 状态不得提供 StableObservation。", nameof(stableObservation));
            case StableDesktopObservationStatus.Unavailable when captureFailures.Count == 0:
                throw new ArgumentException("Unavailable 状态至少需要一个 CaptureFailure。", nameof(captureFailures));
            case StableDesktopObservationStatus.Unavailable when stabilityIssues.Count != 0:
                throw new ArgumentException("Unavailable 状态不得携带 StabilityIssue。", nameof(stabilityIssues));
        }

        Status = status;
        StableObservation = stableObservation;
        CaptureFailures = Array.AsReadOnly(captureFailures.ToArray());
        StabilityIssues = Array.AsReadOnly(stabilityIssues.ToArray());
    }
}

public interface IStableDesktopObservationSource
{
    Task<StableDesktopObservationResult> CaptureStableAsync(CancellationToken ct = default);
}

/// <summary>
/// M3 的双采样稳定观察编排器。
/// 不读取 history/journal，不调用 Writer；只消费 IDesktopObservationSource 的 fresh facts。
/// </summary>
public sealed class StableDesktopObservationSource : IStableDesktopObservationSource
{
    private readonly IDesktopObservationSource _source;
    private readonly DesktopObservationStabilitySettings _settings;
    private readonly TimeProvider _timeProvider;

    public StableDesktopObservationSource(
        IDesktopObservationSource source,
        DesktopObservationStabilitySettings settings,
        TimeProvider? timeProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StableDesktopObservationResult> CaptureStableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var first = await _source.CaptureAsync(ct);
        if (!first.IsCaptured)
            return Unavailable(DesktopObservationSamplePhase.First, first.Issues);

        if (_settings.SampleInterval > TimeSpan.Zero)
            await Task.Delay(_settings.SampleInterval, _timeProvider, ct);

        ct.ThrowIfCancellationRequested();
        var second = await _source.CaptureAsync(ct);
        if (!second.IsCaptured)
            return Unavailable(DesktopObservationSamplePhase.Second, second.Issues);

        var stability = DesktopObservationStabilityEvaluator.Evaluate(
            first.Observation!,
            second.Observation!);

        if (!stability.IsStable)
        {
            return new StableDesktopObservationResult(
                StableDesktopObservationStatus.Unstable,
                null,
                Array.Empty<DesktopObservationCaptureFailure>(),
                stability.Issues);
        }

        return new StableDesktopObservationResult(
            StableDesktopObservationStatus.Stable,
            stability.StableObservation,
            Array.Empty<DesktopObservationCaptureFailure>(),
            Array.Empty<DesktopObservationStabilityIssue>());
    }

    private static StableDesktopObservationResult Unavailable(
        DesktopObservationSamplePhase phase,
        IReadOnlyList<DesktopObservationCaptureIssue> issues)
    {
        var failures = issues
            .Select(issue => new DesktopObservationCaptureFailure(phase, issue))
            .ToArray();

        return new StableDesktopObservationResult(
            StableDesktopObservationStatus.Unavailable,
            null,
            failures,
            Array.Empty<DesktopObservationStabilityIssue>());
    }
}
