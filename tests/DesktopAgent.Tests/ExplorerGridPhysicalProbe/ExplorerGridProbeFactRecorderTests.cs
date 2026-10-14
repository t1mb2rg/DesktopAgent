using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

namespace DesktopAgent.Tests.ExplorerGridPhysicalProbe;

public sealed class ExplorerGridProbeFactRecorderTests
{
    private const string Identity = "explorer-topology-v1:AAA";
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FinishedAt = new(2026, 8, 27, 10, 5, 0, TimeSpan.Zero);

    [Fact]
    public async Task PassingReport_WritesMeasuredFactsForEveryDisplay()
    {
        var store = new FakeStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        var report = Report(
            ExplorerGridProbeRunStatus.Passed,
            Identity,
            [Display("DISPLAY1", 93, 122, 16, 2, activeHorizontal: 4, activeVertical: 3, activeCorner: 4),
             Display("DISPLAY2", 93, 122, 16, 2, activeHorizontal: 3, activeVertical: 5, activeCorner: 4)]);

        var result = await recorder.RecordAsync(report);

        Assert.Empty(result.Rejections);
        Assert.Equal(2, result.Facts.Count);
        Assert.Equal(3, store.Actions.Count); // Initialize 先于写入
        Assert.Equal("Initialize", store.Actions[0]);

        var first = store.Appended[0];
        Assert.Equal(ExplorerGridEvidenceSource.Measured, first.Source);
        Assert.Equal("DISPLAY1", first.DeviceName);
        Assert.Equal(new PixelPoint(93, 122), first.Spacing);
        Assert.Equal(new PixelPoint(16, 2), first.Phase);
        Assert.Equal(11, first.SampleCount);
        Assert.Equal(11, first.ConsensusCount);
        Assert.Equal(Identity, first.TopologyFingerprint);
        Assert.Equal(FinishedAt, first.ObservedAtUtc);
        Assert.True(first.CanAuthorizeExecution(Identity));
    }

    [Fact]
    public async Task PartialReport_WritesFactsOnlyForTheDisplaysThatWereActuallyMeasured()
    {
        var store = new FakeStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        var measured = Display("DISPLAY1", 93, 122, 16, 2, 4, 3, 4);
        var unmeasured = Display("DISPLAY2", 93, 122, 16, 2, 0, 0, 0) with
        {
            IsMeasured = false,
            UnmeasuredReason =
                "seed 请求 (-540,730) 的落点被 Explorer 放到了 (2,702)，不在 DISPLAY2 的 WorkArea 内",
            PhaseVerified = false,
            ObservedSpacingConstant = false,
            ObservedPhaseConstant = false,
        };

        var result = await recorder.RecordAsync(Report(
            ExplorerGridProbeRunStatus.Partial,
            Identity,
            [measured, unmeasured]));

        // 逐屏事实：测到的屏写入 Measured 事实，没测到的屏只留原因，绝不被补成可执行几何。
        Assert.Single(result.Facts);
        Assert.Equal("DISPLAY1", result.Facts[0].DeviceName);
        Assert.Single(result.Rejections);
        Assert.Contains("DISPLAY2", result.Rejections[0], StringComparison.Ordinal);
        Assert.Contains("未完成实测", result.Rejections[0], StringComparison.Ordinal);
        Assert.Single(store.Appended);
        Assert.All(store.Appended, fact => Assert.Equal(ExplorerGridEvidenceSource.Measured, fact.Source));
    }

    [Fact]
    public async Task PartialReportWithoutAnyMeasuredDisplay_WritesNothing()
    {
        var store = new FakeStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        var unmeasured = Display("DISPLAY1", 93, 122, 16, 2, 0, 0, 0) with
        {
            IsMeasured = false,
            UnmeasuredReason = "seed 落点被 Explorer 放到了别的显示器",
        };

        var result = await recorder.RecordAsync(Report(
            ExplorerGridProbeRunStatus.Partial,
            Identity,
            unmeasured));

        Assert.Empty(result.Facts);
        Assert.Single(result.Rejections);
        Assert.Empty(store.Appended);
    }

    [Fact]
    public async Task NonPassingReport_WritesNothing()
    {
        var store = new FakeStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        var report = Report(
            ExplorerGridProbeRunStatus.Blocked,
            Identity,
            Display("DISPLAY1", 93, 122, 16, 2, 4, 3, 4));

        var result = await recorder.RecordAsync(report);

        Assert.Empty(result.Facts);
        Assert.Single(result.Rejections);
        Assert.Contains("Physical Gate 未通过", result.Rejections[0], StringComparison.Ordinal);
        Assert.Empty(store.Appended);
    }

    [Fact]
    public async Task ReportWithoutPerDisplayPhaseVerification_WritesNothing()
    {
        var store = new FakeStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        var report = Report(
            ExplorerGridProbeRunStatus.Passed,
            Identity,
            Display("DISPLAY1", 93, 122, 16, 2, 4, 3, 4),
            phaseVerifiedPerDisplay: false);

        var result = await recorder.RecordAsync(report);

        // 相位不再要求跨屏相同，但必须逐屏可信：不可信的相位不能变成 Measured 事实。
        Assert.Empty(result.Facts);
        Assert.Single(result.Rejections);
        Assert.Contains("逐屏相位校验未通过", result.Rejections[0], StringComparison.Ordinal);
        Assert.Empty(store.Appended);
    }

    [Fact]
    public async Task DisplayWithoutEnoughActiveSamples_IsNotWritten_AndIsNotTurnedIntoInference()
    {
        var store = new FakeStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        var report = Report(
            ExplorerGridProbeRunStatus.Passed,
            Identity,
            [Display("DISPLAY1", 93, 122, 16, 2, activeHorizontal: 4, activeVertical: 3, activeCorner: 4),
             Display("DISPLAY2", 93, 122, 16, 2, activeHorizontal: 2, activeVertical: 6, activeCorner: 4)]);

        var result = await recorder.RecordAsync(report);

        // 达不到门槛的显示器只留下"没测到"这一事实，不会被多数投票补成可执行几何。
        Assert.Single(result.Facts);
        Assert.Equal("DISPLAY1", result.Facts[0].DeviceName);
        Assert.Single(result.Rejections);
        Assert.Contains("主动落点样本不足", result.Rejections[0], StringComparison.Ordinal);
        Assert.Single(store.Appended);
        Assert.All(store.Appended, fact => Assert.Equal(ExplorerGridEvidenceSource.Measured, fact.Source));
    }

    [Fact]
    public async Task NonConstantObservation_IsNotWritten()
    {
        var store = new FakeStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        var display = Display("DISPLAY1", 93, 122, 16, 2, 4, 3, 4) with
        {
            ObservedPhaseConstant = false,
        };

        var result = await recorder.RecordAsync(Report(ExplorerGridProbeRunStatus.Passed, Identity, display));

        Assert.Empty(result.Facts);
        Assert.Single(result.Rejections);
        Assert.Empty(store.Appended);
    }

    [Fact]
    public async Task MissingTopologyIdentity_WritesNothing()
    {
        var store = new FakeStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        var result = await recorder.RecordAsync(Report(
            ExplorerGridProbeRunStatus.Passed,
            null,
            Display("DISPLAY1", 93, 122, 16, 2, 4, 3, 4)));

        Assert.Empty(result.Facts);
        Assert.Single(result.Rejections);
        Assert.Contains("拓扑身份", result.Rejections[0], StringComparison.Ordinal);
        Assert.Empty(store.Appended);
    }

    [Fact]
    public async Task WrittenFactAuthorizesOnlyItsOwnTopology_AndStaleFactIsStillReadable()
    {
        var store = new FakeStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        await recorder.RecordAsync(Report(
            ExplorerGridProbeRunStatus.Passed,
            Identity,
            Display("DISPLAY1", 93, 122, 16, 2, 4, 3, 4)));

        var active = await store.GetActiveAsync("DISPLAY1", Identity);
        Assert.NotNull(active);
        Assert.True(active.CanAuthorizeExecution(Identity));

        var afterTopologyChange = await store.GetActiveAsync("DISPLAY1", "explorer-topology-v1:BBB");
        Assert.Null(afterTopologyChange);

        var stale = await store.GetLatestByDeviceAsync("DISPLAY1");
        Assert.NotNull(stale);
        Assert.False(stale.CanAuthorizeExecution("explorer-topology-v1:BBB"));
    }

    [Fact]
    public void Recorder_DependsOnlyOnTheMeasurementStore()
    {
        var parameterTypes = typeof(ExplorerGridProbeFactRecorder)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        // Diagnostics 只写事实；策略与执行 authority 不在这一层。
        Assert.Single(parameterTypes);
        Assert.Contains(typeof(IExplorerGridMeasurementStore), parameterTypes);
    }

    private static ExplorerGridPhysicalProbeReport Report(
        ExplorerGridProbeRunStatus status,
        string? topologyIdentity,
        ExplorerGridProbeDisplayResult display,
        bool phaseVerifiedPerDisplay = true) =>
        Report(status, topologyIdentity, [display], phaseVerifiedPerDisplay);

    private static ExplorerGridPhysicalProbeReport Report(
        ExplorerGridProbeRunStatus status,
        string? topologyIdentity,
        IReadOnlyList<ExplorerGridProbeDisplayResult> displays,
        bool phaseVerifiedPerDisplay = true) =>
        new(
            status,
            StartedAt,
            FinishedAt,
            Array.AsReadOnly(displays.ToArray()),
            Array.AsReadOnly(Array.Empty<ExplorerGridProbeMove>()),
            Array.AsReadOnly(Array.Empty<ExplorerGridProbePassiveSample>()),
            Array.AsReadOnly(Array.Empty<ExplorerGridProbeCleanupResult>()),
            SharedSpacing: true,
            PhaseVerifiedPerDisplay: phaseVerifiedPerDisplay,
            OwnedShortcutsDeleted: true,
            UserIconsUnchanged: true,
            Failure: null)
        {
            TopologyIdentity = topologyIdentity,
        };

    private static ExplorerGridProbeDisplayResult Display(
        string deviceName,
        int spacingX,
        int spacingY,
        int phaseX,
        int phaseY,
        int activeHorizontal,
        int activeVertical,
        int activeCorner) =>
        new(
            deviceName,
            new PixelPoint(spacingX, spacingY),
            new PixelPoint(phaseX, phaseY),
            activeHorizontal,
            activeVertical,
            activeCorner,
            ObservedSpacingConstant: true,
            ObservedPhaseConstant: true)
        {
            ActiveScanSampleCount = activeHorizontal + activeVertical + activeCorner,
            PassiveOccupiedSampleCount = 0,
            ActiveHorizontalSampleCount = activeHorizontal,
            ActiveVerticalSampleCount = activeVertical,
            ActiveCornerSampleCount = activeCorner,
        };

    private sealed class FakeStore : IExplorerGridMeasurementStore
    {
        public List<ExplorerGridMeasurement> Appended { get; } = [];
        public List<string> Actions { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Actions.Add("Initialize");
            return Task.CompletedTask;
        }

        public Task AppendObservationAsync(
            ExplorerGridMeasurement observation,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("Append");
            Appended.Add(observation);
            return Task.CompletedTask;
        }

        public Task<ExplorerGridMeasurement?> GetActiveAsync(
            string deviceName,
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ExplorerGridMeasurement?>(Appended.LastOrDefault(m =>
                m.DeviceName == deviceName && m.TopologyFingerprint == currentTopologyFingerprint));

        public Task<ExplorerGridMeasurement?> GetLatestByDeviceAsync(
            string deviceName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ExplorerGridMeasurement?>(Appended.LastOrDefault(m => m.DeviceName == deviceName));

        public Task<IReadOnlyList<ExplorerGridMeasurement>> ListActiveAsync(
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExplorerGridMeasurement>>(
                Appended.Where(m => m.TopologyFingerprint == currentTopologyFingerprint).ToArray());
    }
}
