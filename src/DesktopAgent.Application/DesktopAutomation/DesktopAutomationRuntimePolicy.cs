namespace DesktopAgent.Application.DesktopAutomation;

public enum DesktopAutomationMode
{
    Manual = 0,
    Suggestion = 1,
    Automatic = 2,
}

public enum DesktopAutomationBlockReason
{
    RequestedManualMode = 0,
    RequestedSuggestionMode = 1,
    UnattendedWriteGateClosed = 2,
    Paused = 3,
    CooldownActive = 4,
    FuseTripped = 5,
}

public enum DesktopAutomationUnattendedGateReason
{
    None = 0,
    M2RealDualMonitorAcceptancePending = 1,
}

public enum DesktopAutomationExecutionOutcome
{
    Success = 0,
    Failure = 1,
}

/// <summary>
/// 环境级无人值守写入能力门。
/// 05A 只消费这个事实，不持有 Writer；当前 PoC 必须使用 M3AProvisionalClosed。
/// </summary>
public sealed record DesktopAutomationUnattendedGate
{
    public bool IsOpen { get; }
    public DesktopAutomationUnattendedGateReason Reason { get; }

    private DesktopAutomationUnattendedGate(
        bool isOpen,
        DesktopAutomationUnattendedGateReason reason)
    {
        if (isOpen && reason != DesktopAutomationUnattendedGateReason.None)
        {
            throw new ArgumentException(
                "Open unattended gate 不得携带阻断原因。",
                nameof(reason));
        }

        if (!isOpen && reason == DesktopAutomationUnattendedGateReason.None)
        {
            throw new ArgumentException(
                "Closed unattended gate 必须携带阻断原因。",
                nameof(reason));
        }

        IsOpen = isOpen;
        Reason = reason;
    }

    public static DesktopAutomationUnattendedGate M3AProvisionalClosed { get; } =
        new(
            false,
            DesktopAutomationUnattendedGateReason.M2RealDualMonitorAcceptancePending);

    /// <summary>
    /// 仅供未来完成真实 M2 双屏验收后的 composition root / 自动测试使用。
    /// 这个对象本身不授予 Writer，只表示 policy 的外部能力事实。
    /// </summary>
    public static DesktopAutomationUnattendedGate ValidatedOpen { get; } =
        new(true, DesktopAutomationUnattendedGateReason.None);
}

public sealed record DesktopAutomationRuntimePolicySettings
{
    public TimeSpan ExecutionCooldown { get; }
    public int FuseFailureThreshold { get; }

    public DesktopAutomationRuntimePolicySettings(
        TimeSpan executionCooldown,
        int fuseFailureThreshold)
    {
        if (executionCooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(executionCooldown));
        if (fuseFailureThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(fuseFailureThreshold));

        ExecutionCooldown = executionCooldown;
        FuseFailureThreshold = fuseFailureThreshold;
    }
}

public sealed record DesktopAutomationRuntimeSnapshot
{
    public long Revision { get; }
    public DesktopAutomationMode RequestedMode { get; }
    public DesktopAutomationMode EffectiveMode { get; }
    public DesktopAutomationUnattendedGate UnattendedGate { get; }
    public bool IsPaused { get; }
    public string? PauseReason { get; }
    public DateTimeOffset? PauseUntilUtc { get; }
    public bool IsCooldownActive { get; }
    public DateTimeOffset? CooldownUntilUtc { get; }
    public int ConsecutiveExecutionFailures { get; }
    public bool IsFuseTripped { get; }
    public int FuseFailureThreshold { get; }
    public DesktopAutomationExecutionOutcome? LastExecutionOutcome { get; }
    public DateTimeOffset? LastExecutionAtUtc { get; }
    public IReadOnlyList<DesktopAutomationBlockReason> UnattendedBlockReasons { get; }

    public bool CanObserveInBackground => RequestedMode != DesktopAutomationMode.Manual;
    public bool CanPresentSuggestions => RequestedMode != DesktopAutomationMode.Manual;
    public bool CanAttemptUnattendedExecution =>
        EffectiveMode == DesktopAutomationMode.Automatic;

    public DesktopAutomationRuntimeSnapshot(
        long revision,
        DesktopAutomationMode requestedMode,
        DesktopAutomationMode effectiveMode,
        DesktopAutomationUnattendedGate unattendedGate,
        bool isPaused,
        string? pauseReason,
        DateTimeOffset? pauseUntilUtc,
        bool isCooldownActive,
        DateTimeOffset? cooldownUntilUtc,
        int consecutiveExecutionFailures,
        bool isFuseTripped,
        int fuseFailureThreshold,
        DesktopAutomationExecutionOutcome? lastExecutionOutcome,
        DateTimeOffset? lastExecutionAtUtc,
        IEnumerable<DesktopAutomationBlockReason> unattendedBlockReasons)
    {
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(requestedMode))
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        if (!Enum.IsDefined(effectiveMode))
            throw new ArgumentOutOfRangeException(nameof(effectiveMode));
        ArgumentNullException.ThrowIfNull(unattendedGate);
        ArgumentNullException.ThrowIfNull(unattendedBlockReasons);
        if (consecutiveExecutionFailures < 0)
            throw new ArgumentOutOfRangeException(nameof(consecutiveExecutionFailures));
        if (fuseFailureThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(fuseFailureThreshold));
        if (isPaused != (pauseReason is not null))
        {
            throw new ArgumentException(
                "PauseReason 必须且只能在 IsPaused=true 时存在。",
                nameof(pauseReason));
        }
        if (!isPaused && pauseUntilUtc is not null)
        {
            throw new ArgumentException(
                "非暂停状态不得暴露 PauseUntilUtc。",
                nameof(pauseUntilUtc));
        }
        if (isCooldownActive != (cooldownUntilUtc is not null))
        {
            throw new ArgumentException(
                "CooldownUntilUtc 必须且只能在 cooldown active 时存在。",
                nameof(cooldownUntilUtc));
        }
        if ((lastExecutionOutcome is null) != (lastExecutionAtUtc is null))
        {
            throw new ArgumentException(
                "LastExecutionOutcome 与 LastExecutionAtUtc 必须同时存在或同时为空。",
                nameof(lastExecutionOutcome));
        }

        var reasons = unattendedBlockReasons.ToArray();
        if (reasons.Any(reason => !Enum.IsDefined(reason)))
            throw new ArgumentException("包含未知 unattended block reason。", nameof(unattendedBlockReasons));
        if (reasons.Distinct().Count() != reasons.Length)
            throw new ArgumentException("UnattendedBlockReasons 不得重复。", nameof(unattendedBlockReasons));

        Revision = revision;
        RequestedMode = requestedMode;
        EffectiveMode = effectiveMode;
        UnattendedGate = unattendedGate;
        IsPaused = isPaused;
        PauseReason = pauseReason;
        PauseUntilUtc = pauseUntilUtc?.ToUniversalTime();
        IsCooldownActive = isCooldownActive;
        CooldownUntilUtc = cooldownUntilUtc?.ToUniversalTime();
        ConsecutiveExecutionFailures = consecutiveExecutionFailures;
        IsFuseTripped = isFuseTripped;
        FuseFailureThreshold = fuseFailureThreshold;
        LastExecutionOutcome = lastExecutionOutcome;
        LastExecutionAtUtc = lastExecutionAtUtc?.ToUniversalTime();
        UnattendedBlockReasons = Array.AsReadOnly(reasons);
    }
}

/// <summary>
/// M3-05A 纯进程内自动化运行策略状态机。
///
/// 它只回答“当前请求模式实际能做到哪一层”，不接收 Candidate，
/// 不调用 Writer，也不负责启动/停止 monitoring。
/// </summary>
public sealed class DesktopAutomationRuntimePolicy
{
    private readonly object _gate = new();
    private readonly DesktopAutomationRuntimePolicySettings _settings;
    private readonly DesktopAutomationUnattendedGate _unattendedGate;
    private readonly TimeProvider _timeProvider;

    private long _revision;
    private DesktopAutomationMode _requestedMode;
    private bool _pauseRequested;
    private string? _pauseReason;
    private DateTimeOffset? _pauseUntilUtc;
    private DateTimeOffset? _cooldownUntilUtc;
    private int _consecutiveFailures;
    private bool _fuseTripped;
    private DesktopAutomationExecutionOutcome? _lastExecutionOutcome;
    private DateTimeOffset? _lastExecutionAtUtc;

    public DesktopAutomationRuntimePolicy(
        DesktopAutomationRuntimePolicySettings settings,
        DesktopAutomationUnattendedGate unattendedGate,
        TimeProvider? timeProvider = null,
        DesktopAutomationMode initialMode = DesktopAutomationMode.Suggestion)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _unattendedGate = unattendedGate ?? throw new ArgumentNullException(nameof(unattendedGate));
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (!Enum.IsDefined(initialMode))
            throw new ArgumentOutOfRangeException(nameof(initialMode));

        _requestedMode = initialMode;
    }

    public DesktopAutomationRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return CreateSnapshot(_timeProvider.GetUtcNow());
        }
    }

    public DesktopAutomationRuntimeSnapshot SetRequestedMode(DesktopAutomationMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        lock (_gate)
        {
            if (_requestedMode != mode)
            {
                _requestedMode = mode;
                _revision++;
            }

            return CreateSnapshot(_timeProvider.GetUtcNow());
        }
    }

    public DesktopAutomationRuntimeSnapshot Pause(
        string reason,
        TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Pause reason 不能为空。", nameof(reason));
        if (duration is not null && duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            _pauseRequested = true;
            _pauseReason = reason;
            _pauseUntilUtc = duration is null ? null : now + duration.Value;
            _revision++;
            return CreateSnapshot(now);
        }
    }

    public DesktopAutomationRuntimeSnapshot Resume()
    {
        lock (_gate)
        {
            if (_pauseRequested)
            {
                _pauseRequested = false;
                _pauseReason = null;
                _pauseUntilUtc = null;
                _revision++;
            }

            return CreateSnapshot(_timeProvider.GetUtcNow());
        }
    }

    public DesktopAutomationRuntimeSnapshot RecordExecutionSuccess()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            _lastExecutionOutcome = DesktopAutomationExecutionOutcome.Success;
            _lastExecutionAtUtc = now;
            _cooldownUntilUtc = CreateCooldownDeadline(now);

            // 连续失败在成功后清零；已经真正触发的 fuse 仍必须显式 ResetFuse，
            // 避免任何旁路成功自动解除熔断。
            if (!_fuseTripped)
                _consecutiveFailures = 0;

            _revision++;
            return CreateSnapshot(now);
        }
    }

    public DesktopAutomationRuntimeSnapshot RecordExecutionFailure()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            _lastExecutionOutcome = DesktopAutomationExecutionOutcome.Failure;
            _lastExecutionAtUtc = now;
            _cooldownUntilUtc = CreateCooldownDeadline(now);

            if (!_fuseTripped)
            {
                _consecutiveFailures = Math.Min(
                    _consecutiveFailures + 1,
                    _settings.FuseFailureThreshold);
                if (_consecutiveFailures >= _settings.FuseFailureThreshold)
                    _fuseTripped = true;
            }

            _revision++;
            return CreateSnapshot(now);
        }
    }

    public DesktopAutomationRuntimeSnapshot ResetFuse()
    {
        lock (_gate)
        {
            if (_fuseTripped || _consecutiveFailures != 0)
            {
                _fuseTripped = false;
                _consecutiveFailures = 0;
                _revision++;
            }

            return CreateSnapshot(_timeProvider.GetUtcNow());
        }
    }

    private DateTimeOffset? CreateCooldownDeadline(DateTimeOffset now) =>
        _settings.ExecutionCooldown == TimeSpan.Zero
            ? null
            : now + _settings.ExecutionCooldown;

    private DesktopAutomationRuntimeSnapshot CreateSnapshot(DateTimeOffset now)
    {
        now = now.ToUniversalTime();

        var isPaused = _pauseRequested
            && (_pauseUntilUtc is null || now < _pauseUntilUtc.Value);
        var pauseReason = isPaused ? _pauseReason : null;
        var pauseUntil = isPaused ? _pauseUntilUtc : null;

        var isCooldownActive = _cooldownUntilUtc is not null
            && now < _cooldownUntilUtc.Value;
        var cooldownUntil = isCooldownActive ? _cooldownUntilUtc : null;

        var reasons = new List<DesktopAutomationBlockReason>();
        switch (_requestedMode)
        {
            case DesktopAutomationMode.Manual:
                reasons.Add(DesktopAutomationBlockReason.RequestedManualMode);
                break;
            case DesktopAutomationMode.Suggestion:
                reasons.Add(DesktopAutomationBlockReason.RequestedSuggestionMode);
                break;
        }

        if (!_unattendedGate.IsOpen)
            reasons.Add(DesktopAutomationBlockReason.UnattendedWriteGateClosed);
        if (isPaused)
            reasons.Add(DesktopAutomationBlockReason.Paused);
        if (isCooldownActive)
            reasons.Add(DesktopAutomationBlockReason.CooldownActive);
        if (_fuseTripped)
            reasons.Add(DesktopAutomationBlockReason.FuseTripped);

        var effectiveMode = _requestedMode switch
        {
            DesktopAutomationMode.Manual => DesktopAutomationMode.Manual,
            DesktopAutomationMode.Suggestion => DesktopAutomationMode.Suggestion,
            DesktopAutomationMode.Automatic
                when !_unattendedGate.IsOpen || isPaused || isCooldownActive || _fuseTripped =>
                DesktopAutomationMode.Suggestion,
            DesktopAutomationMode.Automatic => DesktopAutomationMode.Automatic,
            _ => throw new InvalidOperationException("未知 DesktopAutomationMode。"),
        };

        return new DesktopAutomationRuntimeSnapshot(
            _revision,
            _requestedMode,
            effectiveMode,
            _unattendedGate,
            isPaused,
            pauseReason,
            pauseUntil,
            isCooldownActive,
            cooldownUntil,
            _consecutiveFailures,
            _fuseTripped,
            _settings.FuseFailureThreshold,
            _lastExecutionOutcome,
            _lastExecutionAtUtc,
            reasons);
    }
}
