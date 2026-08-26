using DesktopAgent.Core.DesktopLayouts.Workspaces;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 一次 Provider 逻辑工作区规划请求。Workspace 与失败反馈均不包含 Writer authority。
/// </summary>
public sealed record DesktopLogicalWorkspacePlanningRequest
{
    public const int MaxUserGuidanceChars = 16 * 1024;

    public DesktopObservedLogicalWorkspace Workspace { get; }
    public string UserGuidance { get; }
    public DesktopDesiredWorkspacePlan? PreviousRejectedPlan { get; }
    public IReadOnlyList<DesktopDesiredWorkspacePlanIssue> ValidationIssues { get; }

    public DesktopLogicalWorkspacePlanningRequest(
        DesktopObservedLogicalWorkspace workspace,
        string userGuidance,
        DesktopDesiredWorkspacePlan? previousRejectedPlan = null,
        IEnumerable<DesktopDesiredWorkspacePlanIssue>? validationIssues = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(userGuidance))
            throw new ArgumentException("UserGuidance 不能为空。", nameof(userGuidance));
        if (userGuidance.Length > MaxUserGuidanceChars)
            throw new ArgumentException(
                $"UserGuidance 不能超过 {MaxUserGuidanceChars} 个字符。",
                nameof(userGuidance));

        var issueArray = validationIssues?.ToArray() ?? [];
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("ValidationIssues 不能包含 null。", nameof(validationIssues));
        if ((previousRejectedPlan is null) != (issueArray.Length == 0))
        {
            throw new ArgumentException(
                "PreviousRejectedPlan 与 ValidationIssues 必须同时提供或同时省略。");
        }
        if (previousRejectedPlan is not null &&
            (previousRejectedPlan.SourceObservationId != workspace.ObservationId ||
             !string.Equals(
                 previousRejectedPlan.TopologyFingerprint,
                 workspace.TopologyFingerprint,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 previousRejectedPlan.GridFingerprint,
                 workspace.GridFingerprint,
                 StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "PreviousRejectedPlan 必须绑定当前 Workspace 的 Observation、拓扑与网格。",
                nameof(previousRejectedPlan));
        }

        Workspace = workspace;
        UserGuidance = userGuidance.Trim();
        PreviousRejectedPlan = previousRejectedPlan;
        ValidationIssues = Array.AsReadOnly(issueArray);
    }
}

/// <summary>
/// Provider 单次规划结果。只有 ValidatedPlan 可以进入后续 Preview；RejectedPlan 只用于诊断和重试。
/// </summary>
public sealed record DesktopLogicalWorkspacePlanningProviderResult
{
    public IntelligenceProviderStatus Status { get; }
    public DesktopDesiredWorkspacePlan? ValidatedPlan { get; }
    public DesktopDesiredWorkspacePlan? RejectedPlan { get; }
    public IReadOnlyList<DesktopDesiredWorkspacePlanIssue> ValidationIssues { get; }
    public string? DiagnosticMessage { get; }
    public int? HttpStatusCode { get; }
    public bool Succeeded => Status == IntelligenceProviderStatus.Success && ValidatedPlan is not null;
    public bool CanRetryValidation =>
        Status == IntelligenceProviderStatus.InvalidResponse &&
        RejectedPlan is not null &&
        ValidationIssues.Count > 0;

    private DesktopLogicalWorkspacePlanningProviderResult(
        IntelligenceProviderStatus status,
        DesktopDesiredWorkspacePlan? validatedPlan,
        DesktopDesiredWorkspacePlan? rejectedPlan,
        IEnumerable<DesktopDesiredWorkspacePlanIssue>? validationIssues,
        string? diagnosticMessage,
        int? httpStatusCode)
    {
        var issueArray = validationIssues?.ToArray() ?? [];
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("ValidationIssues 不能包含 null。", nameof(validationIssues));

        Status = status;
        ValidatedPlan = validatedPlan;
        RejectedPlan = rejectedPlan;
        ValidationIssues = Array.AsReadOnly(issueArray);
        DiagnosticMessage = diagnosticMessage;
        HttpStatusCode = httpStatusCode;
    }

    public static DesktopLogicalWorkspacePlanningProviderResult Success(
        DesktopDesiredWorkspacePlan validatedPlan)
    {
        ArgumentNullException.ThrowIfNull(validatedPlan);
        return new(
            IntelligenceProviderStatus.Success,
            validatedPlan,
            null,
            null,
            null,
            null);
    }

    public static DesktopLogicalWorkspacePlanningProviderResult ValidationFailure(
        DesktopDesiredWorkspacePlan rejectedPlan,
        IEnumerable<DesktopDesiredWorkspacePlanIssue> validationIssues)
    {
        ArgumentNullException.ThrowIfNull(rejectedPlan);
        ArgumentNullException.ThrowIfNull(validationIssues);
        var issueArray = validationIssues.ToArray();
        if (issueArray.Length == 0)
            throw new ArgumentException("ValidationFailure 至少需要一个问题。", nameof(validationIssues));

        return new(
            IntelligenceProviderStatus.InvalidResponse,
            null,
            rejectedPlan,
            issueArray,
            "Provider Desired Workspace Plan 未通过本地严格校验。",
            null);
    }

    public static DesktopLogicalWorkspacePlanningProviderResult Failure(
        IntelligenceProviderStatus status,
        string? diagnosticMessage = null,
        int? httpStatusCode = null)
    {
        if (status == IntelligenceProviderStatus.Success)
            throw new ArgumentException("Failure 不能使用 Success 状态。", nameof(status));

        return new(status, null, null, null, diagnosticMessage, httpStatusCode);
    }
}

public interface IDesktopLogicalWorkspacePlanner
{
    Task<DesktopLogicalWorkspacePlanningProviderResult> PlanAsync(
        DesktopLogicalWorkspacePlanningRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 只对可结构化修复的 Validator 拒绝进行有限重试。它不生成 Preview、Candidate 或 Writer command。
/// </summary>
public sealed class DesktopLogicalWorkspacePlanningCoordinator
{
    public const int DefaultMaxValidationRetries = 2;

    private readonly IDesktopLogicalWorkspacePlanner _planner;
    private readonly int _maxValidationRetries;

    public DesktopLogicalWorkspacePlanningCoordinator(
        IDesktopLogicalWorkspacePlanner planner,
        int maxValidationRetries = DefaultMaxValidationRetries)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        if (maxValidationRetries < 0 || maxValidationRetries > 5)
            throw new ArgumentOutOfRangeException(
                nameof(maxValidationRetries),
                "MaxValidationRetries 必须位于 [0, 5]。");

        _maxValidationRetries = maxValidationRetries;
    }

    public async Task<DesktopLogicalWorkspacePlanningRunResult> PlanAsync(
        DesktopObservedLogicalWorkspace workspace,
        string userGuidance,
        CancellationToken cancellationToken = default)
    {
        var request = new DesktopLogicalWorkspacePlanningRequest(workspace, userGuidance);

        for (var attempt = 1; ; attempt++)
        {
            var result = await _planner.PlanAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.CanRetryValidation || attempt > _maxValidationRetries)
                return new DesktopLogicalWorkspacePlanningRunResult(result, attempt);

            request = new DesktopLogicalWorkspacePlanningRequest(
                workspace,
                userGuidance,
                result.RejectedPlan,
                result.ValidationIssues);
        }
    }
}

public sealed record DesktopLogicalWorkspacePlanningRunResult
{
    public DesktopLogicalWorkspacePlanningProviderResult FinalResult { get; }
    public int AttemptCount { get; }
    public bool Succeeded => FinalResult.Succeeded;

    public DesktopLogicalWorkspacePlanningRunResult(
        DesktopLogicalWorkspacePlanningProviderResult finalResult,
        int attemptCount)
    {
        FinalResult = finalResult ?? throw new ArgumentNullException(nameof(finalResult));
        if (attemptCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        AttemptCount = attemptCount;
    }
}
