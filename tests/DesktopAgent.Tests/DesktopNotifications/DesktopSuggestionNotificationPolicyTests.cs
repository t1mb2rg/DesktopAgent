using DesktopAgent.Application.DesktopNotifications;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopNotifications;

public sealed class DesktopSuggestionNotificationPolicyTests
{
    private static readonly DisplayDescriptor Display = new(
        "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1080),
        96,
        96,
        true);

    [Fact]
    public async Task FirstActiveSuggestion_IsPublishedOnce()
    {
        var suggestion = await CreateSuggestionAsync();
        var lifecycle = Active(suggestion);
        var policy = new DesktopSuggestionNotificationPolicy(
            new DesktopSuggestionNotificationPolicySettings(TimeSpan.FromMinutes(10)));
        var now = DateTimeOffset.Parse("2026-08-08T00:00:00Z");

        var first = policy.Evaluate(lifecycle, now);

        Assert.True(first.HasPublish);
        Assert.False(first.HasWithdraw);
        Assert.Equal(suggestion.SuggestionId, first.PublishPayload!.SuggestionId);
        policy.AcknowledgePublished(first.PublishPayload, now);

        var second = policy.Evaluate(lifecycle, now.AddSeconds(30));

        Assert.False(second.HasPublish);
        Assert.False(second.HasWithdraw);
        Assert.Equal(
            DesktopSuggestionNotificationSuppressionReason.AlreadyPublished,
            second.SuppressionReason);
    }

    [Fact]
    public async Task SuspendedSuggestion_IsWithdrawn_AndResumeDoesNotRepublishSameSuggestion()
    {
        var suggestion = await CreateSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var active = lifecycle.Publish(suggestion);
        var policy = new DesktopSuggestionNotificationPolicy(
            new DesktopSuggestionNotificationPolicySettings(TimeSpan.Zero));
        var now = DateTimeOffset.Parse("2026-08-08T00:00:00Z");
        var publish = policy.Evaluate(active, now);
        policy.AcknowledgePublished(publish.PublishPayload!, now);

        var suspended = lifecycle.Suspend(
            DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable);
        var withdraw = policy.Evaluate(suspended, now.AddMinutes(1));

        Assert.Equal(suggestion.SuggestionId, withdraw.WithdrawSuggestionId);
        Assert.Null(withdraw.PublishPayload);
        Assert.Equal(
            DesktopSuggestionNotificationSuppressionReason.SuggestionSuspended,
            withdraw.SuppressionReason);
        policy.AcknowledgeWithdrawn(suggestion.SuggestionId);

        var resumed = lifecycle.ConfirmNoDesktopChanges();
        var afterResume = policy.Evaluate(resumed, now.AddMinutes(2));

        Assert.False(afterResume.HasPublish);
        Assert.False(afterResume.HasWithdraw);
        Assert.Equal(
            DesktopSuggestionNotificationSuppressionReason.AlreadyPublished,
            afterResume.SuppressionReason);
    }

    [Fact]
    public async Task NewSuggestionsInsideCooldown_DeferAndLatestActiveWinsAtEligibility()
    {
        var firstSuggestion = await CreateSuggestionAsync();
        var secondSuggestion = CloneWithNewId(firstSuggestion);
        var thirdSuggestion = CloneWithNewId(firstSuggestion);
        var policy = new DesktopSuggestionNotificationPolicy(
            new DesktopSuggestionNotificationPolicySettings(TimeSpan.FromMinutes(10)));
        var start = DateTimeOffset.Parse("2026-08-08T00:00:00Z");

        var firstPlan = policy.Evaluate(Active(firstSuggestion), start);
        policy.AcknowledgePublished(firstPlan.PublishPayload!, start);

        var secondPlan = policy.Evaluate(Active(secondSuggestion), start.AddMinutes(2));

        Assert.Equal(firstSuggestion.SuggestionId, secondPlan.WithdrawSuggestionId);
        Assert.Null(secondPlan.PublishPayload);
        Assert.Equal(start.AddMinutes(10), secondPlan.DeferredUntilUtc);
        Assert.Equal(
            DesktopSuggestionNotificationSuppressionReason.Cooldown,
            secondPlan.SuppressionReason);
        policy.AcknowledgeWithdrawn(firstSuggestion.SuggestionId);

        var thirdPlan = policy.Evaluate(Active(thirdSuggestion), start.AddMinutes(4));

        Assert.False(thirdPlan.HasWithdraw);
        Assert.Null(thirdPlan.PublishPayload);
        Assert.Equal(start.AddMinutes(10), thirdPlan.DeferredUntilUtc);

        var eligible = policy.Evaluate(Active(thirdSuggestion), start.AddMinutes(10));

        Assert.True(eligible.HasPublish);
        Assert.Equal(thirdSuggestion.SuggestionId, eligible.PublishPayload!.SuggestionId);
        Assert.Null(eligible.DeferredUntilUtc);
    }

    [Fact]
    public async Task NotificationPayload_DoesNotExposeCandidateOrWriterTypes()
    {
        var suggestion = await CreateSuggestionAsync();
        var payload = DesktopSuggestionNotificationPayload.FromLifecycle(Active(suggestion));

        Assert.Equal(suggestion.SuggestionId, payload.SuggestionId);
        Assert.Equal(suggestion.SourceObservationId, payload.SourceObservationId);
        Assert.Equal(suggestion.MoveRequiredCount, payload.MoveRequiredCount);

        var propertyTypes = typeof(DesktopSuggestionNotificationPayload)
            .GetProperties()
            .Select(property => property.PropertyType)
            .ToArray();
        Assert.DoesNotContain(
            propertyTypes,
            type => type.Name.Contains("ExecutionCandidate", StringComparison.Ordinal)
                || type.Name.Contains("ExecutionPlan", StringComparison.Ordinal)
                || type == typeof(IDesktopIconPositionWriter));

        var sinkMethodParameterTypes = typeof(IDesktopSuggestionNotificationSink)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), sinkMethodParameterTypes);
    }

    private static DesktopLayoutSuggestionLifecycleSnapshot Active(
        DesktopLayoutSuggestion suggestion)
    {
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        return lifecycle.Publish(suggestion);
    }

    private static DesktopLayoutSuggestion CloneWithNewId(
        DesktopLayoutSuggestion source) =>
        new(
            Guid.NewGuid(),
            source.CreatedAtUtc.AddSeconds(1),
            source.SourceObservationId,
            source.PreviousObservationId,
            source.Binding,
            source.SourceChangeSet,
            source.Candidate);

    private static async Task<DesktopLayoutSuggestion> CreateSuggestionAsync()
    {
        var templateId = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(
            templateId,
            "Notification Test",
            1,
            TargetDisplayBinding.ForPrimaryDisplay(),
            new[]
            {
                new DesktopLayoutZone(
                    Guid.NewGuid(),
                    "Main",
                    new NormalizedRect(0, 0, 10000, 10000),
                    0,
                    ZonePlacementFlow.ColumnMajor,
                    null)
            });
        var builder = new DesktopLayoutSuggestionBuilder(
            new TemplateStore(template),
            new RuleStore(DesktopLayoutIconRuleSet.Empty(templateId)),
            new DesktopLayoutSnapshotFactory());
        var previous = Observation(160);
        var current = Observation(80);
        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(templateId),
            DesktopStateDiffer.Compare(previous, current),
            current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.Suggested, result.Status);
        return result.Suggestion!;
    }

    private static DesktopObservation Observation(int x)
    {
        var icon = new DesktopIconDescriptor(
            "a",
            "a.txt",
            "c:\\desktop\\a.txt",
            ItemKind.File,
            new PixelPoint(x, 0),
            Display.DeviceName,
            false,
            true);
        return new DesktopObservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { Display },
            new DesktopIconPositioningCapabilities(
                false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                null),
            new[] { icon },
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);
    }

    private sealed class TemplateStore(DesktopLayoutTemplate template)
        : IDesktopLayoutTemplateStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopLayoutTemplate value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DesktopLayoutTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DesktopLayoutTemplate?>(template.TemplateId == templateId ? template : null);
        public Task<IReadOnlyList<DesktopLayoutTemplateSummary>> ListAsync(int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DesktopLayoutTemplateSummary>>(Array.Empty<DesktopLayoutTemplateSummary>());
        public Task<bool> DeleteAsync(Guid templateId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RuleStore(DesktopLayoutIconRuleSet ruleSet)
        : IDesktopLayoutIconRuleStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopLayoutIconRuleSet value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DesktopLayoutIconRuleSet?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DesktopLayoutIconRuleSet?>(ruleSet.TemplateId == templateId ? ruleSet : null);
        public Task<bool> DeleteAsync(Guid templateId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
