using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Tests.DesktopAutomation;

/// <summary>
/// 站定授权签发的口径：只有用户当面确认过的路径会调用它，它自己只负责
/// "把决定绑到写入当时的那套显示设置上"。任何一步不成立都不写、不猜，
/// 而且必须把一个明确的结局回给调用方——"大概记住了"在这里是不允许的。
/// </summary>
public sealed class DesktopGridDisplayAuthorizationGrantServiceTests
{
    private static readonly PixelPoint TestSpacing = new(80, 80);
    private static readonly DateTimeOffset GrantedAtUtc = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task GrantAsync_WritesOneGrantedRecordPerDisplayBoundToTheCurrentTopology()
    {
        var observation = Observation([Display("DISPLAY1"), Display("DISPLAY2", isPrimary: false)]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(fingerprint);
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationGrantService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(GrantedAtUtc));

        var outcome = await service.GrantAsync(["DISPLAY1", "DISPLAY2"]);

        Assert.Equal(DesktopGridDisplayAuthorizationGrantStatus.Granted, outcome.Status);
        Assert.True(outcome.Succeeded);
        Assert.Equal(fingerprint, outcome.TopologyFingerprint);
        Assert.Equal(["DISPLAY1", "DISPLAY2"], outcome.GrantedDeviceNames);
        Assert.Equal(["Initialize", "Append", "Append"], store.Actions);
        Assert.Equal(
            ["DISPLAY1", "DISPLAY2"],
            store.Appended.Select(static record => record.DeviceName));
        Assert.All(store.Appended, record =>
        {
            Assert.Equal(ExplorerGridAuthorizationState.Granted, record.State);
            Assert.Equal(fingerprint, record.TopologyFingerprint);
            Assert.Equal(GrantedAtUtc, record.AuthorizedAtUtc);
        });
    }

    [Fact]
    public async Task GrantAsync_TrimsAndDeduplicatesDeviceNames()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationGrantService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(GrantedAtUtc));

        var outcome = await service.GrantAsync([" DISPLAY1 ", "DISPLAY1", "DISPLAY1"]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["DISPLAY1"], outcome.GrantedDeviceNames);
        Assert.Single(store.Appended);
    }

    [Fact]
    public async Task GrantAsync_WithoutAnyUsableDeviceName_RecordsNothingAndNeverTouchesTheDesktop()
    {
        var store = new RecordingAuthorizationStore();
        var source = new FakeObservationSource(Observation([Display("DISPLAY1")]));
        var service = new DesktopGridDisplayAuthorizationGrantService(
            source,
            store,
            new FixedTimeProvider(GrantedAtUtc));

        var outcome = await service.GrantAsync(["  ", string.Empty]);

        Assert.Equal(DesktopGridDisplayAuthorizationGrantStatus.NothingRequested, outcome.Status);
        Assert.False(outcome.Succeeded);
        Assert.Empty(outcome.GrantedDeviceNames);
        Assert.Empty(store.Actions);
        // 没有决定要记录时，连桌面都不该去读。
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task GrantAsync_WhenTheDesktopCannotBeReobserved_WritesNothing()
    {
        var store = new RecordingAuthorizationStore();
        var source = new FakeObservationSource(observation: null);
        var service = new DesktopGridDisplayAuthorizationGrantService(
            source,
            store,
            new FixedTimeProvider(GrantedAtUtc));

        var outcome = await service.GrantAsync(["DISPLAY1"]);

        Assert.Equal(DesktopGridDisplayAuthorizationGrantStatus.ObservationUnavailable, outcome.Status);
        Assert.Empty(outcome.GrantedDeviceNames);
        Assert.Empty(store.Actions);
        Assert.Equal(1, source.CaptureCount);
    }

    [Fact]
    public async Task GrantAsync_WhenTheObservationThrows_ReportsObservationUnavailable()
    {
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationGrantService(
            new ThrowingObservationSource(new InvalidOperationException("桌面读取炸了。")),
            store,
            new FixedTimeProvider(GrantedAtUtc));

        var outcome = await service.GrantAsync(["DISPLAY1"]);

        Assert.Equal(DesktopGridDisplayAuthorizationGrantStatus.ObservationUnavailable, outcome.Status);
        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task GrantAsync_WhenTheTopologyFingerprintCannotBeComputed_WritesNothing()
    {
        // 一块显示器都没有的观察算不出拓扑指纹：这时授权没有可依附的环境。
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationGrantService(
            new FakeObservationSource(Observation([])),
            store,
            new FixedTimeProvider(GrantedAtUtc));

        var outcome = await service.GrantAsync(["DISPLAY1"]);

        Assert.Equal(DesktopGridDisplayAuthorizationGrantStatus.TopologyUnavailable, outcome.Status);
        Assert.Equal(string.Empty, outcome.TopologyFingerprint);
        Assert.Empty(outcome.GrantedDeviceNames);
        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task GrantAsync_WhenTheStoreCannotBeInitialized_ReportsStoreFailed()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var store = new RecordingAuthorizationStore
        {
            InitializeFailure = new InvalidOperationException("库打不开。"),
        };
        var service = new DesktopGridDisplayAuthorizationGrantService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(GrantedAtUtc));

        var outcome = await service.GrantAsync(["DISPLAY1"]);

        Assert.Equal(DesktopGridDisplayAuthorizationGrantStatus.StoreFailed, outcome.Status);
        Assert.False(outcome.Succeeded);
        Assert.Empty(outcome.GrantedDeviceNames);
        // 算得出的指纹照样带回去：调用方要能说明"当时这套显示设置是确定的"。
        Assert.Equal(ExplorerGridTopologyIdentity.TryComputeFor(observation), outcome.TopologyFingerprint);
    }

    [Fact]
    public async Task GrantAsync_WhenAnAppendFailsAfterTheFirstOne_ReportsStoreFailedAndNeverClaimsSuccess()
    {
        var observation = Observation([Display("DISPLAY1"), Display("DISPLAY2", isPrimary: false)]);
        var store = new RecordingAuthorizationStore { FailOnAppendNumber = 2 };
        var service = new DesktopGridDisplayAuthorizationGrantService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(GrantedAtUtc));

        var outcome = await service.GrantAsync(["DISPLAY1", "DISPLAY2"]);

        Assert.Equal(DesktopGridDisplayAuthorizationGrantStatus.StoreFailed, outcome.Status);
        Assert.False(outcome.Succeeded);
        // 部分写入不构成授权：已经落盘的那一条如实报告，但整体结论必须是失败。
        Assert.Equal(["DISPLAY1"], outcome.GrantedDeviceNames);
        Assert.Equal(["DISPLAY1"], store.Appended.Select(static record => record.DeviceName));
    }

    [Fact]
    public async Task GrantAsync_GrantingTwiceForTheSameDisplay_AppendsAgainInsteadOfReplacingHistory()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationGrantService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(GrantedAtUtc));

        Assert.True((await service.GrantAsync(["DISPLAY1"])).Succeeded);
        Assert.True((await service.GrantAsync(["DISPLAY1"])).Succeeded);

        Assert.Equal(2, store.Appended.Count);
    }

    [Fact]
    public async Task GrantAsync_WritesRecordsTheStoreCanActuallyReadBackForTheSameTopology()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"grid-authorization-grant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var observation = Observation([Display("DISPLAY1")]);
            var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
            Assert.NotNull(fingerprint);

            var store = new SqliteExplorerGridDisplayAuthorizationStore(
                Path.Combine(directory, "desktop-agent.db"));
            var service = new DesktopGridDisplayAuthorizationGrantService(
                new FakeObservationSource(observation),
                store,
                new FixedTimeProvider(GrantedAtUtc));

            var outcome = await service.GrantAsync(["DISPLAY1"]);

            Assert.True(outcome.Succeeded);
            var active = await store.GetActiveAsync("DISPLAY1", fingerprint);
            Assert.NotNull(active);
            Assert.Equal(ExplorerGridAuthorizationState.Granted, active.State);
            Assert.Equal(GrantedAtUtc, active.AuthorizedAtUtc);
            Assert.True(active.AuthorizesUnattendedExecution(fingerprint));

            // 授权是环境绑定的：同一块屏在另一套拓扑下不成立，别的屏也不成立。
            const string StaleFingerprint = "explorer-topology-v1:stale";
            Assert.False(active.AuthorizesUnattendedExecution(StaleFingerprint));
            Assert.Null(await store.GetActiveAsync("DISPLAY1", StaleFingerprint));
            Assert.Null(await store.GetActiveAsync("DISPLAY2", fingerprint));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GrantAsync_WithAnAlreadyCancelledToken_Throws()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationGrantService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(GrantedAtUtc));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GrantAsync(["DISPLAY1"], new CancellationToken(canceled: true)));

        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task Constructor_NullDependencies_Throw()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var source = new FakeObservationSource(observation);
        var store = new RecordingAuthorizationStore();

        Assert.Throws<ArgumentNullException>(() =>
            new DesktopGridDisplayAuthorizationGrantService(null!, store));
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopGridDisplayAuthorizationGrantService(source, null!));

        // 授权列表本身也不允许为 null：签发必须有明确的接收者。
        var service = new DesktopGridDisplayAuthorizationGrantService(source, store);
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.GrantAsync(null!));
    }

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

    private static DisplayDescriptor Display(string deviceName, bool isPrimary = true)
        => new(
            deviceName,
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            isPrimary);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeObservationSource(DesktopObservation? observation) : IDesktopObservationSource
    {
        public int CaptureCount { get; private set; }

        public Task<DesktopObservationCaptureResult> CaptureAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(observation is null
                ? DesktopObservationCaptureResult.Unavailable(
                    new DesktopObservationCaptureIssue(
                        DesktopObservationCaptureIssueCode.ShellObservationFailed,
                        "capture unavailable"))
                : DesktopObservationCaptureResult.Captured(observation));
        }
    }

    private sealed class ThrowingObservationSource(Exception failure) : IDesktopObservationSource
    {
        public Task<DesktopObservationCaptureResult> CaptureAsync(
            CancellationToken cancellationToken = default)
            => Task.FromException<DesktopObservationCaptureResult>(failure);
    }

    private sealed class RecordingAuthorizationStore : IExplorerGridDisplayAuthorizationStore
    {
        public List<string> Actions { get; } = [];

        public List<ExplorerGridDisplayAuthorization> Appended { get; } = [];

        /// <summary>第 N 次 Append（1 起数）抛异常；0 表示不注入失败。</summary>
        public int FailOnAppendNumber { get; set; }

        public Exception? InitializeFailure { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Actions.Add("Initialize");
            if (InitializeFailure is not null)
                throw InitializeFailure;
            return Task.CompletedTask;
        }

        public Task AppendAsync(
            ExplorerGridDisplayAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("Append");
            if (FailOnAppendNumber > 0 && Appended.Count + 1 == FailOnAppendNumber)
                throw new InvalidOperationException("模拟授权写入失败。");
            Appended.Add(authorization);
            return Task.CompletedTask;
        }

        public Task<ExplorerGridDisplayAuthorization?> GetActiveAsync(
            string deviceName,
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Appended.LastOrDefault(record =>
                string.Equals(record.DeviceName, deviceName, StringComparison.Ordinal) &&
                string.Equals(record.TopologyFingerprint, currentTopologyFingerprint, StringComparison.Ordinal)));

        public Task<ExplorerGridDisplayAuthorization?> GetLatestByDeviceAsync(
            string deviceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Appended.LastOrDefault(record =>
                string.Equals(record.DeviceName, deviceName, StringComparison.Ordinal)));

        public Task<IReadOnlyList<ExplorerGridDisplayAuthorization>> ListActiveAsync(
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExplorerGridDisplayAuthorization>>(
                Appended
                    .Where(record => string.Equals(
                        record.TopologyFingerprint,
                        currentTopologyFingerprint,
                        StringComparison.Ordinal))
                    .ToArray());
    }
}
