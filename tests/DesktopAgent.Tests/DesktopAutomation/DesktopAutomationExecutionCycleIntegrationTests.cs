using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopGeometryEvidence = DesktopAgent.Core.Automation.DesktopGeometryEvidence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Tests.DesktopLayouts.Restore.Execution;
using DesktopAgent.Tests.DesktopNotifications;
using DesktopAgent.WindowsInterop.DesktopAutomation;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class DesktopAutomationExecutionCycleIntegrationTests
{
    [Fact]
    public async Task ValidatedAutomaticCycle_UsesFreshBoundary_ExistingCoordinator_AndRuntimeFeedback()
    {
        var originalSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var freshSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var freshCandidate = freshSuggestion.Candidate;

        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(originalSuggestion);

        var runtime = new DesktopAutomationRuntimePolicy(
            new DesktopAutomationRuntimePolicySettings(TimeSpan.FromMinutes(2), 3),
            DesktopAutomationUnattendedGate.ValidatedOpen);
        runtime.SetRequestedMode(DesktopAutomationMode.Automatic);

        var risk = new DesktopAutomationCandidateRiskPolicy(
            new DesktopAutomationCandidateRiskPolicySettings(100, 100, true));
        var preflightPolicy = new DesktopAutomationPreflightDecisionPolicy(risk);

        var freshObservation = Observation(freshCandidate);
        var stableSource = new StableSource(freshObservation);
        var rebuilder = new Rebuilder(
            new DesktopLayoutFreshCandidateRebuildResult(
                DesktopLayoutFreshCandidateRebuildStatus.Ready,
                freshObservation.ObservationId,
                freshCandidate,
                Array.Empty<DesktopLayoutFreshCandidateRebuildIssue>()));
        var handoff = new DesktopAutomationFreshExecutionHandoff(
            lifecycle,
            stableSource,
            rebuilder);

        var recovery = new EmptyRecoveryStore();
        var safety = new DesktopAutomationAutomaticSafetyGate(
            lifecycle,
            runtime,
            risk,
            recovery,
            new MeasuredGridDisplayClearanceProvider());

        var writer = ScriptWriterFor(freshCandidate);
        var coordinator = CreateM2Coordinator(freshCandidate, writer);
        using var bridge = new DesktopAutomationCoordinatorExecutionBridge(
            coordinator,
            lifecycle,
            runtime,
            risk,
            recovery);
        var outcome = new DesktopAutomationExecutionOutcomeCoordinator(bridge, runtime);

        using var cycle = new DesktopAutomationExecutionCycleCoordinator(
            runtime,
            lifecycle,
            preflightPolicy,
            handoff,
            safety,
            outcome);

        var result = await cycle.RunCurrentAsync();

        Assert.Equal(DesktopAutomationExecutionCycleStatus.ExecutionStageReturned, result.Status);
        Assert.NotNull(result.Preflight);
        Assert.Equal(
            DesktopAutomationPreflightDecisionStatus.ReadyForFreshSafetyCheck,
            result.Preflight!.Status);
        Assert.NotNull(result.Handoff);
        Assert.Equal(
            DesktopAutomationFreshExecutionHandoffStatus.ReadyForAutomaticSafetyGate,
            result.Handoff!.Status);
        Assert.NotNull(result.SafetyGate);
        Assert.Equal(
            DesktopAutomationAutomaticSafetyGateStatus.ReadyForCoordinatorPreWriteGate,
            result.SafetyGate!.Status);
        Assert.NotNull(result.Outcome);
        Assert.Equal(
            DesktopAutomationExecutionOutcomeFeedbackStatus.SuccessRecorded,
            result.Outcome!.Status);
        Assert.NotNull(result.Outcome.BridgeResult.CoordinatorRun);
        Assert.Equal(
            TargetDisplayLayoutExecutionRunStatus.Completed,
            result.Outcome.BridgeResult.CoordinatorRun!.Status);
        Assert.Equal(freshCandidate.ExecutionStepCount, writer.AttemptCount);
        Assert.NotNull(result.Outcome.BridgeResult.CoordinatorRun.SafetySnapshotId);
        Assert.NotNull(result.Outcome.BridgeResult.CoordinatorRun.Session);

        Assert.Equal(1, stableSource.CallCount);
        Assert.Equal(1, rebuilder.CallCount);
        Assert.Equal(DesktopAutomationExecutionOutcome.Success, result.RuntimeAfter.LastExecutionOutcome);
        Assert.True(result.RuntimeAfter.IsCooldownActive);
        Assert.Equal(DesktopAutomationMode.Suggestion, result.RuntimeAfter.EffectiveMode);

        // Execution success 不提前篡改 Suggestion lifecycle；等待 monitoring 用真实桌面事实推进。
        Assert.Equal(
            originalSuggestion.SuggestionId,
            lifecycle.Snapshot.CurrentSuggestion?.SuggestionId);
        Assert.Equal(
            DesktopLayoutSuggestionPresentationState.Active,
            lifecycle.Snapshot.State);
    }

    private static TargetDisplayLayoutExecutionCoordinator CreateM2Coordinator(
        DesktopLayoutRuleAwareExecutionCandidate candidate,
        ScriptedDesktopIconPositionWriter writer)
    {
        var topology = new FakeDisplayTopologyReader(new[] { candidate.TargetDisplay });
        var reader = new FakeDesktopIconReader(LiveIconsFrom(candidate));
        var snapshotStore = new InMemoryDesktopLayoutSnapshotStore();

        return new TargetDisplayLayoutExecutionCoordinator(
            topology,
            reader,
            writer,
            new StaThreadExecutor(),
            new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
            snapshotStore,
            TargetDisplayLayoutExecutionPlanner.CreatePlan,
            (_, _, _) => Task.FromResult(
                TargetDisplayLayoutExecutionVerificationResult.ForwardPassed(DateTimeOffset.UtcNow)),
            (_, _, _) => Task.FromResult(
                TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(DateTimeOffset.UtcNow)));
    }

    private static ScriptedDesktopIconPositionWriter ScriptWriterFor(
        DesktopLayoutRuleAwareExecutionCandidate candidate)
    {
        var writer = new ScriptedDesktopIconPositionWriter(
            new DesktopIconPositioningCapabilities(
                false,
                candidate.SourcePreviewPlan.IconSpacing,
                ConnectionMethod.FindWindowSW,
                null));
        foreach (var step in candidate.ExecutionPlan.Steps)
            writer.ScriptExactResult(step.StableId, step.FromPosition, step.ToPosition);
        return writer;
    }

    private static DesktopIconDescriptor[] LiveIconsFrom(
        DesktopLayoutRuleAwareExecutionCandidate candidate) =>
        candidate.RestorePlan.Items
            .Where(item => item.CurrentPosition is not null)
            .Select(item => new DesktopIconDescriptor(
                item.StableId,
                item.DisplayName,
                null,
                ItemKind.File,
                item.CurrentPosition!.Value,
                item.CurrentMonitorDeviceName,
                item.IsSystemItem,
                item.IsFileSystemItem))
            .ToArray();

    private static DesktopObservation Observation(
        DesktopLayoutRuleAwareExecutionCandidate candidate)
    {
        var icons = LiveIconsFrom(candidate);
        return new DesktopObservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { candidate.TargetDisplay },
            new DesktopIconPositioningCapabilities(
                false,
                candidate.SourcePreviewPlan.IconSpacing,
                ConnectionMethod.FindWindowSW,
                null),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);
    }

    private sealed class StableSource(DesktopObservation observation)
        : IStableDesktopObservationSource
    {
        public int CallCount { get; private set; }

        public Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(
                new StableDesktopObservationResult(
                    StableDesktopObservationStatus.Stable,
                    observation,
                    Array.Empty<DesktopObservationCaptureFailure>(),
                    Array.Empty<DesktopObservationStabilityIssue>()));
        }
    }

    private sealed class Rebuilder(DesktopLayoutFreshCandidateRebuildResult result)
        : IDesktopLayoutFreshCandidateRebuilder
    {
        public int CallCount { get; private set; }

        public Task<DesktopLayoutFreshCandidateRebuildResult> RebuildAsync(
            DesktopLayoutSuggestionBinding binding,
            DesktopObservation freshObservation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class EmptyRecoveryStore : IRecoverySessionStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RecoverySessionRecord> UpdateAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RecoverySessionRecord?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<RecoverySessionRecord>>(
                Array.Empty<RecoverySessionRecord>());
        }
    }

    /// <summary>
    /// 集成测试关注的是"预检 → 安全门 → 协调器"的接线，网格证据与站定授权固定用"已实测 + 已授权"这一档，
    /// 免得 A 方案的两道门挡住与网格无关的集成路径（这两道门各自有专门的测试）。
    /// </summary>
    private sealed class MeasuredGridDisplayClearanceProvider : IDesktopGridDisplayClearanceProvider
    {
        public Task<DesktopGridDisplayClearance> GetClearanceAsync(
            string? targetDeviceName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DesktopGridDisplayClearance(
                DesktopGeometryEvidence.Measured,
                isAuthorizedForUnattendedExecution: true));
        }
    }
}
