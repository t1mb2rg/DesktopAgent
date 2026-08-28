using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.Productization;

public sealed class M5077CrossGenerationApplyInterlockTests
{
    [Fact]
    public async Task LegacyDirectApply_UnresolvedWorkspaceJournal_BlocksBeforeFreshObservationOrExecution()
    {
        var preparation = CreateReadyPreparation(CreateObservation(new PixelPoint(1600, 800)));
        var observationSource = new CountingObservationSource(CreateObservation(new PixelPoint(1600, 800)));
        var workspaceStore = new FakeWorkspaceJournalStore(
            new DesktopWorkspaceExecutionJournal
            {
                SessionId = Guid.NewGuid(),
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress,
            });
        var executeCount = 0;

        var service = new DesktopLayoutDirectApplyService(
            observationSource,
            new FakeSnapshotStore(),
            new FakeRecoveryStore(),
            (_, _, _) =>
            {
                executeCount++;
                return Task.FromResult(TargetDisplayLayoutExecutionRunResult.NoOp());
            },
            timeProvider: null,
            workspaceExecutionJournalStore: workspaceStore);

        var result = await service.ApplyAsync(preparation);

        Assert.Equal(DesktopLayoutDirectApplyOutcome.UnresolvedRecoveryBlocked, result.Outcome);
        Assert.Contains("Workspace", Assert.Single(result.Issues), StringComparison.Ordinal);
        Assert.Equal(0, observationSource.CaptureCount);
        Assert.Equal(0, executeCount);
        Assert.Equal(1, workspaceStore.ListCount);
    }

    private static DesktopLayoutDirectApplyPreparation CreateReadyPreparation(
        DesktopObservation observation)
    {
        var draft = DesktopLayoutPresetFactory.CreateNewDraft(
            "Cross generation interlock",
            DesktopLayoutPresetKind.Full);
        var preparation = DesktopLayoutDirectApplyPreparationService.Create(draft, observation);
        Assert.True(
            preparation.IsReady,
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
            [display],
            new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled: false,
                new PixelPoint(80, 80),
                ConnectionMethod.FindWindowSW,
                primaryPathException: null),
            [
                new DesktopIconDescriptor(
                    "C:\\Users\\test\\Desktop\\note.txt",
                    "note.txt",
                    "C:\\Users\\test\\Desktop\\note.txt",
                    ItemKind.File,
                    iconPosition,
                    "DISPLAY1",
                    IsSystemItem: false,
                    IsFileSystemItem: true),
            ],
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5));
    }

    private sealed class CountingObservationSource : IDesktopObservationSource
    {
        private readonly DesktopObservation _observation;
        public int CaptureCount { get; private set; }

        public CountingObservationSource(DesktopObservation observation)
            => _observation = observation;

        public Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(DesktopObservationCaptureResult.Captured(_observation));
        }
    }

    private sealed class FakeSnapshotStore : IDesktopLayoutSnapshotStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopLayoutSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DesktopLayoutSnapshot?> GetAsync(Guid snapshotId, CancellationToken cancellationToken = default)
            => Task.FromResult<DesktopLayoutSnapshot?>(null);
        public Task<IReadOnlyList<DesktopLayoutSnapshotSummary>> ListAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DesktopLayoutSnapshotSummary>>(Array.Empty<DesktopLayoutSnapshotSummary>());
        public Task<bool> DeleteAsync(Guid snapshotId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class FakeRecoveryStore : IRecoverySessionStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(RecoverySessionRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RecoverySessionRecord> UpdateAsync(RecoverySessionRecord record, CancellationToken cancellationToken = default)
            => Task.FromResult(record);
        public Task<RecoverySessionRecord?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<RecoverySessionRecord?>(null);
        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecoverySessionRecord>>(Array.Empty<RecoverySessionRecord>());
    }

    private sealed class FakeWorkspaceJournalStore : IDesktopWorkspaceExecutionJournalStore
    {
        private readonly IReadOnlyList<DesktopWorkspaceExecutionJournal> _unresolved;
        public int ListCount { get; private set; }

        public FakeWorkspaceJournalStore(params DesktopWorkspaceExecutionJournal[] unresolved)
            => _unresolved = unresolved;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DesktopWorkspaceExecutionJournal journal, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<DesktopWorkspaceExecutionJournal> UpdateAsync(
            DesktopWorkspaceExecutionJournal journal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<DesktopWorkspaceExecutionJournal?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DesktopWorkspaceExecutionJournal?>(null);
        public Task<IReadOnlyList<DesktopWorkspaceExecutionJournal>> ListUnresolvedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCount++;
            return Task.FromResult(_unresolved);
        }
    }
}
