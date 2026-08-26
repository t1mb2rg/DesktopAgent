using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

public enum DesktopLogicalWorkspacePlanningSessionStatus
{
    Success = 0,
    ProjectionFailed = 1,
    PlanningFailed = 2,
}

/// <summary>
/// 一次已验证逻辑工作区规划会话。公开表面只包含 Provider-safe Workspace 与 Validated Plan；
/// StableId、设备身份和像素校准继续封装在 Application 内部 authority sidecar 中。
/// </summary>
public sealed class DesktopLogicalWorkspacePlanningSession
{
    public DesktopObservedLogicalWorkspace Workspace { get; }
    public DesktopDesiredWorkspacePlan ValidatedPlan { get; }
    public int ProviderAttemptCount { get; }

    internal DesktopLogicalWorkspaceProjectionAuthority Authority { get; }

    internal DesktopLogicalWorkspacePlanningSession(
        DesktopObservedLogicalWorkspace workspace,
        DesktopDesiredWorkspacePlan validatedPlan,
        int providerAttemptCount,
        DesktopLogicalWorkspaceProjectionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(validatedPlan);
        ArgumentNullException.ThrowIfNull(authority);
        if (providerAttemptCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(providerAttemptCount));
        if (authority.ObservationId != workspace.ObservationId)
        {
            throw new ArgumentException(
                "Authority 必须绑定当前 Workspace 的 Observation。",
                nameof(authority));
        }

        var validation = DesktopDesiredWorkspacePlanValidator.Validate(workspace, validatedPlan);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "PlanningSession 只能封装通过严格本地校验的 Plan。",
                nameof(validatedPlan));
        }

        Workspace = workspace;
        ValidatedPlan = validatedPlan;
        ProviderAttemptCount = providerAttemptCount;
        Authority = authority;
    }
}

/// <summary>
/// Fresh Observation 到已验证逻辑终态的原子结果。失败结果不会携带 Session authority。
/// </summary>
public sealed class DesktopLogicalWorkspacePlanningSessionResult
{
    public DesktopLogicalWorkspacePlanningSessionStatus Status { get; }
    public DesktopLogicalWorkspacePlanningSession? Session { get; }
    public IReadOnlyList<DesktopLogicalWorkspaceProjectionIssue> ProjectionIssues { get; }
    public DesktopLogicalWorkspacePlanningRunResult? PlanningRun { get; }
    public bool Succeeded =>
        Status == DesktopLogicalWorkspacePlanningSessionStatus.Success && Session is not null;

    private DesktopLogicalWorkspacePlanningSessionResult(
        DesktopLogicalWorkspacePlanningSessionStatus status,
        DesktopLogicalWorkspacePlanningSession? session,
        IEnumerable<DesktopLogicalWorkspaceProjectionIssue>? projectionIssues,
        DesktopLogicalWorkspacePlanningRunResult? planningRun)
    {
        var issueArray = projectionIssues?.ToArray() ?? [];
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("ProjectionIssues 不能包含 null。", nameof(projectionIssues));

        var isSuccess =
            status == DesktopLogicalWorkspacePlanningSessionStatus.Success &&
            session is not null &&
            issueArray.Length == 0 &&
            planningRun is not null &&
            planningRun.Succeeded;
        var isProjectionFailure =
            status == DesktopLogicalWorkspacePlanningSessionStatus.ProjectionFailed &&
            session is null &&
            issueArray.Length > 0 &&
            planningRun is null;
        var isPlanningFailure =
            status == DesktopLogicalWorkspacePlanningSessionStatus.PlanningFailed &&
            session is null &&
            issueArray.Length == 0 &&
            planningRun is not null &&
            !planningRun.Succeeded;
        if (!isSuccess && !isProjectionFailure && !isPlanningFailure)
            throw new ArgumentException("PlanningSessionResult 状态组合无效。");

        Status = status;
        Session = session;
        ProjectionIssues = Array.AsReadOnly(issueArray);
        PlanningRun = planningRun;
    }

    internal static DesktopLogicalWorkspacePlanningSessionResult Success(
        DesktopLogicalWorkspacePlanningSession session,
        DesktopLogicalWorkspacePlanningRunResult planningRun) =>
        new(
            DesktopLogicalWorkspacePlanningSessionStatus.Success,
            session,
            null,
            planningRun);

    internal static DesktopLogicalWorkspacePlanningSessionResult ProjectionFailure(
        IEnumerable<DesktopLogicalWorkspaceProjectionIssue> issues) =>
        new(
            DesktopLogicalWorkspacePlanningSessionStatus.ProjectionFailed,
            null,
            issues,
            null);

    internal static DesktopLogicalWorkspacePlanningSessionResult PlanningFailure(
        DesktopLogicalWorkspacePlanningRunResult planningRun) =>
        new(
            DesktopLogicalWorkspacePlanningSessionStatus.PlanningFailed,
            null,
            null,
            planningRun);
}

/// <summary>
/// 将一次 fresh read-only Observation 投影、规划并严格验收为不透明 Session。
/// 不生成 Draft、Preview、Candidate、Execution Plan 或 Writer command。
/// </summary>
public sealed class DesktopLogicalWorkspacePlanningSessionCoordinator
{
    private readonly DesktopLogicalWorkspacePlanningCoordinator _planningCoordinator;

    public DesktopLogicalWorkspacePlanningSessionCoordinator(
        IDesktopLogicalWorkspacePlanner planner,
        int maxValidationRetries = DesktopLogicalWorkspacePlanningCoordinator.DefaultMaxValidationRetries)
    {
        _planningCoordinator = new DesktopLogicalWorkspacePlanningCoordinator(
            planner,
            maxValidationRetries);
    }

    public async Task<DesktopLogicalWorkspacePlanningSessionResult> PlanAsync(
        DesktopObservation freshObservation,
        string userGuidance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(freshObservation);

        var projection = DesktopLogicalWorkspaceProjector.Project(freshObservation);
        if (!projection.Succeeded)
            return DesktopLogicalWorkspacePlanningSessionResult.ProjectionFailure(projection.Issues);

        var workspace = projection.Workspace!;
        var planningRun = await _planningCoordinator
            .PlanAsync(workspace, userGuidance, cancellationToken)
            .ConfigureAwait(false);
        if (!planningRun.Succeeded)
            return DesktopLogicalWorkspacePlanningSessionResult.PlanningFailure(planningRun);

        var claimedPlan = planningRun.FinalResult.ValidatedPlan!;
        var boundaryValidation = DesktopDesiredWorkspacePlanValidator.Validate(workspace, claimedPlan);
        if (!boundaryValidation.IsValid)
        {
            var rejectedResult = DesktopLogicalWorkspacePlanningProviderResult.ValidationFailure(
                claimedPlan,
                boundaryValidation.Issues);
            return DesktopLogicalWorkspacePlanningSessionResult.PlanningFailure(
                new DesktopLogicalWorkspacePlanningRunResult(
                    rejectedResult,
                    planningRun.AttemptCount));
        }

        var session = new DesktopLogicalWorkspacePlanningSession(
            workspace,
            claimedPlan,
            planningRun.AttemptCount,
            projection.Authority!);
        return DesktopLogicalWorkspacePlanningSessionResult.Success(session, planningRun);
    }
}
