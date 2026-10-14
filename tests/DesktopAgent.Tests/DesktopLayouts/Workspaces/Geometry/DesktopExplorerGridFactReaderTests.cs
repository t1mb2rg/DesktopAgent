using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class DesktopExplorerGridFactReaderTests
{
    private static readonly PixelPoint TestSpacing = new(80, 80);
    private static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryReadActiveAsync_ObservationWithNoDisplays_ReturnsNull_AndNeverTouchesTheStore()
    {
        // 身份不可计算时读方必须直接返回 null，不能把"空观测"当成一次可查询的拓扑。
        // 这条 fake 在任何调用上都会抛：如果实现真的访问了 store，测试会先被异常打失败。
        var store = new FakeStore { ThrowOnCall = true };
        var reader = new DesktopExplorerGridFactReader(store);

        var facts = await reader.TryReadActiveAsync(Observation([]));

        Assert.Null(facts);
        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task TryReadActiveAsync_ReturnsTheFactsTheStoreGaveIt_InitializeBeforeListActive()
    {
        var store = new FakeStore();
        var reader = new DesktopExplorerGridFactReader(store);
        var observation = Observation([Display("DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)]);

        store.ActiveFacts.Add(Fact("DISPLAY1", ExplorerGridTopologyIdentity.Compute(false, TestSpacing, observation.Displays), new PixelPoint(20, 30)));
        store.ActiveFacts.Add(Fact("DISPLAY1", ExplorerGridTopologyIdentity.Compute(false, TestSpacing, observation.Displays), new PixelPoint(60, 70)));

        var facts = await reader.TryReadActiveAsync(observation);

        Assert.NotNull(facts);
        Assert.Equal(store.ActiveFacts, facts);
        Assert.Equal(new[] { "Initialize", "ListActive" }, store.Actions);
    }

    [Fact]
    public async Task TryReadActiveAsync_PassesTheIdentityBothDerivationsAgreeOn_NonNullAndNonBlank()
    {
        // 写方与读方同一口径：读方递给 store 的指纹必须等于适配器为同一观测算出的身份，
        // 也等于 Core 自己的 TryComputeFor。两条口径不一致时，写入的事实永远读不到。
        var store = new FakeStore();
        var reader = new DesktopExplorerGridFactReader(store);
        var observation = Observation([Display("DISPLAY1", 0, 0, 1920, 1080, 0, 0, 1920, 1040, true)]);

        var facts = await reader.TryReadActiveAsync(observation);

        Assert.NotNull(facts);
        var passedFingerprint = store.Fingerprints.Single();
        var adapterIdentity = DesktopExplorerWorkspaceGeometryAdapter.ComputeTopologyIdentity(observation);
        var coreIdentity = ExplorerGridTopologyIdentity.TryComputeFor(observation);

        Assert.NotNull(adapterIdentity);
        Assert.NotNull(coreIdentity);
        Assert.False(string.IsNullOrWhiteSpace(passedFingerprint), "递给 store 的拓扑身份不能是空白。");
        Assert.Equal(adapterIdentity, passedFingerprint);
        Assert.Equal(coreIdentity, passedFingerprint);
    }

    [Fact]
    public async Task TryReadActiveAsync_NullObservation_Throws()
    {
        var reader = new DesktopExplorerGridFactReader(new FakeStore());
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.TryReadActiveAsync(null!));
    }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DesktopExplorerGridFactReader(null!));
    }

    private static ExplorerGridMeasurement Fact(
        string deviceName,
        string topologyFingerprint,
        PixelPoint phase)
        => new(
            ExplorerGridEvidenceSource.Measured,
            deviceName,
            TestSpacing,
            phase,
            4,
            4,
            spacingConstant: true,
            phaseConstant: true,
            topologyFingerprint,
            ObservedAtUtc);

    private static DesktopObservation Observation(IReadOnlyList<DisplayDescriptor> displays)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            displays,
            new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled: false,
                TestSpacing,
                ConnectionMethod.FindWindowSW,
                primaryPathException: null),
            [],
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero);

    private static DisplayDescriptor Display(
        string deviceName,
        int left,
        int top,
        int right,
        int bottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        bool isPrimary)
        => new(
            deviceName,
            new PixelRect(left, top, right, bottom),
            new PixelRect(workLeft, workTop, workRight, workBottom),
            96,
            96,
            isPrimary);

    private sealed class FakeStore : IExplorerGridMeasurementStore
    {
        public List<string> Actions { get; } = [];
        public List<string> Fingerprints { get; } = [];
        public List<ExplorerGridMeasurement> ActiveFacts { get; } = [];
        public bool ThrowOnCall { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Actions.Add("Initialize");
            if (ThrowOnCall)
                throw new InvalidOperationException("身份不可计算时不应访问 store。");
            return Task.CompletedTask;
        }

        public Task AppendObservationAsync(
            ExplorerGridMeasurement observation,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("Append");
            if (ThrowOnCall)
                throw new InvalidOperationException("读方不应写入事实。");
            return Task.CompletedTask;
        }

        public Task<ExplorerGridMeasurement?> GetActiveAsync(
            string deviceName,
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("GetActive");
            if (ThrowOnCall)
                throw new InvalidOperationException("读方必须走 ListActive，不能按显示器取单条。");
            return Task.FromResult<ExplorerGridMeasurement?>(null);
        }

        public Task<ExplorerGridMeasurement?> GetLatestByDeviceAsync(
            string deviceName,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("GetLatest");
            if (ThrowOnCall)
                throw new InvalidOperationException("读方不应读取历史观测。");
            return Task.FromResult<ExplorerGridMeasurement?>(null);
        }

        public Task<IReadOnlyList<ExplorerGridMeasurement>> ListActiveAsync(
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("ListActive");
            Fingerprints.Add(currentTopologyFingerprint);
            if (ThrowOnCall)
                throw new InvalidOperationException("身份不可计算时不应访问 store。");
            return Task.FromResult<IReadOnlyList<ExplorerGridMeasurement>>(
                Array.AsReadOnly(ActiveFacts.ToArray()));
        }
    }
}
