using DesktopAgent.Application.DesktopSuggestions;

namespace DesktopAgent.Application.DesktopNotifications;

public enum DesktopSuggestionNotificationActionKind
{
    OpenSuggestion = 0,
    IgnoreSuggestion = 1,
}

/// <summary>
/// Windows notification / tray 等展示层允许回传的只读交互。
/// SuggestionId 只用于定位建议，不构成任何执行授权。
/// </summary>
public sealed record DesktopSuggestionNotificationInteraction
{
    public Guid SuggestionId { get; }
    public DesktopSuggestionNotificationActionKind Action { get; }

    public DesktopSuggestionNotificationInteraction(
        Guid suggestionId,
        DesktopSuggestionNotificationActionKind action)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("SuggestionId 不能为空。", nameof(suggestionId));
        if (!Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action));

        SuggestionId = suggestionId;
        Action = action;
    }
}

/// <summary>
/// Notification 展示层可见的最小只读数据。
/// 刻意不暴露 M2 Candidate / ExecutionPlan，避免通知基础设施获得执行计划。
/// </summary>
public sealed record DesktopSuggestionNotificationPayload
{
    public Guid SuggestionId { get; }
    public long LifecycleRevision { get; }
    public DateTimeOffset SuggestionCreatedAtUtc { get; }
    public Guid TemplateId { get; }
    public string TargetDeviceName { get; }
    public int MoveRequiredCount { get; }
    public int ExecutionStepCount { get; }
    public bool UsesTemporaryStaging { get; }
    public Guid SourceObservationId { get; }

    public DesktopSuggestionNotificationPayload(
        Guid suggestionId,
        long lifecycleRevision,
        DateTimeOffset suggestionCreatedAtUtc,
        Guid templateId,
        string targetDeviceName,
        int moveRequiredCount,
        int executionStepCount,
        bool usesTemporaryStaging,
        Guid sourceObservationId)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("SuggestionId 不能为空。", nameof(suggestionId));
        if (lifecycleRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(lifecycleRevision));
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为空。", nameof(templateId));
        if (string.IsNullOrWhiteSpace(targetDeviceName))
            throw new ArgumentException("TargetDeviceName 不能为空。", nameof(targetDeviceName));
        if (moveRequiredCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(moveRequiredCount));
        if (executionStepCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(executionStepCount));
        if (sourceObservationId == Guid.Empty)
            throw new ArgumentException("SourceObservationId 不能为空。", nameof(sourceObservationId));

        SuggestionId = suggestionId;
        LifecycleRevision = lifecycleRevision;
        SuggestionCreatedAtUtc = suggestionCreatedAtUtc.ToUniversalTime();
        TemplateId = templateId;
        TargetDeviceName = targetDeviceName;
        MoveRequiredCount = moveRequiredCount;
        ExecutionStepCount = executionStepCount;
        UsesTemporaryStaging = usesTemporaryStaging;
        SourceObservationId = sourceObservationId;
    }

    public static DesktopSuggestionNotificationPayload FromLifecycle(
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        if (!lifecycle.IsActionablePresentation || lifecycle.CurrentSuggestion is null)
        {
            throw new ArgumentException(
                "只有 Active Suggestion lifecycle 才能转换为 notification payload。",
                nameof(lifecycle));
        }

        var suggestion = lifecycle.CurrentSuggestion;
        return new DesktopSuggestionNotificationPayload(
            suggestion.SuggestionId,
            lifecycle.Revision,
            suggestion.CreatedAtUtc,
            suggestion.TemplateId,
            suggestion.TargetDeviceName,
            suggestion.MoveRequiredCount,
            suggestion.ExecutionStepCount,
            suggestion.UsesTemporaryStaging,
            suggestion.SourceObservationId);
    }
}

/// <summary>
/// 平台通知输出 port。实现可以是 Windows App Notification、tray balloon 或测试 fake。
/// 只能展示/撤回通知，不存在执行 Suggestion 的方法。
/// </summary>
public interface IDesktopSuggestionNotificationSink
{
    event Action<DesktopSuggestionNotificationInteraction>? InteractionReceived;

    Task PublishAsync(
        DesktopSuggestionNotificationPayload payload,
        CancellationToken ct = default);

    Task WithdrawAsync(
        Guid suggestionId,
        CancellationToken ct = default);
}
