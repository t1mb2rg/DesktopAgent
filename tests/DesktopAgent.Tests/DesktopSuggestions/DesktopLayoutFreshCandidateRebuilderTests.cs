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

public sealed class DesktopLayoutFreshCandidateRebuilderTests
{
    private static readonly DisplayDescriptor Display = new(
        "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1080),
        96,
        96,
        true);

    [Fact]
    public void Rebuilder_DoesNotExposeWriterDependency()
    {
        var parameterTypes = typeof(DesktopLayoutFreshCandidateRebuilder)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IDesktopIconPositionWriter), parameterTypes);
    }

    [Fact]
    public async Task SameFreshObservation_RebuildsDistinctCandidateInstances()
    {
        var template = Template(TargetDisplayBinding.ForPrimaryDisplay());
        var rebuilder = Rebuilder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));
        var observation = Observation(80);
        var binding = new DesktopLayoutSuggestionBinding(template.TemplateId);

        var first = await rebuilder.RebuildAsync(binding, observation);
        var second = await rebuilder.RebuildAsync(binding, observation);

        Assert.Equal(DesktopLayoutFreshCandidateRebuildStatus.Ready, first.Status);
        Assert.Equal(DesktopLayoutFreshCandidateRebuildStatus.Ready, second.Status);
        Assert.NotNull(first.Candidate);
        Assert.NotNull(second.Candidate);
        Assert.NotSame(first.Candidate, second.Candidate);
        Assert.Equal(first.Candidate!.MoveRequiredCount, second.Candidate!.MoveRequiredCount);
        Assert.Equal(first.Candidate.ExecutionStepCount, second.Candidate.ExecutionStepCount);
        Assert.Equal(observation.ObservationId, first.ObservationId);
    }

    [Fact]
    public async Task AlreadySatisfiedFreshDesktop_ReturnsNoActionNeeded()
    {
        var template = Template(TargetDisplayBinding.ForPrimaryDisplay());
        var rebuilder = Rebuilder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));

        var result = await rebuilder.RebuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId),
            Observation(0));

        Assert.Equal(DesktopLayoutFreshCandidateRebuildStatus.NoActionNeeded, result.Status);
        Assert.NotNull(result.Candidate);
        Assert.True(result.Candidate!.IsNoOp);
    }

    [Fact]
    public async Task AutoArrangeEnabled_IsRejectedByExistingM2CandidatePlanner()
    {
        var template = Template(TargetDisplayBinding.ForPrimaryDisplay());
        var rebuilder = Rebuilder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));

        var result = await rebuilder.RebuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId),
            Observation(80, autoArrange: true));

        Assert.Equal(DesktopLayoutFreshCandidateRebuildStatus.CandidateRejected, result.Status);
        Assert.Null(result.Candidate);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public async Task MissingTemplateAndIncompleteSelectedTarget_FailClosed()
    {
        var missingId = Guid.NewGuid();
        var missing = Rebuilder(null, null);
        var missingResult = await missing.RebuildAsync(
            new DesktopLayoutSuggestionBinding(missingId),
            Observation(80));
        Assert.Equal(DesktopLayoutFreshCandidateRebuildStatus.TemplateMissing, missingResult.Status);

        var selected = Template(TargetDisplayBinding.ForSelectedTarget());
        var selectedRebuilder = Rebuilder(
            selected,
            DesktopLayoutIconRuleSet.Empty(selected.TemplateId));
        var selectedResult = await selectedRebuilder.RebuildAsync(
            new DesktopLayoutSuggestionBinding(selected.TemplateId),
            Observation(80));
        Assert.Equal(DesktopLayoutFreshCandidateRebuildStatus.BindingIncomplete, selectedResult.Status);
    }

    [Fact]
    public async Task ExactTargetMissing_FailsBeforePreview()
    {
        var template = Template(TargetDisplayBinding.ForExactDeviceName("DISPLAY404"));
        var rebuilder = Rebuilder(template, DesktopLayoutIconRuleSet.Empty(template.TemplateId));

        var result = await rebuilder.RebuildAsync(
            new DesktopLayoutSuggestionBinding(template.TemplateId),
            Observation(80));

        Assert.Equal(
            DesktopLayoutFreshCandidateRebuildStatus.TargetDisplayResolutionFailed,
            result.Status);
        Assert.Null(result.Candidate);
    }

    private static DesktopLayoutFreshCandidateRebuilder Rebuilder(
        DesktopLayoutTemplate? template,
        DesktopLayoutIconRuleSet? ruleSet) =>
        new(
            new TemplateStore(template),
            new RuleStore(ruleSet),
            new DesktopLayoutSnapshotFactory());

    private static DesktopLayoutTemplate Template(TargetDisplayBinding binding)
    {
        var templateId = Guid.NewGuid();
        return new DesktopLayoutTemplate(
            templateId,
            "Fresh Candidate Test",
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

    private static DesktopObservation Observation(int x, bool autoArrange = false)
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
                autoArrange,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                null),
            new[] { icon },
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);
    }

    private sealed class TemplateStore(DesktopLayoutTemplate? template)
        : IDesktopLayoutTemplateStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopLayoutTemplate value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DesktopLayoutTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DesktopLayoutTemplate?>(template?.TemplateId == templateId ? template : null);
        public Task<IReadOnlyList<DesktopLayoutTemplateSummary>> ListAsync(int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DesktopLayoutTemplateSummary>>(Array.Empty<DesktopLayoutTemplateSummary>());
        public Task<bool> DeleteAsync(Guid templateId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RuleStore(DesktopLayoutIconRuleSet? ruleSet)
        : IDesktopLayoutIconRuleStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopLayoutIconRuleSet value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DesktopLayoutIconRuleSet?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DesktopLayoutIconRuleSet?>(ruleSet?.TemplateId == templateId ? ruleSet : null);
        public Task<bool> DeleteAsync(Guid templateId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
