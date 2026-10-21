using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

/// <summary>
/// 「撤销上次整理」的服务层接线：现场 interlock → 只读核对 → 按安全快照排还原计划 →
/// 同一套 ScopeGate → 受控执行委托 → 结果映射。
///
/// 这里不碰真机也不写盘：观察、快照、会话查询、最终执行全部是假件。
/// 断言的重点是<strong>什么时候绝不动桌面</strong>，以及动的时候把什么交给了执行层。
/// </summary>
public sealed class DesktopLayoutRollbackServiceTests
{
    private static readonly string Td = @"\\.\DISPLAY1";
    private static int _ordinal;

    private static DisplayDescriptor Display()
        => new(Td, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96, true);

    private static readonly string D2 = @"\\.\DISPLAY2";

    private static DisplayDescriptor Display2()
        => new(D2, new PixelRect(1920, 0, 3840, 1080), new PixelRect(1920, 0, 3840, 1040), 96, 96, false);

    private static DesktopIconDescriptor Live(string stableId, PixelPoint position, string? monitor = null)
        => new(stableId, $"N_{stableId}", null, ItemKind.File, position, monitor ?? Td, false, true);

    private static DesktopIconSnapshotItem Snap(string stableId, PixelPoint position, string? monitor = null)
        => new(_ordinal++, stableId, $"N_{stableId}", null, ItemKind.File, position, monitor ?? Td, false, true);

    private static DesktopIconPositioningCapabilities Capabilities()
        => new(false, new PixelPoint(80, 80), ConnectionMethod.FindWindowSW, null);

    private static DesktopLayoutSnapshot Snapshot(params DesktopIconSnapshotItem[] items)
        => new(Guid.NewGuid(), DateTimeOffset.UtcNow, Td, ConnectionMethod.FindWindowSW, 1, items);

    /// <summary>
    /// 工作区设计应用写盘前存的是<strong>整桌面</strong>检查点：没有「目标显示器」，
    /// 每一项自带所属屏。真机上就是这份快照让单屏判定失效的。
    /// </summary>
    private static DesktopLayoutSnapshot WholeDesktopSnapshot(params DesktopIconSnapshotItem[] items)
        => new(Guid.NewGuid(), DateTimeOffset.UtcNow, null, ConnectionMethod.FindWindowSW, 1, items);

    private static TargetDisplayLayoutExecutionPlan ExecutionPlan(
        params TargetDisplayLayoutExecutionStep[] steps)
        => new(
            Td,
            true,
            false,
            steps,
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

    private static TargetDisplayLayoutExecutionStep Move(int index, string stableId, PixelPoint from, PixelPoint to)
        => new(index, stableId, $"N_{stableId}", from, to, TargetDisplayLayoutExecutionStepKind.MoveToTarget, Td);

    private static RecoverySessionRecord CommittedSession(
        DesktopLayoutSnapshot snapshot,
        IReadOnlyList<DesktopIconDescriptor> liveAfterApply,
        RecoverySessionLifecycle lifecycle,
        params TargetDisplayLayoutExecutionStep[] steps)
        => new()
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = snapshot.SnapshotId,
            TargetDeviceName = Td,
            StartedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Lifecycle = lifecycle,
            SourceRestorePlan = TargetDisplayLayoutRestorePlanner.CreatePlan(
                snapshot,
                new DesktopIconReadResult(liveAfterApply, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null),
                new[] { Display() },
                Capabilities(),
                Td),
            ExecutionPlan = ExecutionPlan(steps),
            ForwardAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            RollbackAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            CompletedForwardStepIndexes = steps.Select(step => step.StepIndex).ToArray(),
            CompletedRollbackSourceStepIndexes = Array.Empty<int>(),
            Revision = 1,
        };

    private static DesktopObservation Observation(params DesktopIconDescriptor[] icons)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { Display() },
            Capabilities(),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5));

    private static DesktopObservation TwoDisplayObservation(params DesktopIconDescriptor[] icons)
        => new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new[] { Display(), Display2() },
            Capabilities(),
            icons,
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(5));

    /// <summary>一套完整可撤销的现场：安全检查点 + 已收尾会话 + 桌面仍是整理后的样子。</summary>
    private static async Task<(InMemoryDesktopLayoutSnapshotStore Snapshots, RecoverySessionRecord Session, DesktopObservation Observation)>
        ReadySceneAsync()
    {
        var snapshot = Snapshot(Snap("a", new PixelPoint(160, 240)), Snap("b", new PixelPoint(160, 360)));
        var after = new[] { Live("a", new PixelPoint(240, 320)), Live("b", new PixelPoint(240, 440)) };
        var session = CommittedSession(
            snapshot,
            after,
            RecoverySessionLifecycle.Completed,
            Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320)),
            Move(1, "b", new PixelPoint(160, 360), new PixelPoint(240, 440)));

        var store = new InMemoryDesktopLayoutSnapshotStore();
        await store.SaveAsync(snapshot);
        return (store, session, Observation(after));
    }

    private static DesktopLayoutRollbackService CreateService(
        IDesktopObservationSource observation,
        ICommittedRecoverySessionQuery committed,
        IDesktopLayoutSnapshotStore snapshots,
        IRecoverySessionStore recovery,
        ExecutionProbe probe,
        ICommittedWorkspaceExecutionJournalQuery? workspaceWrites = null)
        => new(observation, committed, snapshots, recovery, probe.ExecuteAsync, committedWorkspaceWrites: workspaceWrites);

    [Fact]
    public async Task UnresolvedRecoverySite_BlocksWithoutEvenReadingTheDesktop()
    {
        var (snapshots, session, _) = await ReadySceneAsync();
        var stuck = session with { Lifecycle = RecoverySessionLifecycle.Executing };
        var observation = new RecordingObservationSource(DesktopObservationCaptureResult.Captured(Observation()));
        var probe = new ExecutionProbe();

        var result = await CreateService(observation, new FakeCommittedSessions(stuck), snapshots, new FakeRecoveryStore(stuck), probe)
            .RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.UnresolvedRecoveryBlocked, result.Outcome);
        Assert.Equal(DesktopLayoutRollbackBlockReason.UnresolvedRecoverySite, result.BlockReason);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, probe.Calls);
        // 现场没收敛时不读桌面：连"现在长什么样"都不该碰。
        Assert.Equal(0, observation.CaptureCount);
    }

    [Fact]
    public async Task UnreadableDesktop_ReportsItsOwnReasonAndNeverDelegates()
    {
        var (snapshots, session, _) = await ReadySceneAsync();
        var observation = new RecordingObservationSource(DesktopObservationCaptureResult.Unavailable(
            new DesktopObservationCaptureIssue(
                DesktopObservationCaptureIssueCode.ShellObservationFailed,
                "读取桌面图标列表失败。")));
        var probe = new ExecutionProbe();

        var result = await CreateService(observation, new FakeCommittedSessions(session), snapshots, new FakeRecoveryStore(), probe)
            .RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.PreflightBlocked, result.Outcome);
        Assert.Equal(DesktopLayoutRollbackBlockReason.CurrentDesktopUnreadable, result.BlockReason);
        Assert.Contains("读取桌面图标列表失败", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task WithoutCommittedSession_RefusesAndKeepsTheDesktopUntouched()
    {
        var (snapshots, _, observation) = await ReadySceneAsync();
        var probe = new ExecutionProbe();

        var result = await CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(observation)),
            new FakeCommittedSessions(),
            snapshots,
            new FakeRecoveryStore(),
            probe).RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.PreflightBlocked, result.Outcome);
        Assert.Equal(DesktopLayoutRollbackBlockReason.NoCommittedSession, result.BlockReason);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task DesktopChangedSinceApply_StopsAndNamesTheItem()
    {
        var (snapshots, session, _) = await ReadySceneAsync();
        var movedByUser = Observation(
            Live("a", new PixelPoint(240, 320)),
            Live("b", new PixelPoint(1000, 900)));
        var probe = new ExecutionProbe();

        var result = await CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(movedByUser)),
            new FakeCommittedSessions(session),
            snapshots,
            new FakeRecoveryStore(),
            probe).RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.PreflightBlocked, result.Outcome);
        Assert.Equal(DesktopLayoutRollbackBlockReason.DesktopChangedSinceApply, result.BlockReason);
        Assert.Contains("N_b", result.Issues);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task RestorePlanBlockedByStationaryIcon_ReportsPlanReasonAndDoesNotDelegate()
    {
        // 用户后来在"整理前的位置"上放了一个新图标：还原目标被占住，
        // 这时既不能算"桌面没被改过"，也不能硬挤——必须在计划层停住。
        var (snapshots, session, _) = await ReadySceneAsync();
        var blocked = Observation(
            Live("a", new PixelPoint(240, 320)),
            Live("b", new PixelPoint(240, 440)),
            Live("z", new PixelPoint(160, 240)));
        var probe = new ExecutionProbe();

        var result = await CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(blocked)),
            new FakeCommittedSessions(session),
            snapshots,
            new FakeRecoveryStore(),
            probe).RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.PlanRejected, result.Outcome);
        Assert.Equal(DesktopLayoutRollbackBlockReason.RestorePlanBlocked, result.BlockReason);
        Assert.NotEmpty(result.Issues);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task ReadyScene_DelegatesExactlyOnceAndReportsSuccess()
    {
        var (snapshots, session, observation) = await ReadySceneAsync();
        var probe = new ExecutionProbe
        {
            ResultFactory = _ => TargetDisplayLayoutExecutionRunResult.FromPersistedRecoveryTerminal(
                CompletedRecord(session.SafetySnapshotId)),
        };

        var result = await CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(observation)),
            new FakeCommittedSessions(session),
            snapshots,
            new FakeRecoveryStore(),
            probe).RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.Completed, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Null(result.BlockReason);
        Assert.Equal(1, probe.Calls);
        Assert.NotNull(result.Preflight);
        Assert.True(result.Preflight!.CanRollback);
        // 交给执行层的是「按安全检查点还原」的计划：两个项目都要挪回去。
        Assert.Equal(2, probe.Plan!.MoveRequiredCount);
        Assert.False(probe.UsesTemporaryStaging);
    }

    [Fact]
    public async Task NoOpExecution_IsReportedAsCompletedNoOp()
    {
        var (snapshots, session, observation) = await ReadySceneAsync();
        var probe = new ExecutionProbe
        {
            ResultFactory = _ => TargetDisplayLayoutExecutionRunResult.NoOp(),
        };

        var result = await CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(observation)),
            new FakeCommittedSessions(session),
            snapshots,
            new FakeRecoveryStore(),
            probe).RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.CompletedNoOp, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task FailedExecution_IsReportedWithTheUnderlyingReason()
    {
        var (snapshots, session, observation) = await ReadySceneAsync();
        var probe = new ExecutionProbe
        {
            ResultFactory = _ => TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite(
                "UnauthorizedAccessException",
                "拒绝访问桌面图标列表。"),
        };

        var result = await CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(observation)),
            new FakeCommittedSessions(session),
            snapshots,
            new FakeRecoveryStore(),
            probe).RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.ExecutionFailed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Contains("拒绝访问桌面图标列表。", result.Issues);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task PrepareAsync_OnlyReadsAndNeverDelegates()
    {
        var (snapshots, session, observation) = await ReadySceneAsync();
        var probe = new ExecutionProbe();

        var preflight = await CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(observation)),
            new FakeCommittedSessions(session),
            snapshots,
            new FakeRecoveryStore(),
            probe).PrepareAsync();

        Assert.True(preflight.CanRollback);
        Assert.Equal(2, preflight.MoveBackCount);
        Assert.Equal(session.SessionId, preflight.SessionId);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task PrepareAsync_WithoutCommittedSession_ExplainsWhy()
    {
        var (snapshots, _, observation) = await ReadySceneAsync();
        var probe = new ExecutionProbe();

        var preflight = await CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(observation)),
            new FakeCommittedSessions(),
            snapshots,
            new FakeRecoveryStore(),
            probe).PrepareAsync();

        Assert.False(preflight.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.NoCommittedSession, preflight.Reason);
        Assert.Equal(0, probe.Calls);
    }

    /// <summary>
    /// 工作区设计应用（只写安全检查点 + 动态 journal，不写恢复会话）比更早的恢复会话新：
    /// 撤销必须选中工作区那次，而不是退回去撤上一次「撤销」。
    /// </summary>
    [Fact]
    public async Task NewerWorkspaceApply_IsChosenOverAnOlderRecoverySession()
    {
        var (snapshots, session, observation) = await ReadySceneAsync();
        var olderSession = session with { StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-2) };
        var workspace = WorkspaceApply(
            session.SafetySnapshotId,
            DateTimeOffset.UtcNow.AddHours(-1),
            ("a", new PixelPoint(240, 320)),
            ("b", new PixelPoint(240, 440)));
        var probe = new ExecutionProbe
        {
            ResultFactory = _ => TargetDisplayLayoutExecutionRunResult.FromPersistedRecoveryTerminal(
                CompletedRecord(session.SafetySnapshotId)),
        };

        var service = CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(observation)),
            new FakeCommittedSessions(olderSession),
            snapshots,
            new FakeRecoveryStore(),
            probe,
            new FakeCommittedWorkspaceWrites(workspace));

        var preflight = await service.PrepareAsync();

        Assert.True(preflight.CanRollback);
        Assert.Equal(2, preflight.MoveBackCount);
        Assert.Equal(workspace.SafetySnapshotId, preflight.SafetySnapshotId);
        // 文案点明来源：用户看到的是「撤掉这次设计应用」，不是含混的「上次整理」。
        Assert.Contains("工作区设计应用", preflight.Detail, StringComparison.Ordinal);

        var result = await service.RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.Completed, result.Outcome);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(2, probe.Plan!.MoveRequiredCount);
    }

    /// <summary>工作区那次整理之后桌面又被人动过：撤销必须停手并点名，绝不盲目回退。</summary>
    [Fact]
    public async Task WorkspaceApply_ChangedDesktop_StopsAndNamesTheItem()
    {
        var (snapshots, session, observation) = await ReadySceneAsync();
        var workspace = WorkspaceApply(
            session: session.SafetySnapshotId,
            startedAtUtc: DateTimeOffset.UtcNow,
            ("a", new PixelPoint(240, 320)),
            // 这个项目的终点与桌面现状不符：整理之后它又被移走了。
            ("b", new PixelPoint(999, 999)));
        var probe = new ExecutionProbe();

        var preflight = await CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(observation)),
            new FakeCommittedSessions(),
            snapshots,
            new FakeRecoveryStore(),
            probe,
            new FakeCommittedWorkspaceWrites(workspace)).PrepareAsync();

        Assert.False(preflight.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.DesktopChangedSinceApply, preflight.Reason);
        Assert.Contains("N_b", preflight.Detail, StringComparison.Ordinal);
        Assert.Equal(0, probe.Calls);
    }

    /// <summary>
    /// 真机踩到过：工作区设计应用的安全检查点是整桌面的（没有「目标显示器」），
    /// 而且它把两块屏的项目都写到了同一块屏上。撤销必须按每一项原属的屏逐屏还原，
    /// 而不是被「快照没有目标屏」拦下，也不是只还原其中一块屏。
    /// </summary>
    [Fact]
    public async Task WorkspaceApply_WholeDesktopCheckpoint_RestoresEveryAffectedDisplay()
    {
        var snapshot = WholeDesktopSnapshot(
            Snap("a", new PixelPoint(160, 240)),
            Snap("b", new PixelPoint(2000, 240), monitor: D2));
        var after = new[]
        {
            // 整理后两个项目都落在 DISPLAY2 上（跨屏聚合）。
            Live("a", new PixelPoint(2200, 300), monitor: D2),
            Live("b", new PixelPoint(2400, 300), monitor: D2),
        };
        var workspace = WorkspaceApply(
            snapshot.SnapshotId,
            DateTimeOffset.UtcNow,
            ("a", new PixelPoint(2200, 300)),
            ("b", new PixelPoint(2400, 300)));

        var store = new InMemoryDesktopLayoutSnapshotStore();
        await store.SaveAsync(snapshot);

        var probe = new ExecutionProbe
        {
            ResultFactory = _ => TargetDisplayLayoutExecutionRunResult.FromPersistedRecoveryTerminal(
                CompletedRecord(snapshot.SnapshotId)),
        };
        var service = CreateService(
            new RecordingObservationSource(DesktopObservationCaptureResult.Captured(
                TwoDisplayObservation(after))),
            new FakeCommittedSessions(),
            store,
            new FakeRecoveryStore(),
            probe,
            new FakeCommittedWorkspaceWrites(workspace));

        var preflight = await service.PrepareAsync();

        Assert.True(preflight.CanRollback);
        Assert.Equal(2, preflight.MoveBackCount);

        var result = await service.RollbackAsync();

        Assert.Equal(DesktopLayoutRollbackOutcome.Completed, result.Outcome);
        // 两块受影响的屏各排一次计划、各执行一次。
        Assert.Equal(2, probe.Calls);
        Assert.Equal(2, result.Executions.Count);
    }

    /// <summary>按动态 journal 的「已授权移动目标」造一条已收尾的工作区应用记录。</summary>
    private static DesktopWorkspaceDynamicExecutionJournal WorkspaceApply(
        Guid session,
        DateTimeOffset startedAtUtc,
        params (string StableId, PixelPoint FinalPosition)[] moves)
    {
        var goals = moves
            .Select(move => new DesktopWorkspaceDynamicExecutionItemGoal(
                move.StableId,
                $"N_{move.StableId}",
                DesktopWorkspaceCompiledItemDisposition.Place,
                isMovable: true,
                isMoveAuthorized: true,
                Td,
                move.FinalPosition,
                finalSlotId: $"slot-{move.StableId}"))
            .ToArray();
        var authority = new DesktopWorkspaceDynamicExecutionAuthoritySnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "preview-fp",
            "exact-fp",
            "geometry-fp",
            goals);

        return new DesktopWorkspaceDynamicExecutionJournal
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = session,
            BlueprintId = Guid.NewGuid(),
            BlueprintRevision = 1,
            CertificateFingerprint = "cert-fp",
            StartedAtUtc = startedAtUtc,
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Committed,
            Authority = authority,
            LastObservedSatisfiedGoalCount = authority.MoveGoalCount,
            FinalVerifiedAtUtc = startedAtUtc.AddMinutes(1),
            Revision = 2,
            UpdatedAtUtc = startedAtUtc.AddMinutes(1),
        };
    }

    private sealed class FakeCommittedWorkspaceWrites : ICommittedWorkspaceExecutionJournalQuery
    {
        private readonly IReadOnlyList<DesktopWorkspaceDynamicExecutionJournal> _journals;

        public FakeCommittedWorkspaceWrites(params DesktopWorkspaceDynamicExecutionJournal[] journals)
            => _journals = journals;

        public Task<IReadOnlyList<DesktopWorkspaceDynamicExecutionJournal>> ListCommittedAsync(
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DesktopWorkspaceDynamicExecutionJournal>>(
                _journals.Take(limit).ToArray());
    }

    private static RecoverySessionRecord CompletedRecord(Guid safetySnapshotId)
        => new()
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = safetySnapshotId,
            TargetDeviceName = Td,
            StartedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Lifecycle = RecoverySessionLifecycle.Completed,
            SourceRestorePlan = TargetDisplayLayoutRestorePlanner.CreatePlan(
                Snapshot(Snap("a", new PixelPoint(160, 240))),
                new DesktopIconReadResult(
                    new[] { Live("a", new PixelPoint(240, 320)) },
                    0,
                    ConnectionMethod.FindWindowSW,
                    TimeSpan.Zero,
                    null),
                new[] { Display() },
                Capabilities(),
                Td),
            ExecutionPlan = ExecutionPlan(Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320))),
            ForwardAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            RollbackAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            CompletedForwardStepIndexes = new[] { 0 },
            CompletedRollbackSourceStepIndexes = Array.Empty<int>(),
            Revision = 2,
        };

    private sealed class RecordingObservationSource : IDesktopObservationSource
    {
        private readonly DesktopObservationCaptureResult _result;

        public RecordingObservationSource(DesktopObservationCaptureResult result) => _result = result;

        public int CaptureCount { get; private set; }

        public Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeCommittedSessions : ICommittedRecoverySessionQuery
    {
        private readonly IReadOnlyList<RecoverySessionRecord> _sessions;

        public FakeCommittedSessions(params RecoverySessionRecord[] sessions) => _sessions = sessions;

        public Task<IReadOnlyList<RecoverySessionRecord>> ListCommittedAsync(
            int limit = 1,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecoverySessionRecord>>(_sessions.Take(limit).ToArray());
    }

    private sealed class FakeRecoveryStore : IRecoverySessionStore
    {
        private readonly IReadOnlyList<RecoverySessionRecord> _unresolved;

        public FakeRecoveryStore(params RecoverySessionRecord[] unresolved) => _unresolved = unresolved;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(RecoverySessionRecord record, CancellationToken cancellationToken = default)
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
            => Task.FromResult<IReadOnlyList<RecoverySessionRecord>>(_unresolved.Take(limit).ToArray());
    }

    private sealed class ExecutionProbe
    {
        public int Calls { get; private set; }
        public TargetDisplayLayoutRestorePlan? Plan { get; private set; }
        public bool UsesTemporaryStaging { get; private set; }

        public Func<TargetDisplayLayoutRestorePlan, TargetDisplayLayoutExecutionRunResult> ResultFactory { get; set; }
            = _ => TargetDisplayLayoutExecutionRunResult.NoOp();

        public Task<TargetDisplayLayoutExecutionRunResult> ExecuteAsync(
            TargetDisplayLayoutRestorePlan plan,
            bool usesTemporaryStaging,
            CancellationToken cancellationToken)
        {
            Calls++;
            Plan = plan;
            UsesTemporaryStaging = usesTemporaryStaging;
            return Task.FromResult(ResultFactory(plan));
        }
    }
}
