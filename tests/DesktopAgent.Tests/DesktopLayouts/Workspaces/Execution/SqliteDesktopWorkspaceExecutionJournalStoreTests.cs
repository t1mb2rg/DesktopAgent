using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Execution;

public sealed class SqliteDesktopWorkspaceExecutionJournalStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _databasePath;

    public SqliteDesktopWorkspaceExecutionJournalStoreTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DesktopAgent.Tests.WorkspaceJournal.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _databasePath = Path.Combine(_tempDirectory, "journal.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 尽力清理。
        }
    }

    [Fact]
    public async Task SaveAndGet_PreparedJournal_RoundTripsFrozenIdentityAndPlan()
    {
        var store = await CreateStoreWithSnapshotAsync();
        var journal = CreatePreparedJournal();

        await store.SaveAsync(journal);
        var loaded = Assert.IsType<DesktopWorkspaceExecutionJournal>(
            await store.GetAsync(journal.SessionId));

        Assert.Equal(journal.SessionId, loaded.SessionId);
        Assert.Equal(journal.SafetySnapshotId, loaded.SafetySnapshotId);
        Assert.Equal(journal.BlueprintId, loaded.BlueprintId);
        Assert.Equal(journal.BlueprintRevision, loaded.BlueprintRevision);
        Assert.Equal(journal.CertificateFingerprint, loaded.CertificateFingerprint);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.Prepared, loaded.Lifecycle);
        Assert.Equal(1, loaded.Revision);
        Assert.Equal(journal.ExecutionPlan.ExecutionPlanFingerprint, loaded.ExecutionPlan.ExecutionPlanFingerprint);
        Assert.Equal(journal.ExecutionPlan.PreviewFingerprint, loaded.ExecutionPlan.PreviewFingerprint);
        Assert.Equal(journal.ExecutionPlan.ExactPlanFingerprint, loaded.ExecutionPlan.ExactPlanFingerprint);
        Assert.Equal(2, loaded.ExecutionPlan.Steps.Count);
        Assert.Equal("a", loaded.ExecutionPlan.Steps[0].StableId);
        Assert.Equal("secondary_1", loaded.ExecutionPlan.Steps[0].ToDisplayRef);
        Assert.Empty(loaded.ForwardAttempts);
        Assert.Empty(loaded.RollbackAttempts);
    }

    [Fact]
    public async Task Update_ForwardProgress_IsCasProtected_AndCommittedLeavesUnresolvedList()
    {
        var store = await CreateStoreWithSnapshotAsync();
        var initial = CreatePreparedJournal();
        await store.SaveAsync(initial);

        var step0 = initial.ExecutionPlan.Steps[0];
        var firstAttempt = ExactForwardAttempt(0, step0);
        var inProgress = initial with
        {
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress,
            ForwardAttempts = [firstAttempt],
            CompletedForwardStepIndexes = [0],
        };
        var revision2 = await store.UpdateAsync(inProgress);
        Assert.Equal(2, revision2.Revision);
        Assert.Single(await store.ListUnresolvedAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(inProgress));

        var step1 = revision2.ExecutionPlan.Steps[1];
        var secondAttempt = ExactForwardAttempt(1, step1);
        var committedCandidate = revision2 with
        {
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Committed,
            ForwardAttempts = [firstAttempt, secondAttempt],
            CompletedForwardStepIndexes = [0, 1],
        };
        var committed = await store.UpdateAsync(committedCandidate);

        Assert.Equal(3, committed.Revision);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.Committed, committed.Lifecycle);
        Assert.Empty(await store.ListUnresolvedAsync());
    }

    [Fact]
    public async Task Update_RollbackEvidence_RoundTripsReverseStepSemanticsAndCanReachRestored()
    {
        var store = await CreateStoreWithSnapshotAsync();
        var initial = CreatePreparedJournal();
        await store.SaveAsync(initial);

        var step0 = initial.ExecutionPlan.Steps[0];
        var step1 = initial.ExecutionPlan.Steps[1];
        var forward0 = ExactForwardAttempt(0, step0);
        var forward1 = ExactForwardAttempt(1, step1);
        var rollingBack = initial with
        {
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.RollingBack,
            ForwardAttempts = [forward0, forward1],
            CompletedForwardStepIndexes = [0, 1],
            RollbackAttempts = [ExactRollbackAttempt(2, step1)],
            CompletedRollbackSourceStepIndexes = [1],
        };
        var revision2 = await store.UpdateAsync(rollingBack);

        var restoredCandidate = revision2 with
        {
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored,
            RollbackAttempts = [
                ExactRollbackAttempt(2, step1),
                ExactRollbackAttempt(3, step0),
            ],
            CompletedRollbackSourceStepIndexes = [0, 1],
        };
        var restored = await store.UpdateAsync(restoredCandidate);
        var loaded = Assert.IsType<DesktopWorkspaceExecutionJournal>(
            await store.GetAsync(initial.SessionId));

        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.Restored, restored.Lifecycle);
        Assert.Equal(restored.Revision, loaded.Revision);
        Assert.Equal(2, loaded.RollbackAttempts.Count);
        Assert.Equal(DesktopWorkspaceExecutionAttemptPhase.Rollback, loaded.RollbackAttempts[0].Phase);
        Assert.Equal(step1.ToDisplayRef, loaded.RollbackAttempts[0].ExpectedPreviousDisplayRef);
        Assert.Equal(step1.FromDisplayRef, loaded.RollbackAttempts[0].RequestedDisplayRef);
        Assert.Empty(await store.ListUnresolvedAsync());
    }

    [Fact]
    public async Task Save_MissingSafetySnapshot_IsRejectedByForeignKey()
    {
        var store = new SqliteDesktopWorkspaceExecutionJournalStore(_databasePath);
        await store.InitializeAsync();
        var journal = CreatePreparedJournal();

        await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync(journal));
    }

    [Fact]
    public async Task Get_TamperedHeaderFingerprint_IsRejectedInsteadOfTrustingPayload()
    {
        var store = await CreateStoreWithSnapshotAsync();
        var journal = CreatePreparedJournal();
        await store.SaveAsync(journal);

        using (var connection = OpenRawConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                UPDATE workspace_execution_journals
                SET execution_plan_fingerprint = 'tampered'
                WHERE session_id = @session_id;
            ";
            command.Parameters.AddWithValue("@session_id", journal.SessionId.ToString("D"));
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(journal.SessionId));
    }

    private async Task<SqliteDesktopWorkspaceExecutionJournalStore> CreateStoreWithSnapshotAsync()
    {
        var store = new SqliteDesktopWorkspaceExecutionJournalStore(_databasePath);
        await store.InitializeAsync();

        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO desktop_layout_snapshots
                (snapshot_id, created_at_utc, target_monitor_device_name,
                 connection_method, format_version, icon_count)
            VALUES
                (@snapshot_id, '2026-08-28T10:00:00.0000000+00:00', NULL, 0, 1, 0);
        ";
        command.Parameters.AddWithValue("@snapshot_id", SafetySnapshotId.ToString("D"));
        command.ExecuteNonQuery();
        return store;
    }

    private static DesktopWorkspaceExecutionJournal CreatePreparedJournal()
    {
        var started = new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);
        return new DesktopWorkspaceExecutionJournal
        {
            SessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            SafetySnapshotId = SafetySnapshotId,
            BlueprintId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            BlueprintRevision = 2,
            CertificateFingerprint = "workspace-certificate-v1:test",
            StartedAtUtc = started,
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Prepared,
            ExecutionPlan = CreatePlan(),
            ForwardAttempts = [],
            RollbackAttempts = [],
            CompletedForwardStepIndexes = [],
            CompletedRollbackSourceStepIndexes = [],
            Revision = 1,
            UpdatedAtUtc = started,
        };
    }

    private static DesktopWorkspaceExecutionPlanSnapshot CreatePlan()
        => new(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            "workspace-preview-v1:test",
            "workspace-plan-v1:test",
            "workspace-execution-plan-v1:test",
            [
                new DesktopWorkspaceExecutionStep(
                    0,
                    "a",
                    "Alpha",
                    DesktopWorkspaceExecutionStepKind.MoveToTarget,
                    "primary",
                    new PixelPoint(10, 20),
                    "secondary_1",
                    new PixelPoint(410, 20),
                    "secondary_1:slot:0000:0000"),
                new DesktopWorkspaceExecutionStep(
                    1,
                    "b",
                    "Beta",
                    DesktopWorkspaceExecutionStepKind.MoveToTarget,
                    "secondary_1",
                    new PixelPoint(490, 20),
                    "primary",
                    new PixelPoint(90, 20),
                    "primary:slot:0001:0000"),
            ],
            moveItemCount: 2,
            usesTemporaryStaging: false);

    private static DesktopWorkspaceExecutionAttemptEvidence ExactForwardAttempt(
        int attemptIndex,
        DesktopWorkspaceExecutionStep step)
        => new(
            attemptIndex,
            step.StepIndex,
            DesktopWorkspaceExecutionAttemptPhase.Forward,
            step.StableId,
            step.FromDisplayRef,
            step.FromPixelPosition,
            step.ToDisplayRef,
            step.ToPixelPosition,
            step.StableId,
            step.FromPixelPosition,
            step.ToPixelPosition,
            hasTrustedActualPosition: true,
            isExact: true,
            DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess,
            ConnectionMethod.FindWindowSW,
            null,
            null,
            null,
            null);

    private static DesktopWorkspaceExecutionAttemptEvidence ExactRollbackAttempt(
        int attemptIndex,
        DesktopWorkspaceExecutionStep step)
        => new(
            attemptIndex,
            step.StepIndex,
            DesktopWorkspaceExecutionAttemptPhase.Rollback,
            step.StableId,
            step.ToDisplayRef,
            step.ToPixelPosition,
            step.FromDisplayRef,
            step.FromPixelPosition,
            step.StableId,
            step.ToPixelPosition,
            step.FromPixelPosition,
            hasTrustedActualPosition: true,
            isExact: true,
            DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess,
            ConnectionMethod.FindWindowSW,
            null,
            null,
            null,
            null);

    private SqliteConnection OpenRawConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static readonly Guid SafetySnapshotId =
        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
}
