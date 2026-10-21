using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;
using Microsoft.Data.Sqlite;

namespace DesktopAgent.Tests.Persistence;

public sealed class SqliteDesktopWorkspaceDynamicExecutionJournalStoreTests
{
    [Fact]
    public async Task RoundTrip_PreservesFreshRouteChanges_WithoutRequiringStaticFromIdentity()
    {
        using var temp = new TempDatabase();
        var store = new SqliteDesktopWorkspaceDynamicExecutionJournalStore(temp.Path);
        await store.InitializeAsync();
        var snapshotId = Guid.NewGuid();
        InsertSafetySnapshot(temp.Path, snapshotId);

        var journal = Journal(snapshotId);
        await store.SaveAsync(journal);

        var target = new PixelPoint(1555, 1946);
        var attempt0 = Attempt(
            attemptIndex: 0,
            decisionIndex: 0,
            freshObservationId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            kind: DesktopWorkspaceDynamicMoveKind.MoveToTemporaryStaging,
            freshFrom: new PixelPoint(1218, 1486),
            requested: new PixelPoint(1000, 1000),
            requestedSlotId: "secondary_2:slot:staging",
            frozenTarget: target);
        var attempt1 = Attempt(
            attemptIndex: 1,
            decisionIndex: 1,
            freshObservationId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            kind: DesktopWorkspaceDynamicMoveKind.MoveToFrozenTarget,
            freshFrom: new PixelPoint(1218, 1290),
            requested: target,
            requestedSlotId: "secondary_2:slot:final",
            frozenTarget: target);

        var inProgress = journal with
        {
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress,
            ForwardAttempts = [attempt0, attempt1],
            LastObservedSatisfiedGoalCount = 1,
        };
        var updated = await store.UpdateAsync(inProgress);
        var readBack = await store.GetAsync(updated.SessionId);

        var actual = Assert.IsType<DesktopWorkspaceDynamicExecutionJournal>(readBack);
        Assert.Equal(2, actual.Revision);
        Assert.Equal(2, actual.ForwardAttempts.Count);
        Assert.Equal(new PixelPoint(1218, 1486), actual.ForwardAttempts[0].FreshFromPixelPosition);
        Assert.Equal(new PixelPoint(1218, 1290), actual.ForwardAttempts[1].FreshFromPixelPosition);
        Assert.Equal(target, actual.ForwardAttempts[0].FrozenTargetPixelPosition);
        Assert.Equal(target, actual.ForwardAttempts[1].FrozenTargetPixelPosition);
        Assert.Equal(target, actual.ForwardAttempts[1].RequestedPixelPosition);
    }

    [Fact]
    public async Task Update_UsesRevisionCas_AndRejectsStaleWriter()
    {
        using var temp = new TempDatabase();
        var store = new SqliteDesktopWorkspaceDynamicExecutionJournalStore(temp.Path);
        await store.InitializeAsync();
        var snapshotId = Guid.NewGuid();
        InsertSafetySnapshot(temp.Path, snapshotId);
        var journal = Journal(snapshotId);
        await store.SaveAsync(journal);

        var first = await store.UpdateAsync(journal with
        {
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress,
        });
        Assert.Equal(2, first.Revision);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateAsync(journal with
            {
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention,
                ForwardFailureType = "stale",
            }));
    }

    [Fact]
    public async Task ListUnresolved_UserAcceptedNeedsAttention_IsHistoricalEvidenceNotExecutionLock()
    {
        using var temp = new TempDatabase();
        var store = new SqliteDesktopWorkspaceDynamicExecutionJournalStore(temp.Path);
        await store.InitializeAsync();
        var snapshotId = Guid.NewGuid();
        InsertSafetySnapshot(temp.Path, snapshotId);
        var journal = Journal(snapshotId);
        await store.SaveAsync(journal);

        var pending = await store.UpdateAsync(journal with
        {
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention,
            ForwardFailureType = "DynamicDecisionBlocked",
            ForwardFailureMessage = "test",
        });
        Assert.Single(await store.ListUnresolvedAsync());

        var accepted = await store.UpdateAsync(pending with
        {
            RecoveryFailureType = DesktopWorkspaceExecutionResolutionMarkers.UserAcceptedCurrentState,
            RecoveryFailureMessage = "用户明确接受当前桌面。",
        });
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention, accepted.Lifecycle);
        Assert.Empty(await store.ListUnresolvedAsync());

        var historical = await store.GetAsync(accepted.SessionId);
        Assert.NotNull(historical);
        Assert.Equal(DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention, historical!.Lifecycle);
        Assert.Equal(
            DesktopWorkspaceExecutionResolutionMarkers.UserAcceptedCurrentState,
            historical.RecoveryFailureType);
    }

    [Fact]
    public async Task Read_TamperedAuthorityPayload_IsRejectedBeforeRecoveryCanTrustIt()
    {
        using var temp = new TempDatabase();
        var store = new SqliteDesktopWorkspaceDynamicExecutionJournalStore(temp.Path);
        await store.InitializeAsync();
        var snapshotId = Guid.NewGuid();
        InsertSafetySnapshot(temp.Path, snapshotId);
        var journal = Journal(snapshotId);
        await store.SaveAsync(journal);

        using (var connection = new SqliteConnection($"Data Source={temp.Path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE workspace_dynamic_execution_journals
                SET authority_json = REPLACE(authority_json, '1555', '1556')
                WHERE session_id = @session_id;
            ";
            command.Parameters.AddWithValue("@session_id", journal.SessionId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(journal.SessionId));
    }

    [Fact]
    public async Task Initialize_IsIdempotent_AndDoesNotPrematurelyBumpSharedUserVersion()
    {
        using var temp = new TempDatabase();
        var store = new SqliteDesktopWorkspaceDynamicExecutionJournalStore(temp.Path);
        await store.InitializeAsync();
        await store.InitializeAsync();

        using var connection = new SqliteConnection($"Data Source={temp.Path}");
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(13L, (long)version.ExecuteScalar()!);

        using var tables = connection.CreateCommand();
        tables.CommandText = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('workspace_dynamic_execution_journals', 'workspace_dynamic_execution_attempts');
        ";
        Assert.Equal(2L, (long)tables.ExecuteScalar()!);
    }

    private static DesktopWorkspaceDynamicExecutionJournal Journal(Guid snapshotId)
    {
        var started = DateTimeOffset.Parse("2026-08-29T06:00:00+00:00");
        var authority = new DesktopWorkspaceDynamicExecutionAuthoritySnapshot(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "preview:test",
            "exact-plan:test",
            "geometry:test",
            [
                new DesktopWorkspaceDynamicExecutionItemGoal(
                    "a",
                    "A",
                    DesktopWorkspaceCompiledItemDisposition.Place,
                    isMovable: true,
                    isMoveAuthorized: true,
                    "secondary_2",
                    new PixelPoint(1555, 1946),
                    "secondary_2:slot:final"),
                new DesktopWorkspaceDynamicExecutionItemGoal(
                    "fixed",
                    "Fixed",
                    DesktopWorkspaceCompiledItemDisposition.Keep,
                    isMovable: false,
                    isMoveAuthorized: false,
                    "primary",
                    new PixelPoint(10, 10),
                    finalSlotId: null),
            ]);

        var journal = new DesktopWorkspaceDynamicExecutionJournal
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = snapshotId,
            BlueprintId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            BlueprintRevision = 3,
            CertificateFingerprint = "certificate:test",
            StartedAtUtc = started,
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Prepared,
            Authority = authority,
            ForwardAttempts = [],
            LastObservedSatisfiedGoalCount = 0,
            Revision = 1,
            UpdatedAtUtc = started,
        };
        journal.Validate();
        return journal;
    }

    private static DesktopWorkspaceDynamicExecutionAttemptEvidence Attempt(
        int attemptIndex,
        int decisionIndex,
        Guid freshObservationId,
        DesktopWorkspaceDynamicMoveKind kind,
        PixelPoint freshFrom,
        PixelPoint requested,
        string requestedSlotId,
        PixelPoint frozenTarget)
        => new(
            attemptIndex,
            decisionIndex,
            DesktopWorkspaceDynamicExecutionAttemptPhase.Forward,
            freshObservationId,
            "a",
            kind,
            "secondary_2",
            freshFrom,
            "secondary_2",
            requested,
            requestedSlotId,
            "secondary_2",
            frozenTarget,
            "secondary_2:slot:final",
            satisfiedGoalCountBeforeDecision: 0,
            reportedStableId: "a",
            reportedPreviousPixelPosition: freshFrom,
            actualPixelPosition: requested,
            hasTrustedActualPosition: true,
            isExact: true,
            outcome: DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess,
            connectionMethod: null,
            exceptionType: null,
            errorMessage: null,
            primaryPathExceptionType: null,
            primaryPathExceptionMessage: null);

    private static void InsertSafetySnapshot(string databasePath, Guid snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO desktop_layout_snapshots (
                snapshot_id, created_at_utc, target_monitor_device_name,
                connection_method, format_version, icon_count)
            VALUES (@id, @created, NULL, 0, 1, 0);
        ";
        command.Parameters.AddWithValue("@id", snapshotId.ToString("D"));
        command.Parameters.AddWithValue("@created", "2026-08-29T06:00:00.0000000+00:00");
        command.ExecuteNonQuery();
    }

    private sealed class TempDatabase : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"desktop-agent-dynamic-journal-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            TryDelete(Path);
            TryDelete(Path + "-wal");
            TryDelete(Path + "-shm");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // best-effort test cleanup
            }
        }
    }
}
