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
/// 站定授权的"看得见、收得回"口径：
///
/// 看得见——只有绑定当前显示拓扑、且仍然 Granted 的授权才算当前授权；
/// 读不出来必须说"读不出来"，不能讲成"没有授权"；
/// 收得回——撤销是追加一条 Revoked，历史必须留着，而且撤销必须绑在当前显示设置上；
/// 收回不移动任何桌面项目，也不要求这块屏之前真的被授权过（收回是一个决定，不是一次结算）。
/// </summary>
public sealed class DesktopGridDisplayAuthorizationGovernanceTests
{
    private static readonly PixelPoint TestSpacing = new(80, 80);
    private static readonly DateTimeOffset GrantedAtUtc = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RevokedAtUtc = new(2026, 10, 14, 10, 30, 0, TimeSpan.Zero);

    // ── 看得见 ──

    [Fact]
    public async Task ReadAsync_WithNothingStored_ReportsAvailableWithNoAuthorizations()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationQueryService(
            new FakeObservationSource(observation),
            store);

        var snapshot = await service.ReadAsync();

        Assert.True(snapshot.IsAvailable);
        Assert.Equal(DesktopGridDisplayAuthorizationQueryStatus.Available, snapshot.Status);
        Assert.Equal(fingerprint, snapshot.TopologyFingerprint);
        Assert.Empty(snapshot.Authorizations);
        Assert.Equal(["Initialize", "ListActive"], store.Actions);
    }

    [Fact]
    public async Task ReadAsync_ReportsOnlyTheLatestGrantedDecisionForTheCurrentTopology()
    {
        var observation = Observation([Display("DISPLAY1"), Display("DISPLAY2", isPrimary: false)]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(fingerprint);

        var store = new RecordingAuthorizationStore();
        // DISPLAY1：授权后又收回 → "最新一条决定"是收回，因此它不在当前授权里。
        store.Seed(new ExplorerGridDisplayAuthorization(
            ExplorerGridAuthorizationState.Granted, "DISPLAY1", fingerprint, GrantedAtUtc));
        store.Seed(new ExplorerGridDisplayAuthorization(
            ExplorerGridAuthorizationState.Revoked, "DISPLAY1", fingerprint, RevokedAtUtc));
        // DISPLAY2：仍然授权。
        store.Seed(new ExplorerGridDisplayAuthorization(
            ExplorerGridAuthorizationState.Granted, "DISPLAY2", fingerprint, GrantedAtUtc));
        // DISPLAY3：授权绑的是另一套显示设置 → 对当前环境不成立。
        store.Seed(new ExplorerGridDisplayAuthorization(
            ExplorerGridAuthorizationState.Granted, "DISPLAY3", "explorer-topology-v1:stale", GrantedAtUtc));

        var service = new DesktopGridDisplayAuthorizationQueryService(
            new FakeObservationSource(observation),
            store);

        var snapshot = await service.ReadAsync();

        Assert.True(snapshot.IsAvailable);
        var authorization = Assert.Single(snapshot.Authorizations);
        Assert.Equal("DISPLAY2", authorization.DeviceName);
        Assert.Equal(ExplorerGridAuthorizationState.Granted, authorization.State);
    }

    [Fact]
    public async Task ReadAsync_WhenTheDesktopCannotBeReobserved_ReportsObservationUnavailableAndNeverTouchesTheStore()
    {
        var store = new RecordingAuthorizationStore();
        var source = new FakeObservationSource(observation: null);
        var service = new DesktopGridDisplayAuthorizationQueryService(source, store);

        var snapshot = await service.ReadAsync();

        Assert.False(snapshot.IsAvailable);
        Assert.Equal(DesktopGridDisplayAuthorizationQueryStatus.ObservationUnavailable, snapshot.Status);
        Assert.Empty(snapshot.Authorizations);
        Assert.Equal(1, source.CaptureCount);
        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task ReadAsync_WhenTheObservationThrows_ReportsObservationUnavailable()
    {
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationQueryService(
            new ThrowingObservationSource(new InvalidOperationException("桌面读取炸了。")),
            store);

        var snapshot = await service.ReadAsync();

        Assert.Equal(DesktopGridDisplayAuthorizationQueryStatus.ObservationUnavailable, snapshot.Status);
        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task ReadAsync_WhenTheTopologyFingerprintCannotBeComputed_ReportsTopologyUnavailable()
    {
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationQueryService(
            new FakeObservationSource(Observation([])),
            store);

        var snapshot = await service.ReadAsync();

        Assert.Equal(DesktopGridDisplayAuthorizationQueryStatus.TopologyUnavailable, snapshot.Status);
        Assert.Equal(string.Empty, snapshot.TopologyFingerprint);
        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task ReadAsync_WhenTheStoreFails_ReportsStoreFailedRatherThanNoAuthorizations()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        var store = new RecordingAuthorizationStore
        {
            ListFailure = new InvalidOperationException("库读坏了。"),
        };
        var service = new DesktopGridDisplayAuthorizationQueryService(
            new FakeObservationSource(observation),
            store);

        var snapshot = await service.ReadAsync();

        // “不知道”不等于“没有授权”：界面必须能分辨这两件事。
        Assert.False(snapshot.IsAvailable);
        Assert.Equal(DesktopGridDisplayAuthorizationQueryStatus.StoreFailed, snapshot.Status);
        Assert.Equal(fingerprint, snapshot.TopologyFingerprint);
        Assert.Empty(snapshot.Authorizations);
    }

    [Fact]
    public async Task ReadAsync_WithTheRealStore_SeesWhatTheGrantServiceWrote()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"grid-authorization-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var observation = Observation([Display("DISPLAY1"), Display("DISPLAY2", isPrimary: false)]);
            var source = new FakeObservationSource(observation);
            var store = new SqliteExplorerGridDisplayAuthorizationStore(
                Path.Combine(directory, "desktop-agent.db"));

            var grant = new DesktopGridDisplayAuthorizationGrantService(
                source, store, new FixedTimeProvider(GrantedAtUtc));
            Assert.True((await grant.GrantAsync(["DISPLAY1"])).Succeeded);

            var query = new DesktopGridDisplayAuthorizationQueryService(source, store);
            var snapshot = await query.ReadAsync();

            Assert.True(snapshot.IsAvailable);
            var authorization = Assert.Single(snapshot.Authorizations);
            Assert.Equal("DISPLAY1", authorization.DeviceName);
            Assert.Equal(GrantedAtUtc, authorization.AuthorizedAtUtc);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── 收得回 ──

    [Fact]
    public async Task RevokeAsync_AppendsARevokedRecordPerDisplayBoundToTheCurrentTopology()
    {
        var observation = Observation([Display("DISPLAY1"), Display("DISPLAY2", isPrimary: false)]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationRevokeService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(RevokedAtUtc));

        var outcome = await service.RevokeAsync(["DISPLAY1", "DISPLAY2"]);

        Assert.Equal(DesktopGridDisplayAuthorizationRevokeStatus.Revoked, outcome.Status);
        Assert.True(outcome.Succeeded);
        Assert.Equal(fingerprint, outcome.TopologyFingerprint);
        Assert.Equal(["DISPLAY1", "DISPLAY2"], outcome.RevokedDeviceNames);
        Assert.Equal(["Initialize", "Append", "Append"], store.Actions);
        Assert.All(store.Appended, record =>
        {
            Assert.Equal(ExplorerGridAuthorizationState.Revoked, record.State);
            Assert.Equal(fingerprint, record.TopologyFingerprint);
            Assert.Equal(RevokedAtUtc, record.AuthorizedAtUtc);
        });
    }

    [Fact]
    public async Task RevokeAsync_MakesTheAuthorizationStopAuthorizingAndKeepsTheHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"grid-authorization-revoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var observation = Observation([Display("DISPLAY1")]);
            var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
            Assert.NotNull(fingerprint);

            var source = new FakeObservationSource(observation);
            var store = new SqliteExplorerGridDisplayAuthorizationStore(
                Path.Combine(directory, "desktop-agent.db"));

            var grant = new DesktopGridDisplayAuthorizationGrantService(
                source, store, new FixedTimeProvider(GrantedAtUtc));
            Assert.True((await grant.GrantAsync(["DISPLAY1"])).Succeeded);

            var query = new DesktopGridDisplayAuthorizationQueryService(source, store);
            Assert.Single((await query.ReadAsync()).Authorizations);

            var revoke = new DesktopGridDisplayAuthorizationRevokeService(
                source, store, new FixedTimeProvider(RevokedAtUtc));
            Assert.True((await revoke.RevokeAsync(["DISPLAY1"])).Succeeded);

            // 收回之后：不再构成授权，也不在“当前记住的显示器”里。
            Assert.Empty((await query.ReadAsync()).Authorizations);

            var active = await store.GetActiveAsync("DISPLAY1", fingerprint);
            Assert.NotNull(active);
            Assert.Equal(ExplorerGridAuthorizationState.Revoked, active.State);
            Assert.False(active.AuthorizesUnattendedExecution(fingerprint));

            // 历史仍然可读：撤销是追加，不是删除。
            Assert.Single(await store.ListActiveAsync(fingerprint!));

            // 另一套显示设置下也不会被这条撤销串到：它绑的是当时的拓扑。
            Assert.Null(await store.GetActiveAsync("DISPLAY1", "explorer-topology-v1:stale"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RevokeAsync_ForADisplayThatWasNeverGranted_StillRecordsTheDecision()
    {
        // 收回是一个决定，不是一次结算：它不要求这块屏以前真的被授权过。
        var observation = Observation([Display("DISPLAY1")]);
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationRevokeService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(RevokedAtUtc));

        var outcome = await service.RevokeAsync(["DISPLAY1"]);

        Assert.True(outcome.Succeeded);
        Assert.Single(store.Appended);
    }

    [Fact]
    public async Task RevokeAsync_WithoutAnyUsableDeviceName_NeverTouchesTheDesktop()
    {
        var store = new RecordingAuthorizationStore();
        var source = new FakeObservationSource(Observation([Display("DISPLAY1")]));
        var service = new DesktopGridDisplayAuthorizationRevokeService(
            source, store, new FixedTimeProvider(RevokedAtUtc));

        var outcome = await service.RevokeAsync(["  ", string.Empty]);

        Assert.Equal(DesktopGridDisplayAuthorizationRevokeStatus.NothingRequested, outcome.Status);
        Assert.False(outcome.Succeeded);
        Assert.Empty(outcome.RevokedDeviceNames);
        Assert.Empty(store.Actions);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task RevokeAsync_WhenTheDesktopCannotBeReobserved_WritesNothing()
    {
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationRevokeService(
            new FakeObservationSource(observation: null),
            store,
            new FixedTimeProvider(RevokedAtUtc));

        var outcome = await service.RevokeAsync(["DISPLAY1"]);

        Assert.Equal(DesktopGridDisplayAuthorizationRevokeStatus.ObservationUnavailable, outcome.Status);
        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task RevokeAsync_WhenTheTopologyFingerprintCannotBeComputed_WritesNothing()
    {
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationRevokeService(
            new FakeObservationSource(Observation([])),
            store,
            new FixedTimeProvider(RevokedAtUtc));

        var outcome = await service.RevokeAsync(["DISPLAY1"]);

        Assert.Equal(DesktopGridDisplayAuthorizationRevokeStatus.TopologyUnavailable, outcome.Status);
        Assert.Equal(string.Empty, outcome.TopologyFingerprint);
        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task RevokeAsync_WhenAnAppendFailsAfterTheFirst_ReportsStoreFailedAndThePartialSet()
    {
        var observation = Observation([Display("DISPLAY1"), Display("DISPLAY2", isPrimary: false)]);
        var store = new RecordingAuthorizationStore { FailOnAppendNumber = 2 };
        var service = new DesktopGridDisplayAuthorizationRevokeService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(RevokedAtUtc));

        var outcome = await service.RevokeAsync(["DISPLAY1", "DISPLAY2"]);

        Assert.Equal(DesktopGridDisplayAuthorizationRevokeStatus.StoreFailed, outcome.Status);
        Assert.False(outcome.Succeeded);
        // 部分写入不构成收回：已经落盘的那一条如实报告，但整体结论必须是失败。
        Assert.Equal(["DISPLAY1"], outcome.RevokedDeviceNames);
        Assert.Equal(["DISPLAY1"], store.Appended.Select(static record => record.DeviceName));
    }

    [Fact]
    public async Task RevokeAsync_WithAnAlreadyCancelledToken_Throws()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var store = new RecordingAuthorizationStore();
        var service = new DesktopGridDisplayAuthorizationRevokeService(
            new FakeObservationSource(observation),
            store,
            new FixedTimeProvider(RevokedAtUtc));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RevokeAsync(["DISPLAY1"], new CancellationToken(canceled: true)));

        Assert.Empty(store.Actions);
    }

    [Fact]
    public async Task Constructors_NullDependenciesAndNullDeviceNameLists_Throw()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var source = new FakeObservationSource(observation);
        var store = new RecordingAuthorizationStore();

        Assert.Throws<ArgumentNullException>(() =>
            new DesktopGridDisplayAuthorizationQueryService(null!, store));
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopGridDisplayAuthorizationQueryService(source, null!));

        Assert.Throws<ArgumentNullException>(() =>
            new DesktopGridDisplayAuthorizationRevokeService(null!, store));
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopGridDisplayAuthorizationRevokeService(source, null!));

        var revoke = new DesktopGridDisplayAuthorizationRevokeService(source, store);
        await Assert.ThrowsAsync<ArgumentNullException>(() => revoke.RevokeAsync(null!));
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

    /// <summary>
    /// 与真实 SQLite store 同构的内存替身：ListActive 返回"每块屏在当前拓扑下的最新一条决定"，
    /// 而不是把历史全倒出来——否则查询侧的去重逻辑会被测试掩盖。
    /// </summary>
    private sealed class RecordingAuthorizationStore : IExplorerGridDisplayAuthorizationStore
    {
        public List<string> Actions { get; } = [];

        public List<ExplorerGridDisplayAuthorization> Appended { get; } = [];

        /// <summary>第 N 次 Append（1 起数）抛异常；0 表示不注入失败。</summary>
        public int FailOnAppendNumber { get; set; }

        public Exception? InitializeFailure { get; set; }

        public Exception? ListFailure { get; set; }

        public void Seed(ExplorerGridDisplayAuthorization authorization) => Appended.Add(authorization);

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
        {
            Actions.Add("ListActive");
            if (ListFailure is not null)
                throw ListFailure;

            var latest = Appended
                .Where(record => string.Equals(
                    record.TopologyFingerprint,
                    currentTopologyFingerprint,
                    StringComparison.Ordinal))
                .GroupBy(record => record.DeviceName, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();

            return Task.FromResult<IReadOnlyList<ExplorerGridDisplayAuthorization>>(latest);
        }
    }
}
