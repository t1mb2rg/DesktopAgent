using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopObservations;

public enum DesktopStateBaselineUpdateStatus
{
    BaselineEstablished = 0,
    Compared = 1,
}

public sealed record DesktopStateBaselineUpdateResult
{
    public DesktopStateBaselineUpdateStatus Status { get; }
    public DesktopObservation CurrentObservation { get; }
    public DesktopObservation? PreviousObservation { get; }
    public DesktopStateChangeSet? ChangeSet { get; }

    public bool HasChanges => ChangeSet?.HasChanges == true;

    public DesktopStateBaselineUpdateResult(
        DesktopStateBaselineUpdateStatus status,
        DesktopObservation currentObservation,
        DesktopObservation? previousObservation,
        DesktopStateChangeSet? changeSet)
    {
        ArgumentNullException.ThrowIfNull(currentObservation);

        if (status == DesktopStateBaselineUpdateStatus.BaselineEstablished
            && (previousObservation is not null || changeSet is not null))
        {
            throw new ArgumentException(
                "BaselineEstablished 不得包含 PreviousObservation 或 ChangeSet。",
                nameof(status));
        }

        if (status == DesktopStateBaselineUpdateStatus.Compared
            && (previousObservation is null || changeSet is null))
        {
            throw new ArgumentException(
                "Compared 必须包含 PreviousObservation 与 ChangeSet。",
                nameof(status));
        }

        Status = status;
        CurrentObservation = currentObservation;
        PreviousObservation = previousObservation;
        ChangeSet = changeSet;
    }
}

/// <summary>
/// M3-A 当前会话内的 stable observation baseline。
/// 首次 stable observation 只建立 baseline，不伪造“所有图标刚刚 Added”的启动通知。
/// 不持久化到 SQLite；history 不能反向成为当前物理事实。
/// </summary>
public sealed class DesktopStateBaselineTracker
{
    private readonly object _gate = new();
    private DesktopObservation? _baseline;

    public DesktopStateBaselineUpdateResult AcceptStableObservation(DesktopObservation current)
    {
        ArgumentNullException.ThrowIfNull(current);

        lock (_gate)
        {
            if (_baseline is null)
            {
                _baseline = current;
                return new DesktopStateBaselineUpdateResult(
                    DesktopStateBaselineUpdateStatus.BaselineEstablished,
                    current,
                    null,
                    null);
            }

            var previous = _baseline;
            var diff = DesktopStateDiffer.Compare(previous, current);
            _baseline = current;

            return new DesktopStateBaselineUpdateResult(
                DesktopStateBaselineUpdateStatus.Compared,
                current,
                previous,
                diff);
        }
    }

    public void Reset()
    {
        lock (_gate)
            _baseline = null;
    }
}
