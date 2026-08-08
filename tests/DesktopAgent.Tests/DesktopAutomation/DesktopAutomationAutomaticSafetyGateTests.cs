using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Tests.DesktopNotifications;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class DesktopAutomationAutomaticSafetyGateTests
{
    [Fact]
    public void SafetyGate_DoesNotDirectlyDependOnWriterOrExecutionCoordinator()
    {
        var parameterTypes = typeof(DesktopAutomationAutomaticSafetyGate)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
        Assert.DoesNotContain(typeof(TargetDisplayLayoutExecutionCoordinator), parameterTypes);
    }

    [Fact]
    public async Task HandoffNotReady_DoesNotQueryRecoveryOrRisk()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var active = lifecycle.Publish(suggestion);
        var blockedRuntimePolicy = RuntimePolicy(DesktopAutomationUnattendedGate.M3AProvisionalClosed);
        var blockedRuntime = blockedRuntimePolicy.SetRequestedMode(DesktopAutomationMode.Automatic);
        var preflight = new DesktopAutomationPreflightDecisionPolicy(PermissiveSuggestionRisk())
            .Evaluate(blockedRuntime, active);
        var handoff = new DesktopAutomationFreshExecutionHandoffResult(
            DesktopAutomationFreshExecutionHandoffStatus.PreflightNotReady,
            preflight);

        var runtimePolicy = RuntimePolicy(DesktopAutomationUnattendedGate.ValidatedOpen);
        runtimePolicy.SetRequestedMode(DesktopAutomationMode.Automatic);
        var recovery = new FakeRecoveryStore();
        var risk = new FakeFreshRiskPolicy();
        var gate = new DesktopAutomationAutomaticSafetyGate(
            lifecycle, runtimePolicy, risk, recovery);

        var result = await gate.EvaluateAsync(handoff);

        Assert.Equal(DesktopAutomationAutomaticSafetyGateStatus.HandoffNotReady, result.Status);
        Assert.Equal(0, recovery.ListCalls);
        Assert.Equal(0, risk.CallCount);
    }

    [Fact]
    public async Task SuggestionChangedAfterHandoff_IsRejectedBeforeRecoveryQuery()
    {
        var fixture = await ReadyFixtureAsync();
        var replacement = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        fixture.Lifecycle.Publish(replacement);
        var recovery = new FakeRecoveryStore();
        var risk = new FakeFreshRiskPolicy();
        var gate = fixture.Gate(recovery, risk);

        var result = await gate.EvaluateAsync(fixture.Handoff);

        Assert.Equal(
            DesktopAutomationAutomaticSafetyGateStatus.SuggestionNoLongerCurrent,
            result.Status);
        Assert.Equal(0, recovery.ListCalls);
        Assert.Equal(0, risk.CallCount);
    }

    [Fact]
    public async Task RuntimeRevisionChangedAfterPreflight_IsRejectedBeforeRecoveryQuery()
    {
        var fixture = await ReadyFixtureAsync();
        fixture.RuntimePolicy.Pause("test pause");
        var recovery = new FakeRecoveryStore();
        var risk = new FakeFreshRiskPolicy();
        var gate = fixture.Gate(recovery, risk);

        var result = await gate.EvaluateAsync(fixture.Handoff);

        Assert.Equal(
            DesktopAutomationAutomaticSafetyGateStatus.RuntimeChangedSincePreflight,
            result.Status);
        Assert.Equal(0, recovery.ListCalls);
        Assert.Equal(0, risk.CallCount);
    }

    [Fact]
    public async Task RecoveryStateUnavailable_FailsClosedBeforeRisk()
    {
        var fixture = await ReadyFixtureAsync();
        var recovery = new FakeRecoveryStore
        {
            Failure = new InvalidOperationException("db unavailable")
        };
        var risk = new FakeFreshRiskPolicy();
        var gate = fixture.Gate(recovery, risk);

        var result = await gate.EvaluateAsync(fixture.Handoff);

        Assert.Equal(
            DesktopAutomationAutomaticSafetyGateStatus.RecoveryStateUnavailable,
            result.Status);
        Assert.Equal(1, recovery.ListCalls);
        Assert.Equal(0, risk.CallCount);
        Assert.False(result.MayEnterCoordinatorPreWriteGate);
    }

    [Fact]
    public async Task UnresolvedRecovery_BlocksAutomaticExecutionBeforeRisk()
    {
        var fixture = await ReadyFixtureAsync();
        var recovery = new FakeRecoveryStore(unresolvedCount: 2);
        var risk = new FakeFreshRiskPolicy();
        var gate = fixture.Gate(recovery, risk);

        var result = await gate.EvaluateAsync(fixture.Handoff);

        Assert.Equal(
            DesktopAutomationAutomaticSafetyGateStatus.UnresolvedRecoveryPresent,
            result.Status);
        Assert.Equal(2, result.UnresolvedRecoveryCount);
        Assert.Equal(0, risk.CallCount);
        Assert.False(result.MayEnterCoordinatorPreWriteGate);
    }

    [Fact]
    public async Task LifecycleChangingDuringRecoveryQuery_IsRejectedBeforeRisk()
    {
        var fixture = await ReadyFixtureAsync();
        var recovery = new FakeRecoveryStore(
            beforeReturn: () => fixture.Lifecycle.Suspend(
                DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable));
        var risk = new FakeFreshRiskPolicy();
        var gate = fixture.Gate(recovery, risk);

        var result = await gate.EvaluateAsync(fixture.Handoff);

        Assert.Equal(
            DesktopAutomationAutomaticSafetyGateStatus.SuggestionNoLongerCurrent,
            result.Status);
        Assert.Equal(1, recovery.ListCalls);
        Assert.Equal(0, risk.CallCount);
    }

    [Fact]
    public async Task RuntimeChangingDuringRecoveryQuery_IsRejectedBeforeRisk()
    {
        var fixture = await ReadyFixtureAsync();
        var recovery = new FakeRecoveryStore(
            beforeReturn: () => fixture.RuntimePolicy.Pause("race pause"));
        var risk = new FakeFreshRiskPolicy();
        var gate = fixture.Gate(recovery, risk);

        var result = await gate.EvaluateAsync(fixture.Handoff);

        Assert.Equal(
            DesktopAutomationAutomaticSafetyGateStatus.RuntimeChangedSincePreflight,
            result.Status);
        Assert.Equal(0, risk.CallCount);
    }

    [Fact]
    public async Task FreshCandidateRiskIsRecomputedFromFreshCandidateStatistics()
    {
        var fixture = await ReadyFixtureAsync();
        var recovery = new FakeRecoveryStore();
        var risk = new FakeFreshRiskPolicy(withinBudget: false);
        var gate = fixture.Gate(recovery, risk);

        var result = await gate.EvaluateAsync(fixture.Handoff);

        Assert.Equal(
            DesktopAutomationAutomaticSafetyGateStatus.FreshRiskBudgetBlocked,
            result.Status);
        Assert.Equal(1, risk.CallCount);
        Assert.NotNull(risk.LastEvidence);
        Assert.Equal(fixture.OriginalSuggestion.SuggestionId, risk.LastEvidence!.SuggestionId);
        Assert.Equal(fixture.FreshCandidate.MoveRequiredCount, risk.LastEvidence.MoveRequiredCount);
        Assert.Equal(fixture.FreshCandidate.ExecutionStepCount, risk.LastEvidence.ExecutionStepCount);
        Assert.Equal(fixture.FreshCandidate.UsesTemporaryStaging, risk.LastEvidence.UsesTemporaryStaging);
        Assert.False(result.MayEnterCoordinatorPreWriteGate);
    }

    [Fact]
    public async Task CorruptFreshRiskEvidence_FailsClosed()
    {
        var fixture = await ReadyFixtureAsync();
        var gate = fixture.Gate(
            new FakeRecoveryStore(),
            new FakeFreshRiskPolicy(corruptEvidence: true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.EvaluateAsync(fixture.Handoff));

        Assert.Contains("evidence", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllChecksPass_ReachesOnlyCoordinatorPreWriteGate()
    {
        var fixture = await ReadyFixtureAsync();
        var recovery = new FakeRecoveryStore();
        var risk = new FakeFreshRiskPolicy();
        var gate = fixture.Gate(recovery, risk);

        var result = await gate.EvaluateAsync(fixture.Handoff);

        Assert.Equal(
            DesktopAutomationAutomaticSafetyGateStatus.ReadyForCoordinatorPreWriteGate,
            result.Status);
        Assert.True(result.MayEnterCoordinatorPreWriteGate);
        Assert.Equal(0, result.UnresolvedRecoveryCount);
        Assert.NotNull(result.FreshRiskAssessment);
        Assert.True(result.FreshRiskAssessment!.WithinAutomationRiskBudget);
        Assert.Empty(result.RejectionReasons);
        Assert.Equal(1, recovery.ListCalls);
        Assert.Equal(1, risk.CallCount);
    }

    private static async Task<ReadyFixture> ReadyFixtureAsync()
    {
        var originalSuggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var freshCandidateSource = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var active = lifecycle.Publish(originalSuggestion);

        var runtimePolicy = RuntimePolicy(DesktopAutomationUnattendedGate.ValidatedOpen);
        var runtime = runtimePolicy.SetRequestedMode(DesktopAutomationMode.Automatic);
        var preflight = new DesktopAutomationPreflightDecisionPolicy(PermissiveSuggestionRisk())
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

        return new ReadyFixture(
            originalSuggestion,
            freshCandidateSource.Candidate,
            lifecycle,
            runtimePolicy,
            handoff);
    }

    private static DesktopAutomationRuntimePolicy RuntimePolicy(
        DesktopAutomationUnattendedGate gate) =>
        new(
            new DesktopAutomationRuntimePolicySettings(
                TimeSpan.Zero,
                fuseFailureThreshold: 3),
            gate);

    private static DesktopAutomationCandidateRiskPolicy PermissiveSuggestionRisk() =>
        new(
            new DesktopAutomationCandidateRiskPolicySettings(
                maxMoveCount: 100,
                maxExecutionStepCount: 100,
                allowTemporaryStaging: true));

    private sealed record ReadyFixture(
        DesktopLayoutSuggestion OriginalSuggestion,
        DesktopAgent.Core.DesktopLayouts.Templates.Execution.DesktopLayoutRuleAwareExecutionCandidate FreshCandidate,
        DesktopLayoutSuggestionLifecycle Lifecycle,
        DesktopAutomationRuntimePolicy RuntimePolicy,
        DesktopAutomationFreshExecutionHandoffResult Handoff)
    {
        public DesktopAutomationAutomaticSafetyGate Gate(
            IRecoverySessionStore recovery,
            IDesktopAutomationCandidateRiskEvidencePolicy risk) =>
            new(Lifecycle, RuntimePolicy, risk, recovery);
    }

    private sealed class FakeFreshRiskPolicy(
        bool withinBudget = true,
        bool corruptEvidence = false)
        : IDesktopAutomationCandidateRiskEvidencePolicy
    {
        public int CallCount { get; private set; }
        public DesktopAutomationCandidateRiskEvidence? LastEvidence { get; private set; }

        public DesktopAutomationCandidateRiskAssessment Evaluate(
            DesktopAutomationCandidateRiskEvidence evidence)
        {
            CallCount++;
            LastEvidence = evidence;

            var returnedEvidence = corruptEvidence
                ? new DesktopAutomationCandidateRiskEvidence(
                    Guid.NewGuid(),
                    evidence.MoveRequiredCount,
                    evidence.ExecutionStepCount,
                    evidence.UsesTemporaryStaging)
                : evidence;
            var settings = new DesktopAutomationCandidateRiskPolicySettings(
                Math.Max(1, evidence.MoveRequiredCount),
                Math.Max(1, evidence.ExecutionStepCount),
                allowTemporaryStaging: true);
            var reasons = withinBudget
                ? Array.Empty<DesktopAutomationCandidateRiskBlockReason>()
                : new[] { DesktopAutomationCandidateRiskBlockReason.MoveCountLimitExceeded };

            return new DesktopAutomationCandidateRiskAssessment(
                returnedEvidence,
                settings,
                reasons);
        }
    }

    private sealed class FakeRecoveryStore(
        int unresolvedCount = 0,
        Action? beforeReturn = null)
        : IRecoverySessionStore
    {
        public int ListCalls { get; private set; }
        public Exception? Failure { get; init; }

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
            ListCalls++;
            beforeReturn?.Invoke();
            if (Failure is not null)
                throw Failure;

            IReadOnlyList<RecoverySessionRecord> records = Enumerable
                .Repeat<RecoverySessionRecord>(null!, unresolvedCount)
                .ToArray();
            return Task.FromResult(records);
        }
    }
}
