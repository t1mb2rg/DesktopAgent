using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Application.DesktopNotifications;

public sealed record DesktopSuggestionNotificationPolicySettings
{
    public TimeSpan MinimumPublishInterval { get; }

    public DesktopSuggestionNotificationPolicySettings(TimeSpan minimumPublishInterval)
    {
        if (minimumPublishInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumPublishInterval));

        MinimumPublishInterval = minimumPublishInterval;
    }
}

public enum DesktopSuggestionNotificationSuppressionReason
{
    NoActiveSuggestion = 0,
    SuggestionSuspended = 1,
    AlreadyPublished = 2,
    Cooldown = 3,
}

public sealed record DesktopSuggestionNotificationPolicyState(
    Guid? SurfacedSuggestionId,
    Guid? LastPublishedSuggestionId,
    DateTimeOffset? LastPublishedAtUtc);

/// <summary>
/// 对当前 Suggestion lifecycle 的一次通知对账计划。
/// Withdraw 与 Publish 可以同时存在；Publish 与 Deferred 互斥。
/// </summary>
public sealed record DesktopSuggestionNotificationPlan
{
    public long LifecycleRevision { get; }
    public Guid? WithdrawSuggestionId { get; }
    public DesktopSuggestionNotificationPayload? PublishPayload { get; }
    public DateTimeOffset? DeferredUntilUtc { get; }
    public DesktopSuggestionNotificationSuppressionReason? SuppressionReason { get; }

    public bool HasWithdraw => WithdrawSuggestionId is not null;
    public bool HasPublish => PublishPayload is not null;
    public bool IsDeferred => DeferredUntilUtc is not null;
    public bool HasSinkWork => HasWithdraw || HasPublish;

    public DesktopSuggestionNotificationPlan(
        long lifecycleRevision,
        Guid? withdrawSuggestionId,
        DesktopSuggestionNotificationPayload? publishPayload,
        DateTimeOffset? deferredUntilUtc,
        DesktopSuggestionNotificationSuppressionReason? suppressionReason)
    {
        if (lifecycleRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(lifecycleRevision));
        if (withdrawSuggestionId == Guid.Empty)
            throw new ArgumentException(
                "WithdrawSuggestionId 不能是 Guid.Empty。",
                nameof(withdrawSuggestionId));
        if (publishPayload is not null && deferredUntilUtc is not null)
        {
            throw new ArgumentException(
                "Publish 与 Deferred 不能同时存在。",
                nameof(publishPayload));
        }
        if (deferredUntilUtc is not null
            && suppressionReason != DesktopSuggestionNotificationSuppressionReason.Cooldown)
        {
            throw new ArgumentException(
                "Deferred 必须使用 Cooldown suppression reason。",
                nameof(suppressionReason));
        }
        if (publishPayload is not null && suppressionReason is not null)
        {
            throw new ArgumentException(
                "Publish 计划不得同时携带 SuppressionReason。",
                nameof(suppressionReason));
        }

        LifecycleRevision = lifecycleRevision;
        WithdrawSuggestionId = withdrawSuggestionId;
        PublishPayload = publishPayload;
        DeferredUntilUtc = deferredUntilUtc?.ToUniversalTime();
        SuppressionReason = suppressionReason;
    }
}

/// <summary>
/// M3-04A 纯通知策略。
/// 只消费 Suggestion lifecycle，不读取 Diff / Shell / history，也不接触 Writer。
/// 状态只在 sink 成功后通过 Acknowledge* 推进，sink 失败不会“假装已经通知”。
/// </summary>
public sealed class DesktopSuggestionNotificationPolicy
{
    private readonly object _gate = new();
    private readonly DesktopSuggestionNotificationPolicySettings _settings;

    private Guid? _surfacedSuggestionId;
    private Guid? _lastPublishedSuggestionId;
    private DateTimeOffset? _lastPublishedAtUtc;

    public DesktopSuggestionNotificationPolicy(
        DesktopSuggestionNotificationPolicySettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public DesktopSuggestionNotificationPolicyState State
    {
        get
        {
            lock (_gate)
                return StateUnsafe();
        }
    }

    public DesktopSuggestionNotificationPlan Evaluate(
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        nowUtc = nowUtc.ToUniversalTime();

        lock (_gate)
        {
            var activeSuggestion = lifecycle.IsActionablePresentation
                ? lifecycle.CurrentSuggestion
                : null;

            var withdraw = _surfacedSuggestionId is not null
                && _surfacedSuggestionId != activeSuggestion?.SuggestionId
                    ? _surfacedSuggestionId
                    : null;

            if (activeSuggestion is null)
            {
                var reason = lifecycle.State == DesktopLayoutSuggestionPresentationState.Suspended
                    ? DesktopSuggestionNotificationSuppressionReason.SuggestionSuspended
                    : DesktopSuggestionNotificationSuppressionReason.NoActiveSuggestion;

                return new DesktopSuggestionNotificationPlan(
                    lifecycle.Revision,
                    withdraw,
                    null,
                    null,
                    reason);
            }

            if (_lastPublishedSuggestionId == activeSuggestion.SuggestionId)
            {
                return new DesktopSuggestionNotificationPlan(
                    lifecycle.Revision,
                    withdraw,
                    null,
                    null,
                    DesktopSuggestionNotificationSuppressionReason.AlreadyPublished);
            }

            if (_lastPublishedAtUtc is not null)
            {
                var eligibleAt = _lastPublishedAtUtc.Value + _settings.MinimumPublishInterval;
                if (nowUtc < eligibleAt)
                {
                    return new DesktopSuggestionNotificationPlan(
                        lifecycle.Revision,
                        withdraw,
                        null,
                        eligibleAt,
                        DesktopSuggestionNotificationSuppressionReason.Cooldown);
                }
            }

            return new DesktopSuggestionNotificationPlan(
                lifecycle.Revision,
                withdraw,
                DesktopSuggestionNotificationPayload.FromLifecycle(lifecycle),
                null,
                null);
        }
    }

    public DesktopSuggestionNotificationPolicyState AcknowledgePublished(
        DesktopSuggestionNotificationPayload payload,
        DateTimeOffset publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);
        publishedAtUtc = publishedAtUtc.ToUniversalTime();

        lock (_gate)
        {
            _surfacedSuggestionId = payload.SuggestionId;
            _lastPublishedSuggestionId = payload.SuggestionId;
            _lastPublishedAtUtc = _lastPublishedAtUtc is null
                || publishedAtUtc > _lastPublishedAtUtc.Value
                    ? publishedAtUtc
                    : _lastPublishedAtUtc;

            return StateUnsafe();
        }
    }

    public DesktopSuggestionNotificationPolicyState AcknowledgeWithdrawn(Guid suggestionId)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("suggestionId 不能为空。", nameof(suggestionId));

        lock (_gate)
        {
            if (_surfacedSuggestionId == suggestionId)
                _surfacedSuggestionId = null;

            return StateUnsafe();
        }
    }

    private DesktopSuggestionNotificationPolicyState StateUnsafe() =>
        new(
            _surfacedSuggestionId,
            _lastPublishedSuggestionId,
            _lastPublishedAtUtc);
}
