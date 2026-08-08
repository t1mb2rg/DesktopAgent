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
}

public enum DesktopLayoutSuggestionRetirementReason
{
    DesktopChanged = 0,
    BindingChanged = 1,
    UserDismissed = 2,
    ReplacedByNewSuggestion = 3,
    BaselineReestablished = 4,
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

    /// <summary>
    /// 接受一份新的 Suggested 结果并设为 Active。
    /// 若当前已有建议，会先以 ReplacedByNewSuggestion 退休。
    /// </summary>
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

    /// <summary>
    /// 新的 stable State Diff 已证明桌面事实发生变化。
    /// 旧建议无论 Active/Suspended 都立即退休；新建议必须随后重新 Build/Publish。
    /// </summary>
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

    /// <summary>
    /// baseline 被显式重置或首次重新建立时，旧建议失去连续比较链的依据并退休。
    /// 初始没有建议时此操作为 no-op。
    /// </summary>
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
    /// 当前无法证明桌面仍稳定/可完整观察时，保留建议内容但暂停其 Active 展示。
    /// </summary>
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

    /// <summary>
    /// 后续 fresh stable observation 与最近 stable baseline 无事实变化时，
    /// 可把因 Unstable/Unavailable 暂停的建议恢复为 Active。
    /// </summary>
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
