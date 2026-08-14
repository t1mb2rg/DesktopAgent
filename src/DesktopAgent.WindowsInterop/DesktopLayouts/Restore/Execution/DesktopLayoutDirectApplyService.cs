using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

public enum DesktopLayoutDirectApplyOutcome
{
    Completed = 0,
    CompletedNoOp = 1,
    PreparationNotReady = 2,
    UnresolvedRecoveryBlocked = 3,
    FreshObservationUnavailable = 4,
    FreshCandidateRejected = 5,
    AuthorizationTypeChanged = 6,
    PreviewChanged = 7,
    ScopeRejected = 8,
    ExecutionFailed = 9,
}

/// <summary>
/// 产品 Direct Apply 的受控结果。Writer 是否真正开始，仍以 ExecutionResult 为最终事实来源。
/// </summary>
public sealed record DesktopLayoutDirectApplyResult
{
    public DesktopLayoutDirectApplyOutcome Outcome { get; }
    public TargetDisplayLayoutExecutionRunResult? ExecutionResult { get; }
    public IReadOnlyList<string> Issues { get; }

    public bool IsSuccess => Outcome is
        DesktopLayoutDirectApplyOutcome.Completed or
        DesktopLayoutDirectApplyOutcome.CompletedNoOp;

    public bool RequiresManualRecovery => ExecutionResult?.RequiresManualRecovery == true;
    public bool IsPositionUncertain => ExecutionResult?.IsPositionUncertain == true;

    public DesktopLayoutDirectApplyResult(
        DesktopLayoutDirectApplyOutcome outcome,
        TargetDisplayLayoutExecutionRunResult? executionResult,
        IReadOnlyList<string>? issues = null)
    {
        Outcome = outcome;
        ExecutionResult = executionResult;
        Issues = Array.AsReadOnly((issues ?? Array.Empty<string>()).ToArray());
    }

    internal static DesktopLayoutDirectApplyResult Rejected(
        DesktopLayoutDirectApplyOutcome outcome,
        params string[] issues)
        => new(outcome, null, issues);

    internal static DesktopLayoutDirectApplyResult FromExecution(
        TargetDisplayLayoutExecutionRunResult executionResult)
    {
        ArgumentNullException.ThrowIfNull(executionResult);

        if (executionResult.IsSuccess)
        {
            return new DesktopLayoutDirectApplyResult(
                executionResult.IsNoOp
                    ? DesktopLayoutDirectApplyOutcome.CompletedNoOp
                    : DesktopLayoutDirectApplyOutcome.Completed,
                executionResult);
        }

        var issues = new List<string>();
        if (!string.IsNullOrWhiteSpace(executionResult.ForwardFailureMessage))
            issues.Add(executionResult.ForwardFailureMessage);
        if (!string.IsNullOrWhiteSpace(executionResult.RecoveryFailureMessage))
            issues.Add(executionResult.RecoveryFailureMessage);

        return new DesktopLayoutDirectApplyResult(
            DesktopLayoutDirectApplyOutcome.ExecutionFailed,
            executionResult,
            issues);
    }
}

/// <summary>
/// M5-07.7 产品级 Direct User layout apply 边界。
///
/// WinUI 只提交已经由 Application 层冻结的用户确认对象；本服务独占真实 Writer 组装、
/// unresolved recovery preflight、fresh observation、fresh Candidate、语义比较、ScopeGate 与 Coordinator。
/// </summary>
public sealed class DesktopLayoutDirectApplyService
{
    private readonly IDesktopObservationSource _observationSource;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;
    private readonly IRecoverySessionStore _recoveryStore;
    private readonly Func<TargetDisplayLayoutRestorePlan, bool, CancellationToken,
        Task<TargetDisplayLayoutExecutionRunResult>> _executeAuthorizedAsync;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    /// <summary>
    /// 产品构造器。调用方只提供持久化接口；真实 Shell Writer 和 Coordinator 在 WindowsInterop 内部组装。
    /// </summary>
    public DesktopLayoutDirectApplyService(
        IDesktopLayoutSnapshotStore snapshotStore,
        IRecoverySessionStore recoveryStore,
        TimeProvider? timeProvider = null)
    {
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _recoveryStore = recoveryStore ?? throw new ArgumentNullException(nameof(recoveryStore));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var topologyReader = new NativeDisplayTopologyReader();
        var iconReader = new DesktopShellIconReader();
        var capabilitiesReader = new DesktopShellIconPositioningCapabilitiesReader();
        var positionWriter = new DesktopShellIconPositionWriter();
        var staExecutor = new StaThreadExecutor();
        var snapshotFactory = new DesktopLayoutSnapshotFactory(_timeProvider);

        _observationSource = new WindowsDesktopObservationSource(
            topologyReader,
            iconReader,
            capabilitiesReader,
            staExecutor,
            _timeProvider);

        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            topologyReader,
            iconReader,
            positionWriter,
            staExecutor,
            new DefaultSafetySnapshotFactory(snapshotFactory),
            snapshotStore,
            recoveryStore);

        _executeAuthorizedAsync = (restorePlan, usesTemporaryStaging, ct) =>
            coordinator.ExecuteWithScopeGateAsync(
                restorePlan,
                SelectAuthorizedScopeGate(usesTemporaryStaging),
                ct);
    }

    /// <summary>
    /// 测试/组合入口。仍然由本服务执行 preflight、fresh replan 与语义校验；
    /// 仅替换 observation 和最终受控执行委托。
    /// </summary>
    internal DesktopLayoutDirectApplyService(
        IDesktopObservationSource observationSource,
        IDesktopLayoutSnapshotStore snapshotStore,
        IRecoverySessionStore recoveryStore,
        Func<TargetDisplayLayoutRestorePlan, bool, CancellationToken,
            Task<TargetDisplayLayoutExecutionRunResult>> executeAuthorizedAsync,
        TimeProvider? timeProvider = null)
    {
        _observationSource = observationSource
            ?? throw new ArgumentNullException(nameof(observationSource));
        _snapshotStore = snapshotStore
            ?? throw new ArgumentNullException(nameof(snapshotStore));
        _recoveryStore = recoveryStore
            ?? throw new ArgumentNullException(nameof(recoveryStore));
        _executeAuthorizedAsync = executeAuthorizedAsync
            ?? throw new ArgumentNullException(nameof(executeAuthorizedAsync));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DesktopLayoutDirectApplyResult> ApplyAsync(
        DesktopLayoutDirectApplyPreparation confirmedPreparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedPreparation);

        if (!confirmedPreparation.IsReady || confirmedPreparation.Candidate is null)
        {
            return DesktopLayoutDirectApplyResult.Rejected(
                DesktopLayoutDirectApplyOutcome.PreparationNotReady,
                "用户确认对象没有可执行 Candidate。请重新预览。");
        }

        await _applyLock.WaitAsync(cancellationToken);
        try
        {
            await _snapshotStore.InitializeAsync(cancellationToken);
            await _recoveryStore.InitializeAsync(cancellationToken);

            // 1. 旧恢复会话未解决时 fail-closed。Persistence 只提供候选，业务层仍按 Core
            // RecoverySessionDisposition 重新投影，避免持久化查询获得最终行为解释权。
            var unresolvedCandidates = await _recoveryStore.ListUnresolvedAsync(50, cancellationToken);
            var unresolved = unresolvedCandidates
                .Where(record => record.GetDisposition().IsUnresolved)
                .Take(1)
                .ToArray();
            if (unresolved.Length != 0)
            {
                return DesktopLayoutDirectApplyResult.Rejected(
                    DesktopLayoutDirectApplyOutcome.UnresolvedRecoveryBlocked,
                    $"检测到未解决恢复会话 {unresolved[0].SessionId:D}，新的真实桌面写入已暂停。");
            }

            var confirmedCandidate = confirmedPreparation.Candidate;
            var confirmedUsesTemporaryStaging = confirmedCandidate.UsesTemporaryStaging;
            var authorizedScopeGate = SelectAuthorizedScopeGate(confirmedUsesTemporaryStaging);

            // 2. 用户确认时的 Candidate 自己先过一次对应 ScopeGate。
            var confirmedGate = authorizedScopeGate(
                confirmedCandidate.RestorePlan,
                confirmedCandidate.ExecutionPlan);
            if (!confirmedGate.IsAllowed)
            {
                return DesktopLayoutDirectApplyResult.Rejected(
                    DesktopLayoutDirectApplyOutcome.ScopeRejected,
                    confirmedGate.RejectionReasons.ToArray());
            }

            // 3. fresh DesktopObservation。WindowsDesktopObservationSource 自己负责
            // topology 双读、完整 Shell 读取、StableId 唯一性和 MonitorDeviceName 归属。
            var freshCapture = await _observationSource.CaptureAsync(cancellationToken);
            if (!freshCapture.IsCaptured || freshCapture.Observation is null)
            {
                var issues = freshCapture.Issues.Count == 0
                    ? new[] { "无法建立 fresh DesktopObservation。" }
                    : freshCapture.Issues.Select(issue => issue.Message).ToArray();
                return DesktopLayoutDirectApplyResult.Rejected(
                    DesktopLayoutDirectApplyOutcome.FreshObservationUnavailable,
                    issues);
            }

            // 4. 必须使用用户实际确认过的 frozen Draft + explicit-inbound 白名单重建，
            // 不从 SQLite 重读旧模板，也不允许 fresh replan 偷偷增删跨屏授权。
            var freshPreparation = DesktopLayoutDirectApplyPreparationService.Create(
                confirmedPreparation.FrozenDraft,
                freshCapture.Observation,
                confirmedPreparation.SelectedTargetDeviceName,
                _timeProvider,
                confirmedPreparation.ExplicitInboundStableIds);

            if (!freshPreparation.IsReady || freshPreparation.Candidate is null)
            {
                var issues = freshPreparation.CandidateBuildResult.Issues.Count == 0
                    ? new[] { "fresh Candidate 构建失败。" }
                    : freshPreparation.CandidateBuildResult.Issues
                        .Select(issue => $"[{issue.Code}] {issue.Message}")
                        .ToArray();
                return DesktopLayoutDirectApplyResult.Rejected(
                    DesktopLayoutDirectApplyOutcome.FreshCandidateRejected,
                    issues);
            }

            var freshCandidate = freshPreparation.Candidate;

            // 5. 授权类型冻结。fresh replan 不得从 no-staging 偷换成 staging，反之亦然。
            if (freshCandidate.UsesTemporaryStaging != confirmedUsesTemporaryStaging)
            {
                return DesktopLayoutDirectApplyResult.Rejected(
                    DesktopLayoutDirectApplyOutcome.AuthorizationTypeChanged,
                    "fresh Candidate 的 staging/no-staging 类型与用户确认时不同。请重新预览并重新确认。");
            }

            // 6. confirmed ↔ fresh 做完整语义比较；任何影响用户确认/执行的变化都 fail-closed。
            var comparison = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(
                confirmedCandidate,
                freshCandidate);
            if (!comparison.IsEquivalent)
            {
                return new DesktopLayoutDirectApplyResult(
                    DesktopLayoutDirectApplyOutcome.PreviewChanged,
                    null,
                    comparison.Differences);
            }

            // 7. 使用用户确认时冻结的同一类 ScopeGate 再审 fresh Candidate。
            var freshGate = authorizedScopeGate(
                freshCandidate.RestorePlan,
                freshCandidate.ExecutionPlan);
            if (!freshGate.IsAllowed)
            {
                return DesktopLayoutDirectApplyResult.Rejected(
                    DesktopLayoutDirectApplyOutcome.ScopeRejected,
                    freshGate.RejectionReasons.ToArray());
            }

            // 到这里仍然是 0 Writer。允许调用 Coordinator 前最后响应一次取消。
            cancellationToken.ThrowIfCancellationRequested();

            // 8. 用户已经在产品层明确确认，因此进入 Coordinator 后不允许 UI 取消打断 Writer 边界。
            // Coordinator 仍会再次读取当前桌面、重建 ExecutionPlan、再次执行同一 ScopeGate，
            // 并在第一笔 Writer 前保存+回读 safety snapshot / recovery checkpoint。
            var executionResult = await _executeAuthorizedAsync(
                freshCandidate.RestorePlan,
                confirmedUsesTemporaryStaging,
                CancellationToken.None);

            return DesktopLayoutDirectApplyResult.FromExecution(executionResult);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private static Func<
        TargetDisplayLayoutRestorePlan,
        TargetDisplayLayoutExecutionPlan,
        TargetDisplayLayoutExecutionScopeGate.GateResult> SelectAuthorizedScopeGate(
        bool usesTemporaryStaging)
        => usesTemporaryStaging
            ? TargetDisplayLayoutStagingExecutionScopeGate.Evaluate
            : TargetDisplayLayoutExecutionScopeGate.EvaluateNoStagingBatch;
}
