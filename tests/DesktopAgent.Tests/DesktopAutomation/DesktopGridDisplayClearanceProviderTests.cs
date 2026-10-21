using DesktopAgent.Application.DesktopAutomation;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopGeometryEvidence = DesktopAgent.Core.Automation.DesktopGeometryEvidence;

namespace DesktopAgent.Tests.DesktopAutomation;

/// <summary>
/// A 方案（推断默认 + 受控执行 + 探测做可选校准）里"这块屏现在能不能自己动手"的查询口径：
/// 证据（实测网格事实）与授权（用户站定同意）必须同时成立，任何一条缺失都 fail closed。
/// </summary>
public sealed class DesktopGridDisplayClearanceProviderTests
{
    private static readonly PixelPoint TestSpacing = new(80, 80);
    private static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ExplorerGridEvidenceSource.Measured, DesktopGeometryEvidence.Measured)]
    [InlineData(ExplorerGridEvidenceSource.Inferred, DesktopGeometryEvidence.Inferred)]
    public async Task GetClearanceAsync_ReportsTheStoredFactOfTheTargetDisplay(
        ExplorerGridEvidenceSource stored,
        DesktopGeometryEvidence expected)
    {
        var observation = Observation([Display("DISPLAY1")]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(fingerprint);
        var measurements = new FakeMeasurementStore { Active = Fact("DISPLAY1", fingerprint, stored) };
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            measurements,
            new FakeAuthorizationStore());

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(expected, clearance.GeometryEvidence);
        Assert.Equal(new[] { fingerprint }, measurements.Fingerprints);
        Assert.Equal(new[] { "DISPLAY1" }, measurements.DeviceNames);
        Assert.Equal(new[] { "Initialize", "GetActive" }, measurements.Actions);
    }

    [Fact]
    public async Task GetClearanceAsync_MeasuredFactWithGrantedAuthorization_AuthorizesUnattendedExecution()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(fingerprint);
        var authorizations = new FakeAuthorizationStore
        {
            Active = Authorization(ExplorerGridAuthorizationState.Granted, "DISPLAY1", fingerprint),
        };
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore { Active = Fact("DISPLAY1", fingerprint, ExplorerGridEvidenceSource.Measured) },
            authorizations);

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Measured, clearance.GeometryEvidence);
        Assert.True(clearance.IsAuthorizedForUnattendedExecution);
        Assert.Equal(new[] { fingerprint }, authorizations.Fingerprints);
        Assert.Equal(new[] { "Initialize", "GetActive" }, authorizations.Actions);
    }

    [Fact]
    public async Task GetClearanceAsync_MeasuredFactWithoutAnyAuthorization_IsNotAuthorized()
    {
        // 实测过了不等于有人同意过：这是 A 方案里"证据"与"决定"的分界。
        var observation = Observation([Display("DISPLAY1")]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(fingerprint);
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore { Active = Fact("DISPLAY1", fingerprint, ExplorerGridEvidenceSource.Measured) },
            new FakeAuthorizationStore());

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Measured, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public async Task GetClearanceAsync_RevokedAuthorization_IsNotAuthorizedEvenWithAMeasuredFact()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(fingerprint);
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore { Active = Fact("DISPLAY1", fingerprint, ExplorerGridEvidenceSource.Measured) },
            new FakeAuthorizationStore
            {
                Active = Authorization(ExplorerGridAuthorizationState.Revoked, "DISPLAY1", fingerprint),
            });

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public async Task GetClearanceAsync_InferredFactWithGrantedAuthorization_CannotAuthorize()
    {
        // 授权绑定的是"实测过的那套网格"。网格只是推断时，授权没有可依附的对象。
        var observation = Observation([Display("DISPLAY1")]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(fingerprint);
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore { Active = Fact("DISPLAY1", fingerprint, ExplorerGridEvidenceSource.Inferred) },
            new FakeAuthorizationStore
            {
                Active = Authorization(ExplorerGridAuthorizationState.Granted, "DISPLAY1", fingerprint),
            });

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Inferred, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public async Task GetClearanceAsync_FactBelongsToAnotherDisplay_IsUnknown()
    {
        // 事实是逐屏的：另一块屏测过、同意过，不等于这块屏能自动执行。
        var observation = Observation([Display("DISPLAY1")]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(fingerprint);
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore { Active = Fact("DISPLAY2", fingerprint, ExplorerGridEvidenceSource.Measured) },
            new FakeAuthorizationStore
            {
                Active = Authorization(ExplorerGridAuthorizationState.Granted, "DISPLAY2", fingerprint),
            });

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Unknown, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public async Task GetClearanceAsync_FactFromAnotherTopology_IsUnknown()
    {
        // 即使 store 没有按拓扑过滤，读方也必须自己复核：陈旧事实不得授权执行。
        var observation = Observation([Display("DISPLAY1")]);
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore
            {
                Active = Fact(
                    "DISPLAY1",
                    "explorer-topology-v1:stale-fingerprint-from-an-older-desktop-layout",
                    ExplorerGridEvidenceSource.Measured),
            },
            new FakeAuthorizationStore());

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Unknown, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public async Task GetClearanceAsync_AuthorizationFromAnotherTopology_IsNotAuthorized()
    {
        // store 按 device + fingerprint 查询，所以换拓扑后旧的"同意"读不出来（这里 fake 如实模拟：
        // 只有查询指纹与记录指纹一致才返回记录）。
        var observation = Observation([Display("DISPLAY1")]);
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore
            {
                Active = Fact(
                    "DISPLAY1",
                    ExplorerGridTopologyIdentity.TryComputeFor(observation)!,
                    ExplorerGridEvidenceSource.Measured),
            },
            new FakeAuthorizationStore
            {
                Active = Authorization(
                    ExplorerGridAuthorizationState.Granted,
                    "DISPLAY1",
                    "explorer-topology-v1:fingerprint-of-the-previous-display-layout"),
            });

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Measured, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public async Task GetClearanceAsync_NothingStored_IsUnknownAndNotAuthorized()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore(),
            new FakeAuthorizationStore());

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Unknown, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public async Task GetClearanceAsync_ObservationUnavailable_IsUnknown_AndNeverTouchesTheStores()
    {
        var measurements = new FakeMeasurementStore { ThrowOnCall = true };
        var authorizations = new FakeAuthorizationStore { ThrowOnCall = true };
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation: null),
            measurements,
            authorizations);

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Unknown, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
        Assert.Empty(measurements.Actions);
        Assert.Empty(authorizations.Actions);
    }

    [Fact]
    public async Task GetClearanceAsync_ObservationWithoutDisplays_IsUnknown_AndNeverTouchesTheStores()
    {
        // 拓扑身份算不出来时不能查 store：空观测不是一次可查询的拓扑。
        var measurements = new FakeMeasurementStore { ThrowOnCall = true };
        var authorizations = new FakeAuthorizationStore { ThrowOnCall = true };
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(Observation([])),
            measurements,
            authorizations);

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Unknown, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
        Assert.Empty(measurements.Actions);
        Assert.Empty(authorizations.Actions);
    }

    [Fact]
    public async Task GetClearanceAsync_MeasurementStoreThrows_FailsClosed()
    {
        var observation = Observation([Display("DISPLAY1")]);
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore { Failure = new InvalidOperationException("db locked") },
            new FakeAuthorizationStore());

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Unknown, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public async Task GetClearanceAsync_AuthorizationStoreThrows_FailsClosed()
    {
        // 授权读不出来不是"那就当同意过"：整份资格作废。
        var observation = Observation([Display("DISPLAY1")]);
        var fingerprint = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(fingerprint);
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            new FakeMeasurementStore { Active = Fact("DISPLAY1", fingerprint, ExplorerGridEvidenceSource.Measured) },
            new FakeAuthorizationStore { Failure = new InvalidOperationException("db locked") });

        var clearance = await provider.GetClearanceAsync("DISPLAY1");

        Assert.Equal(DesktopGeometryEvidence.Unknown, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public async Task GetClearanceAsync_BlankTargetDisplay_IsUnknown_WithoutReadingTheDesktop()
    {
        var source = new FakeObservationSource(Observation([Display("DISPLAY1")]));
        var provider = new DesktopGridDisplayClearanceProvider(
            source,
            new FakeMeasurementStore(),
            new FakeAuthorizationStore());

        var clearance = await provider.GetClearanceAsync(" ");

        Assert.Equal(DesktopGeometryEvidence.Unknown, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task GetClearanceAsync_UsesTheSameTopologyIdentityAsTheWriter()
    {
        // 写事实的探针与读事实的这里是两条独立口径。它们不一致 = 写得进读不出，
        // 于是"实测通过"永远授权不了执行——这条断言把两条口径钉在一起。
        var observation = Observation([Display("DISPLAY1")]);
        var measurements = new FakeMeasurementStore();
        var authorizations = new FakeAuthorizationStore();
        var provider = new DesktopGridDisplayClearanceProvider(
            new FakeObservationSource(observation),
            measurements,
            authorizations);

        await provider.GetClearanceAsync("DISPLAY1");

        var measurementFingerprint = measurements.Fingerprints.Single();
        var authorizationFingerprint = authorizations.Fingerprints.Single();
        var adapterIdentity = DesktopExplorerWorkspaceGeometryAdapter.ComputeTopologyIdentity(observation);
        var coreIdentity = ExplorerGridTopologyIdentity.TryComputeFor(observation);
        Assert.NotNull(adapterIdentity);
        Assert.NotNull(coreIdentity);
        Assert.False(string.IsNullOrWhiteSpace(measurementFingerprint), "递给 store 的拓扑身份不能是空白。");
        Assert.Equal(adapterIdentity, measurementFingerprint);
        Assert.Equal(coreIdentity, measurementFingerprint);
        // 证据与授权必须用同一个身份查：否则会出现"用 A 时刻的证据配 B 时刻的授权"。
        Assert.Equal(measurementFingerprint, authorizationFingerprint);
    }

    [Theory]
    [InlineData(DesktopGeometryEvidence.Unknown)]
    [InlineData(DesktopGeometryEvidence.Inferred)]
    public void Clearance_ClaimedAuthorizationWithoutMeasuredEvidence_IsDowngraded(
        DesktopGeometryEvidence evidence)
    {
        var clearance = new DesktopGridDisplayClearance(evidence, isAuthorizedForUnattendedExecution: true);

        Assert.Equal(evidence, clearance.GeometryEvidence);
        Assert.False(clearance.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public void Clearance_UndefinedEvidence_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopGridDisplayClearance((DesktopGeometryEvidence)42, isAuthorizedForUnattendedExecution: true));
    }

    [Fact]
    public void Clearance_Unknown_IsUnknownAndNotAuthorized()
    {
        Assert.Equal(DesktopGeometryEvidence.Unknown, DesktopGridDisplayClearance.Unknown.GeometryEvidence);
        Assert.False(DesktopGridDisplayClearance.Unknown.IsAuthorizedForUnattendedExecution);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var observation = Observation([Display("DISPLAY1")]);
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopGridDisplayClearanceProvider(null!, new FakeMeasurementStore(), new FakeAuthorizationStore()));
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopGridDisplayClearanceProvider(
                new FakeObservationSource(observation),
                null!,
                new FakeAuthorizationStore()));
        Assert.Throws<ArgumentNullException>(() =>
            new DesktopGridDisplayClearanceProvider(
                new FakeObservationSource(observation),
                new FakeMeasurementStore(),
                null!));
    }

    private static ExplorerGridMeasurement Fact(
        string deviceName,
        string topologyFingerprint,
        ExplorerGridEvidenceSource source)
        => new(
            source,
            deviceName,
            TestSpacing,
            new PixelPoint(20, 30),
            4,
            4,
            spacingConstant: true,
            phaseConstant: true,
            topologyFingerprint,
            ObservedAtUtc);

    private static ExplorerGridDisplayAuthorization Authorization(
        ExplorerGridAuthorizationState state,
        string deviceName,
        string topologyFingerprint)
        => new(state, deviceName, topologyFingerprint, ObservedAtUtc);

    private static DisplayDescriptor Display(string deviceName)
        => new(
            deviceName,
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            IsPrimary: true);

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

    private sealed class FakeObservationSource(DesktopObservation? observation)
        : IDesktopObservationSource
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

    private sealed class FakeMeasurementStore : IExplorerGridMeasurementStore
    {
        public List<string> Actions { get; } = [];
        public List<string> Fingerprints { get; } = [];
        public List<string> DeviceNames { get; } = [];
        public ExplorerGridMeasurement? Active { get; set; }
        public bool ThrowOnCall { get; set; }
        public Exception? Failure { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Actions.Add("Initialize");
            ThrowIfNeeded();
            return Task.CompletedTask;
        }

        public Task AppendObservationAsync(
            ExplorerGridMeasurement observation,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("Append");
            ThrowIfNeeded();
            return Task.CompletedTask;
        }

        public Task<ExplorerGridMeasurement?> GetActiveAsync(
            string deviceName,
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("GetActive");
            Fingerprints.Add(currentTopologyFingerprint);
            DeviceNames.Add(deviceName);
            ThrowIfNeeded();
            var match = Active is not null && string.Equals(Active.DeviceName, deviceName, StringComparison.Ordinal)
                ? Active
                : null;
            return Task.FromResult(match);
        }

        public Task<ExplorerGridMeasurement?> GetLatestByDeviceAsync(
            string deviceName,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("GetLatest");
            ThrowIfNeeded();
            return Task.FromResult(Active);
        }

        public Task<IReadOnlyList<ExplorerGridMeasurement>> ListActiveAsync(
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("ListActive");
            Fingerprints.Add(currentTopologyFingerprint);
            ThrowIfNeeded();
            IReadOnlyList<ExplorerGridMeasurement> facts = Active is null
                ? []
                : [Active];
            return Task.FromResult(facts);
        }

        private void ThrowIfNeeded()
        {
            if (ThrowOnCall)
                throw new InvalidOperationException("这条路径不应该访问 store。");
            if (Failure is not null)
                throw Failure;
        }
    }

    private sealed class FakeAuthorizationStore : IExplorerGridDisplayAuthorizationStore
    {
        public List<string> Actions { get; } = [];
        public List<string> Fingerprints { get; } = [];
        public List<string> DeviceNames { get; } = [];
        public ExplorerGridDisplayAuthorization? Active { get; set; }
        public bool ThrowOnCall { get; set; }
        public Exception? Failure { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Actions.Add("Initialize");
            ThrowIfNeeded();
            return Task.CompletedTask;
        }

        public Task AppendAsync(
            ExplorerGridDisplayAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("Append");
            ThrowIfNeeded();
            return Task.CompletedTask;
        }

        public Task<ExplorerGridDisplayAuthorization?> GetActiveAsync(
            string deviceName,
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("GetActive");
            Fingerprints.Add(currentTopologyFingerprint);
            DeviceNames.Add(deviceName);
            ThrowIfNeeded();

            // 如实模拟 store 的查询语义：device 与 fingerprint 都必须与记录一致。
            var match = Active is not null
                && string.Equals(Active.DeviceName, deviceName, StringComparison.Ordinal)
                && string.Equals(Active.TopologyFingerprint, currentTopologyFingerprint, StringComparison.Ordinal)
                    ? Active
                    : null;
            return Task.FromResult(match);
        }

        public Task<ExplorerGridDisplayAuthorization?> GetLatestByDeviceAsync(
            string deviceName,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("GetLatest");
            ThrowIfNeeded();
            return Task.FromResult(Active);
        }

        public Task<IReadOnlyList<ExplorerGridDisplayAuthorization>> ListActiveAsync(
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default)
        {
            Actions.Add("ListActive");
            Fingerprints.Add(currentTopologyFingerprint);
            ThrowIfNeeded();
            IReadOnlyList<ExplorerGridDisplayAuthorization> authorizations = Active is null
                ? []
                : [Active];
            return Task.FromResult(authorizations);
        }

        private void ThrowIfNeeded()
        {
            if (ThrowOnCall)
                throw new InvalidOperationException("这条路径不应该访问 store。");
            if (Failure is not null)
                throw Failure;
        }
    }
}
