using DesktopAgent.Application.DesktopAutomation;

namespace DesktopAgent.Tests.DesktopAutomation;

public sealed class DesktopAutomationRuntimePolicyTests
{
    [Fact]
    public void DefaultSuggestionMode_WithCurrentM3AGate_CannotAttemptUnattendedExecution()
    {
        var policy = Policy(
            DesktopAutomationUnattendedGate.M3AProvisionalClosed);

        var snapshot = policy.Snapshot;

        Assert.Equal(DesktopAutomationMode.Suggestion, snapshot.RequestedMode);
        Assert.Equal(DesktopAutomationMode.Suggestion, snapshot.EffectiveMode);
        Assert.True(snapshot.CanObserveInBackground);
        Assert.True(snapshot.CanPresentSuggestions);
        Assert.False(snapshot.CanAttemptUnattendedExecution);
        Assert.Equal(
            new[]
            {
                DesktopAutomationBlockReason.RequestedSuggestionMode,
                DesktopAutomationBlockReason.UnattendedWriteGateClosed,
            },
            snapshot.UnattendedBlockReasons);
        Assert.Equal(
            DesktopAutomationUnattendedGateReason.M2RealDualMonitorAcceptancePending,
            snapshot.UnattendedGate.Reason);
    }

    [Fact]
    public void RequestedAutomatic_WithCurrentM3AGate_DegradesToSuggestion()
    {
        var policy = Policy(
            DesktopAutomationUnattendedGate.M3AProvisionalClosed);

        var snapshot = policy.SetRequestedMode(DesktopAutomationMode.Automatic);

        Assert.Equal(DesktopAutomationMode.Automatic, snapshot.RequestedMode);
        Assert.Equal(DesktopAutomationMode.Suggestion, snapshot.EffectiveMode);
        Assert.False(snapshot.CanAttemptUnattendedExecution);
        Assert.Equal(
            new[] { DesktopAutomationBlockReason.UnattendedWriteGateClosed },
            snapshot.UnattendedBlockReasons);
    }

    [Fact]
    public void ValidatedOpenGate_AllowsAutomaticOnlyWhenNoRuntimeBlockersExist()
    {
        var policy = Policy(DesktopAutomationUnattendedGate.ValidatedOpen);

        var snapshot = policy.SetRequestedMode(DesktopAutomationMode.Automatic);

        Assert.Equal(DesktopAutomationMode.Automatic, snapshot.EffectiveMode);
        Assert.True(snapshot.CanAttemptUnattendedExecution);
        Assert.Empty(snapshot.UnattendedBlockReasons);
    }

    [Fact]
    public void ManualMode_DisablesBackgroundObservationAndSuggestions()
    {
        var policy = Policy(DesktopAutomationUnattendedGate.ValidatedOpen);

        var snapshot = policy.SetRequestedMode(DesktopAutomationMode.Manual);

        Assert.Equal(DesktopAutomationMode.Manual, snapshot.EffectiveMode);
        Assert.False(snapshot.CanObserveInBackground);
        Assert.False(snapshot.CanPresentSuggestions);
        Assert.False(snapshot.CanAttemptUnattendedExecution);
        Assert.Equal(
            new[] { DesktopAutomationBlockReason.RequestedManualMode },
            snapshot.UnattendedBlockReasons);
    }

    [Fact]
    public void Pause_DowngradesAutomaticButKeepsEyesAndSuggestionsAvailable()
    {
        var policy = Policy(DesktopAutomationUnattendedGate.ValidatedOpen);
        policy.SetRequestedMode(DesktopAutomationMode.Automatic);

        var paused = policy.Pause("user pause");

        Assert.True(paused.IsPaused);
        Assert.Equal("user pause", paused.PauseReason);
        Assert.Null(paused.PauseUntilUtc);
        Assert.Equal(DesktopAutomationMode.Suggestion, paused.EffectiveMode);
        Assert.True(paused.CanObserveInBackground);
        Assert.True(paused.CanPresentSuggestions);
        Assert.False(paused.CanAttemptUnattendedExecution);
        Assert.Contains(DesktopAutomationBlockReason.Paused, paused.UnattendedBlockReasons);

        var resumed = policy.Resume();

        Assert.False(resumed.IsPaused);
        Assert.Equal(DesktopAutomationMode.Automatic, resumed.EffectiveMode);
        Assert.True(resumed.CanAttemptUnattendedExecution);
    }

    [Fact]
    public void TimedPause_ExpiresFromCurrentTimeWithoutBackgroundTimer()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var policy = Policy(
            DesktopAutomationUnattendedGate.ValidatedOpen,
            clock);
        policy.SetRequestedMode(DesktopAutomationMode.Automatic);

        var paused = policy.Pause("maintenance", TimeSpan.FromMinutes(5));
        Assert.Equal(clock.Origin.AddMinutes(5), paused.PauseUntilUtc);
        Assert.True(paused.IsPaused);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(policy.Snapshot.IsPaused);
        Assert.Equal(DesktopAutomationMode.Suggestion, policy.Snapshot.EffectiveMode);

        clock.Advance(TimeSpan.FromMinutes(1));
        var expired = policy.Snapshot;
        Assert.False(expired.IsPaused);
        Assert.Null(expired.PauseReason);
        Assert.Null(expired.PauseUntilUtc);
        Assert.Equal(DesktopAutomationMode.Automatic, expired.EffectiveMode);
    }

    [Fact]
    public void ExecutionFailure_EntersCooldown_AndCooldownExpiresFromClock()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var policy = Policy(
            DesktopAutomationUnattendedGate.ValidatedOpen,
            clock,
            cooldown: TimeSpan.FromMinutes(2),
            fuseThreshold: 3);
        policy.SetRequestedMode(DesktopAutomationMode.Automatic);

        var failed = policy.RecordExecutionFailure();

        Assert.Equal(1, failed.ConsecutiveExecutionFailures);
        Assert.False(failed.IsFuseTripped);
        Assert.True(failed.IsCooldownActive);
        Assert.Equal(clock.Origin.AddMinutes(2), failed.CooldownUntilUtc);
        Assert.Equal(DesktopAutomationExecutionOutcome.Failure, failed.LastExecutionOutcome);
        Assert.Equal(clock.Origin, failed.LastExecutionAtUtc);
        Assert.Equal(DesktopAutomationMode.Suggestion, failed.EffectiveMode);
        Assert.Contains(
            DesktopAutomationBlockReason.CooldownActive,
            failed.UnattendedBlockReasons);

        clock.Advance(TimeSpan.FromMinutes(2));
        var expired = policy.Snapshot;

        Assert.False(expired.IsCooldownActive);
        Assert.Null(expired.CooldownUntilUtc);
        Assert.Equal(DesktopAutomationMode.Automatic, expired.EffectiveMode);
    }

    [Fact]
    public void Success_ResetsUntrippedFailureCount_AndAlsoEntersCooldown()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var policy = Policy(
            DesktopAutomationUnattendedGate.ValidatedOpen,
            clock,
            cooldown: TimeSpan.FromSeconds(30),
            fuseThreshold: 3);
        policy.SetRequestedMode(DesktopAutomationMode.Automatic);
        policy.RecordExecutionFailure();
        clock.Advance(TimeSpan.FromSeconds(30));

        var success = policy.RecordExecutionSuccess();

        Assert.Equal(0, success.ConsecutiveExecutionFailures);
        Assert.False(success.IsFuseTripped);
        Assert.True(success.IsCooldownActive);
        Assert.Equal(clock.Origin.AddSeconds(60), success.CooldownUntilUtc);
        Assert.Equal(DesktopAutomationExecutionOutcome.Success, success.LastExecutionOutcome);
        Assert.Equal(clock.Origin.AddSeconds(30), success.LastExecutionAtUtc);
        Assert.Equal(DesktopAutomationMode.Suggestion, success.EffectiveMode);
    }

    [Fact]
    public void ConsecutiveFailures_TripFuse_AndSuccessDoesNotAutoResetTrippedFuse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
        var policy = Policy(
            DesktopAutomationUnattendedGate.ValidatedOpen,
            clock,
            cooldown: TimeSpan.Zero,
            fuseThreshold: 3);
        policy.SetRequestedMode(DesktopAutomationMode.Automatic);

        policy.RecordExecutionFailure();
        policy.RecordExecutionFailure();
        var third = policy.RecordExecutionFailure();

        Assert.Equal(3, third.ConsecutiveExecutionFailures);
        Assert.True(third.IsFuseTripped);
        Assert.Equal(DesktopAutomationMode.Suggestion, third.EffectiveMode);
        Assert.Contains(DesktopAutomationBlockReason.FuseTripped, third.UnattendedBlockReasons);

        var lateSuccess = policy.RecordExecutionSuccess();

        Assert.True(lateSuccess.IsFuseTripped);
        Assert.Equal(3, lateSuccess.ConsecutiveExecutionFailures);
        Assert.Equal(DesktopAutomationMode.Suggestion, lateSuccess.EffectiveMode);

        var reset = policy.ResetFuse();

        Assert.False(reset.IsFuseTripped);
        Assert.Equal(0, reset.ConsecutiveExecutionFailures);
        Assert.Equal(DesktopAutomationMode.Automatic, reset.EffectiveMode);
    }

    [Fact]
    public void Automatic_CanExposeAllRuntimeBlockersAtOnceInDeterministicOrder()
    {
        var policy = Policy(
            DesktopAutomationUnattendedGate.M3AProvisionalClosed,
            cooldown: TimeSpan.FromMinutes(1),
            fuseThreshold: 1);
        policy.SetRequestedMode(DesktopAutomationMode.Automatic);
        policy.Pause("operator");
        var snapshot = policy.RecordExecutionFailure();

        Assert.Equal(
            new[]
            {
                DesktopAutomationBlockReason.UnattendedWriteGateClosed,
                DesktopAutomationBlockReason.Paused,
                DesktopAutomationBlockReason.CooldownActive,
                DesktopAutomationBlockReason.FuseTripped,
            },
            snapshot.UnattendedBlockReasons);
        Assert.Equal(DesktopAutomationMode.Suggestion, snapshot.EffectiveMode);
    }

    [Fact]
    public void ZeroCooldown_DoesNotCreateArtificialCooldownBlocker()
    {
        var policy = Policy(
            DesktopAutomationUnattendedGate.ValidatedOpen,
            cooldown: TimeSpan.Zero);
        policy.SetRequestedMode(DesktopAutomationMode.Automatic);

        var failed = policy.RecordExecutionFailure();

        Assert.False(failed.IsCooldownActive);
        Assert.Null(failed.CooldownUntilUtc);
        Assert.DoesNotContain(
            DesktopAutomationBlockReason.CooldownActive,
            failed.UnattendedBlockReasons);
    }

    [Fact]
    public void SettingsAndPauseArguments_FailClosedOnInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopAutomationRuntimePolicySettings(TimeSpan.FromSeconds(-1), 3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopAutomationRuntimePolicySettings(TimeSpan.Zero, 0));

        var policy = Policy(DesktopAutomationUnattendedGate.ValidatedOpen);
        Assert.Throws<ArgumentException>(() => policy.Pause("   "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            policy.Pause("bad duration", TimeSpan.Zero));
    }

    private static DesktopAutomationRuntimePolicy Policy(
        DesktopAutomationUnattendedGate gate,
        TimeProvider? timeProvider = null,
        TimeSpan? cooldown = null,
        int fuseThreshold = 3) =>
        new(
            new DesktopAutomationRuntimePolicySettings(
                cooldown ?? TimeSpan.FromSeconds(30),
                fuseThreshold),
            gate,
            timeProvider);

    private sealed class ManualTimeProvider(DateTimeOffset origin) : TimeProvider
    {
        private DateTimeOffset _utcNow = origin;
        public DateTimeOffset Origin { get; } = origin;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
