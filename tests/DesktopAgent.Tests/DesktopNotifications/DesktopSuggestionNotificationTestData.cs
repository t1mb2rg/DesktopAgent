using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopNotifications;

internal static class DesktopSuggestionNotificationTestData
{
    private static readonly DisplayDescriptor Display = new(
        "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1080),
        96,
        96,
        true);

    public static DesktopLayoutSuggestionLifecycleSnapshot Active(
        DesktopLayoutSuggestion suggestion)
    {
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        return lifecycle.Publish(suggestion);
    }

    public static DesktopLayoutSuggestion CloneWithNewId(
        DesktopLayoutSuggestion source) =>
        new(
            Guid.NewGuid(),
            source.CreatedAtUtc.AddSeconds(1),
            source.SourceObservationId,
            source.PreviousObservationId,
            source.Binding,
            source.SourceChangeSet,
            source.Candidate);

    public static async Task<DesktopLayoutSuggestion> CreateSuggestionAsync()
    {
        var templateId = Guid.NewGuid();
        var template = new DesktopLayoutTemplate(
            templateId,
            "Notification Dispatcher Test",
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

        if (result.Status != DesktopLayoutSuggestionBuildStatus.Suggested
            || result.Suggestion is null)
        {
            throw new InvalidOperationException(
                $"Notification test fixture 无法生成 Suggestion：{result.Status}");
        }

        return result.Suggestion;
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
