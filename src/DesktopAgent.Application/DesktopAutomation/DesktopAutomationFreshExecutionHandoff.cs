using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;

namespace DesktopAgent.Application.DesktopAutomation;

public enum DesktopAutomationFreshExecutionHandoffStatus
{
    PreflightNotReady = 0,
    SuggestionNoLongerCurrent = 1,
    ObservationUnstable = 2,
    ObservationUnavailable = 3,
    ObservationNotFresh = 4,
    FreshCandidateNoActionNeeded = 5,
    FreshCandidateRejected = 6,
    FreshCandidateNotFresh = 7,
    TargetChanged = 8,
    ReadyForAutomaticSafetyGate = 9,
}

/// <summary>
/// M3-B01 将旧 Suggestion 证据安全换成当前事实下的 fresh Candidate。
/// ReadyForAutomaticSafetyGate 仍然不是执行授权；后续必须继续经过 automatic safety gate。
/// </summary>
public sealed record DesktopAutomationFreshExecutionHandoffResult
{
    public DesktopAutomationFreshExecutionHandoffStatus Status { get; }
    public DesktopAutomationPreflightDecision Preflight { get; }
    public Guid? FreshObservationId { get; }
    public DesktopLayoutFreshCandidateRebuildResult? Rebuild { get; }

    public Guid? SuggestionId => Preflight.SuggestionId;
    public DesktopLayoutRuleAwareExecutionCandidate? FreshCandidate => Rebuild?.Candidate;
    public bool MayProceedToAutomaticSafetyGate =>
        Status == DesktopAutomationFreshExecutionHandoffStatus.ReadyForAutomaticSafetyGate;

    public DesktopAutomationFreshExecutionHandoffResult(
        DesktopAutomationFreshExecutionHandoffStatus status,
        DesktopAutomationPreflightDecision preflight,
        Guid? freshObservationId = null,
        DesktopLayoutFreshCandidateRebuildResult? rebuild = null)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        if (status == DesktopAutomationFreshExecutionHandoffStatus.ReadyForAutomaticSafetyGate)
        {
            if (!preflight.MayProceedToFreshSafetyCheck
                || freshObservationId is null
                || rebuild is null
                || !rebuild.IsReady
                || rebuild.Candidate is null)
            {
                throw new ArgumentException(
                    "ReadyForAutomaticSafetyGate 必须来自 ready preflight、fresh observation 和 ready rebuild。",
                    nameof(status));
            }
        }

        Status = status;
        Preflight = preflight;
        FreshObservationId = freshObservationId;
        Rebuild = rebuild;
    }
}

public sealed class DesktopAutomationFreshExecutionHandoff
{
    private readonly DesktopLayoutSuggestionLifecycle _lifecycle;
    private readonly IStableDesktopObservationSource _stableObservationSource;
    private readonly IDesktopLayoutFreshCandidateRebuilder _rebuilder;

    public DesktopAutomationFreshExecutionHandoff(
        DesktopLayoutSuggestionLifecycle lifecycle,
        IStableDesktopObservationSource stableObservationSource,
        IDesktopLayoutFreshCandidateRebuilder rebuilder)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _stableObservationSource = stableObservationSource
            ?? throw new ArgumentNullException(nameof(stableObservationSource));
        _rebuilder = rebuilder ?? throw new ArgumentNullException(nameof(rebuilder));
    }

    public async Task<DesktopAutomationFreshExecutionHandoffResult> PrepareAsync(
        DesktopAutomationPreflightDecision preflight,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ct.ThrowIfCancellationRequested();

        if (!preflight.MayProceedToFreshSafetyCheck)
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.PreflightNotReady,
                preflight);
        }

        var expectedLifecycle = preflight.SuggestionLifecycle;
        var expectedSuggestion = expectedLifecycle.CurrentSuggestion!;

        if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.SuggestionNoLongerCurrent,
                preflight);
        }

        var stable = await _stableObservationSource.CaptureStableAsync(ct);
        if (stable.Status == StableDesktopObservationStatus.Unstable)
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.ObservationUnstable,
                preflight);
        }
        if (stable.Status == StableDesktopObservationStatus.Unavailable)
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.ObservationUnavailable,
                preflight);
        }

        var freshObservation = stable.StableObservation!;
        if (freshObservation.ObservationId == expectedSuggestion.SourceObservationId)
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.ObservationNotFresh,
                preflight,
                freshObservation.ObservationId);
        }

        if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.SuggestionNoLongerCurrent,
                preflight,
                freshObservation.ObservationId);
        }

        var rebuild = await _rebuilder.RebuildAsync(
            expectedSuggestion.Binding,
            freshObservation,
            ct);

        if (!MatchesExpectedActiveLifecycle(_lifecycle.Snapshot, expectedLifecycle))
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.SuggestionNoLongerCurrent,
                preflight,
                freshObservation.ObservationId,
                rebuild);
        }

        if (rebuild.Status == DesktopLayoutFreshCandidateRebuildStatus.NoActionNeeded)
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.FreshCandidateNoActionNeeded,
                preflight,
                freshObservation.ObservationId,
                rebuild);
        }

        if (!rebuild.IsReady || rebuild.Candidate is null)
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.FreshCandidateRejected,
                preflight,
                freshObservation.ObservationId,
                rebuild);
        }

        var freshCandidate = rebuild.Candidate;
        if (ReferenceEquals(freshCandidate, expectedSuggestion.Candidate))
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.FreshCandidateNotFresh,
                preflight,
                freshObservation.ObservationId,
                rebuild);
        }

        if (!string.Equals(
                freshCandidate.TargetDeviceName,
                expectedSuggestion.TargetDeviceName,
                StringComparison.Ordinal))
        {
            return new DesktopAutomationFreshExecutionHandoffResult(
                DesktopAutomationFreshExecutionHandoffStatus.TargetChanged,
                preflight,
                freshObservation.ObservationId,
                rebuild);
        }

        return new DesktopAutomationFreshExecutionHandoffResult(
            DesktopAutomationFreshExecutionHandoffStatus.ReadyForAutomaticSafetyGate,
            preflight,
            freshObservation.ObservationId,
            rebuild);
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
}
