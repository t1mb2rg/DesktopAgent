using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.DesktopSuggestions;

public sealed class DesktopLayoutSuggestionBuilderTests
{
    private static readonly PixelPoint Spacing = new(80, 80);
    private static readonly DisplayDescriptor PrimaryDisplay = new(
        "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1080),
        96,
        96,
        true);

    [Fact]
    public void Constructor_DoesNotExposeWriterDependency()
    {
        var parameters = typeof(DesktopLayoutSuggestionBuilder)
            .GetConstructors()
            .Single()
            .GetParameters();

        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType == typeof(IDesktopIconPositionWriter));
    }

    [Fact]
    public async Task BuildAsync_MoveRequired_ReturnsSuggestionBackedByM2Candidate()
    {
        var template = Template(TargetDisplayBinding.ForPrimaryDisplay());
        var builder = Builder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));
        var previous = Observation(new PixelPoint(160, 0));
        var current = Observation(new PixelPoint(80, 0));
        var diff = DesktopStateDiffer.Compare(previous, current);

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId),
            diff,
            current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.Suggested, result.Status);
        Assert.True(result.IsSuggested);
        var suggestion = Assert.IsType<DesktopLayoutSuggestion>(result.Suggestion);
        Assert.Same(result.Candidate, suggestion.Candidate);
        Assert.Equal(current.ObservationId, suggestion.SourceObservationId);
        Assert.Equal(previous.ObservationId, suggestion.PreviousObservationId);
        Assert.Equal(template.TemplateId, suggestion.TemplateId);
        Assert.Equal("DISPLAY1", suggestion.TargetDeviceName);
        Assert.True(suggestion.MoveRequiredCount > 0);
        Assert.True(suggestion.ExecutionStepCount > 0);
        Assert.True(suggestion.Candidate.IsExecutable);
        Assert.False(suggestion.Candidate.IsNoOp);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task BuildAsync_ChangedDesktopButTemplateAlreadySatisfied_ReturnsNoActionNeeded()
    {
        var template = Template(TargetDisplayBinding.ForPrimaryDisplay());
        var builder = Builder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));
        var previous = Observation(new PixelPoint(80, 0));
        var current = Observation(new PixelPoint(0, 0));
        var diff = DesktopStateDiffer.Compare(previous, current);

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId),
            diff,
            current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.NoActionNeeded, result.Status);
        Assert.Null(result.Suggestion);
        Assert.NotNull(result.Candidate);
        Assert.True(result.Candidate!.IsNoOp);
    }

    [Fact]
    public async Task BuildAsync_EmptyDiff_DoesNotLoadTemplate()
    {
        var current = Observation(new PixelPoint(80, 0));
        var diff = DesktopStateDiffer.Compare(current, CloneWithNewId(current));
        var templateStore = new FakeTemplateStore(null);
        var ruleStore = new FakeRuleStore(null);
        var builder = new DesktopLayoutSuggestionBuilder(
            templateStore,
            ruleStore,
            new DesktopLayoutSnapshotFactory());

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(Guid.NewGuid()),
            diff,
            CloneWithObservationId(current, diff.CurrentObservationId));

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.NoChanges, result.Status);
        Assert.Equal(0, templateStore.GetCount);
        Assert.Equal(0, ruleStore.GetCount);
    }

    [Fact]
    public async Task BuildAsync_ObservationIdDoesNotMatchDiff_RejectsBeforeStores()
    {
        var previous = Observation(new PixelPoint(160, 0));
        var current = Observation(new PixelPoint(80, 0));
        var diff = DesktopStateDiffer.Compare(previous, current);
        var unrelated = Observation(new PixelPoint(80, 0));
        var templateStore = new FakeTemplateStore(null);
        var ruleStore = new FakeRuleStore(null);
        var builder = new DesktopLayoutSuggestionBuilder(
            templateStore,
            ruleStore,
            new DesktopLayoutSnapshotFactory());

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(Guid.NewGuid()),
            diff,
            unrelated);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.ObservationMismatch, result.Status);
        Assert.Equal(0, templateStore.GetCount);
        Assert.Equal(0, ruleStore.GetCount);
        Assert.Equal(
            DesktopLayoutSuggestionIssueCode.ObservationMismatch,
            Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task BuildAsync_TemplateMissing_IsExplicit()
    {
        var previous = Observation(new PixelPoint(160, 0));
        var current = Observation(new PixelPoint(80, 0));
        var diff = DesktopStateDiffer.Compare(previous, current);
        var binding = new DesktopLayoutSuggestionBinding(Guid.NewGuid());
        var builder = Builder(null, null);

        var result = await builder.BuildAsync(binding, diff, current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.TemplateMissing, result.Status);
        Assert.Equal(
            DesktopLayoutSuggestionIssueCode.TemplateMissing,
            Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task BuildAsync_RuleSetMissing_IsExplicit()
    {
        var template = Template(TargetDisplayBinding.ForPrimaryDisplay());
        var previous = Observation(new PixelPoint(160, 0));
        var current = Observation(new PixelPoint(80, 0));
        var diff = DesktopStateDiffer.Compare(previous, current);
        var builder = Builder(template, null);

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId),
            diff,
            current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.RuleSetMissing, result.Status);
        Assert.Equal(
            DesktopLayoutSuggestionIssueCode.RuleSetMissing,
            Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task BuildAsync_SelectedTargetWithoutConfiguredDevice_IsBindingIncomplete()
    {
        var template = Template(TargetDisplayBinding.ForSelectedTarget());
        var previous = Observation(new PixelPoint(160, 0));
        var current = Observation(new PixelPoint(80, 0));
        var diff = DesktopStateDiffer.Compare(previous, current);
        var builder = Builder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId),
            diff,
            current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.BindingIncomplete, result.Status);
        Assert.Equal(
            DesktopLayoutSuggestionIssueCode.SelectedTargetRequired,
            Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task BuildAsync_SelectedTargetWithConfiguredDevice_UsesExistingM2BindingSemantics()
    {
        var template = Template(TargetDisplayBinding.ForSelectedTarget());
        var previous = Observation(new PixelPoint(160, 0));
        var current = Observation(new PixelPoint(80, 0));
        var diff = DesktopStateDiffer.Compare(previous, current);
        var builder = Builder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId, "DISPLAY1"),
            diff,
            current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.Suggested, result.Status);
        Assert.Equal("DISPLAY1", result.Suggestion!.TargetDeviceName);
    }

    [Fact]
    public async Task BuildAsync_AutoArrangeEnabled_DoesNotPromoteCandidateToSuggestion()
    {
        var template = Template(TargetDisplayBinding.ForPrimaryDisplay());
        var previous = Observation(new PixelPoint(160, 0));
        var current = Observation(new PixelPoint(80, 0), autoArrange: true);
        var diff = DesktopStateDiffer.Compare(previous, current);
        var builder = Builder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId),
            diff,
            current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.CandidateRejected, result.Status);
        Assert.Null(result.Suggestion);
        Assert.Contains(
            result.Issues,
            issue => issue.Message.Contains("AutoArrangeEnabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_ExactTargetMissing_ReturnsPreviewRejected()
    {
        var template = Template(TargetDisplayBinding.ForExactDeviceName("DISPLAY404"));
        var previous = Observation(new PixelPoint(160, 0));
        var current = Observation(new PixelPoint(80, 0));
        var diff = DesktopStateDiffer.Compare(previous, current);
        var builder = Builder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));

        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId),
            diff,
            current);

        Assert.Equal(DesktopLayoutSuggestionBuildStatus.PreviewRejected, result.Status);
        Assert.Equal(
            DesktopLayoutSuggestionIssueCode.TargetDisplayResolutionFailed,
            Assert.Single(result.Issues).Code);
    }

    private static DesktopLayoutSuggestionBuilder Builder(
        DesktopLayoutTemplate? template,
        DesktopLayoutIconRuleSet? ruleSet) =>
        new(
            new FakeTemplateStore(template),
            new FakeRuleStore(ruleSet),
            new DesktopLayoutSnapshotFactory());

    private static DesktopLayoutTemplate Template(TargetDisplayBinding binding)
    {
        var templateId = Guid.NewGuid();
        return new DesktopLayoutTemplate(
            templateId,
            "Suggestion Test",
            1,
            binding,
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
    }

    private static DesktopObservation Observation(
        PixelPoint position,
        bool autoArrange = false)
    {
        var icon = new DesktopIconDescriptor(
            "a",
            "a.txt",
            "c:\\desktop\\a.txt",
            ItemKind.File,
            position,
            PrimaryDisplay.DeviceName,
            false,
            true);
        var capabilities = new DesktopIconPositioningCapabilities(
            autoArrange,
            Spacing,
            ConnectionMethod.FindWindowSW,
            null);

        return new DesktopObservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { PrimaryDisplay },
            capabilities,
            new[] { icon },
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(2));
    }

    private static DesktopObservation CloneWithNewId(DesktopObservation source) =>
        CloneWithObservationId(source, Guid.NewGuid());

    private static DesktopObservation CloneWithObservationId(
        DesktopObservation source,
        Guid observationId) =>
        new(
            observationId,
            source.ObservedAtUtc,
            source.Displays,
            source.PositioningCapabilities,
            source.Icons,
            source.IconReadConnectionMethod,
            source.IconReadTime);

    private sealed class FakeTemplateStore(DesktopLayoutTemplate? template)
        : IDesktopLayoutTemplateStore
    {
        public int GetCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveAsync(
            DesktopLayoutTemplate value,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DesktopLayoutTemplate?> GetAsync(
            Guid templateId,
            CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(
                template is not null && template.TemplateId == templateId
                    ? template
                    : null);
        }

        public Task<IReadOnlyList<DesktopLayoutTemplateSummary>> ListAsync(
            int limit = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DesktopLayoutTemplateSummary>>(
                Array.Empty<DesktopLayoutTemplateSummary>());

        public Task<bool> DeleteAsync(
            Guid templateId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeRuleStore(DesktopLayoutIconRuleSet? ruleSet)
        : IDesktopLayoutIconRuleStore
    {
        public int GetCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveAsync(
            DesktopLayoutIconRuleSet value,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DesktopLayoutIconRuleSet?> GetAsync(
            Guid templateId,
            CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(
                ruleSet is not null && ruleSet.TemplateId == templateId
                    ? ruleSet
                    : null);
        }

        public Task<bool> DeleteAsync(
            Guid templateId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
