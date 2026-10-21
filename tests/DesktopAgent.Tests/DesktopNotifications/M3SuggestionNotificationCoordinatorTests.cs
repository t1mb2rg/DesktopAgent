using DesktopAgent.Application.DesktopNotifications;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Application.DesktopSuggestions;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Poc;

namespace DesktopAgent.Tests.DesktopNotifications;

public sealed class M3SuggestionNotificationCoordinatorTests
{
    [Fact]
    public async Task StaleIgnore_DoesNotDismissReplacement_AndReconcilesSurfaceToCurrentSuggestion()
    {
        var first = await DesktopSuggestionNotificationTestData.CreateSuggestionAsync();
        var second = DesktopSuggestionNotificationTestData.CloneWithNewId(first);
        var lifecycle = new DesktopLayoutSuggestionLifecycle();

        var runner = new DesktopObservationDiagnosticsRunner(
            new NeverStableObservationSource(),
            new DesktopStateBaselineTracker());
        using var monitoringSession = new M3ReadOnlyMonitoringSession(
            runner,
            new DesktopObservationHintBatchSettings(TimeSpan.Zero, TimeSpan.Zero),
            new NeverHintSourceFactory(),
            new NeverPeriodicHintSource());
        using var suggestionCoordinator = new M3SuggestionMonitoringCoordinator(
            runner,
            monitoringSession,
            new DesktopLayoutSuggestionPipeline(new NeverSuggestionBuilder(), lifecycle),
            new NeverTemplateStore(),
            new NeverRuleStore(),
            new EmptyDisplayReader());

        var sink = new M3DiagnosticsNotificationSink();
        var dispatcher = new DesktopSuggestionNotificationDispatcher(
            new DesktopSuggestionNotificationPolicy(
                new DesktopSuggestionNotificationPolicySettings(TimeSpan.Zero)),
            sink);
        await using var scheduler = new DesktopSuggestionNotificationScheduler(dispatcher);
        using var bridge = new M3SuggestionNotificationCoordinator(
            suggestionCoordinator,
            lifecycle,
            scheduler,
            sink);

        var firstActive = lifecycle.Publish(first);
        await scheduler.ReconcileAsync(firstActive);
        Assert.Equal(first.SuggestionId, sink.Snapshot.CurrentNotification!.SuggestionId);

        // 模拟旧 notification 尚未被新 lifecycle 对账取代时，桌面已经产生了 replacement Suggestion。
        var secondActive = lifecycle.Publish(second);
        Assert.Same(second, secondActive.CurrentSuggestion);
        Assert.Equal(first.SuggestionId, sink.Snapshot.CurrentNotification!.SuggestionId);

        var handled = new TaskCompletionSource<M3NotificationInteractionHandlingResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.InteractionHandled += result => handled.TrySetResult(result);

        bridge.SimulateInteraction(
            first.SuggestionId,
            DesktopSuggestionNotificationActionKind.IgnoreSuggestion);

        var result = await handled.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.LifecycleChanged);
        Assert.Equal(first.SuggestionId, result.Interaction.SuggestionId);
        Assert.Equal(DesktopLayoutSuggestionPresentationState.Active, result.Lifecycle.State);
        Assert.Same(second, result.Lifecycle.CurrentSuggestion);
        Assert.Equal(second.SuggestionId, lifecycle.Snapshot.CurrentSuggestion!.SuggestionId);
        Assert.Equal(second.SuggestionId, sink.Snapshot.CurrentNotification!.SuggestionId);
    }

    private sealed class NeverStableObservationSource : IStableDesktopObservationSource
    {
        public Task<StableDesktopObservationResult> CaptureStableAsync(
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
    }

    private sealed class NeverHintSourceFactory : IM3ReadOnlyMonitoringHintSourceFactory
    {
        public IM3ReadOnlyMonitoringHintSource Create(
            Action<DesktopObservationHintKind, string> publishHint) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
    }

    private sealed class NeverPeriodicHintSource : IPeriodicObservationHintSource
    {
        public Task RunAsync(
            Action<DesktopObservationHintKind, string> publishHint,
            CancellationToken ct) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
    }

    private sealed class NeverSuggestionBuilder : IDesktopLayoutSuggestionBuilder
    {
        public Task<DesktopLayoutSuggestionBuildResult> BuildAsync(
            DesktopLayoutSuggestionBinding binding,
            DesktopStateChangeSet changeSet,
            DesktopObservation currentObservation,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
    }

    private sealed class NeverTemplateStore : IDesktopLayoutTemplateStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
        public Task SaveAsync(DesktopLayoutTemplate template, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
        public Task<DesktopLayoutTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
        public Task<IReadOnlyList<DesktopLayoutTemplateSummary>> ListAsync(int limit = 50, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
        public Task<bool> DeleteAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
    }

    private sealed class NeverRuleStore : IDesktopLayoutIconRuleStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
        public Task SaveAsync(DesktopLayoutIconRuleSet ruleSet, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
        public Task<DesktopLayoutIconRuleSet?> GetAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
        public Task<bool> DeleteAsync(Guid templateId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not expected in notification bridge test.");
    }

    private sealed class EmptyDisplayReader : IDisplayTopologyReader
    {
        public IReadOnlyList<DisplayDescriptor> GetDisplays() => Array.Empty<DisplayDescriptor>();
    }
}
