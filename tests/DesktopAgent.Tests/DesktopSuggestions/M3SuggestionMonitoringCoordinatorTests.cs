using System.Threading.Channels;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Poc;
using DesktopAgent.WindowsInterop.DesktopObservations;

namespace DesktopAgent.Tests.DesktopSuggestions;

public sealed class M3SuggestionMonitoringCoordinatorTests
{
    private static readonly DisplayDescriptor Display = new(
        "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1080),
        96,
        96,
        true);

    [Fact]
    public async Task StartMonitoringAsync_PrimeIsProcessedBeforeBufferedBackgroundRun()
    {
        var template = Template();
        var stableSource = new ScriptedStableSource(
            Stable(Observation(160)),
            Stable(Observation(80)));
        var baseline = new DesktopStateBaselineTracker();
        var runner = new DesktopObservationDiagnosticsRunner(stableSource, baseline);
        var periodic = new IdlePeriodicSource();
        using var session = new M3ReadOnlyMonitoringSession(
            runner,
            new DesktopObservationHintBatchSettings(TimeSpan.Zero, TimeSpan.Zero),
            new DelegateHintSourceFactory(publish =>
                new PublishingHintSource(() =>
                    publish(DesktopObservationHintKind.FileSystem, "startup-file-hint"))),
            periodic);
        var templateStore = new TemplateStore(template);
        var ruleStore = new RuleStore(DesktopLayoutIconRuleSet.Empty(template.TemplateId));
        var pipeline = new DesktopLayoutSuggestionPipeline(
            new DesktopLayoutSuggestionBuilder(
                templateStore,
                ruleStore,
                new DesktopLayoutSnapshotFactory()),
            new DesktopLayoutSuggestionLifecycle());
        using var coordinator = new M3SuggestionMonitoringCoordinator(
            runner,
            session,
            pipeline,
            templateStore,
            ruleStore,
            new FakeDisplayReader());
        coordinator.SetBinding(new DesktopLayoutSuggestionBinding(template.TemplateId));

        var reports = Channel.CreateUnbounded<M3SuggestionProcessingReport>();
        coordinator.SuggestionProcessed += report => reports.Writer.TryWrite(report);

        var start = await coordinator.StartMonitoringAsync();
        var first = await ReadReportAsync(reports.Reader);
        var second = await ReadReportAsync(reports.Reader);

        Assert.Equal(M3ReadOnlyMonitoringStartStatus.Started, start.Monitoring.Status);
        Assert.Equal(M3SuggestionProcessingOrigin.MonitoringPrime, first.Origin);
        Assert.Equal(
            DesktopLayoutSuggestionPipelineStatus.BaselineReestablished,
            first.PipelineResult!.Status);
        Assert.Equal(M3SuggestionProcessingOrigin.Background, second.Origin);
        Assert.Equal(
            DesktopLayoutSuggestionPipelineStatus.Suggested,
            second.PipelineResult!.Status);
        Assert.Equal(
            second.Diagnostics!.BaselineUpdate!.CurrentObservation.ObservationId,
            second.Lifecycle.CurrentSuggestion!.SourceObservationId);

        await coordinator.StopMonitoringAsync();
    }

    [Fact]
    public async Task ManualObservation_FlowsThroughSuggestionPipeline_AndResetRetiresImmediately()
    {
        var template = Template();
        var stableSource = new ScriptedStableSource(
            Stable(Observation(160)),
            Stable(Observation(80)));
        var baseline = new DesktopStateBaselineTracker();
        var runner = new DesktopObservationDiagnosticsRunner(stableSource, baseline);
        using var session = new M3ReadOnlyMonitoringSession(
            runner,
            new DesktopObservationHintBatchSettings(TimeSpan.Zero, TimeSpan.Zero),
            new DelegateHintSourceFactory(_ => new PublishingHintSource()),
            new IdlePeriodicSource());
        var templateStore = new TemplateStore(template);
        var ruleStore = new RuleStore(DesktopLayoutIconRuleSet.Empty(template.TemplateId));
        var pipeline = new DesktopLayoutSuggestionPipeline(
            new DesktopLayoutSuggestionBuilder(
                templateStore,
                ruleStore,
                new DesktopLayoutSnapshotFactory()),
            new DesktopLayoutSuggestionLifecycle());
        using var coordinator = new M3SuggestionMonitoringCoordinator(
            runner,
            session,
            pipeline,
            templateStore,
            ruleStore,
            new FakeDisplayReader());
        coordinator.SetBinding(new DesktopLayoutSuggestionBinding(template.TemplateId));

        var first = await coordinator.RunManualAsync();
        var second = await coordinator.RunManualAsync();

        Assert.Equal(M3SuggestionProcessingOrigin.Manual, first.Origin);
        Assert.Equal(
            DesktopLayoutSuggestionPipelineStatus.BaselineReestablished,
            first.PipelineResult!.Status);
        Assert.Equal(
            DesktopLayoutSuggestionPipelineStatus.Suggested,
            second.PipelineResult!.Status);
        Assert.Equal(
            DesktopLayoutSuggestionPresentationState.Active,
            coordinator.SuggestionSnapshot.State);

        var reset = coordinator.ResetBaseline();

        Assert.Equal(DesktopLayoutSuggestionPresentationState.None, reset.State);
        Assert.Equal(
            DesktopLayoutSuggestionRetirementReason.BaselineReset,
            reset.LastRetiredSuggestion!.Reason);
    }

    [Fact]
    public async Task ListTemplatesAsync_InitializesTemplateAndRuleStoresOnce()
    {
        var template = Template();
        var templateStore = new TemplateStore(template);
        var ruleStore = new RuleStore(DesktopLayoutIconRuleSet.Empty(template.TemplateId));
        var stableSource = new ScriptedStableSource(Stable(Observation(160)));
        var baseline = new DesktopStateBaselineTracker();
        var runner = new DesktopObservationDiagnosticsRunner(stableSource, baseline);
        using var session = new M3ReadOnlyMonitoringSession(
            runner,
            new DesktopObservationHintBatchSettings(TimeSpan.Zero, TimeSpan.Zero),
            new DelegateHintSourceFactory(_ => new PublishingHintSource()),
            new IdlePeriodicSource());
        using var coordinator = new M3SuggestionMonitoringCoordinator(
            runner,
            session,
            new DesktopLayoutSuggestionPipeline(
                new DesktopLayoutSuggestionBuilder(
                    templateStore,
                    ruleStore,
                    new DesktopLayoutSnapshotFactory()),
                new DesktopLayoutSuggestionLifecycle()),
            templateStore,
            ruleStore,
            new FakeDisplayReader());

        var first = await coordinator.ListTemplatesAsync();
        var second = await coordinator.ListTemplatesAsync();

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, templateStore.InitializeCount);
        Assert.Equal(1, ruleStore.InitializeCount);
        Assert.Equal(2, templateStore.ListCount);
    }

    private static async Task<M3SuggestionProcessingReport> ReadReportAsync(
        ChannelReader<M3SuggestionProcessingReport> reader) =>
        await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

    private static DesktopLayoutTemplate Template()
    {
        var id = Guid.NewGuid();
        return new DesktopLayoutTemplate(
            id,
            "Suggestion Coordinator Test",
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

    private static StableDesktopObservationResult Stable(DesktopObservation observation) =>
        new(
            StableDesktopObservationStatus.Stable,
            observation,
            Array.Empty<DesktopObservationCaptureFailure>(),
            Array.Empty<DesktopObservationStabilityIssue>());

    private sealed class ScriptedStableSource(
        params StableDesktopObservationResult[] results)
        : IStableDesktopObservationSource
    {
        private int _index;

        public Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var index = Math.Min(_index++, results.Length - 1);
            return Task.FromResult(results[index]);
        }
    }

    private sealed class DelegateHintSourceFactory(
        Func<Action<DesktopObservationHintKind, string>, IM3ReadOnlyMonitoringHintSource> create)
        : IM3ReadOnlyMonitoringHintSourceFactory
    {
        public IM3ReadOnlyMonitoringHintSource Create(
            Action<DesktopObservationHintKind, string> publishHint) =>
            create(publishHint);
    }

    private sealed class PublishingHintSource(Action? onStart = null)
        : IM3ReadOnlyMonitoringHintSource
    {
        public DesktopFileSystemHintSourceStartResult Start()
        {
            onStart?.Invoke();
            return new DesktopFileSystemHintSourceStartResult(
                true,
                1,
                new[] { "fake-desktop" },
                Array.Empty<string>());
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class IdlePeriodicSource : IPeriodicObservationHintSource
    {
        public async Task RunAsync(
            Action<DesktopObservationHintKind, string> publishHint,
            CancellationToken ct)
        {
            _ = publishHint;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private sealed class FakeDisplayReader : IDisplayTopologyReader
    {
        public IReadOnlyList<DisplayDescriptor> GetDisplays() => new[] { Display };
    }

    private sealed class TemplateStore(DesktopLayoutTemplate template)
        : IDesktopLayoutTemplateStore
    {
        public int InitializeCount { get; private set; }
        public int ListCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task SaveAsync(
            DesktopLayoutTemplate value,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DesktopLayoutTemplate?> GetAsync(
            Guid templateId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DesktopLayoutTemplate?>(
                template.TemplateId == templateId ? template : null);

        public Task<IReadOnlyList<DesktopLayoutTemplateSummary>> ListAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCount++;
            return Task.FromResult<IReadOnlyList<DesktopLayoutTemplateSummary>>(
                new[]
                {
                    new DesktopLayoutTemplateSummary(
                        template.TemplateId,
                        template.Name,
                        template.Version,
                        template.TargetDisplayBinding,
                        template.Zones.Count)
                });
        }

        public Task<bool> DeleteAsync(
            Guid templateId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RuleStore(DesktopLayoutIconRuleSet ruleSet)
        : IDesktopLayoutIconRuleStore
    {
        public int InitializeCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task SaveAsync(
            DesktopLayoutIconRuleSet value,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DesktopLayoutIconRuleSet?> GetAsync(
            Guid templateId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DesktopLayoutIconRuleSet?>(
                ruleSet.TemplateId == templateId ? ruleSet : null);

        public Task<bool> DeleteAsync(
            Guid templateId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
