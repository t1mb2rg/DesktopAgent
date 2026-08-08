using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;

namespace DesktopAgent.Application.DesktopAutomation;

public enum DesktopAutomationAutomaticSafetyGateStatus
{
    HandoffNotReady = 0,
    SuggestionNoLongerCurrent = 1,
    RuntimeChangedSincePreflight = 2,
    RuntimeBlocked = 3,
    RecoveryStateUnavailable = 4,
    UnresolvedRecoveryPresent = 5,
    CandidateInvariantRejected = 6,
    FreshRiskBudgetBlocked = 7,
    ReadyForCoordinatorPreWriteGate = 8,
}

/// <summary>
/// B02 对 B01 fresh Candidate 的 Application 层最终自动化门。
/// ReadyForCoordinatorPreWriteGate 只允许进入 B03 / Coordinator 的 pre-writer gate，仍不是 Writer 授权。
/// </summary>
public sealed record DesktopAutomationAutomaticSafetyGateResult
{
    public DesktopAutomationAutomaticSafetyGateStatus Status { get; }
    public DesktopAutomationFreshExecutionHandoffResult Handoff { get; }
    public DesktopAutomationRuntimeSnapshot Runtime { get; }
    public DesktopAutomationCandidateRiskAssessment? FreshRiskAssessment { get; }
    public int? UnresolvedRecoveryCount { get; }
    public IReadOnlyList<string> RejectionReasons { get; }

    public bool MayEnterCoordinatorPreWriteGate =>
        Status == DesktopAutomationAutomaticSafetyGateStatus.ReadyForCoordinatorPreWriteGate;

    public DesktopAutomationAutomaticSafetyGateResult(
        DesktopAutomationAutomaticSafetyGateStatus status,
        DesktopAutomationFreshExecutionHandoffResult handoff,
        DesktopAutomationRuntimeSnapshot runtime,
        DesktopAutomationCandidateRiskAssessment? freshRiskAssessment = null,
        int? unresolvedRecoveryCount = null,
        IEnumerable<string>? rejectionReasons = null)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(runtime);
        if (unresolvedRecoveryCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(unresolvedRecoveryCount));

        var reasons = (rejectionReasons ?? Array.Empty<string>()).ToArray();
        if (reasons.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("RejectionReasons 不得包含空白项。", nameof(rejectionReasons));

        if (status == DesktopAutomationAutomaticSafetyGateStatus.ReadyForCoordinatorPreWriteGate)
        {
            if (!handoff.MayProceedToAutomaticSafetyGate
                || !runtime.CanAttemptUnattendedExecution
                || freshRiskAssessment is null
                || !freshRiskAssessment.WithinAutomationRiskBudget
                || unresolvedRecoveryCount != 0
                || reasons.Length != 0)
            {
                throw new ArgumentException(
                    "ReadyForCoordinatorPreWriteGate 必须满足 handoff/runtime/risk/recovery 全部通过。",
                    nameof(status));
            }
        }

        Status = status;
        Handoff = handoff;
        Runtime = runtime;
        FreshRiskAssessment = freshRiskAssessment;
        UnresolvedRecoveryCount = unresolvedRecoveryCount;
        RejectionReasons = Array.AsReadOnly(reasons);
    }
}

public sealed class DesktopAutomationAutomaticSafetyGate
{
    private readonly DesktopLayoutSuggestionLifecycle _lifecycle;
    private readonly DesktopAutomationRuntimePolicy _runtimePolicy;
    private readonly IDesktopAutomationCandidateRiskEvidencePolicy _riskPolicy;
    private readonly IRecoverySessionStore _recoveryStore;

    public DesktopAutomationAutomaticSafetyGate(
        DesktopLayoutSuggestionLifecycle lifecycle,
        DesktopAutomationRuntimePolicy runtimePolicy,
        IDesktopAutomationCandidateRiskEvidencePolicy riskPolicy,
        IRecoverySessionStore recoveryStore)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _runtimePolicy = runtimePolicy ?? throw new ArgumentNullException(nameof(runtimePolicy));
        _riskPolicy = riskPolicy ?? throw new ArgumentNullException(nameof(riskPolicy));
        _recoveryStore = recoveryStore ?? throw new ArgumentNullException(nameof(recoveryStore));
    }

    public async Task<DesktopAutomationAutomaticSafetyGateResult> EvaluateAsync(
        DesktopAutomationFreshExecutionHandoffResult handoff,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        ct.ThrowIfCancellationRequested();

        var currentRuntime = _runtimePolicy.Snapshot;

        if (!handoff.MayProceedToAutomaticSafetyGate)
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.HandoffNotReady,
                handoff,
                currentRuntime,
                "B01 handoff 尚未达到 ReadyForAutomaticSafetyGate。");
        }

        var expectedLifecycle = handoff.Preflight.SuggestionLifecycle;
        if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.SuggestionNoLongerCurrent,
                handoff,
                currentRuntime,
                "当前 Suggestion lifecycle 已不同于 05C/B01 证据。请重新 preflight。");
        }

        if (currentRuntime.Revision != handoff.Preflight.Runtime.Revision)
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.RuntimeChangedSincePreflight,
                handoff,
                currentRuntime,
                $"Runtime Revision 已从 {handoff.Preflight.Runtime.Revision} 变化到 {currentRuntime.Revision}。请重新 preflight。");
        }

        if (!currentRuntime.CanAttemptUnattendedExecution)
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.RuntimeBlocked,
                handoff,
                currentRuntime,
                $"当前 EffectiveMode={currentRuntime.EffectiveMode}，不允许 unattended execution。");
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
                DesktopAutomationAutomaticSafetyGateStatus.RecoveryStateUnavailable,
                handoff,
                currentRuntime,
                $"读取 unresolved recovery state 失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (unresolved.Count != 0)
        {
            return new DesktopAutomationAutomaticSafetyGateResult(
                DesktopAutomationAutomaticSafetyGateStatus.UnresolvedRecoveryPresent,
                handoff,
                currentRuntime,
                unresolvedRecoveryCount: unresolved.Count,
                rejectionReasons: new[]
                {
                    $"存在 {unresolved.Count} 个未解决 recovery session，自动执行禁止继续。"
                });
        }

        // Recovery query 自身跨越了 async 边界。查询完成后先重新确认旧票仍有效，
        // 不让已经 stale 的 lifecycle/runtime 继续进入 Candidate risk 判断。
        if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.SuggestionNoLongerCurrent,
                handoff,
                _runtimePolicy.Snapshot,
                "Recovery 查询期间 Suggestion lifecycle 发生变化。请重新 preflight。");
        }

        currentRuntime = _runtimePolicy.Snapshot;
        if (currentRuntime.Revision != handoff.Preflight.Runtime.Revision)
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.RuntimeChangedSincePreflight,
                handoff,
                currentRuntime,
                "Recovery 查询期间 Runtime Revision 发生变化。请重新 preflight。");
        }
        if (!currentRuntime.CanAttemptUnattendedExecution)
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.RuntimeBlocked,
                handoff,
                currentRuntime,
                "Recovery 查询结束时 runtime 已不允许 unattended execution。");
        }

        var candidate = handoff.FreshCandidate!;
        var invariantReasons = ValidateFreshCandidate(candidate, expectedLifecycle.CurrentSuggestion!);
        if (invariantReasons.Count != 0)
        {
            return new DesktopAutomationAutomaticSafetyGateResult(
                DesktopAutomationAutomaticSafetyGateStatus.CandidateInvariantRejected,
                handoff,
                currentRuntime,
                unresolvedRecoveryCount: 0,
                rejectionReasons: invariantReasons);
        }

        var freshEvidence = new DesktopAutomationCandidateRiskEvidence(
            expectedLifecycle.CurrentSuggestion!.SuggestionId,
            candidate.MoveRequiredCount,
            candidate.ExecutionStepCount,
            candidate.UsesTemporaryStaging);
        var freshRisk = _riskPolicy.Evaluate(freshEvidence);
        ValidateRiskAssessmentMatchesEvidence(freshEvidence, freshRisk);

        if (!freshRisk.WithinAutomationRiskBudget)
        {
            return new DesktopAutomationAutomaticSafetyGateResult(
                DesktopAutomationAutomaticSafetyGateStatus.FreshRiskBudgetBlocked,
                handoff,
                currentRuntime,
                freshRisk,
                unresolvedRecoveryCount: 0,
                rejectionReasons: freshRisk.BlockReasons.Select(reason => reason.ToString()));
        }

        // Risk 评估期间也可能发生 UI 或 monitoring 状态变化。
        // 在返回 Ready 前再次核对 lifecycle + runtime revision。
        if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.SuggestionNoLongerCurrent,
                handoff,
                _runtimePolicy.Snapshot,
                "Safety gate 风险评估期间 Suggestion lifecycle 发生变化。");
        }

        var finalRuntime = _runtimePolicy.Snapshot;
        if (finalRuntime.Revision != handoff.Preflight.Runtime.Revision)
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.RuntimeChangedSincePreflight,
                handoff,
                finalRuntime,
                "Safety gate 风险评估期间 Runtime Revision 发生变化。");
        }
        if (!finalRuntime.CanAttemptUnattendedExecution)
        {
            return Reject(
                DesktopAutomationAutomaticSafetyGateStatus.RuntimeBlocked,
                handoff,
                finalRuntime,
                "Safety gate 评估结束时 runtime 已不允许 unattended execution。");
        }

        return new DesktopAutomationAutomaticSafetyGateResult(
            DesktopAutomationAutomaticSafetyGateStatus.ReadyForCoordinatorPreWriteGate,
            handoff,
            finalRuntime,
            freshRisk,
            unresolvedRecoveryCount: 0);
    }

    private static List<string> ValidateFreshCandidate(
        DesktopLayoutRuleAwareExecutionCandidate candidate,
        DesktopLayoutSuggestion suggestion)
    {
        var reasons = new List<string>();
        if (!candidate.IsExecutable)
            reasons.Add("Fresh Candidate IsExecutable=false。");
        if (candidate.IsNoOp)
            reasons.Add("Fresh Candidate IsNoOp=true。");
        if (candidate.MoveRequiredCount <= 0)
            reasons.Add($"Fresh Candidate MoveRequiredCount={candidate.MoveRequiredCount}，预期 > 0。");
        if (candidate.ExecutionStepCount <= 0)
            reasons.Add($"Fresh Candidate ExecutionStepCount={candidate.ExecutionStepCount}，预期 > 0。");
        if (candidate.RestoreBlockedCount != 0)
            reasons.Add($"Fresh Candidate RestoreBlockedCount={candidate.RestoreBlockedCount}，预期 0。");
        if (candidate.RestoreMissingCount != 0)
            reasons.Add($"Fresh Candidate RestoreMissingCount={candidate.RestoreMissingCount}，预期 0。");
        if (!string.Equals(candidate.TargetDeviceName, suggestion.TargetDeviceName, StringComparison.Ordinal))
            reasons.Add("Fresh Candidate target 与当前 Suggestion target 不一致。");
        if (!string.Equals(candidate.RestorePlan.TargetDeviceName, candidate.TargetDeviceName, StringComparison.Ordinal))
            reasons.Add("Fresh Candidate RestorePlan target 不一致。");
        if (!string.Equals(candidate.ExecutionPlan.TargetDeviceName, candidate.TargetDeviceName, StringComparison.Ordinal))
            reasons.Add("Fresh Candidate ExecutionPlan target 不一致。");
        return reasons;
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
                "Fresh risk policy 返回的 evidence 与 B01 fresh Candidate 不一致。Safety gate fail closed。");
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

    private static DesktopAutomationAutomaticSafetyGateResult Reject(
        DesktopAutomationAutomaticSafetyGateStatus status,
        DesktopAutomationFreshExecutionHandoffResult handoff,
        DesktopAutomationRuntimeSnapshot runtime,
        string reason) =>
        new(status, handoff, runtime, rejectionReasons: new[] { reason });
}
