using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Persistence.DesktopLayouts;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

public sealed class SqliteRecoverySessionStoreDispositionAuthorityTests : IDisposable
{
    private const string TargetDisplay = @"\\.\DISPLAY1";
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "DesktopAgent-R9-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ListUnresolved_ClassifiesEveryLifecycleThroughDispositionSemantics()
    {
        Directory.CreateDirectory(_tempDir);
        var store = new SqliteRecoverySessionStore(Path.Combine(_tempDir, "authority.db"));
        await store.InitializeAsync();

        var start = new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
        var ready = MakeRecord(RecoverySessionLifecycle.Ready, start.AddMinutes(1));
        var executing = MakeRecord(RecoverySessionLifecycle.Executing, start.AddMinutes(2));
        var completed = MakeRecord(RecoverySessionLifecycle.Completed, start.AddMinutes(3));
        var rolledBack = MakeRecord(RecoverySessionLifecycle.FailedAndRolledBack, start.AddMinutes(4));
        var knownIncomplete = MakeRecord(
            RecoverySessionLifecycle.FailedRollbackIncomplete,
            start.AddMinutes(5));
        var positionUncertain = MakeRecord(
            RecoverySessionLifecycle.PositionUncertain,
            start.AddMinutes(6));

        foreach (var record in new[]
                 {
                     ready,
                     executing,
                     completed,
                     rolledBack,
                     knownIncomplete,
                     positionUncertain,
                 })
        {
            await store.SaveAsync(record);
        }

        var unresolved = await store.ListUnresolvedAsync();

        Assert.Equal(
            new[]
            {
                positionUncertain.SessionId,
                knownIncomplete.SessionId,
                executing.SessionId,
            },
            unresolved.Select(r => r.SessionId));

        Assert.All(unresolved, r => Assert.True(r.GetDisposition().IsUnresolved));
        Assert.DoesNotContain(unresolved, r => r.SessionId == ready.SessionId);
        Assert.DoesNotContain(unresolved, r => r.SessionId == completed.SessionId);
        Assert.DoesNotContain(unresolved, r => r.SessionId == rolledBack.SessionId);
    }

    [Fact]
    public async Task ListUnresolved_LimitIsAppliedAfterDispositionClassification()
    {
        Directory.CreateDirectory(_tempDir);
        var store = new SqliteRecoverySessionStore(Path.Combine(_tempDir, "limit.db"));
        await store.InitializeAsync();

        var start = new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);

        // 最新的两条都是 resolved。limit=1 不能在权威分类之前把真正 unresolved 的记录截掉。
        await store.SaveAsync(MakeRecord(
            RecoverySessionLifecycle.Completed,
            start.AddMinutes(30)));
        await store.SaveAsync(MakeRecord(
            RecoverySessionLifecycle.FailedAndRolledBack,
            start.AddMinutes(20)));

        var executing = MakeRecord(
            RecoverySessionLifecycle.Executing,
            start.AddMinutes(10));
        await store.SaveAsync(executing);

        var unresolved = await store.ListUnresolvedAsync(limit: 1);

        var only = Assert.Single(unresolved);
        Assert.Equal(executing.SessionId, only.SessionId);
        Assert.Equal(
            RecoverySessionHandling.AutomaticReconciliation,
            only.GetDisposition().Handling);
    }

    private static RecoverySessionRecord MakeRecord(
        RecoverySessionLifecycle lifecycle,
        DateTimeOffset startedAtUtc)
    {
        var (restorePlan, executionPlan) = MakeNoOpPlans();

        return new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = Guid.NewGuid(),
            TargetDeviceName = TargetDisplay,
            StartedAtUtc = startedAtUtc,
            Lifecycle = lifecycle,
            SourceRestorePlan = restorePlan,
            ExecutionPlan = executionPlan,
            ForwardAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            RollbackAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            CompletedForwardStepIndexes = Array.Empty<int>(),
            CompletedRollbackSourceStepIndexes = Array.Empty<int>(),
            ForwardFailureType = lifecycle is RecoverySessionLifecycle.FailedRollbackIncomplete
                or RecoverySessionLifecycle.PositionUncertain
                ? "R9AuthorityTest"
                : null,
            Revision = 1,
            UpdatedAtUtc = startedAtUtc,
        };
    }

    private static (
        TargetDisplayLayoutRestorePlan RestorePlan,
        TargetDisplayLayoutExecutionPlan ExecutionPlan) MakeNoOpPlans()
    {
        var restorePlan = new TargetDisplayLayoutRestorePlan(
            TargetDisplay,
            snapshotTargetIconCount: 0,
            alreadyAtTargetCount: 0,
            moveRequiredCount: 0,
            missingCount: 0,
            blockedCount: 0,
            hasBlockingErrors: false,
            requiresTemporaryStaging: false,
            isNoOp: true,
            Array.Empty<TargetDisplayLayoutRestorePlanItem>(),
            Array.Empty<TargetDisplayLayoutRestoreDependency>(),
            Array.Empty<TargetDisplayLayoutRestoreCycle>(),
            Array.Empty<TargetDisplayLayoutRestorePlanIssue>(),
            Array.Empty<string>());

        var executionPlan = new TargetDisplayLayoutExecutionPlan(
            TargetDisplay,
            isExecutable: true,
            isNoOp: true,
            Array.Empty<TargetDisplayLayoutExecutionStep>(),
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

        return (restorePlan, executionPlan);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
