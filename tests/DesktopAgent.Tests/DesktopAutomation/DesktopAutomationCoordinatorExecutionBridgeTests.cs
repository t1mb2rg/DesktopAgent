using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Tests.DesktopLayouts.Restore.Execution;
using DesktopAgent.Tests.DesktopNotifications;
using DesktopAgent.WindowsInterop.DesktopAutomation;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class DesktopAutomationCoordinatorExecutionBridgeTests
{
    [Fact]
    public void Bridge_HasSingleM2CoordinatorDependency_NotDirectWriterDependency()
    {
        var parameterTypes = typeof(DesktopAutomationCoordinatorExecutionBridge)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(TargetDisplayLayoutExecutionCoordinator), parameterTypes);
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public async Task SafetyGateNotReady_DoesNotInvokeCoordinatorOrWriter()
    {
        var fixture = await ReadyFixtureAsync();
        var blocked = new DesktopAutomationAutomaticSafetyGateResult(
            DesktopAutomationAutomaticSafetyGateStatus.RuntimeBlocked,
            fixture.SafetyGate.Handoff,
            fixture.RuntimePolicy.Snapshot,
            rejectionReasons: new[] { "test blocked" });
        var writer = ScriptWriterFor(fixture.FreshCandidate);
        using var bridge = fixture.CreateBridge(writer, new FakeRecoveryStore());

        var result = await bridge.ExecuteAsync(blocked);

        Assert.Equal(DesktopAutomationCoordinatorExecutionBridgeStatus.SafetyGateNotReady, result.Status);
        Assert.False(result.CoordinatorWasInvoked);
        Assert.Equal(0, writer.AttemptCount);
    }

    [Fact]
    public async Task UnresolvedRecoveryBeforeCoordinator_BlocksWithZeroWriterCalls()
    {
        var fixture = await ReadyFixtureAsync();
        var writer = ScriptWriterFor(fixture.FreshCandidate);
        using var bridge = fixture.CreateBridge(
            writer,
            new FakeRecoveryStore(unresolvedCount: 1));

        var result = await bridge.ExecuteAsync(fixture.SafetyGate);

        Assert.Equal(
            DesktopAutomationCoordinatorExecutionBridgeStatus.UnresolvedRecoveryPresent,
            result.Status);
        Assert.Equal(1, result.UnresolvedRecoveryCount);
        Assert.False(result.CoordinatorWasInvoked);
        Assert.Equal(0, writer.AttemptCount);
    }

    [Fact]
    public async Task RuntimeChangedBeforeCoordinator_BlocksWithZeroWriterCalls()
    {
        var fixture = await ReadyFixtureAsync();
        fixture.RuntimePolicy.Pause("before coordinator");
        var writer = ScriptWriterFor(fixture.FreshCandidate);
        using var bridge = fixture.CreateBridge(writer, new FakeRecoveryStore());

        var result = await bridge.ExecuteAsync(fixture.SafetyGate);

        Assert.Equal(
            DesktopAutomationCoordinatorExecutionBridgeStatus.RuntimeChangedSincePreflight,
            result.Status);
        Assert.False(result.CoordinatorWasInvoked);
        Assert.Equal(0, writer.AttemptCount);
    }

    [Fact]
    public async Task CoordinatorFreshReadDetectsStaleRestorePlan_BeforeScopeGateAndWriter()
    {
        var fixture = await ReadyFixtureAsync();
        var writer = ScriptWriterFor(fixture.FreshCandidate);
        var staleIcons = LiveIconsFrom(fixture.FreshCandidate)
            .Select(icon => icon with
            {
                PixelPosition = new PixelPoint(
                    checked(icon.PixelPosition.X + 80),
                    icon.PixelPosition.Y)
            })
            .ToArray();
        using var bridge = fixture.CreateBridge(
            writer,
            new FakeRecoveryStore(),
            currentIcons: staleIcons);

        var result = await bridge.ExecuteAsync(fixture.SafetyGate);

        Assert.Equal(DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned, result.Status);
        Assert.NotNull(result.CoordinatorRun);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.CoordinatorRun!.Status);
        Assert.Null(result.PreWriteGate);
        Assert.Equal(0, writer.AttemptCount);
    }

    [Fact]
    public async Task RuntimeChangesDuringCoordinatorReplan_PreWriteGateRejectsWithZeroWriterCalls()
    {
        var fixture = await ReadyFixtureAsync();
        var writer = ScriptWriterFor(fixture.FreshCandidate);
        using var bridge = fixture.CreateBridge(
            writer,
            new FakeRecoveryStore(),
            beforeCreatePlan: () => fixture.RuntimePolicy.Pause("commit point race"));

        var result = await bridge.ExecuteAsync(fixture.SafetyGate);

        Assert.Equal(DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned, result.Status);
        Assert.NotNull(result.PreWriteGate);
        Assert.False(result.PreWriteGate!.IsAllowed);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.CoordinatorRun!.Status);
        Assert.Contains(
            result.PreWriteGate.RejectionReasons,
            reason => reason.Contains("Runtime Revision", StringComparison.Ordinal));
        Assert.Equal(0, writer.AttemptCount);
    }

    [Fact]
    public async Task LatestExecutionRiskBlocked_PreWriteGateRejectsWithZeroWriterCalls()
    {
        var fixture = await ReadyFixtureAsync();
        var writer = ScriptWriterFor(fixture.FreshCandidate);
        using var bridge = fixture.CreateBridge(
            writer,
            new FakeRecoveryStore(),
            bridgeRiskPolicy: new AlwaysBlockingFreshRiskPolicy());

        var result = await bridge.ExecuteAsync(fixture.SafetyGate);

        Assert.Equal(DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned, result.Status);
        Assert.NotNull(result.PreWriteGate);
        Assert.False(result.PreWriteGate!.IsAllowed);
        Assert.Contains(
            result.PreWriteGate.RejectionReasons,
            reason => reason.Contains("LatestExecutionRisk", StringComparison.Ordinal));
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite, result.CoordinatorRun!.Status);
        Assert.Equal(0, writer.AttemptCount);
    }

    [Fact]
    public async Task AllAutomaticGatesPass_UsesExistingCoordinatorWriterChain()
    {
        var fixture = await ReadyFixtureAsync();
        var writer = ScriptWriterFor(fixture.FreshCandidate);
        using var bridge = fixture.CreateBridge(writer, new FakeRecoveryStore());

        var result = await bridge.ExecuteAsync(fixture.SafetyGate);

        Assert.Equal(DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned, result.Status);
        Assert.True(result.CoordinatorWasInvoked);
        Assert.NotNull(result.PreWriteGate);
        Assert.True(result.PreWriteGate!.IsAllowed);
        Assert.NotNull(result.CoordinatorRun);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, result.CoordinatorRun!.Status);
        Assert.True(result.CoordinatorRun.IsSuccess);
        Assert.Equal(fixture.FreshCandidate.ExecutionStepCount, writer.AttemptCount);
        Assert.NotNull(result.CoordinatorRun.SafetySnapshotId);
        Assert.NotNull(result.CoordinatorRun.Session);
    }

    [Fact]
    public async Task ConcurrentAutomaticAttempt_IsRejectedInsteadOfQueued()
    {
        var fixture = await ReadyFixtureAsync();
        var recovery = new FakeRecoveryStore(blockFirstQuery: true);
        var writer = ScriptWriterFor(fixture.FreshCandidate);
        using var bridge = fixture.CreateBridge(writer, recovery);

        var first = bridge.ExecuteAsync(fixture.SafetyGate);
        await recovery.FirstQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await bridge.ExecuteAsync(fixture.SafetyGate);
        Assert.Equal(
            DesktopAutomationCoordinatorExecutionBridgeStatus.ExecutionAlreadyInProgress,
            second.Status);
        Assert.False(second.CoordinatorWasInvoked);

        recovery.ReleaseFirstQuery.TrySetResult();
        var firstResult = await first;
        Assert.Equal(DesktopAutomationCoordinatorExecutionBridgeStatus.CoordinatorReturned, firstResult.Status);
        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.Completed, firstResult.CoordinatorRun!.Status);
    }

    private static async Task<ReadyFixture> ReadyFixtureAsync()
    {
        var originalSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var freshCandidateSource = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var active = lifecycle.Publish(originalSuggestion);

        var runtimePolicy = new DesktopAutomationRuntimePolicy(
            new DesktopAutomationRuntimePolicySettings(TimeSpan.Zero, 3),
            DesktopAutomationUnattendedGate.ValidatedOpen);
        var runtime = runtimePolicy.SetRequestedMode(DesktopAutomationMode.Automatic);

        var permissive = PermissiveRiskPolicy();
        var preflight = new DesktopAutomationPreflightDecisionPolicy(permissive)
            .Evaluate(runtime, active);
        Assert.True(preflight.MayProceedToFreshSafetyCheck);

        var observationId = Guid.NewGuid();
        var rebuild = new DesktopLayoutFreshCandidateRebuildResult(
            DesktopLayoutFreshCandidateRebuildStatus.Ready,
            observationId,
            freshCandidateSource.Candidate,
            Array.Empty<DesktopLayoutFreshCandidateRebuildIssue>());
        var handoff = new DesktopAutomationFreshExecutionHandoffResult(
            DesktopAutomationFreshExecutionHandoffStatus.ReadyForAutomaticSafetyGate,
            preflight,
            observationId,
            rebuild);

        var freshEvidence = new DesktopAutomationCandidateRiskEvidence(
            originalSuggestion.SuggestionId,
            freshCandidateSource.Candidate.MoveRequiredCount,
            freshCandidateSource.Candidate.ExecutionStepCount,
            freshCandidateSource.Candidate.UsesTemporaryStaging);
        var freshRisk = permissive.Evaluate(freshEvidence);
        var safetyGate = new DesktopAutomationAutomaticSafetyGateResult(
            DesktopAutomationAutomaticSafetyGateStatus.ReadyForCoordinatorPreWriteGate,
            handoff,
            runtime,
            freshRisk,
            unresolvedRecoveryCount: 0);

        return new ReadyFixture(
            originalSuggestion,
            freshCandidateSource.Candidate,
            lifecycle,
            runtimePolicy,
            safetyGate);
    }

    private static DesktopAutomationCandidateRiskPolicy PermissiveRiskPolicy() =>
        new(
            new DesktopAutomationCandidateRiskPolicySettings(
                maxMoveCount: 100,
                maxExecutionStepCount: 100,
                allowTemporaryStaging: true));

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

    private sealed record ReadyFixture(
        DesktopLayoutSuggestion OriginalSuggestion,
        DesktopLayoutRuleAwareExecutionCandidate FreshCandidate,
        DesktopLayoutSuggestionLifecycle Lifecycle,
        DesktopAutomationRuntimePolicy RuntimePolicy,
        DesktopAutomationAutomaticSafetyGateResult SafetyGate)
    {
        public DesktopAutomationCoordinatorExecutionBridge CreateBridge(
            ScriptedDesktopIconPositionWriter writer,
            IRecoverySessionStore recoveryStore,
            IReadOnlyList<DesktopIconDescriptor>? currentIcons = null,
            Action? beforeCreatePlan = null,
            IDesktopAutomationCandidateRiskEvidencePolicy? bridgeRiskPolicy = null)
        {
            var icons = currentIcons ?? LiveIconsFrom(FreshCandidate);
            var topology = new FakeDisplayTopologyReader(new[] { FreshCandidate.TargetDisplay });
            var reader = new FakeDesktopIconReader(icons);
            var snapshotStore = new InMemoryDesktopLayoutSnapshotStore();
            var coordinator = new TargetDisplayLayoutExecutionCoordinator(
                topology,
                reader,
                writer,
                new StaThreadExecutor(),
                new DefaultSafetySnapshotFactory(new DesktopLayoutSnapshotFactory()),
                snapshotStore,
                (restorePlan, read, displays, capabilities) =>
                {
                    beforeCreatePlan?.Invoke();
                    return TargetDisplayLayoutExecutionPlanner.CreatePlan(
                        restorePlan, read, displays, capabilities);
                },
                (_, _, _) => Task.FromResult(
                    TargetDisplayLayoutExecutionVerificationResult.ForwardPassed(DateTimeOffset.UtcNow)),
                (_, _, _) => Task.FromResult(
                    TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(DateTimeOffset.UtcNow)));

            return new DesktopAutomationCoordinatorExecutionBridge(
                coordinator,
                Lifecycle,
                RuntimePolicy,
                bridgeRiskPolicy ?? PermissiveRiskPolicy(),
                recoveryStore);
        }
    }

    private sealed class AlwaysBlockingFreshRiskPolicy
        : IDesktopAutomationCandidateRiskEvidencePolicy
    {
        public DesktopAutomationCandidateRiskAssessment Evaluate(
            DesktopAutomationCandidateRiskEvidence evidence) =>
            new(
                evidence,
                new DesktopAutomationCandidateRiskPolicySettings(
                    Math.Max(1, evidence.MoveRequiredCount),
                    Math.Max(1, evidence.ExecutionStepCount),
                    true),
                new[] { DesktopAutomationCandidateRiskBlockReason.MoveCountLimitExceeded });
    }

    private sealed class FakeRecoveryStore(
        int unresolvedCount = 0,
        bool blockFirstQuery = false)
        : IRecoverySessionStore
    {
        private int _queryCount;
        public TaskCompletionSource FirstQueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstQuery { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public async Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = Interlocked.Increment(ref _queryCount);
            if (blockFirstQuery && query == 1)
            {
                FirstQueryStarted.TrySetResult();
                await ReleaseFirstQuery.Task.WaitAsync(cancellationToken);
            }

            return Enumerable.Repeat<RecoverySessionRecord>(null!, unresolvedCount).ToArray();
        }
    }
}
