using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Tests.DesktopNotifications;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class DesktopAutomationFreshExecutionHandoffTests
{
    [Fact]
    public void Handoff_DoesNotExposeWriterDependency()
    {
        var parameterTypes = typeof(DesktopAutomationFreshExecutionHandoff)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public async Task NonReadyPreflight_DoesNotCaptureOrRebuild()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var preflight = Preflight(
            suggestion,
            lifecycle,
            DesktopAutomationUnattendedGate.M3AProvisionalClosed);
        var source = new FakeStableSource(Stable(FreshObservation()));
        var rebuilder = new FakeRebuilder(null);
        var handoff = new DesktopAutomationFreshExecutionHandoff(lifecycle, source, rebuilder);

        var result = await handoff.PrepareAsync(preflight);

        Assert.Equal(DesktopAutomationFreshExecutionHandoffStatus.PreflightNotReady, result.Status);
        Assert.Equal(0, source.CallCount);
        Assert.Equal(0, rebuilder.CallCount);
    }

    [Fact]
    public async Task SuggestionReplacedBeforeCapture_IsRejectedWithoutCapture()
    {
        var s1 = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var s2 = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(s1);
        var preflight = ReadyPreflight(s1, lifecycle);
        lifecycle.Publish(s2);
        var source = new FakeStableSource(Stable(FreshObservation()));
        var rebuilder = new FakeRebuilder(null);
        var handoff = new DesktopAutomationFreshExecutionHandoff(lifecycle, source, rebuilder);

        var result = await handoff.PrepareAsync(preflight);

        Assert.Equal(
            DesktopAutomationFreshExecutionHandoffStatus.SuggestionNoLongerCurrent,
            result.Status);
        Assert.Equal(0, source.CallCount);
        Assert.Equal(0, rebuilder.CallCount);
    }

    [Fact]
    public async Task LifecycleChangesDuringFreshCapture_IsRejectedBeforeRebuild()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var preflight = ReadyPreflight(suggestion, lifecycle);
        var observation = FreshObservation();
        var source = new FakeStableSource(
            Stable(observation),
            () => lifecycle.Suspend(DesktopLayoutSuggestionSuspensionReason.ObservationUnstable));
        var rebuilder = new FakeRebuilder(null);
        var handoff = new DesktopAutomationFreshExecutionHandoff(lifecycle, source, rebuilder);

        var result = await handoff.PrepareAsync(preflight);

        Assert.Equal(
            DesktopAutomationFreshExecutionHandoffStatus.SuggestionNoLongerCurrent,
            result.Status);
        Assert.Equal(1, source.CallCount);
        Assert.Equal(0, rebuilder.CallCount);
    }

    [Fact]
    public async Task UnstableAndUnavailableObservations_FailClosed()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();

        foreach (var stableResult in new[] { Unstable(), Unavailable() })
        {
            var lifecycle = new DesktopLayoutSuggestionLifecycle();
            lifecycle.Publish(suggestion);
            var preflight = ReadyPreflight(suggestion, lifecycle);
            var source = new FakeStableSource(stableResult);
            var rebuilder = new FakeRebuilder(null);
            var handoff = new DesktopAutomationFreshExecutionHandoff(lifecycle, source, rebuilder);

            var result = await handoff.PrepareAsync(preflight);

            Assert.Contains(
                result.Status,
                new[]
                {
                    DesktopAutomationFreshExecutionHandoffStatus.ObservationUnstable,
                    DesktopAutomationFreshExecutionHandoffStatus.ObservationUnavailable,
                });
            Assert.Equal(0, rebuilder.CallCount);
        }
    }

    [Fact]
    public async Task SourceReturningOriginalSuggestionObservationId_IsRejectedAsNotFresh()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var preflight = ReadyPreflight(suggestion, lifecycle);
        var source = new FakeStableSource(
            Stable(FreshObservation(suggestion.SourceObservationId)));
        var rebuilder = new FakeRebuilder(null);
        var handoff = new DesktopAutomationFreshExecutionHandoff(lifecycle, source, rebuilder);

        var result = await handoff.PrepareAsync(preflight);

        Assert.Equal(DesktopAutomationFreshExecutionHandoffStatus.ObservationNotFresh, result.Status);
        Assert.Equal(0, rebuilder.CallCount);
    }

    [Fact]
    public async Task RebuilderReturningOriginalCandidateInstance_IsRejected()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var preflight = ReadyPreflight(suggestion, lifecycle);
        var observation = FreshObservation();
        var rebuild = ReadyRebuild(observation.ObservationId, suggestion.Candidate);
        var handoff = new DesktopAutomationFreshExecutionHandoff(
            lifecycle,
            new FakeStableSource(Stable(observation)),
            new FakeRebuilder(rebuild));

        var result = await handoff.PrepareAsync(preflight);

        Assert.Equal(
            DesktopAutomationFreshExecutionHandoffStatus.FreshCandidateNotFresh,
            result.Status);
        Assert.Same(suggestion.Candidate, result.FreshCandidate);
        Assert.False(result.MayProceedToAutomaticSafetyGate);
    }

    [Fact]
    public async Task TrulyFreshCandidate_ReachesOnlyAutomaticSafetyGate()
    {
        var suggestion = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var freshCandidateSource = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var preflight = ReadyPreflight(suggestion, lifecycle);
        var observation = FreshObservation();
        var rebuild = ReadyRebuild(observation.ObservationId, freshCandidateSource.Candidate);
        var handoff = new DesktopAutomationFreshExecutionHandoff(
            lifecycle,
            new FakeStableSource(Stable(observation)),
            new FakeRebuilder(rebuild));

        var result = await handoff.PrepareAsync(preflight);

        Assert.Equal(
            DesktopAutomationFreshExecutionHandoffStatus.ReadyForAutomaticSafetyGate,
            result.Status);
        Assert.True(result.MayProceedToAutomaticSafetyGate);
        Assert.Same(freshCandidateSource.Candidate, result.FreshCandidate);
        Assert.NotSame(suggestion.Candidate, result.FreshCandidate);
        Assert.Equal(observation.ObservationId, result.FreshObservationId);
    }

    private static DesktopAutomationPreflightDecision ReadyPreflight(
        DesktopLayoutSuggestion suggestion,
        DesktopLayoutSuggestionLifecycle lifecycle) =>
        Preflight(suggestion, lifecycle, DesktopAutomationUnattendedGate.ValidatedOpen);

    private static DesktopAutomationPreflightDecision Preflight(
        DesktopLayoutSuggestion suggestion,
        DesktopLayoutSuggestionLifecycle lifecycle,
        DesktopAutomationUnattendedGate gate)
    {
        var snapshot = lifecycle.Snapshot;
        Assert.Equal(suggestion.SuggestionId, snapshot.CurrentSuggestion?.SuggestionId);

        var runtimePolicy = new DesktopAutomationRuntimePolicy(
            new DesktopAutomationRuntimePolicySettings(TimeSpan.Zero, 3),
            gate);
        var runtime = runtimePolicy.SetRequestedMode(DesktopAutomationMode.Automatic);
        var risk = new DesktopAutomationCandidateRiskPolicy(
            new DesktopAutomationCandidateRiskPolicySettings(
                Math.Max(1, suggestion.MoveRequiredCount),
                Math.Max(1, suggestion.ExecutionStepCount),
                allowTemporaryStaging: true));
        return new DesktopAutomationPreflightDecisionPolicy(risk)
            .Evaluate(runtime, snapshot);
    }

    private static DesktopLayoutFreshCandidateRebuildResult ReadyRebuild(
        Guid observationId,
        Core.DesktopLayouts.Templates.Execution.DesktopLayoutRuleAwareExecutionCandidate candidate) =>
        new(
            DesktopLayoutFreshCandidateRebuildStatus.Ready,
            observationId,
            candidate,
            Array.Empty<DesktopLayoutFreshCandidateRebuildIssue>());

    private static StableDesktopObservationResult Stable(DesktopObservation observation) =>
        new(
            StableDesktopObservationStatus.Stable,
            observation,
            Array.Empty<DesktopObservationCaptureFailure>(),
            Array.Empty<DesktopObservationStabilityIssue>());

    private static StableDesktopObservationResult Unstable() =>
        new(
            StableDesktopObservationStatus.Unstable,
            null,
            Array.Empty<DesktopObservationCaptureFailure>(),
            new[]
            {
                new DesktopObservationStabilityIssue(
                    DesktopObservationStabilityIssueCode.IconChanged,
                    "test unstable")
            });

    private static StableDesktopObservationResult Unavailable() =>
        new(
            StableDesktopObservationStatus.Unavailable,
            null,
            new[]
            {
                new DesktopObservationCaptureFailure(
                    DesktopObservationSamplePhase.First,
                    new DesktopObservationCaptureIssue(
                        DesktopObservationCaptureIssueCode.ShellObservationFailed,
                        "test unavailable"))
            },
            Array.Empty<DesktopObservationStabilityIssue>());

    private static DesktopObservation FreshObservation(Guid? id = null)
    {
        var display = new DisplayDescriptor(
            "DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1080),
            96,
            96,
            true);
        var icon = new DesktopIconDescriptor(
            "a",
            "a.txt",
            "c:\\desktop\\a.txt",
            ItemKind.File,
            new PixelPoint(80, 0),
            display.DeviceName,
            false,
            true);
        return new DesktopObservation(
            id ?? Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { display },
            new DesktopIconPositioningCapabilities(
                false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                null),
            new[] { icon },
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);
    }

    private sealed class FakeStableSource(
        StableDesktopObservationResult result,
        Action? beforeReturn = null)
        : IStableDesktopObservationSource
    {
        public int CallCount { get; private set; }

        public Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            beforeReturn?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRebuilder(DesktopLayoutFreshCandidateRebuildResult? result)
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
            if (result is null)
                throw new InvalidOperationException("Fake rebuilder should not have been called.");
            return Task.FromResult(result);
        }
    }
}
