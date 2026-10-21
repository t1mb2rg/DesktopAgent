using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.WindowsInterop.DesktopAutomation;

public enum DesktopAutomationCoordinatorExecutionBridgeStatus
{
    SafetyGateNotReady = 0,
    ExecutionAlreadyInProgress = 1,
    SuggestionNoLongerCurrent = 2,
    RuntimeChangedSincePreflight = 3,
    RuntimeBlocked = 4,
    RecoveryStateUnavailable = 5,
    UnresolvedRecoveryPresent = 6,
    SafetyEvidenceMismatch = 7,
    CoordinatorReturned = 8,
}

/// <summary>
/// Coordinator 已用最新桌面读数生成 ExecutionPlan 后、进入 safety snapshot / Writer 链之前，
/// B03 同步执行的自动化 commit-point 审核记录。
/// </summary>
public sealed record DesktopAutomationCoordinatorPreWriteGateDecision
{
    public bool IsAllowed { get; }
    public Guid SuggestionId { get; }
    public long RuntimeRevision { get; }
    public int MoveRequiredCount { get; }
    public int ExecutionStepCount { get; }
    public bool UsesTemporaryStaging { get; }
    public IReadOnlyList<string> RejectionReasons { get; }

    public DesktopAutomationCoordinatorPreWriteGateDecision(
        bool isAllowed,
        Guid suggestionId,
        long runtimeRevision,
        int moveRequiredCount,
        int executionStepCount,
        bool usesTemporaryStaging,
        IEnumerable<string> rejectionReasons)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("SuggestionId 不能为空。", nameof(suggestionId));
        if (runtimeRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(runtimeRevision));
        if (moveRequiredCount < 0)
            throw new ArgumentOutOfRangeException(nameof(moveRequiredCount));
        if (executionStepCount < 0)
            throw new ArgumentOutOfRangeException(nameof(executionStepCount));
        ArgumentNullException.ThrowIfNull(rejectionReasons);

        var reasons = rejectionReasons.ToArray();
        if (reasons.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("RejectionReasons 不得包含空白项。", nameof(rejectionReasons));
        if (isAllowed && reasons.Length != 0)
            throw new ArgumentException("Allowed decision 不得携带 rejection reason。", nameof(rejectionReasons));
        if (!isAllowed && reasons.Length == 0)
            throw new ArgumentException("Rejected decision 必须携带 rejection reason。", nameof(rejectionReasons));

        IsAllowed = isAllowed;
        SuggestionId = suggestionId;
        RuntimeRevision = runtimeRevision;
        MoveRequiredCount = moveRequiredCount;
        ExecutionStepCount = executionStepCount;
        UsesTemporaryStaging = usesTemporaryStaging;
        RejectionReasons = Array.AsReadOnly(reasons);
    }
}

public sealed record DesktopAutomationCoordinatorExecutionBridgeResult
{
    public DesktopAutomationCoordinatorExecutionBridgeStatus Status { get; }
    public DesktopAutomationAutomaticSafetyGateResult SafetyGate { get; }
    public TargetDisplayLayoutExecutionRunResult? CoordinatorRun { get; }
    public DesktopAutomationCoordinatorPreWriteGateDecision? PreWriteGate { get; }
    public int? UnresolvedRecoveryCount { get; }
    public IReadOnlyList<string> RejectionReasons { get; }

    public bool CoordinatorWasInvoked => CoordinatorRun is not null;

    public DesktopAutomationCoordinatorExecutionBridgeResult(
        DesktopAutomationCoordinatorExecutionBridgeStatus status,
        DesktopAutomationAutomaticSafetyGateResult safetyGate,
        TargetDisplayLayoutExecutionRunResult? coordinatorRun = null,
        DesktopAutomationCoordinatorPreWriteGateDecision? preWriteGate = null,
        int? unresolvedRecoveryCount = null,
        IEnumerable<string>? rejectionReasons = null)
    {
        ArgumentNullException.ThrowIfNull(safetyGate);
        if (unresolvedRecoveryCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(unresolvedRecoveryCount));

        var reasons = (rejectionReasons ?? Array.Empty<string>()).ToArray();
        if (reasons.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("RejectionReasons 不得包含空白项。", nameof(rejectionReasons));

        if (status == DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned
            && coordinatorRun is null)
        {
            throw new ArgumentException(
                "CoordinatorReturned 必须携带原始 Coordinator run result。",
                nameof(coordinatorRun));
        }
        if (status != DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned
            && coordinatorRun is not null)
        {
            throw new ArgumentException(
                "Coordinator 尚未被调用的状态不得携带 run result。",
                nameof(coordinatorRun));
        }

        Status = status;
        SafetyGate = safetyGate;
        CoordinatorRun = coordinatorRun;
        PreWriteGate = preWriteGate;
        UnresolvedRecoveryCount = unresolvedRecoveryCount;
        RejectionReasons = Array.AsReadOnly(reasons);
    }
}

/// <summary>
/// M3-B03 唯一自动化执行桥。
///
/// 它不直接持有 IDesktopIconPositionWriter，而是把 B02 fresh RestorePlan 交给既有
/// TargetDisplayLayoutExecutionCoordinator.ExecuteWithScopeGateAsync()。
/// Coordinator 仍拥有唯一真实 Writer / safety snapshot / recovery / verification 链。
/// </summary>
public sealed class DesktopAutomationCoordinatorExecutionBridge : IDisposable
{
    private readonly TargetDisplayLayoutExecutionCoordinator _coordinator;
    private readonly DesktopLayoutSuggestionLifecycle _lifecycle;
    private readonly DesktopAutomationRuntimePolicy _runtimePolicy;
    private readonly IDesktopAutomationCandidateRiskEvidencePolicy _riskPolicy;
    private readonly IRecoverySessionStore _recoveryStore;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private bool _disposed;

    public DesktopAutomationCoordinatorExecutionBridge(
        TargetDisplayLayoutExecutionCoordinator coordinator,
        DesktopLayoutSuggestionLifecycle lifecycle,
        DesktopAutomationRuntimePolicy runtimePolicy,
        IDesktopAutomationCandidateRiskEvidencePolicy riskPolicy,
        IRecoverySessionStore recoveryStore)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _runtimePolicy = runtimePolicy ?? throw new ArgumentNullException(nameof(runtimePolicy));
        _riskPolicy = riskPolicy ?? throw new ArgumentNullException(nameof(riskPolicy));
        _recoveryStore = recoveryStore ?? throw new ArgumentNullException(nameof(recoveryStore));
    }

    public async Task<DesktopAutomationCoordinatorExecutionBridgeResult> ExecuteAsync(
        DesktopAutomationAutomaticSafetyGateResult safetyGate,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(safetyGate);
        ct.ThrowIfCancellationRequested();

        if (!safetyGate.MayEnterCoordinatorPreWriteGate)
        {
            return Reject(
                DesktopAutomationCoordinatorExecutionBridgeStatus.SafetyGateNotReady,
                safetyGate,
                "B02 safety gate 尚未达到 ReadyForCoordinatorPreWriteGate。");
        }

        if (!await _singleFlight.WaitAsync(0, ct))
        {
            return Reject(
                DesktopAutomationCoordinatorExecutionBridgeStatus.ExecutionAlreadyInProgress,
                safetyGate,
                "已有一条 M3 automatic execution 正在进行，本次不排队。请基于之后的新事实重新评估。");
        }

        try
        {
            var expectedLifecycle = safetyGate.Handoff.Preflight.SuggestionLifecycle;
            var expectedSuggestion = expectedLifecycle.CurrentSuggestion!;
            var expectedRuntimeRevision = safetyGate.Handoff.Preflight.Runtime.Revision;
            var freshCandidate = safetyGate.Handoff.FreshCandidate!;

            if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
            {
                return Reject(
                    DesktopAutomationCoordinatorExecutionBridgeStatus.SuggestionNoLongerCurrent,
                    safetyGate,
                    "B03 开始时 Suggestion lifecycle 已变化。请重新从 05C 开始评估。");
            }

            var currentRuntime = _runtimePolicy.Snapshot;
            if (currentRuntime.Revision != expectedRuntimeRevision)
            {
                return Reject(
                    DesktopAutomationCoordinatorExecutionBridgeStatus.RuntimeChangedSincePreflight,
                    safetyGate,
                    $"B03 开始时 Runtime Revision={currentRuntime.Revision}，预期 {expectedRuntimeRevision}。");
            }
            if (!currentRuntime.CanAttemptUnattendedExecution)
            {
                return Reject(
                    DesktopAutomationCoordinatorExecutionBridgeStatus.RuntimeBlocked,
                    safetyGate,
                    "B03 开始时 runtime 已不允许 unattended execution。");
            }

            if (!SafetyEvidenceMatchesFreshCandidate(safetyGate, expectedSuggestion.SuggestionId))
            {
                return Reject(
                    DesktopAutomationCoordinatorExecutionBridgeStatus.SafetyEvidenceMismatch,
                    safetyGate,
                    "B02 fresh risk evidence 与 B01 fresh Candidate 不一致。");
            }

            IReadOnlyList<RecoverySessionRecord> unresolved;
            try
            {
                unresolved = await _recoveryStore.ListUnresolvedAsync(50, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Reject(
                    DesktopAutomationCoordinatorExecutionBridgeStatus.RecoveryStateUnavailable,
                    safetyGate,
                    $"B03 进入 Coordinator 前读取 recovery state 失败：{ex.GetType().Name}: {ex.Message}");
            }

            if (unresolved.Count != 0)
            {
                return new DesktopAutomationCoordinatorExecutionBridgeResult(
                    DesktopAutomationCoordinatorExecutionBridgeStatus.UnresolvedRecoveryPresent,
                    safetyGate,
                    unresolvedRecoveryCount: unresolved.Count,
                    rejectionReasons: new[]
                    {
                        $"Coordinator 调用前发现 {unresolved.Count} 个 unresolved recovery session。"
                    });
            }

            if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
            {
                return Reject(
                    DesktopAutomationCoordinatorExecutionBridgeStatus.SuggestionNoLongerCurrent,
                    safetyGate,
                    "B03 recovery 查询期间 Suggestion lifecycle 发生变化。");
            }

            currentRuntime = _runtimePolicy.Snapshot;
            if (currentRuntime.Revision != expectedRuntimeRevision)
            {
                return Reject(
                    DesktopAutomationCoordinatorExecutionBridgeStatus.RuntimeChangedSincePreflight,
                    safetyGate,
                    "B03 recovery 查询期间 Runtime Revision 发生变化。");
            }
            if (!currentRuntime.CanAttemptUnattendedExecution)
            {
                return Reject(
                    DesktopAutomationCoordinatorExecutionBridgeStatus.RuntimeBlocked,
                    safetyGate,
                    "B03 recovery 查询结束时 runtime 已不允许 unattended execution。");
            }

            DesktopAutomationCoordinatorPreWriteGateDecision? preWriteDecision = null;
            var coordinatorRun = await _coordinator.ExecuteWithScopeGateAsync(
                freshCandidate.RestorePlan,
                (latestRestorePlan, latestExecutionPlan) =>
                {
                    preWriteDecision = EvaluateCoordinatorCommitPoint(
                        latestRestorePlan,
                        latestExecutionPlan,
                        freshCandidate.RestorePlan,
                        expectedLifecycle,
                        expectedSuggestion.SuggestionId,
                        expectedRuntimeRevision);

                    return preWriteDecision.IsAllowed
                        ? TargetDisplayLayoutExecutionScopeGate.GateResult.Allowed()
                        : TargetDisplayLayoutExecutionScopeGate.GateResult.Rejected(
                            preWriteDecision.RejectionReasons.ToList());
                },
                ct);

            return new DesktopAutomationCoordinatorExecutionBridgeResult(
                DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned,
                safetyGate,
                coordinatorRun,
                preWriteDecision,
                unresolvedRecoveryCount: 0);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private DesktopAutomationCoordinatorPreWriteGateDecision EvaluateCoordinatorCommitPoint(
        TargetDisplayLayoutRestorePlan latestRestorePlan,
        TargetDisplayLayoutExecutionPlan latestExecutionPlan,
        TargetDisplayLayoutRestorePlan expectedRestorePlan,
        DesktopLayoutSuggestionLifecycleSnapshot expectedLifecycle,
        Guid suggestionId,
        long expectedRuntimeRevision)
    {
        var reasons = new List<string>();

        if (!ReferenceEquals(latestRestorePlan, expectedRestorePlan))
            reasons.Add("Coordinator scope gate 收到的 RestorePlan 不是 B01 fresh RestorePlan 同一实例。");

        if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
            reasons.Add("Coordinator commit point 时 Suggestion lifecycle 已变化。");

        var runtime = _runtimePolicy.Snapshot;
        if (runtime.Revision != expectedRuntimeRevision)
            reasons.Add($"Coordinator commit point Runtime Revision={runtime.Revision}，预期 {expectedRuntimeRevision}。");
        if (!runtime.CanAttemptUnattendedExecution)
            reasons.Add($"Coordinator commit point EffectiveMode={runtime.EffectiveMode}，不允许 unattended execution。");

        if (latestRestorePlan.HasBlockingErrors)
            reasons.Add("Latest RestorePlan.HasBlockingErrors=true。");
        if (latestRestorePlan.MissingCount != 0)
            reasons.Add($"Latest RestorePlan.MissingCount={latestRestorePlan.MissingCount}，预期 0。");
        if (latestRestorePlan.BlockedCount != 0)
            reasons.Add($"Latest RestorePlan.BlockedCount={latestRestorePlan.BlockedCount}，预期 0。");
        if (latestRestorePlan.MoveRequiredCount <= 0)
            reasons.Add($"Latest RestorePlan.MoveRequiredCount={latestRestorePlan.MoveRequiredCount}，预期 > 0。");
        if (latestRestorePlan.IsNoOp)
            reasons.Add("Latest RestorePlan.IsNoOp=true。");

        if (!latestExecutionPlan.IsExecutable)
            reasons.Add("Latest ExecutionPlan.IsExecutable=false。");
        if (latestExecutionPlan.IsNoOp)
            reasons.Add("Latest ExecutionPlan.IsNoOp=true。");
        if (latestExecutionPlan.Steps.Count <= 0)
            reasons.Add("Latest ExecutionPlan.Steps.Count <= 0。");
        if (!string.Equals(
                latestExecutionPlan.TargetDeviceName,
                latestRestorePlan.TargetDeviceName,
                StringComparison.Ordinal))
        {
            reasons.Add("Latest ExecutionPlan target 与 RestorePlan target 不一致。");
        }

        if (reasons.Count == 0)
        {
            var evidence = new DesktopAutomationCandidateRiskEvidence(
                suggestionId,
                latestRestorePlan.MoveRequiredCount,
                latestExecutionPlan.Steps.Count,
                latestExecutionPlan.UsesTemporaryStaging);
            var assessment = _riskPolicy.Evaluate(evidence);
            ValidateRiskAssessmentMatchesEvidence(evidence, assessment);
            if (!assessment.WithinAutomationRiskBudget)
            {
                reasons.AddRange(
                    assessment.BlockReasons.Select(reason => $"LatestExecutionRisk:{reason}"));
            }
        }

        if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
            reasons.Add("Coordinator commit point risk 评估期间 Suggestion lifecycle 发生变化。");

        runtime = _runtimePolicy.Snapshot;
        if (runtime.Revision != expectedRuntimeRevision)
            reasons.Add("Coordinator commit point risk 评估期间 Runtime Revision 发生变化。");
        if (!runtime.CanAttemptUnattendedExecution)
            reasons.Add("Coordinator commit point risk 评估结束时 runtime 已不允许 unattended execution。");

        return new DesktopAutomationCoordinatorPreWriteGateDecision(
            reasons.Count == 0,
            suggestionId,
            runtime.Revision,
            Math.Max(0, latestRestorePlan.MoveRequiredCount),
            Math.Max(0, latestExecutionPlan.Steps.Count),
            latestExecutionPlan.UsesTemporaryStaging,
            reasons);
    }

    private static bool SafetyEvidenceMatchesFreshCandidate(
        DesktopAutomationAutomaticSafetyGateResult safetyGate,
        Guid suggestionId)
    {
        var candidate = safetyGate.Handoff.FreshCandidate!;
        var evidence = safetyGate.FreshRiskAssessment?.Evidence;
        return evidence is not null
            && evidence.SuggestionId == suggestionId
            && evidence.MoveRequiredCount == candidate.MoveRequiredCount
            && evidence.ExecutionStepCount == candidate.ExecutionStepCount
            && evidence.UsesTemporaryStaging == candidate.UsesTemporaryStaging;
    }

    private static void ValidateRiskAssessmentMatchesEvidence(
        DesktopAutomationCandidateRiskEvidence expected,
        DesktopAutomationCandidateRiskAssessment actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var evidence = actual.Evidence;
        if (evidence.SuggestionId != expected.SuggestionId
            || evidence.MoveRequiredCount != expected.MoveRequiredCount
            || evidence.ExecutionStepCount != expected.ExecutionStepCount
            || evidence.UsesTemporaryStaging != expected.UsesTemporaryStaging)
        {
            throw new InvalidOperationException(
                "Coordinator commit-point risk policy 返回了与最新 ExecutionPlan 不一致的 evidence。Fail closed。");
        }
    }

    private static bool MatchesExpectedActiveLifecycle(
        DesktopLayoutSuggestionLifecycleSnapshot current,
        DesktopLayoutSuggestionLifecycleSnapshot expected) =>
        current.State == DesktopLayoutSuggestionPresentationState.Active
        && current.CurrentSuggestion is not null
        && expected.State == DesktopLayoutSuggestionPresentationState.Active
        && expected.CurrentSuggestion is not null
        && current.Revision == expected.Revision
        && current.CurrentSuggestion.SuggestionId == expected.CurrentSuggestion.SuggestionId;

    private static DesktopAutomationCoordinatorExecutionBridgeResult Reject(
        DesktopAutomationCoordinatorExecutionBridgeStatus status,
        DesktopAutomationAutomaticSafetyGateResult safetyGate,
        string reason) =>
        new(status, safetyGate, rejectionReasons: new[] { reason });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _singleFlight.Dispose();
    }
}
