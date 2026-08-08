namespace DesktopAgent.Application.DesktopSuggestions;

public enum DesktopLayoutSuggestionPresentationState
{
    None = 0,
    Active = 1,
    Suspended = 2,
}

public enum DesktopLayoutSuggestionSuspensionReason
{
    ObservationUnstable = 0,
    ObservationUnavailable = 1,
    ObservationRunFailed = 2,
}

public enum DesktopLayoutSuggestionRetirementReason
{
    DesktopChanged = 0,
    BindingChanged = 1,
    UserDismissed = 2,
    ReplacedByNewSuggestion = 3,
    BaselineReestablished = 4,
    BaselineReset = 5,
}

public sealed record DesktopLayoutRetiredSuggestion(
    DesktopLayoutSuggestion Suggestion,
    DesktopLayoutSuggestionRetirementReason Reason,
    DateTimeOffset RetiredAtUtc,
    Guid? NewStableObservationId = null);

public sealed record DesktopLayoutSuggestionLifecycleSnapshot
{
    public long Revision { get; }
    public DesktopLayoutSuggestionPresentationState State { get; }
    public DesktopLayoutSuggestion? CurrentSuggestion { get; }
    public DesktopLayoutSuggestionSuspensionReason? SuspensionReason { get; }
    public DesktopLayoutRetiredSuggestion? LastRetiredSuggestion { get; }

    public bool HasCurrentSuggestion => CurrentSuggestion is not null;
    public bool IsActionablePresentation =>
        State == DesktopLayoutSuggestionPresentationState.Active
        && CurrentSuggestion is not null;

    public DesktopLayoutSuggestionLifecycleSnapshot(
        long revision,
        DesktopLayoutSuggestionPresentationState state,
        DesktopLayoutSuggestion? currentSuggestion,
        DesktopLayoutSuggestionSuspensionReason? suspensionReason,
        DesktopLayoutRetiredSuggestion? lastRetiredSuggestion)
    {
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision));

        switch (state)
        {
            case DesktopLayoutSuggestionPresentationState.None
                when currentSuggestion is not null || suspensionReason is not null:
                throw new ArgumentException("None 状态不得携带 CurrentSuggestion / SuspensionReason。", nameof(state));
            case DesktopLayoutSuggestionPresentationState.Active
                when currentSuggestion is null || suspensionReason is not null:
                throw new ArgumentException("Active 必须携带 Suggestion 且不得携带 SuspensionReason。", nameof(state));
            case DesktopLayoutSuggestionPresentationState.Suspended
                when currentSuggestion is null || suspensionReason is null:
                throw new ArgumentException("Suspended 必须携带 Suggestion 与 SuspensionReason。", nameof(state));
        }

        Revision = revision;
        State = state;
        CurrentSuggestion = currentSuggestion;
        SuspensionReason = suspensionReason;
        LastRetiredSuggestion = lastRetiredSuggestion;
    }
}

/// <summary>
/// M3 Suggestion 的进程内生命周期槽位。
/// 它只管理“当前建议能否展示为 active”，不执行 Candidate，也不持有 Writer。
/// </summary>
public sealed class DesktopLayoutSuggestionLifecycle
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    private long _revision;
    private DesktopLayoutSuggestionPresentationState _state;
    private DesktopLayoutSuggestion? _current;
    private DesktopLayoutSuggestionSuspensionReason? _suspensionReason;
    private DesktopLayoutRetiredSuggestion? _lastRetired;

    public DesktopLayoutSuggestionLifecycle(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = DesktopLayoutSuggestionPresentationState.None;
    }

    public DesktopLayoutSuggestionLifecycleSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return CreateSnapshot();
        }
    }

    public DesktopLayoutSuggestionLifecycleSnapshot Publish(
        DesktopLayoutSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        lock (_gate)
        {
            if (_current is not null
                && _current.SuggestionId == suggestion.SuggestionId)
            {
                if (_state == DesktopLayoutSuggestionPresentationState.Suspended)
                {
                    _state = DesktopLayoutSuggestionPresentationState.Active;
                    _suspensionReason = null;
                    _revision++;
                }
                return CreateSnapshot();
            }

            RetireCurrent(
                DesktopLayoutSuggestionRetirementReason.ReplacedByNewSuggestion,
                suggestion.SourceObservationId);

            _current = suggestion;
            _state = DesktopLayoutSuggestionPresentationState.Active;
            _suspensionReason = null;
            _revision++;
            return CreateSnapshot();
        }
    }

    public DesktopLayoutSuggestionLifecycleSnapshot MarkDesktopChanged(
        Guid newStableObservationId)
    {
        if (newStableObservationId == Guid.Empty)
            throw new ArgumentException("newStableObservationId 不能为空。", nameof(newStableObservationId));

        lock (_gate)
        {
            if (_current is null)
                return CreateSnapshot();

            RetireCurrent(
                DesktopLayoutSuggestionRetirementReason.DesktopChanged,
                newStableObservationId);
            _revision++;
            return CreateSnapshot();
        }
    }

    public DesktopLayoutSuggestionLifecycleSnapshot MarkBaselineReestablished(
        Guid newBaselineObservationId)
    {
        if (newBaselineObservationId == Guid.Empty)
            throw new ArgumentException("newBaselineObservationId 不能为空。", nameof(newBaselineObservationId));

        lock (_gate)
        {
            if (_current is null)
                return CreateSnapshot();

            RetireCurrent(
                DesktopLayoutSuggestionRetirementReason.BaselineReestablished,
                newBaselineObservationId);
            _revision++;
            return CreateSnapshot();
        }
    }

    /// <summary>
    /// baseline 被用户显式 Reset 的瞬间，旧 Suggestion 立即失去连续证据链。
    /// 不等待下一次 BaselineEstablished 才退休。
    /// </summary>
    public DesktopLayoutSuggestionLifecycleSnapshot MarkBaselineReset()
    {
        lock (_gate)
        {
            if (_current is null)
                return CreateSnapshot();

            RetireCurrent(DesktopLayoutSuggestionRetirementReason.BaselineReset, null);
            _revision++;
            return CreateSnapshot();
        }
    }

    public DesktopLayoutSuggestionLifecycleSnapshot Suspend(
        DesktopLayoutSuggestionSuspensionReason reason)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        lock (_gate)
        {
            if (_current is null)
                return CreateSnapshot();

            if (_state == DesktopLayoutSuggestionPresentationState.Suspended
                && _suspensionReason == reason)
                return CreateSnapshot();

            _state = DesktopLayoutSuggestionPresentationState.Suspended;
            _suspensionReason = reason;
            _revision++;
            return CreateSnapshot();
        }
    }

    public DesktopLayoutSuggestionLifecycleSnapshot ConfirmNoDesktopChanges()
    {
        lock (_gate)
        {
            if (_current is null
                || _state != DesktopLayoutSuggestionPresentationState.Suspended)
                return CreateSnapshot();

            _state = DesktopLayoutSuggestionPresentationState.Active;
            _suspensionReason = null;
            _revision++;
            return CreateSnapshot();
        }
    }

    public DesktopLayoutSuggestionLifecycleSnapshot MarkBindingChanged()
    {
        lock (_gate)
        {
            if (_current is null)
                return CreateSnapshot();

            RetireCurrent(DesktopLayoutSuggestionRetirementReason.BindingChanged, null);
            _revision++;
            return CreateSnapshot();
        }
    }

    public DesktopLayoutSuggestionLifecycleSnapshot DismissCurrent()
    {
        lock (_gate)
        {
            if (_current is null)
                return CreateSnapshot();

            RetireCurrent(DesktopLayoutSuggestionRetirementReason.UserDismissed, null);
            _revision++;
            return CreateSnapshot();
        }
    }

    private void RetireCurrent(
        DesktopLayoutSuggestionRetirementReason reason,
        Guid? newStableObservationId)
    {
        if (_current is null)
            return;

        _lastRetired = new DesktopLayoutRetiredSuggestion(
            _current,
            reason,
            _timeProvider.GetUtcNow(),
            newStableObservationId);
        _current = null;
        _state = DesktopLayoutSuggestionPresentationState.None;
        _suspensionReason = null;
    }

    private DesktopLayoutSuggestionLifecycleSnapshot CreateSnapshot() =>
        new(
            _revision,
            _state,
            _current,
            _suspensionReason,
            _lastRetired);
}
