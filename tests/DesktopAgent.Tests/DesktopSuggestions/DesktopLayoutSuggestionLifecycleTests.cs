using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopSuggestions;

public sealed class DesktopLayoutSuggestionLifecycleTests
{
    private static readonly PixelPoint Spacing = new(80, 80);
    private static readonly DisplayDescriptor Display = new(
        "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1080),
        96,
        96,
        true);

    [Fact]
    public async Task Publish_SetsActiveSuggestion()
    {
        var suggestion = await BuildSuggestionAsync("a", new PixelPoint(160, 0), new PixelPoint(80, 0));
        var lifecycle = new DesktopLayoutSuggestionLifecycle();

        var snapshot = lifecycle.Publish(suggestion);

        Assert.Equal(DesktopLayoutSuggestionPresentationState.Active, snapshot.State);
        Assert.Same(suggestion, snapshot.CurrentSuggestion);
        Assert.True(snapshot.IsActionablePresentation);
        Assert.Equal(1, snapshot.Revision);
        Assert.Null(snapshot.SuspensionReason);
    }

    [Fact]
    public async Task Suspend_ThenNoChanges_ResumesSameSuggestion()
    {
        var suggestion = await BuildSuggestionAsync("a", new PixelPoint(160, 0), new PixelPoint(80, 0));
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);

        var suspended = lifecycle.Suspend(
            DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable);

        Assert.Equal(DesktopLayoutSuggestionPresentationState.Suspended, suspended.State);
        Assert.Same(suggestion, suspended.CurrentSuggestion);
        Assert.False(suspended.IsActionablePresentation);
        Assert.Equal(
            DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable,
            suspended.SuspensionReason);

        var resumed = lifecycle.ConfirmNoDesktopChanges();

        Assert.Equal(DesktopLayoutSuggestionPresentationState.Active, resumed.State);
        Assert.Same(suggestion, resumed.CurrentSuggestion);
        Assert.True(resumed.IsActionablePresentation);
        Assert.Null(resumed.SuspensionReason);
    }

    [Fact]
    public async Task DesktopChanged_RetiresActiveSuggestion()
    {
        var suggestion = await BuildSuggestionAsync("a", new PixelPoint(160, 0), new PixelPoint(80, 0));
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var newObservationId = Guid.NewGuid();

        var snapshot = lifecycle.MarkDesktopChanged(newObservationId);

        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, snapshot.State);
        Assert.Null(snapshot.CurrentSuggestion);
        var retired = Assert.IsType<DesktopLayoutRetiredSuggestion>(snapshot.LastRetiredSuggestion);
        Assert.Same(suggestion, retired.Suggestion);
        Assert.Equal(DesktopLayoutSuggestionRetirementReason.DesktopChanged, retired.Reason);
        Assert.Equal(newObservationId, retired.NewStableObservationId);
    }

    [Fact]
    public async Task DesktopChanged_RetiresSuspendedSuggestionToo()
    {
        var suggestion = await BuildSuggestionAsync("a", new PixelPoint(160, 0), new PixelPoint(80, 0));
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        lifecycle.Suspend(DesktopLayoutSuggestionSuspensionReason.ObservationUnstable);

        var snapshot = lifecycle.MarkDesktopChanged(Guid.NewGuid());

        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, snapshot.State);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.DesktopChanged,
            snapshot.LastRetiredSuggestion!.Reason);
    }

    [Fact]
    public async Task PublishNewSuggestion_RetiresPreviousAsReplaced()
    {
        var first = await BuildSuggestionAsync("a", new PixelPoint(160, 0), new PixelPoint(80, 0));
        var second = await BuildSuggestionAsync("a", new PixelPoint(240, 0), new PixelPoint(160, 0));
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(first);

        var snapshot = lifecycle.Publish(second);

        Assert.Same(second, snapshot.CurrentSuggestion);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.Active, snapshot.State);
        Assert.Same(first, snapshot.LastRetiredSuggestion!.Suggestion);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.ReplacedByNewSuggestion,
            snapshot.LastRetiredSuggestion.Reason);
        Assert.Equal(second.SourceObservationId, snapshot.LastRetiredSuggestion.NewStableObservationId);
    }

    [Fact]
    public async Task BindingChanged_RetiresCurrentSuggestion()
    {
        var suggestion = await BuildSuggestionAsync("a", new PixelPoint(160, 0), new PixelPoint(80, 0));
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);

        var snapshot = lifecycle.MarkBindingChanged();

        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, snapshot.State);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.BindingChanged,
            snapshot.LastRetiredSuggestion!.Reason);
    }

    [Fact]
    public async Task DismissCurrent_RetiresWithUserReason()
    {
        var suggestion = await BuildSuggestionAsync("a", new PixelPoint(160, 0), new PixelPoint(80, 0));
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);

        var snapshot = lifecycle.DismissCurrent();

        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, snapshot.State);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.UserDismissed,
            snapshot.LastRetiredSuggestion!.Reason);
    }

    [Fact]
    public void SuspendAndConfirm_WhenNoSuggestion_AreNoOps()
    {
        var lifecycle = new DesktopLayoutSuggestionLifecycle();

        var suspended = lifecycle.Suspend(
            DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable);
        var confirmed = lifecycle.ConfirmNoDesktopChanges();

        Assert.Equal(0, suspended.Revision);
        Assert.Equal(0, confirmed.Revision);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, confirmed.State);
    }

    private static async Task<DesktopLayoutSuggestion> BuildSuggestionAsync(
        string stableId,
        PixelPoint previousPosition,
        PixelPoint currentPosition)
    {
        var templateId = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(
            templateId,
            "Lifecycle Test",
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
            new FakeTemplateStore(template),
            new FakeRuleStore(DesktopLayoutIconRuleSet.Empty(templateId)),
            new DesktopLayoutSnapshotFactory());
        var previous = Observation(stableId, previousPosition);
        var current = Observation(stableId, currentPosition);
        var diff = DesktopStateDiffer.Compare(previous, current);

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(templateId),
            diff,
            current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.Suggested, result.Status);
        return result.Suggestion!;
    }

    private static DesktopObservation Observation(string stableId, PixelPoint position)
    {
        var icon = new DesktopIconDescriptor(
            stableId,
            stableId + ".txt",
            "c:\\desktop\\" + stableId + ".txt",
            ItemKind.File,
            position,
            Display.DeviceName,
            false,
            true);
        var capabilities = new DesktopIconPositioningCapabilities(
            false,
            Spacing,
            ConnectionMethod.FindWindowSW,
            null);

        return new DesktopObservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { Display },
            capabilities,
            new[] { icon },
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(2));
    }

    private sealed class FakeTemplateStore(DesktopLayoutTemplate template)
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

    private sealed class FakeRuleStore(DesktopLayoutIconRuleSet ruleSet)
        : IDesktopLayoutIconRuleStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopLayoutIconRuleSet value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DesktopLayoutIconRuleSet?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DesktopLayoutIconRuleSet?>(ruleSet.TemplateId == templateId ? ruleSet : null);
        public Task<bool> DeleteAsync(Guid templateId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
