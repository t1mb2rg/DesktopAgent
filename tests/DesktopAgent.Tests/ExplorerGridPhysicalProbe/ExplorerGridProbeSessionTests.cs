using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

namespace DesktopAgent.Tests.ExplorerGridPhysicalProbe;

/// <summary>
/// 共用会话层：报告排版、落盘，以及"事实写入失败不改变物理扫描结论"这条规矩。
/// 跑扫描本身的正确性由 ExplorerGridPhysicalProbeRunnerTests 覆盖；这里只管后半程。
/// </summary>
public sealed class ExplorerGridProbeSessionTests : IDisposable
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FinishedAt = StartedAt.AddMinutes(1);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"grid-probe-session-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void LogDirectoryFor_UsesLogsDirectoryNextToTheDatabase()
    {
        var databasePath = Path.Combine(_directory, "desktop-agent.db");

        Assert.Equal(
            Path.Combine(_directory, "logs"),
            ExplorerGridProbeSession.LogDirectoryFor(databasePath));
    }

    [Fact]
    public void LogDirectoryFor_RejectsBlankDatabasePath()
        => Assert.Throws<ArgumentException>(() => ExplorerGridProbeSession.LogDirectoryFor("   "));

    [Fact]
    public void FormatFactSummary_ReportsWrittenFactsAndEveryRejection()
    {
        var result = new ExplorerGridProbeFactRecordResult(
            [Measurement("\\\\.\\DISPLAY1", 76, 98, 2, 16, 33)],
            ["\\\\.\\DISPLAY2 没有合格实测样本，跳过。"]);

        var text = ExplorerGridProbeSession.FormatFactSummary(result);

        Assert.Contains("=== 网格事实写入 ===", text, StringComparison.Ordinal);
        Assert.Contains("写入 1 条 Measured 事实", text, StringComparison.Ordinal);
        Assert.Contains("\\\\.\\DISPLAY1: spacing=(76,98) phase=(2,16) samples=33", text, StringComparison.Ordinal);
        Assert.Contains("未写入：\\\\.\\DISPLAY2 没有合格实测样本，跳过。", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatFactSummary_WithoutAnyFact_SaysNothingWasWritten()
    {
        var text = ExplorerGridProbeSession.FormatFactSummary(
            new ExplorerGridProbeFactRecordResult([], ["全部显示器都没有实测样本。"]));

        Assert.Contains("=== 网格事实写入 ===", text, StringComparison.Ordinal);
        Assert.Contains("未写入：全部显示器都没有实测样本。", text, StringComparison.Ordinal);
        Assert.DoesNotContain("条 Measured 事实", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistReport_WritesLatestAndOneStampedCopy()
    {
        var path = ExplorerGridProbeSession.PersistReport("报告正文", _directory);

        var latest = Path.Combine(_directory, "grid-probe-latest.txt");
        Assert.Equal(latest, path);
        Assert.Equal("报告正文", File.ReadAllText(latest));

        var stamped = Directory
            .GetFiles(_directory, "grid-probe-*.txt")
            .Where(file => !string.Equals(file, latest, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(stamped);
        Assert.Equal("报告正文", File.ReadAllText(stamped[0]));
    }

    [Fact]
    public void PersistReport_WhenTheLogPathCannotBeUsed_ReturnsNullInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        var occupiedPath = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(occupiedPath, "occupied");

        Assert.Null(ExplorerGridProbeSession.PersistReport("报告正文", occupiedPath));
    }

    [Fact]
    public async Task RecordFactsAsync_WhenTheStoreThrows_KeepsTheScanConclusionAndReportsWhy()
    {
        var recorder = new ExplorerGridProbeFactRecorder(new ThrowingStore());

        var result = await ExplorerGridProbeSession.RecordFactsAsync(recorder, ReportWithOneMeasuredDisplay());

        Assert.Empty(result.Facts);
        var rejection = Assert.Single(result.Rejections);
        Assert.Contains("事实写入失败", rejection, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), rejection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordFactsAsync_WhenTheStoreAccepts_WritesTheMeasuredFact()
    {
        var store = new RecordingStore();
        var recorder = new ExplorerGridProbeFactRecorder(store);

        var result = await ExplorerGridProbeSession.RecordFactsAsync(recorder, ReportWithOneMeasuredDisplay());

        Assert.Single(result.Facts);
        var appended = Assert.Single(store.Appended);
        Assert.Equal("\\\\.\\DISPLAY1", appended.DeviceName);
        Assert.Equal(ExplorerGridEvidenceSource.Measured, appended.Source);
    }

    private static ExplorerGridMeasurement Measurement(
        string deviceName,
        int spacingX,
        int spacingY,
        int phaseX,
        int phaseY,
        int sampleCount) =>
        new(
            ExplorerGridEvidenceSource.Measured,
            deviceName,
            new PixelPoint(spacingX, spacingY),
            new PixelPoint(phaseX, phaseY),
            sampleCount,
            sampleCount,
            spacingConstant: true,
            phaseConstant: true,
            $"explorer-topology-v1:{new string('a', 8)}",
            StartedAt);

    private static ExplorerGridPhysicalProbeReport ReportWithOneMeasuredDisplay()
    {
        var display = new ExplorerGridProbeDisplayResult(
            "\\\\.\\DISPLAY1",
            new PixelPoint(76, 98),
            new PixelPoint(2, 16),
            HorizontalSampleCount: 16,
            VerticalSampleCount: 14,
            CornerSampleCount: 2,
            ObservedSpacingConstant: true,
            ObservedPhaseConstant: true)
        {
            ActiveScanSampleCount = 32,
            PassiveOccupiedSampleCount = 4,
            ActiveHorizontalSampleCount = 16,
            ActiveVerticalSampleCount = 14,
            ActiveCornerSampleCount = 2,
        };

        return new ExplorerGridPhysicalProbeReport(
            ExplorerGridProbeRunStatus.Passed,
            StartedAt,
            FinishedAt,
            [display],
            Array.Empty<ExplorerGridProbeMove>(),
            Array.Empty<ExplorerGridProbePassiveSample>(),
            Array.Empty<ExplorerGridProbeCleanupResult>(),
            SharedSpacing: true,
            PhaseVerifiedPerDisplay: true,
            OwnedShortcutsDeleted: true,
            UserIconsUnchanged: true,
            Failure: null)
        {
            TopologyIdentity = $"explorer-topology-v1:{new string('a', 8)}",
        };
    }

    private sealed class RecordingStore : IExplorerGridMeasurementStore
    {
        public List<ExplorerGridMeasurement> Appended { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AppendObservationAsync(
            ExplorerGridMeasurement observation,
            CancellationToken cancellationToken = default)
        {
            Appended.Add(observation);
            return Task.CompletedTask;
        }

        public Task<ExplorerGridMeasurement?> GetActiveAsync(
            string deviceName,
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ExplorerGridMeasurement?>(null);

        public Task<ExplorerGridMeasurement?> GetLatestByDeviceAsync(
            string deviceName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ExplorerGridMeasurement?>(null);

        public Task<IReadOnlyList<ExplorerGridMeasurement>> ListActiveAsync(
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExplorerGridMeasurement>>([]);
    }

    private sealed class ThrowingStore : IExplorerGridMeasurementStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AppendObservationAsync(
            ExplorerGridMeasurement observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("数据库不可写。");

        public Task<ExplorerGridMeasurement?> GetActiveAsync(
            string deviceName,
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ExplorerGridMeasurement?>(null);

        public Task<ExplorerGridMeasurement?> GetLatestByDeviceAsync(
            string deviceName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ExplorerGridMeasurement?>(null);

        public Task<IReadOnlyList<ExplorerGridMeasurement>> ListActiveAsync(
            string currentTopologyFingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExplorerGridMeasurement>>([]);
    }
}
