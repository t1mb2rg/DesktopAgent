using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopSuggestions;

public sealed class DesktopLayoutSuggestionLifecycleBaselineTests
{
    [Fact]
    public async Task BaselineReestablished_RetiresCurrentSuggestion()
    {
        var suggestion = await BuildSuggestionAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggestion);
        var newBaselineObservationId = Guid.NewGuid();

        var snapshot = lifecycle.MarkBaselineReestablished(newBaselineObservationId);

        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, snapshot.State);
        Assert.Null(snapshot.CurrentSuggestion);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.BaselineReestablished,
            snapshot.LastRetiredSuggestion!.Reason);
        Assert.Equal(
            newBaselineObservationId,
            snapshot.LastRetiredSuggestion.NewStableObservationId);
    }

    private static async Task<DesktopLayoutSuggestion> BuildSuggestionAsync()
    {
        var display = new DisplayDescriptor(
            "DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1080),
            96,
            96,
            true);
        var templateId = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(
            templateId,
            "Baseline Lifecycle Test",
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

        DesktopObservation Obs(int x)
        {
            var icon = new DesktopIconDescriptor(
                "a",
                "a.txt",
                "c:\\desktop\\a.txt",
                ItemKind.File,
                new PixelPoint(x, 0),
                display.DeviceName,
                false,
                true);
            return new DesktopObservation(
                Guid.NewGuid(),
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

        var previous = Obs(160);
        var current = Obs(80);
        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(templateId),
            DesktopStateDiffer.Compare(previous, current),
            current);
        Assert.Equal(DesktopLayoutSuggestionBuildStatus.Suggested, result.Status);
        return result.Suggestion!;
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
