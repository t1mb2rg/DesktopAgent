using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopObservations;

public enum DesktopObservationCaptureIssueCode
{
    NoDisplays = 0,
    DuplicateDisplayDeviceName = 1,
    ShellObservationFailed = 2,
    IncompleteDesktopRead = 3,
    DuplicateStableId = 4,
    IconOutsideDisplayTopology = 5,
}

public sealed record DesktopObservationCaptureIssue(
    DesktopObservationCaptureIssueCode Code,
    string Message,
    string? StableId = null);

public enum DesktopObservationCaptureStatus
{
    Captured = 0,
    Unavailable = 1,
}

public sealed record DesktopObservationCaptureResult
{
    public DesktopObservationCaptureStatus Status { get; }
    public DesktopObservation? Observation { get; }
    public IReadOnlyList<DesktopObservationCaptureIssue> Issues { get; }

    public bool IsCaptured => Status == DesktopObservationCaptureStatus.Captured;

    public DesktopObservationCaptureResult(
        DesktopObservationCaptureStatus status,
        DesktopObservation? observation,
        IReadOnlyList<DesktopObservationCaptureIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (status == DesktopObservationCaptureStatus.Captured && observation is null)
            throw new ArgumentException("Captured 状态必须提供 Observation。", nameof(observation));
        if (status != DesktopObservationCaptureStatus.Captured && observation is not null)
            throw new ArgumentException("非 Captured 状态不得提供 Observation。", nameof(observation));

        Status = status;
        Observation = observation;
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public static DesktopObservationCaptureResult Captured(DesktopObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return new DesktopObservationCaptureResult(
            DesktopObservationCaptureStatus.Captured,
            observation,
            Array.Empty<DesktopObservationCaptureIssue>());
    }

    public static DesktopObservationCaptureResult Unavailable(
        params DesktopObservationCaptureIssue[] issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Length == 0)
            throw new ArgumentException("Unavailable 状态至少需要一个 issue。", nameof(issues));
        return new DesktopObservationCaptureResult(
            DesktopObservationCaptureStatus.Unavailable,
            null,
            issues);
    }
}

/// <summary>
/// M3-A 的只读桌面事实来源。
/// 实现只能观察当前状态，不得暴露或调用真实 Writer。
/// </summary>
public interface IDesktopObservationSource
{
    Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default);
}
