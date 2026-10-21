using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class PersistedRecoveryCoordinatorStableObservationTests : IDisposable
{
    private const string TD = @"\\.\DISPLAY1";
    private static readonly PixelRect WA = new(0, 0, 1920, 1080);
    private static readonly PixelPoint A0 = new(160, 80);
    private static readonly PixelPoint A1 = new(240, 80);
    private static readonly PixelPoint B0 = new(160, 160);
    private static readonly PixelPoint B1 = new(240, 160);

    private readonly string _tempDir;
    private readonly string _dbPath;

    public PersistedRecoveryCoordinatorStableObservationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Tests.RecoveryR3.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task RelevantIconChangesBetweenSamples_FailsClosedWithoutWriter()
    {
        var plan = MixedPlan();
        var seed = await SeedAsync(plan);
        var reader = new SequencedReader(
            Read(("a", A1), ("b", B0)),
            Read(("a", A0), ("b", B0)));
        var writer = new CountingWriter();
        var topology = new SequencedTopologyReader(Display());
        var coordinator = Coordinator(reader, writer, topology);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RecoveryObservationNotStable", result.RecoveryFailureType);
        Assert.Equal(0, writer.CallCount);
    }

    [Fact]
    public async Task TopologyChangesAfterStableObservationBeforeWriter_FailsClosedWithoutWriter()
    {
        var plan = MixedPlan();
        var seed = await SeedAsync(plan);
        var stableRead = Read(("a", A1), ("b", B0));
        var reader = new SequencedReader(stableRead, stableRead);
        var writer = new CountingWriter();

        var changed = new DisplayDescriptor(
            TD,
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(0, 0, 2560, 1400),
            96,
            96,
            true);
        var topology = new SequencedTopologyReader(Display(), Display(), changed);
        var coordinator = Coordinator(reader, writer, topology);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RecoveryTopologyChangedBeforeWrite", result.RecoveryFailureType);
        Assert.Equal(0, writer.CallCount);
    }

    [Fact]
    public async Task PostWriteObservationBecomesUnstable_FailsClosedAfterSingleWriter()
    {
        var plan = MixedPlan();
        var seed = await SeedAsync(plan);
        var mixed = Read(("a", A1), ("b", B0));
        var afterWrite = Read(("a", A0), ("b", B0));
        var externallyChanged = Read(("a", A1), ("b", B0));
        var reader = new SequencedReader(mixed, mixed, afterWrite, externallyChanged);
        var writer = new CountingWriter();
        var topology = new SequencedTopologyReader(Display());
        var coordinator = Coordinator(reader, writer, topology);

        var result = await coordinator.RecoverAsync(seed);

        Assert.Equal(TargetDisplayLayoutExecutionRunStatus.PositionUncertain, result.Status);
        Assert.Equal("RecoveryPostWriteObservationNotStable", result.RecoveryFailureType);
        Assert.Equal(1, writer.CallCount);
    }

    private PersistedRecoveryCoordinator Coordinator(
        IDesktopIconReader reader,
        IDesktopIconPositionWriter writer,
        IDisplayTopologyReader topology)
        => new(
            new SqliteRecoverySessionStore(_dbPath),
            topology,
            reader,
            writer,
            new StaThreadExecutor());

    private async Task<RecoverySessionRecord> SeedAsync(TargetDisplayLayoutExecutionPlan executionPlan)
    {
        var store = new SqliteRecoverySessionStore(_dbPath);
        await store.InitializeAsync();

        var items = executionPlan.Steps.Select(step => new TargetDisplayLayoutRestorePlanItem(
            step.StableId,
            step.DisplayName,
            step.ToPosition,
            step.FromPosition,
            TD,
            TargetDisplayLayoutRestoreItemState.MoveRequired,
            true,
            false)).ToArray();

        var restorePlan = new TargetDisplayLayoutRestorePlan(
            TD,
            items.Length,
            0,
            items.Length,
            0,
            0,
            false,
            false,
            false,
            items,
            Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            items.Select(item => item.StableId).ToArray());

        var record = new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = Guid.NewGuid(),
            TargetDeviceName = TD,
            StartedAtUtc = DateTimeOffset.UtcNow,
            SourceRestorePlan = restorePlan,
            ExecutionPlan = executionPlan,
            Lifecycle = RecoverySessionLifecycle.Executing,
            ForwardAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            RollbackAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            CompletedForwardStepIndexes = Array.Empty<int>(),
            CompletedRollbackSourceStepIndexes = Array.Empty<int>(),
            Revision = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        record.Validate();
        await store.SaveAsync(record);
        return (await store.GetAsync(record.SessionId))!;
    }

    private static TargetDisplayLayoutExecutionPlan MixedPlan()
        => new(
            TD,
            true,
            false,
            new[]
            {
                Step(0, "a", A0, A1),
                Step(1, "b", B0, B1),
            },
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

    private static TargetDisplayLayoutExecutionStep Step(
        int index,
        string stableId,
        PixelPoint from,
        PixelPoint to)
        => new(
            index,
            stableId,
            $"N_{stableId}",
            from,
            to,
            TargetDisplayLayoutExecutionStepKind.MoveToTarget,
            TD);

    private static DesktopIconReadResult Read(
        params (string StableId, PixelPoint Position)[] items)
        => new(
            items.Select(item => new DesktopIconDescriptor(
                item.StableId,
                $"N_{item.StableId}",
                null,
                ItemKind.File,
                item.Position,
                TD,
                false,
                true)).ToArray(),
            0,
            ConnectionMethod.FindWindowSW,
            TimeSpan.Zero,
            null);

    private static DisplayDescriptor Display()
        => new(TD, WA, WA, 96, 96, true);

    private sealed class SequencedReader : IDesktopIconReader
    {
        private readonly Queue<DesktopIconReadResult> _reads;

        public SequencedReader(params DesktopIconReadResult[] reads)
            => _reads = new Queue<DesktopIconReadResult>(reads);

        public DesktopIconReadResult ReadDesktopIcons()
        {
            if (_reads.Count == 0)
                throw new InvalidOperationException("No scripted desktop observation remains.");
            return _reads.Dequeue();
        }
    }

    private sealed class SequencedTopologyReader : IDisplayTopologyReader
    {
        private readonly Queue<IReadOnlyList<DisplayDescriptor>> _reads = new();
        private IReadOnlyList<DisplayDescriptor>? _last;

        public SequencedTopologyReader(params DisplayDescriptor[] displays)
        {
            if (displays.Length == 1)
            {
                _last = new[] { displays[0] };
                return;
            }

            foreach (var display in displays)
                _reads.Enqueue(new[] { display });
        }

        public IReadOnlyList<DisplayDescriptor> GetDisplays()
        {
            if (_reads.Count > 0)
                _last = _reads.Dequeue();
            return _last ?? throw new InvalidOperationException("No scripted topology remains.");
        }
    }

    private sealed class CountingWriter : IDesktopIconPositionWriter
    {
        public int CallCount { get; private set; }

        public DesktopIconPositioningCapabilities GetCapabilities()
            => new(false, new PixelPoint(80, 80), ConnectionMethod.FindWindowSW, null);

        public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint target)
        {
            CallCount++;
            var previous = stableId == "a" ? A1 : B1;
            return new DesktopIconMoveResult(
                stableId,
                previous,
                target,
                target,
                ConnectionMethod.FindWindowSW,
                null);
        }
    }
}
