using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.Productization;

public sealed class M5077DirectLayoutApplyTests
{
    [Fact]
    public void Preparation_FreezesCurrentDraft_AndContainsNoWriterCommandAuthority()
    {
        var draft = DesktopLayoutPresetFactory.CreateNewDraft(
            "Direct Apply",
            DesktopLayoutPresetKind.Full);
        var observation = CreateObservation(new PixelPoint(1600, 800));

        var preparation = DesktopLayoutDirectApplyPreparationService.Create(draft, observation);

        Assert.True(preparation.IsReady);
        Assert.NotNull(preparation.Candidate);
        Assert.NotSame(draft, preparation.FrozenDraft);
        Assert.Equal(draft.TemplateId, preparation.FrozenDraft.TemplateId);
        Assert.Equal(draft.Zones, preparation.FrozenDraft.Zones);
        Assert.Equal(observation.ObservationId, preparation.Preview.ObservationId);
        Assert.DoesNotContain(
            preparation.GetType().GetProperties(),
            property => property.Name.Contains("Writer", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_UnresolvedRecovery_FailsClosedBeforeFreshObservationAndExecution()
    {
        var confirmedObservation = CreateObservation(new PixelPoint(1600, 800));
        var preparation = CreateReadyPreparation(confirmedObservation);
        var candidate = preparation.Candidate!;
        var unresolved = CreateUnresolvedRecord(candidate);
        var source = new FakeObservationSource(confirmedObservation);
        var snapshots = new FakeSnapshotStore();
        var recovery = new FakeRecoveryStore(unresolved);
        var executeCount = 0;

        var service = new DesktopLayoutDirectApplyService(
            source,
            snapshots,
            recovery,
            (_, _, _) =>
            {
                executeCount++;
                return Task.FromResult(TargetDisplayLayoutExecutionRunResult.NoOp());
            });

        var result = await service.ApplyAsync(preparation);

        Assert.Equal(DesktopLayoutDirectApplyOutcome.UnresolvedRecoveryBlocked, result.Outcome);
        Assert.Equal(0, source.CaptureCount);
        Assert.Equal(0, executeCount);
    }

    [Fact]
    public async Task Apply_FreshObservationUnavailable_FailsClosedWithoutExecution()
    {
        var preparation = CreateReadyPreparation(CreateObservation(new PixelPoint(1600, 800)));
        var source = new FakeObservationSource(
            DesktopObservationCaptureResult.Unavailable(
                new DesktopObservationCaptureIssue(
                    DesktopObservationCaptureIssueCode.ShellObservationFailed,
                    "synthetic unavailable")));
        var executeCount = 0;

        var service = new DesktopLayoutDirectApplyService(
            source,
            new FakeSnapshotStore(),
            new FakeRecoveryStore(),
            (_, _, _) =>
            {
                executeCount++;
                return Task.FromResult(TargetDisplayLayoutExecutionRunResult.NoOp());
            });

        var result = await service.ApplyAsync(preparation);

        Assert.Equal(DesktopLayoutDirectApplyOutcome.FreshObservationUnavailable, result.Outcome);
        Assert.Equal(1, source.CaptureCount);
        Assert.Equal(0, executeCount);
    }

    [Fact]
    public async Task Apply_DesktopChangedAfterConfirmation_FailsClosedWithoutExecution()
    {
        var confirmedObservation = CreateObservation(new PixelPoint(1600, 800));
        var preparation = CreateReadyPreparation(confirmedObservation);
        var freshObservation = CreateObservation(new PixelPoint(1520, 800));
        var executeCount = 0;

        var service = new DesktopLayoutDirectApplyService(
            new FakeObservationSource(freshObservation),
            new FakeSnapshotStore(),
            new FakeRecoveryStore(),
            (_, _, _) =>
            {
                executeCount++;
                return Task.FromResult(TargetDisplayLayoutExecutionRunResult.NoOp());
            });

        var result = await service.ApplyAsync(preparation);

        Assert.Equal(DesktopLayoutDirectApplyOutcome.PreviewChanged, result.Outcome);
        Assert.NotEmpty(result.Issues);
        Assert.Equal(0, executeCount);
    }

    [Fact]
    public async Task Apply_EquivalentFreshFacts_DelegatesExactlyOnceThroughFrozenAuthorizationType()
    {
        var observation = CreateObservation(new PixelPoint(1600, 800));
        var preparation = CreateReadyPreparation(observation);
        var executeCount = 0;
        bool? observedStaging = null;

        var service = new DesktopLayoutDirectApplyService(
            new FakeObservationSource(observation),
            new FakeSnapshotStore(),
            new FakeRecoveryStore(),
            (_, usesTemporaryStaging, _) =>
            {
                executeCount++;
                observedStaging = usesTemporaryStaging;
                return Task.FromResult(TargetDisplayLayoutExecutionRunResult.NoOp());
            });

        var result = await service.ApplyAsync(preparation);

        Assert.Equal(DesktopLayoutDirectApplyOutcome.CompletedNoOp, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, executeCount);
        Assert.Equal(preparation.Candidate!.UsesTemporaryStaging, observedStaging);
    }

    [Fact]
    public void ProductWinUi_SourceDoesNotDirectlyReferencePhysicalWriterOrInternalScopeGates()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DesktopAgent.WinUI",
            "LayoutManagementView.xaml.cs"));

        Assert.DoesNotContain("DesktopShellIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDesktopIconPositionWriter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetDisplayLayoutExecutionScopeGate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetDisplayLayoutStagingExecutionScopeGate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteWithScopeGateAsync", source, StringComparison.Ordinal);
    }

    private static DesktopLayoutDirectApplyPreparation CreateReadyPreparation(
        DesktopObservation observation)
    {
        var draft = DesktopLayoutPresetFactory.CreateNewDraft(
            "Direct Apply",
            DesktopLayoutPresetKind.Full);
        var preparation = DesktopLayoutDirectApplyPreparationService.Create(draft, observation);
        Assert.True(preparation.IsReady,
            string.Join("; ", preparation.CandidateBuildResult.Issues.Select(issue => issue.Message)));
        Assert.NotNull(preparation.Candidate);
        Assert.True(preparation.Candidate!.MoveRequiredCount > 0);
        return preparation;
    }

    private static DesktopObservation CreateObservation(PixelPoint iconPosition)
    {
        var display = new DisplayDescriptor(
            "DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            96,
            96,
            true);

        return new DesktopObservation(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { display },
            new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled: false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                primaryPathException: null),
            new[]
            {
                new DesktopIconDescriptor(
                    "C:\\Users\\test\\Desktop\\note.txt",
                    "note.txt",
                    "C:\\Users\\test\\Desktop\\note.txt",
                    ItemKind.File,
                    iconPosition,
                    "DISPLAY1",
                    IsSystemItem: false,
                    IsFileSystemItem: true),
            },
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5));
    }

    private static RecoverySessionRecord CreateUnresolvedRecord(
        DesktopAgent.Core.DesktopLayouts.Templates.Execution.DesktopLayoutRuleAwareExecutionCandidate candidate)
    {
        var now = DateTimeOffset.UtcNow;
        return new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = Guid.NewGuid(),
            TargetDeviceName = candidate.TargetDeviceName,
            StartedAtUtc = now,
            Lifecycle = RecoverySessionLifecycle.Executing,
            LastFailedStepIndex = null,
            SourceRestorePlan = candidate.RestorePlan,
            ExecutionPlan = candidate.ExecutionPlan,
            ForwardAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            RollbackAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            CompletedForwardStepIndexes = Array.Empty<int>(),
            CompletedRollbackSourceStepIndexes = Array.Empty<int>(),
            Revision = 1,
            UpdatedAtUtc = now,
        };
    }

    private sealed class FakeObservationSource : IDesktopObservationSource
    {
        private readonly DesktopObservationCaptureResult _result;
        public int CaptureCount { get; private set; }

        public FakeObservationSource(DesktopObservation observation)
            : this(DesktopObservationCaptureResult.Captured(observation))
        {
        }

        public FakeObservationSource(DesktopObservationCaptureResult result)
            => _result = result;

        public Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeSnapshotStore : IDesktopLayoutSnapshotStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveAsync(
            DesktopLayoutSnapshot snapshot,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<DesktopLayoutSnapshot?> GetAsync(
            Guid snapshotId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DesktopLayoutSnapshot?>(null);

        public Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DesktopLayoutSnapshotSummary>>(
                Array.Empty<DesktopLayoutSnapshotSummary>());

        public Task<bool> DeleteAsync(
            Guid snapshotId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class FakeRecoveryStore : IRecoverySessionStore
    {
        private readonly IReadOnlyList<RecoverySessionRecord> _unresolved;

        public FakeRecoveryStore(params RecoverySessionRecord[] unresolved)
            => _unresolved = unresolved;

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<RecoverySessionRecord> UpdateAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => Task.FromResult(record);

        public Task<RecoverySessionRecord?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<RecoverySessionRecord?>(null);

        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecoverySessionRecord>>(
                _unresolved.Take(limit).ToArray());
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DesktopAgent.sln")))
                return current.FullName;
        }

        throw new Xunit.Sdk.XunitException("无法从测试输出目录定位 DesktopAgent.sln。");
    }
}
