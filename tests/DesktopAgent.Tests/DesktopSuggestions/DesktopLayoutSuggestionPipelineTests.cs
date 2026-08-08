using DesktopAgent.Application.DesktopObservations;
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

public sealed class DesktopLayoutSuggestionPipelineTests
{
    private static readonly DisplayDescriptor Display = new(
        "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1080),
        96,
        96,
        true);

    [Fact]
    public void Constructor_DoesNotExposeWriterDependency()
    {
        var parameters = typeof(DesktopLayoutSuggestionPipeline)
            .GetConstructors()
            .Single()
            .GetParameters();

        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(IDesktopLayoutSuggestionBuilder));
        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(DesktopLayoutSuggestionLifecycle));
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IDesktopIconPositionWriter));
    }

    [Fact]
    public void SetBinding_EqualValue_IsNoOpRevision()
    {
        var pipeline = Pipeline(new ScriptedBuilder(RejectedBuild()));
        var binding = new DesktopLayoutSuggestionBinding(Guid.NewGuid(), "DISPLAY1");

        var first = pipeline.SetBinding(binding);
        var second = pipeline.SetBinding(new DesktopLayoutSuggestionBinding(binding.TemplateId, "DISPLAY1"));

        Assert.Equal(1, first.Revision);
        Assert.Equal(1, second.Revision);
        Assert.Equal(binding, second.Binding);
    }

    [Fact]
    public async Task BaselineEstablished_RetiresExistingSuggestion()
    {
        var suggested = await CreateSuggestedBuildAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggested.Suggestion!);
        var pipeline = new DesktopLayoutSuggestionPipeline(new ScriptedBuilder(suggested), lifecycle);
        var current = Observation(80);

        var result = await pipeline.ProcessAsync(BaselineEstablished(current));

        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.BaselineReestablished, result.Status);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, result.Lifecycle.State);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.BaselineReestablished,
            result.Lifecycle.LastRetiredSuggestion!.Reason);
    }

    [Fact]
    public async Task NoChanges_ResumesSuspendedSuggestion()
    {
        var suggested = await CreateSuggestedBuildAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggested.Suggestion!);
        lifecycle.Suspend(DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable);
        var pipeline = new DesktopLayoutSuggestionPipeline(new ScriptedBuilder(suggested), lifecycle);
        var previous = Observation(80);
        var current = CloneWithNewId(previous);

        var result = await pipeline.ProcessAsync(NoChanges(previous, current));

        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.NoChanges, result.Status);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.Active, result.Lifecycle.State);
        Assert.Same(suggested.Suggestion, result.CurrentSuggestion);
    }

    [Fact]
    public async Task UnstableAndUnavailable_SuspendCurrentSuggestion()
    {
        var suggested = await CreateSuggestedBuildAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggested.Suggestion!);
        var pipeline = new DesktopLayoutSuggestionPipeline(new ScriptedBuilder(suggested), lifecycle);

        var unstable = await pipeline.ProcessAsync(Unstable());
        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.ObservationUnstable, unstable.Status);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.Suspended, unstable.Lifecycle.State);
        Assert.Equal(
            DesktopLayoutSuggestionSuspensionReason.ObservationUnstable,
            unstable.Lifecycle.SuspensionReason);

        var unavailable = await pipeline.ProcessAsync(Unavailable());
        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.ObservationUnavailable, unavailable.Status);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.Suspended, unavailable.Lifecycle.State);
        Assert.Equal(
            DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable,
            unavailable.Lifecycle.SuspensionReason);
    }

    [Fact]
    public async Task ChangesDetected_WithoutBinding_RetiresOldAndReturnsNotConfigured()
    {
        var suggested = await CreateSuggestedBuildAsync();
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        lifecycle.Publish(suggested.Suggestion!);
        var builder = new ScriptedBuilder(suggested);
        var pipeline = new DesktopLayoutSuggestionPipeline(builder, lifecycle);
        var previous = Observation(160);
        var current = Observation(80);

        var result = await pipeline.ProcessAsync(Changes(previous, current));

        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.NotConfigured, result.Status);
        Assert.Equal(0, builder.CallCount);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, result.Lifecycle.State);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.DesktopChanged,
            result.Lifecycle.LastRetiredSuggestion!.Reason);
    }

    [Fact]
    public async Task ChangesDetected_SuggestedBuild_PublishesActiveSuggestion()
    {
        var suggested = await CreateSuggestedBuildAsync();
        var builder = new ScriptedBuilder(suggested);
        var pipeline = Pipeline(builder);
        pipeline.SetBinding(suggested.Suggestion!.Binding);
        var previous = Observation(160);
        var current = Observation(80);

        var result = await pipeline.ProcessAsync(Changes(previous, current));

        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.Suggested, result.Status);
        Assert.Equal(1, builder.CallCount);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.Active, result.Lifecycle.State);
        Assert.Same(suggested.Suggestion, result.CurrentSuggestion);
    }

    [Fact]
    public async Task ChangesDetected_NoActionNeeded_LeavesNoActiveSuggestion()
    {
        var builder = new ScriptedBuilder(NoActionBuild());
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var pipeline = new DesktopLayoutSuggestionPipeline(builder, lifecycle);
        pipeline.SetBinding(new DesktopLayoutSuggestionBinding(Guid.NewGuid()));
        var previous = Observation(160);
        var current = Observation(80);

        var result = await pipeline.ProcessAsync(Changes(previous, current));

        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.NoActionNeeded, result.Status);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, result.Lifecycle.State);
        Assert.Null(result.CurrentSuggestion);
    }

    [Fact]
    public async Task ChangesDetected_RejectedBuild_LeavesNoActiveSuggestion()
    {
        var builder = new ScriptedBuilder(RejectedBuild());
        var pipeline = Pipeline(builder);
        pipeline.SetBinding(new DesktopLayoutSuggestionBinding(Guid.NewGuid()));
        var previous = Observation(160);
        var current = Observation(80);

        var result = await pipeline.ProcessAsync(Changes(previous, current));

        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.BuildRejected, result.Status);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, result.Lifecycle.State);
        Assert.Equal(DesktopLayoutSuggestionBuildStatus.TemplateMissing, result.BuildResult!.Status);
    }

    [Fact]
    public async Task BindingChangesDuringAsyncBuild_OldResultIsDiscarded()
    {
        var suggested = await CreateSuggestedBuildAsync();
        var builder = new DelayedBuilder(suggested);
        var lifecycle = new DesktopLayoutSuggestionLifecycle();
        var pipeline = new DesktopLayoutSuggestionPipeline(builder, lifecycle);
        var firstBinding = suggested.Suggestion!.Binding;
        pipeline.SetBinding(firstBinding);
        var previous = Observation(160);
        var current = Observation(80);

        var processTask = pipeline.ProcessAsync(Changes(previous, current));
        await builder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondBinding = new DesktopLayoutSuggestionBinding(Guid.NewGuid());
        var changed = pipeline.SetBinding(secondBinding);
        builder.Release.TrySetResult();
        var result = await processTask;

        Assert.Equal(2, changed.Revision);
        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.BindingChangedDuringBuild, result.Status);
        Assert.Equal(secondBinding, result.BindingSnapshot.Binding);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, result.Lifecycle.State);
        Assert.Null(result.CurrentSuggestion);
    }

    [Fact]
    public async Task BuilderEscapedException_IsContainedAndLeavesNoActiveSuggestion()
    {
        var pipeline = Pipeline(new ThrowingBuilder());
        pipeline.SetBinding(new DesktopLayoutSuggestionBinding(Guid.NewGuid()));
        var previous = Observation(160);
        var current = Observation(80);

        var result = await pipeline.ProcessAsync(Changes(previous, current));

        Assert.Equal(DesktopLayoutSuggestionPipelineStatus.BuilderFailed, result.Status);
        Assert.Contains("boom", result.EscapedException, StringComparison.Ordinal);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, result.Lifecycle.State);
    }

    private static DesktopLayoutSuggestionPipeline Pipeline(IDesktopLayoutSuggestionBuilder builder) =>
        new(builder, new DesktopLayoutSuggestionLifecycle());

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

    private static DesktopObservation CloneWithNewId(DesktopObservation source) =>
        new(
            Guid.NewGuid(),
            source.ObservedAtUtc,
            source.Displays,
            source.PositioningCapabilities,
            source.Icons,
            source.IconReadConnectionMethod,
            source.IconReadTime);

    private static StableDesktopObservationResult Stable(DesktopObservation observation) =>
        new(
            StableDesktopObservationStatus.Stable,
            observation,
            Array.Empty<DesktopObservationCaptureFailure>(),
            Array.Empty<DesktopObservationStabilityIssue>());

    private static DesktopObservationDiagnosticsResult BaselineEstablished(DesktopObservation current) =>
        new(
            DesktopObservationDiagnosticsStatus.BaselineEstablished,
            Stable(current),
            new DesktopStateBaselineUpdateResult(
                DesktopStateBaselineUpdateStatus.BaselineEstablished,
                current,
                null,
                null));

    private static DesktopObservationDiagnosticsResult NoChanges(
        DesktopObservation previous,
        DesktopObservation current)
    {
        var changeSet = DesktopStateDiffer.Compare(previous, current);
        Assert.False(changeSet.HasChanges);
        return new DesktopObservationDiagnosticsResult(
            DesktopObservationDiagnosticsStatus.NoChanges,
            Stable(current),
            new DesktopStateBaselineUpdateResult(
                DesktopStateBaselineUpdateStatus.Compared,
                current,
                previous,
                changeSet));
    }

    private static DesktopObservationDiagnosticsResult Changes(
        DesktopObservation previous,
        DesktopObservation current)
    {
        var changeSet = DesktopStateDiffer.Compare(previous, current);
        Assert.True(changeSet.HasChanges);
        return new DesktopObservationDiagnosticsResult(
            DesktopObservationDiagnosticsStatus.ChangesDetected,
            Stable(current),
            new DesktopStateBaselineUpdateResult(
                DesktopStateBaselineUpdateStatus.Compared,
                current,
                previous,
                changeSet));
    }

    private static DesktopObservationDiagnosticsResult Unstable()
    {
        var result = new StableDesktopObservationResult(
            StableDesktopObservationStatus.Unstable,
            null,
            Array.Empty<DesktopObservationCaptureFailure>(),
            new[]
            {
                new DesktopObservationStabilityIssue(
                    DesktopObservationStabilityIssueCode.IconChanged,
                    "changed",
                    "a")
            });
        return new DesktopObservationDiagnosticsResult(
            DesktopObservationDiagnosticsStatus.Unstable,
            result,
            null);
    }

    private static DesktopObservationDiagnosticsResult Unavailable()
    {
        var result = new StableDesktopObservationResult(
            StableDesktopObservationStatus.Unavailable,
            null,
            new[]
            {
                new DesktopObservationCaptureFailure(
                    DesktopObservationSamplePhase.First,
                    new DesktopObservationCaptureIssue(
                        DesktopObservationCaptureIssueCode.IncompleteDesktopRead,
                        "incomplete"))
            },
            Array.Empty<DesktopObservationStabilityIssue>());
        return new DesktopObservationDiagnosticsResult(
            DesktopObservationDiagnosticsStatus.Unavailable,
            result,
            null);
    }

    private static DesktopLayoutSuggestionBuildResult RejectedBuild() =>
        new(
            DesktopLayoutSuggestionBuildStatus.TemplateMissing,
            null,
            null,
            new[]
            {
                new DesktopLayoutSuggestionIssue(
                    DesktopLayoutSuggestionIssueCode.TemplateMissing,
                    "missing")
            });

    private static DesktopLayoutSuggestionBuildResult NoActionBuild()
    {
        // 通过真实 03A Builder 生成一个合法 NoOp Candidate，避免在 pipeline test 中伪造 M2 类型不变量。
        return CreateNoActionBuildAsync().GetAwaiter().GetResult();
    }

    private static async Task<DesktopLayoutSuggestionBuildResult> CreateNoActionBuildAsync()
    {
        var templateId = Guid.NewGuid();
        var template = Template(templateId);
        var builder = ConcreteBuilder(template);
        var previous = Observation(80);
        var current = Observation(0);
        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(templateId),
            DesktopStateDiffer.Compare(previous, current),
            current);
        Assert.Equal(DesktopLayoutSuggestionBuildStatus.NoActionNeeded, result.Status);
        return result;
    }

    private static async Task<DesktopLayoutSuggestionBuildResult> CreateSuggestedBuildAsync()
    {
        var templateId = Guid.NewGuid();
        var template = Template(templateId);
        var builder = ConcreteBuilder(template);
        var previous = Observation(160);
        var current = Observation(80);
        var result = await builder.BuildAsync(
            new DesktopLayoutSuggestionBinding(templateId),
            DesktopStateDiffer.Compare(previous, current),
            current);
        Assert.Equal(DesktopLayoutSuggestionBuildStatus.Suggested, result.Status);
        return result;
    }

    private static DesktopLayoutSuggestionBuilder ConcreteBuilder(DesktopLayoutTemplate template) =>
        new(
            new TemplateStore(template),
            new RuleStore(DesktopLayoutIconRuleSet.Empty(template.TemplateId)),
            new DesktopLayoutSnapshotFactory());

    private static DesktopLayoutTemplate Template(Guid templateId) =>
        new(
            templateId,
            "Pipeline Test",
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

    private sealed class ScriptedBuilder(DesktopLayoutSuggestionBuildResult result)
        : IDesktopLayoutSuggestionBuilder
    {
        public int CallCount { get; private set; }

        public Task<DesktopLayoutSuggestionBuildResult> BuildAsync(
            DesktopLayoutSuggestionBinding binding,
            DesktopStateChangeSet changeSet,
            DesktopObservation currentObservation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class DelayedBuilder(DesktopLayoutSuggestionBuildResult result)
        : IDesktopLayoutSuggestionBuilder
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DesktopLayoutSuggestionBuildResult> BuildAsync(
            DesktopLayoutSuggestionBinding binding,
            DesktopStateChangeSet changeSet,
            DesktopObservation currentObservation,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return result;
        }
    }

    private sealed class ThrowingBuilder : IDesktopLayoutSuggestionBuilder
    {
        public Task<DesktopLayoutSuggestionBuildResult> BuildAsync(
            DesktopLayoutSuggestionBinding binding,
            DesktopStateChangeSet changeSet,
            DesktopObservation currentObservation,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
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
