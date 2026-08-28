using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Workspaces.Execution;

public enum DesktopWorkspaceExecutionRunStatus
{
    FailedBeforeWrite = 0,
    NoOp = 1,
    Committed = 2,
    Restored = 3,
    NeedsAttention = 4,
}

/// <summary>
/// Workspace Apply 一次完整运行的产品无关结果。
/// Journal 是持久化事实；Error/VerificationIssues 只是当前运行诊断，不授予新的执行 authority。
/// </summary>
public sealed record DesktopWorkspaceExecutionRunResult
{
    private readonly IReadOnlyList<string> _verificationIssues;

    public DesktopWorkspaceExecutionRunStatus Status { get; }
    public bool WriterStarted { get; }
    public Guid? SafetySnapshotId { get; }
    public Guid? SessionId { get; }
    public DesktopWorkspaceExecutionJournal? Journal { get; }
    public string? ErrorType { get; }
    public string? ErrorMessage { get; }
    public IReadOnlyList<string> VerificationIssues => _verificationIssues;

    public bool Succeeded => Status is DesktopWorkspaceExecutionRunStatus.NoOp
        or DesktopWorkspaceExecutionRunStatus.Committed
        or DesktopWorkspaceExecutionRunStatus.Restored;

    private DesktopWorkspaceExecutionRunResult(
        DesktopWorkspaceExecutionRunStatus status,
        bool writerStarted,
        Guid? safetySnapshotId,
        Guid? sessionId,
        DesktopWorkspaceExecutionJournal? journal,
        string? errorType,
        string? errorMessage,
        IEnumerable<string>? verificationIssues = null)
    {
        Status = status;
        WriterStarted = writerStarted;
        SafetySnapshotId = safetySnapshotId;
        SessionId = sessionId;
        Journal = journal;
        ErrorType = errorType;
        ErrorMessage = errorMessage;
        _verificationIssues = Array.AsReadOnly(
            (verificationIssues ?? Array.Empty<string>()).ToArray());
    }

    internal static DesktopWorkspaceExecutionRunResult FailedBeforeWrite(
        string errorType,
        string errorMessage,
        Guid? safetySnapshotId = null,
        DesktopWorkspaceExecutionJournal? journal = null)
        => new(
            DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite,
            false,
            safetySnapshotId,
            journal?.SessionId,
            journal,
            errorType,
            errorMessage);

    internal static DesktopWorkspaceExecutionRunResult NoOp()
        => new(
            DesktopWorkspaceExecutionRunStatus.NoOp,
            false,
            null,
            null,
            null,
            null,
            null);

    internal static DesktopWorkspaceExecutionRunResult FromJournal(
        DesktopWorkspaceExecutionRunStatus status,
        bool writerStarted,
        DesktopWorkspaceExecutionJournal journal,
        string? errorType = null,
        string? errorMessage = null,
        IEnumerable<string>? verificationIssues = null)
        => new(
            status,
            writerStarted,
            journal.SafetySnapshotId,
            journal.SessionId,
            journal,
            errorType,
            errorMessage,
            verificationIssues);
}

/// <summary>
/// 用户确认过的 Frozen Workspace Preview → Windows side effects 的唯一多屏执行协调器。
///
/// Authority 顺序固定为：fresh Observation → FreshCheck/ExecutionPlan → Safety Snapshot →
/// Prepared Journal readback → ForwardInProgress checkpoint → Writer。
/// AI / redesign / compile 在这个类型里都不存在。
/// </summary>
public sealed class DesktopWorkspaceExecutionCoordinator
{
    private readonly IDesktopObservationSource _observationSource;
    private readonly IDesktopIconPositionWriter _positionWriter;
    private readonly StaThreadExecutor _staExecutor;
    private readonly IDesktopLayoutSafetySnapshotFactory _snapshotFactory;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;
    private readonly IDesktopWorkspaceExecutionJournalStore _journalStore;
    private readonly TimeProvider _timeProvider;

    public DesktopWorkspaceExecutionCoordinator(
        IDesktopObservationSource observationSource,
        IDesktopIconPositionWriter positionWriter,
        StaThreadExecutor staExecutor,
        IDesktopLayoutSafetySnapshotFactory snapshotFactory,
        IDesktopLayoutSnapshotStore snapshotStore,
        IDesktopWorkspaceExecutionJournalStore journalStore,
        TimeProvider? timeProvider = null)
    {
        _observationSource = observationSource ?? throw new ArgumentNullException(nameof(observationSource));
        _positionWriter = positionWriter ?? throw new ArgumentNullException(nameof(positionWriter));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DesktopWorkspaceExecutionRunResult> ExecuteAsync(
        DesktopWorkspaceApplyConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        DesktopWorkspaceExecutionJournal? journal = null;
        DesktopLayoutSnapshot? safetySnapshot = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _snapshotStore.InitializeAsync(cancellationToken);
            await _journalStore.InitializeAsync(cancellationToken);

            var unresolved = await _journalStore.ListUnresolvedAsync(cancellationToken);
            if (unresolved.Count > 0)
            {
                return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                    "UnresolvedWorkspaceExecution",
                    $"存在 {unresolved.Count} 个尚未收敛的 Workspace execution journal，拒绝开始新的写入。 ");
            }

            var capture = await _observationSource.CaptureAsync(cancellationToken);
            if (!capture.IsCaptured)
            {
                return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                    "FreshObservationUnavailable",
                    string.Join("；", capture.Issues.Select(static issue => issue.Message)));
            }

            var observation = capture.Observation!;
            var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);
            if (!geometryResult.Succeeded)
            {
                return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                    "FreshGeometryUnavailable",
                    string.Join("；", geometryResult.Issues.Select(static issue => issue.Message)));
            }

            var geometry = geometryResult.Geometry!;
            IReadOnlyList<DesktopWorkspaceCompilationItemFact> facts;
            try
            {
                facts = DesktopWorkspaceLocalItemFactProjector.Project(observation, geometry);
            }
            catch (Exception ex)
            {
                return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                    "FreshLocalRealityUnavailable",
                    $"无法建立 fresh Workspace local reality：{ex.GetType().Name}: {ex.Message}");
            }

            var planning = DesktopWorkspaceExecutionPlanner.Create(
                confirmation,
                geometry,
                facts);
            if (!planning.Succeeded)
            {
                return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                    "WorkspaceExecutionPlanRejected",
                    string.Join("；", planning.Issues.Select(static issue => issue.Message)));
            }

            var plan = planning.Plan!;
            if (plan.IsNoOp)
                return DesktopWorkspaceExecutionRunResult.NoOp();

            cancellationToken.ThrowIfCancellationRequested();

            safetySnapshot = _snapshotFactory.Create(
                observation.Icons,
                targetMonitorDeviceName: null,
                observation.IconReadConnectionMethod);
            await _snapshotStore.SaveAsync(safetySnapshot, cancellationToken);

            var snapshotReadBack = await _snapshotStore.GetAsync(
                safetySnapshot.SnapshotId,
                cancellationToken);
            var snapshotIssue = VerifySnapshotReadBack(safetySnapshot, snapshotReadBack);
            if (snapshotIssue is not null)
            {
                return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                    "SafetySnapshotVerificationFailed",
                    snapshotIssue,
                    safetySnapshot.SnapshotId);
            }

            cancellationToken.ThrowIfCancellationRequested();

            journal = DesktopWorkspaceExecutionJournal.CreatePrepared(
                safetySnapshot.SnapshotId,
                confirmation,
                plan,
                _timeProvider);
            await _journalStore.SaveAsync(journal, cancellationToken);

            var journalReadBack = await _journalStore.GetAsync(
                journal.SessionId,
                cancellationToken);
            var journalIssue = VerifyPreparedJournalReadBack(journal, journalReadBack);
            if (journalIssue is not null)
            {
                return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                    "PreparedJournalVerificationFailed",
                    journalIssue,
                    safetySnapshot.SnapshotId,
                    journal);
            }
            journal = journalReadBack!;

            if (cancellationToken.IsCancellationRequested)
            {
                var resolved = await TryTransitionAsync(
                    journal,
                    journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored });
                if (resolved is not null)
                    journal = resolved;

                return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                    "OperationCanceledBeforeWrite",
                    "Prepared Journal 已建立，但第一次 Writer 前检测到取消。 ",
                    safetySnapshot.SnapshotId,
                    journal);
            }

            var forwardStarted = await TryTransitionAsync(
                journal,
                journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress });
            if (forwardStarted is null)
            {
                return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                    "ForwardStartCheckpointFailed",
                    "无法在第一次 Writer 前持久化 ForwardInProgress checkpoint。 ",
                    safetySnapshot.SnapshotId,
                    journal);
            }
            journal = forwardStarted;

            return await ExecuteForwardAsync(
                confirmation,
                plan,
                safetySnapshot,
                journal,
                cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                "OperationCanceledBeforeWrite",
                $"Writer 未启动前操作取消：{ex.Message}",
                safetySnapshot?.SnapshotId,
                journal);
        }
        catch (Exception ex)
        {
            return DesktopWorkspaceExecutionRunResult.FailedBeforeWrite(
                ex.GetType().Name,
                $"Workspace execution preparation 失败：{ex.Message}",
                safetySnapshot?.SnapshotId,
                journal);
        }
    }

    private async Task<DesktopWorkspaceExecutionRunResult> ExecuteForwardAsync(
        DesktopWorkspaceApplyConfirmation confirmation,
        DesktopWorkspaceExecutionPlan plan,
        DesktopLayoutSnapshot safetySnapshot,
        DesktopWorkspaceExecutionJournal journal,
        CancellationToken cancellationToken)
    {
        var forwardAttempts = journal.ForwardAttempts.ToList();
        var rollbackAttempts = journal.RollbackAttempts.ToList();
        var completedForward = journal.CompletedForwardStepIndexes.ToList();
        var completedRollback = journal.CompletedRollbackSourceStepIndexes.ToList();
        var attemptIndex = forwardAttempts.Count + rollbackAttempts.Count;
        var writerStarted = false;

        for (var stepIndex = 0; stepIndex < plan.Steps.Count; stepIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (completedForward.Count == 0)
                {
                    var restored = await TryTransitionAsync(
                        journal,
                        journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored });
                    if (restored is not null)
                        journal = restored;
                    return DesktopWorkspaceExecutionRunResult.FromJournal(
                        DesktopWorkspaceExecutionRunStatus.Restored,
                        writerStarted,
                        journal,
                        "OperationCanceledBeforeFirstWrite",
                        "第一次 Writer 前检测到取消，没有桌面副作用。 ");
                }

                return await RollbackKnownPrefixAsync(
                    safetySnapshot,
                    journal,
                    forwardAttempts,
                    rollbackAttempts,
                    completedForward,
                    completedRollback,
                    attemptIndex,
                    "OperationCanceledException",
                    "步骤间检测到取消，开始倒序恢复已知成功步骤。 ");
            }

            var step = plan.Steps[stepIndex];
            DesktopIconMoveResult moveResult;
            writerStarted = true;
            try
            {
                moveResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(
                        step.StableId,
                        step.ToPixelPosition));
            }
            catch (Exception ex)
            {
                forwardAttempts.Add(CreateWriterExceptionEvidence(
                    attemptIndex++,
                    step,
                    DesktopWorkspaceExecutionAttemptPhase.Forward,
                    ex));

                var terminal = await PersistNeedsAttentionAsync(
                    journal,
                    forwardAttempts,
                    rollbackAttempts,
                    completedForward,
                    completedRollback,
                    stepIndex,
                    ex.GetType().Name,
                    $"Forward step #{stepIndex} Writer 异常：{ex.Message}",
                    recoveryFailureType: null,
                    recoveryFailureMessage: null);
                if (terminal is not null)
                    journal = terminal;

                return DesktopWorkspaceExecutionRunResult.FromJournal(
                    DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                    true,
                    journal,
                    ex.GetType().Name,
                    $"Forward step #{stepIndex} Writer 异常；是否产生副作用未知，已停止新的 Writer。 ");
            }

            var evidence = ClassifyMoveResult(
                attemptIndex++,
                step,
                DesktopWorkspaceExecutionAttemptPhase.Forward,
                moveResult);
            forwardAttempts.Add(evidence);

            if (evidence.Outcome != DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess)
            {
                var (errorType, errorMessage) = DescribeInexactEvidence(stepIndex, evidence, moveResult);
                var terminal = await PersistNeedsAttentionAsync(
                    journal,
                    forwardAttempts,
                    rollbackAttempts,
                    completedForward,
                    completedRollback,
                    stepIndex,
                    errorType,
                    errorMessage,
                    recoveryFailureType: null,
                    recoveryFailureMessage: null);
                if (terminal is not null)
                    journal = terminal;

                return DesktopWorkspaceExecutionRunResult.FromJournal(
                    DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                    true,
                    journal,
                    errorType,
                    errorMessage);
            }

            completedForward.Add(stepIndex);
            var checkpoint = journal with
            {
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress,
                ForwardAttempts = forwardAttempts,
                RollbackAttempts = rollbackAttempts,
                CompletedForwardStepIndexes = completedForward,
                CompletedRollbackSourceStepIndexes = completedRollback,
                LastFailedStepIndex = null,
                ForwardFailureType = null,
                ForwardFailureMessage = null,
                RecoveryFailureType = null,
                RecoveryFailureMessage = null,
            };
            var persisted = await TryTransitionAsync(journal, checkpoint);
            if (persisted is null)
            {
                return DesktopWorkspaceExecutionRunResult.FromJournal(
                    DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                    true,
                    journal,
                    "ForwardCheckpointPersistFailed",
                    $"Forward step #{stepIndex} 已精确完成，但 checkpoint 持久化失败。已停止新的 Writer。 ");
            }
            journal = persisted;

            if (cancellationToken.IsCancellationRequested && stepIndex + 1 < plan.Steps.Count)
            {
                return await RollbackKnownPrefixAsync(
                    safetySnapshot,
                    journal,
                    forwardAttempts,
                    rollbackAttempts,
                    completedForward,
                    completedRollback,
                    attemptIndex,
                    "OperationCanceledException",
                    "Writer 完成后检测到取消，开始倒序恢复已知成功步骤。 ");
            }
        }

        var verificationIssues = await VerifyForwardAsync(
            confirmation,
            CancellationToken.None);
        if (verificationIssues.Count > 0)
        {
            var terminal = await PersistNeedsAttentionAsync(
                journal,
                forwardAttempts,
                rollbackAttempts,
                completedForward,
                completedRollback,
                lastFailedStepIndex: null,
                "ForwardVerificationFailed",
                string.Join("；", verificationIssues),
                recoveryFailureType: null,
                recoveryFailureMessage: null);
            if (terminal is not null)
                journal = terminal;

            return DesktopWorkspaceExecutionRunResult.FromJournal(
                DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                true,
                journal,
                "ForwardVerificationFailed",
                "所有 Writer step 已完成，但独立 fresh verification 未通过。 ",
                verificationIssues);
        }

        var committed = await TryTransitionAsync(
            journal,
            journal with
            {
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Committed,
                ForwardAttempts = forwardAttempts,
                RollbackAttempts = rollbackAttempts,
                CompletedForwardStepIndexes = completedForward,
                CompletedRollbackSourceStepIndexes = completedRollback,
                LastFailedStepIndex = null,
                ForwardFailureType = null,
                ForwardFailureMessage = null,
                RecoveryFailureType = null,
                RecoveryFailureMessage = null,
            });
        if (committed is null)
        {
            return DesktopWorkspaceExecutionRunResult.FromJournal(
                DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                true,
                journal,
                "CommitCheckpointPersistFailed",
                "独立验证已通过，但 Committed 终态持久化失败。 ");
        }

        return DesktopWorkspaceExecutionRunResult.FromJournal(
            DesktopWorkspaceExecutionRunStatus.Committed,
            true,
            committed);
    }

    private async Task<DesktopWorkspaceExecutionRunResult> RollbackKnownPrefixAsync(
        DesktopLayoutSnapshot safetySnapshot,
        DesktopWorkspaceExecutionJournal journal,
        List<DesktopWorkspaceExecutionAttemptEvidence> forwardAttempts,
        List<DesktopWorkspaceExecutionAttemptEvidence> rollbackAttempts,
        List<int> completedForward,
        List<int> completedRollback,
        int attemptIndex,
        string forwardFailureType,
        string forwardFailureMessage)
    {
        var rolling = await TryTransitionAsync(
            journal,
            journal with
            {
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.RollingBack,
                ForwardAttempts = forwardAttempts,
                RollbackAttempts = rollbackAttempts,
                CompletedForwardStepIndexes = completedForward,
                CompletedRollbackSourceStepIndexes = completedRollback,
                ForwardFailureType = forwardFailureType,
                ForwardFailureMessage = forwardFailureMessage,
                RecoveryFailureType = null,
                RecoveryFailureMessage = null,
            });
        if (rolling is null)
        {
            return DesktopWorkspaceExecutionRunResult.FromJournal(
                DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                true,
                journal,
                "RollbackStartCheckpointFailed",
                "已有桌面副作用，但无法持久化 RollingBack checkpoint；已停止新的 Writer。 ");
        }
        journal = rolling;

        foreach (var sourceStepIndex in completedForward
            .Except(completedRollback)
            .OrderByDescending(static index => index))
        {
            var step = journal.ExecutionPlan.Steps[sourceStepIndex];
            DesktopIconMoveResult moveResult;
            try
            {
                moveResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(
                        step.StableId,
                        step.FromPixelPosition));
            }
            catch (Exception ex)
            {
                rollbackAttempts.Add(CreateWriterExceptionEvidence(
                    attemptIndex++,
                    step,
                    DesktopWorkspaceExecutionAttemptPhase.Rollback,
                    ex));
                var terminal = await PersistNeedsAttentionAsync(
                    journal,
                    forwardAttempts,
                    rollbackAttempts,
                    completedForward,
                    completedRollback,
                    sourceStepIndex,
                    forwardFailureType,
                    forwardFailureMessage,
                    "RollbackWriterException",
                    $"Rollback source step #{sourceStepIndex} Writer 异常：{ex.Message}");
                if (terminal is not null)
                    journal = terminal;

                return DesktopWorkspaceExecutionRunResult.FromJournal(
                    DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                    true,
                    journal,
                    "RollbackWriterException",
                    $"Rollback source step #{sourceStepIndex} Writer 异常。 ");
            }

            var evidence = ClassifyMoveResult(
                attemptIndex++,
                step,
                DesktopWorkspaceExecutionAttemptPhase.Rollback,
                moveResult);
            rollbackAttempts.Add(evidence);
            if (evidence.Outcome != DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess)
            {
                var (_, diagnostic) = DescribeInexactEvidence(sourceStepIndex, evidence, moveResult);
                var terminal = await PersistNeedsAttentionAsync(
                    journal,
                    forwardAttempts,
                    rollbackAttempts,
                    completedForward,
                    completedRollback,
                    sourceStepIndex,
                    forwardFailureType,
                    forwardFailureMessage,
                    "RollbackNotExact",
                    diagnostic);
                if (terminal is not null)
                    journal = terminal;

                return DesktopWorkspaceExecutionRunResult.FromJournal(
                    DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                    true,
                    journal,
                    "RollbackNotExact",
                    diagnostic);
            }

            completedRollback.Add(sourceStepIndex);
            var checkpoint = journal with
            {
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.RollingBack,
                ForwardAttempts = forwardAttempts,
                RollbackAttempts = rollbackAttempts,
                CompletedForwardStepIndexes = completedForward,
                CompletedRollbackSourceStepIndexes = completedRollback,
                ForwardFailureType = forwardFailureType,
                ForwardFailureMessage = forwardFailureMessage,
                RecoveryFailureType = null,
                RecoveryFailureMessage = null,
            };
            var persisted = await TryTransitionAsync(journal, checkpoint);
            if (persisted is null)
            {
                return DesktopWorkspaceExecutionRunResult.FromJournal(
                    DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                    true,
                    journal,
                    "RollbackCheckpointPersistFailed",
                    $"Rollback source step #{sourceStepIndex} 已精确完成，但 checkpoint 持久化失败。 ");
            }
            journal = persisted;
        }

        var verificationIssues = await VerifyRestoredSnapshotAsync(
            safetySnapshot,
            CancellationToken.None);
        if (verificationIssues.Count > 0)
        {
            var terminal = await PersistNeedsAttentionAsync(
                journal,
                forwardAttempts,
                rollbackAttempts,
                completedForward,
                completedRollback,
                lastFailedStepIndex: null,
                forwardFailureType,
                forwardFailureMessage,
                "RollbackVerificationFailed",
                string.Join("；", verificationIssues));
            if (terminal is not null)
                journal = terminal;

            return DesktopWorkspaceExecutionRunResult.FromJournal(
                DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                true,
                journal,
                "RollbackVerificationFailed",
                "Rollback Writer steps 已完成，但安全快照独立验证未通过。 ",
                verificationIssues);
        }

        var restored = await TryTransitionAsync(
            journal,
            journal with
            {
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored,
                ForwardAttempts = forwardAttempts,
                RollbackAttempts = rollbackAttempts,
                CompletedForwardStepIndexes = completedForward,
                CompletedRollbackSourceStepIndexes = completedRollback,
                RecoveryFailureType = null,
                RecoveryFailureMessage = null,
            });
        if (restored is null)
        {
            return DesktopWorkspaceExecutionRunResult.FromJournal(
                DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                true,
                journal,
                "RestoreCheckpointPersistFailed",
                "安全快照验证已通过，但 Restored 终态持久化失败。 ");
        }

        return DesktopWorkspaceExecutionRunResult.FromJournal(
            DesktopWorkspaceExecutionRunStatus.Restored,
            true,
            restored,
            forwardFailureType,
            forwardFailureMessage);
    }

    private async Task<DesktopWorkspaceExecutionJournal?> PersistNeedsAttentionAsync(
        DesktopWorkspaceExecutionJournal current,
        IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> forwardAttempts,
        IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> rollbackAttempts,
        IReadOnlyList<int> completedForward,
        IReadOnlyList<int> completedRollback,
        int? lastFailedStepIndex,
        string? forwardFailureType,
        string? forwardFailureMessage,
        string? recoveryFailureType,
        string? recoveryFailureMessage)
    {
        return await TryTransitionAsync(
            current,
            current with
            {
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention,
                ForwardAttempts = forwardAttempts,
                RollbackAttempts = rollbackAttempts,
                CompletedForwardStepIndexes = completedForward,
                CompletedRollbackSourceStepIndexes = completedRollback,
                LastFailedStepIndex = lastFailedStepIndex,
                ForwardFailureType = forwardFailureType,
                ForwardFailureMessage = forwardFailureMessage,
                RecoveryFailureType = recoveryFailureType,
                RecoveryFailureMessage = recoveryFailureMessage,
            });
    }

    private async Task<DesktopWorkspaceExecutionJournal?> TryTransitionAsync(
        DesktopWorkspaceExecutionJournal current,
        DesktopWorkspaceExecutionJournal requested)
    {
        try
        {
            if (requested.SessionId != current.SessionId || requested.Revision != current.Revision)
                return null;
            requested.Validate();
            var updated = await _journalStore.UpdateAsync(requested, CancellationToken.None);
            var readBack = await _journalStore.GetAsync(updated.SessionId, CancellationToken.None);
            if (readBack is null || readBack.Revision != updated.Revision)
                return null;
            readBack.Validate();
            return readBack;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> VerifyForwardAsync(
        DesktopWorkspaceApplyConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        var capture = await _observationSource.CaptureAsync(cancellationToken);
        if (!capture.IsCaptured)
        {
            return Array.AsReadOnly(new[]
            {
                "Forward verification 无法取得完整 fresh DesktopObservation：" +
                string.Join("；", capture.Issues.Select(static issue => issue.Message)),
            });
        }

        var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(capture.Observation!);
        if (!geometryResult.Succeeded)
        {
            return Array.AsReadOnly(new[]
            {
                "Forward verification 无法建立 fresh Explorer geometry：" +
                string.Join("；", geometryResult.Issues.Select(static issue => issue.Message)),
            });
        }

        var geometry = geometryResult.Geometry!;
        var preview = confirmation.FrozenPreview;
        var issues = new List<string>();
        if (!string.Equals(
                geometry.GeometryFingerprint,
                preview.ExactPlan.GeometryFingerprint,
                StringComparison.Ordinal))
        {
            issues.Add("Explorer geometry 在执行期间发生变化。 ");
        }

        IReadOnlyList<DesktopWorkspaceCompilationItemFact> facts;
        try
        {
            facts = DesktopWorkspaceLocalItemFactProjector.Project(capture.Observation!, geometry);
        }
        catch (Exception ex)
        {
            issues.Add($"Forward verification 无法建立 local reality：{ex.GetType().Name}: {ex.Message}");
            return Array.AsReadOnly(issues.ToArray());
        }

        var expectedIds = preview.Items.Select(static item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var actualIds = facts.Select(static item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedIds.SetEquals(actualIds))
        {
            issues.Add("执行后的桌面项目集合与 Frozen Preview 不一致。 ");
            return Array.AsReadOnly(issues.ToArray());
        }

        var actualById = facts.ToDictionary(static item => item.StableId, StringComparer.Ordinal);
        foreach (var expected in preview.Items.OrderBy(static item => item.StableId, StringComparer.Ordinal))
        {
            var actual = actualById[expected.StableId];
            var expectedDisplayRef = expected.Disposition == DesktopWorkspaceCompiledItemDisposition.Place
                ? expected.TargetDisplayRef
                : expected.SourceDisplayRef;
            var expectedPixel = expected.Disposition == DesktopWorkspaceCompiledItemDisposition.Place
                ? expected.TargetPixelPosition
                : expected.SourcePixelPosition;

            if (expectedDisplayRef is null || expectedPixel is null)
            {
                issues.Add($"Frozen Preview 项目 \"{expected.DisplayName}\" 缺少可验证最终位置。 ");
                continue;
            }

            if (!string.Equals(actual.CurrentDisplayRef, expectedDisplayRef, StringComparison.Ordinal) ||
                actual.PixelPosition != expectedPixel.Value)
            {
                issues.Add(
                    $"项目 \"{expected.DisplayName}\" 最终位置不符：" +
                    $"实际 {actual.CurrentDisplayRef} ({actual.PixelPosition.X},{actual.PixelPosition.Y})，" +
                    $"期望 {expectedDisplayRef} ({expectedPixel.Value.X},{expectedPixel.Value.Y})。 ");
                if (issues.Count >= 8)
                    break;
            }
        }

        return Array.AsReadOnly(issues.ToArray());
    }

    private async Task<IReadOnlyList<string>> VerifyRestoredSnapshotAsync(
        DesktopLayoutSnapshot safetySnapshot,
        CancellationToken cancellationToken)
    {
        var capture = await _observationSource.CaptureAsync(cancellationToken);
        if (!capture.IsCaptured)
        {
            return Array.AsReadOnly(new[]
            {
                "Rollback verification 无法取得完整 fresh DesktopObservation：" +
                string.Join("；", capture.Issues.Select(static issue => issue.Message)),
            });
        }

        var actual = capture.Observation!.Icons.ToDictionary(
            static icon => icon.StableId,
            StringComparer.Ordinal);
        var expected = safetySnapshot.Icons.ToDictionary(
            static icon => icon.StableId,
            StringComparer.Ordinal);
        var issues = new List<string>();

        if (!actual.Keys.ToHashSet(StringComparer.Ordinal)
            .SetEquals(expected.Keys))
        {
            issues.Add("Rollback 后桌面项目集合与 Safety Snapshot 不一致。 ");
            return Array.AsReadOnly(issues.ToArray());
        }

        foreach (var pair in expected.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var current = actual[pair.Key];
            var snapshotItem = pair.Value;
            if (current.PixelPosition != snapshotItem.PixelPosition ||
                !string.Equals(
                    current.MonitorDeviceName,
                    snapshotItem.MonitorDeviceName,
                    StringComparison.Ordinal))
            {
                issues.Add(
                    $"项目 \"{snapshotItem.DisplayName}\" 未恢复到 Safety Snapshot：" +
                    $"实际 ({current.PixelPosition.X},{current.PixelPosition.Y}) / {current.MonitorDeviceName}，" +
                    $"期望 ({snapshotItem.PixelPosition.X},{snapshotItem.PixelPosition.Y}) / {snapshotItem.MonitorDeviceName}。 ");
                if (issues.Count >= 8)
                    break;
            }
        }

        return Array.AsReadOnly(issues.ToArray());
    }

    private static DesktopWorkspaceExecutionAttemptEvidence ClassifyMoveResult(
        int attemptIndex,
        DesktopWorkspaceExecutionStep step,
        DesktopWorkspaceExecutionAttemptPhase phase,
        DesktopIconMoveResult result)
    {
        var expectedPreviousDisplay = phase == DesktopWorkspaceExecutionAttemptPhase.Forward
            ? step.FromDisplayRef
            : step.ToDisplayRef;
        var expectedPreviousPixel = phase == DesktopWorkspaceExecutionAttemptPhase.Forward
            ? step.FromPixelPosition
            : step.ToPixelPosition;
        var requestedDisplay = phase == DesktopWorkspaceExecutionAttemptPhase.Forward
            ? step.ToDisplayRef
            : step.FromDisplayRef;
        var requestedPixel = phase == DesktopWorkspaceExecutionAttemptPhase.Forward
            ? step.ToPixelPosition
            : step.FromPixelPosition;

        var stableIdMatches = string.Equals(result.StableId, step.StableId, StringComparison.Ordinal);
        var previousMatches = result.PreviousPosition == expectedPreviousPixel;
        var requestedMatches = result.RequestedPosition == requestedPixel;

        DesktopWorkspaceExecutionAttemptOutcome outcome;
        var trustedActual = stableIdMatches;
        var exact = false;
        if (!stableIdMatches)
        {
            outcome = DesktopWorkspaceExecutionAttemptOutcome.StableIdMismatch;
            trustedActual = false;
        }
        else if (!previousMatches || !requestedMatches)
        {
            outcome = DesktopWorkspaceExecutionAttemptOutcome.PreviousPositionMismatch;
        }
        else if (!result.IsExact)
        {
            outcome = DesktopWorkspaceExecutionAttemptOutcome.InexactKnown;
        }
        else
        {
            outcome = DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess;
            exact = true;
        }

        return new DesktopWorkspaceExecutionAttemptEvidence(
            attemptIndex,
            step.StepIndex,
            phase,
            step.StableId,
            expectedPreviousDisplay,
            expectedPreviousPixel,
            requestedDisplay,
            requestedPixel,
            result.StableId,
            result.PreviousPosition,
            result.ActualPosition,
            trustedActual,
            exact,
            outcome,
            result.ConnectionMethod,
            exceptionType: null,
            errorMessage: null,
            result.PrimaryPathException?.GetType().Name,
            result.PrimaryPathException?.Message);
    }

    private static DesktopWorkspaceExecutionAttemptEvidence CreateWriterExceptionEvidence(
        int attemptIndex,
        DesktopWorkspaceExecutionStep step,
        DesktopWorkspaceExecutionAttemptPhase phase,
        Exception exception)
    {
        var expectedPreviousDisplay = phase == DesktopWorkspaceExecutionAttemptPhase.Forward
            ? step.FromDisplayRef
            : step.ToDisplayRef;
        var expectedPreviousPixel = phase == DesktopWorkspaceExecutionAttemptPhase.Forward
            ? step.FromPixelPosition
            : step.ToPixelPosition;
        var requestedDisplay = phase == DesktopWorkspaceExecutionAttemptPhase.Forward
            ? step.ToDisplayRef
            : step.FromDisplayRef;
        var requestedPixel = phase == DesktopWorkspaceExecutionAttemptPhase.Forward
            ? step.ToPixelPosition
            : step.FromPixelPosition;

        return new DesktopWorkspaceExecutionAttemptEvidence(
            attemptIndex,
            step.StepIndex,
            phase,
            step.StableId,
            expectedPreviousDisplay,
            expectedPreviousPixel,
            requestedDisplay,
            requestedPixel,
            reportedStableId: null,
            reportedPreviousPixelPosition: null,
            actualPixelPosition: null,
            hasTrustedActualPosition: false,
            isExact: false,
            DesktopWorkspaceExecutionAttemptOutcome.WriterException,
            connectionMethod: null,
            exception.GetType().Name,
            exception.Message,
            primaryPathExceptionType: null,
            primaryPathExceptionMessage: null);
    }

    private static (string ErrorType, string ErrorMessage) DescribeInexactEvidence(
        int stepIndex,
        DesktopWorkspaceExecutionAttemptEvidence evidence,
        DesktopIconMoveResult result)
    {
        return evidence.Outcome switch
        {
            DesktopWorkspaceExecutionAttemptOutcome.StableIdMismatch => (
                "StableIdMismatch",
                $"Step #{stepIndex} Writer 报告 StableId \"{result.StableId}\"，期望 \"{evidence.ExpectedStableId}\"。 "),
            DesktopWorkspaceExecutionAttemptOutcome.PreviousPositionMismatch => (
                "PreviousPositionMismatch",
                $"Step #{stepIndex} Previous/Requested identity 与冻结 execution step 不一致。 "),
            DesktopWorkspaceExecutionAttemptOutcome.InexactKnown => (
                "InexactKnownPosition",
                $"Step #{stepIndex} 实际落点 ({result.ActualPosition.X},{result.ActualPosition.Y}) " +
                $"不是请求位置 ({evidence.RequestedPixelPosition.X},{evidence.RequestedPixelPosition.Y})。 "),
            _ => (
                "UnexpectedWriterEvidence",
                $"Step #{stepIndex} 返回非 ExactSuccess 证据 {evidence.Outcome}。 "),
        };
    }

    private static string? VerifySnapshotReadBack(
        DesktopLayoutSnapshot expected,
        DesktopLayoutSnapshot? actual)
    {
        if (actual is null)
            return "Safety Snapshot 保存后回读返回 null。 ";
        if (actual.SnapshotId != expected.SnapshotId ||
            actual.CreatedAtUtc != expected.CreatedAtUtc ||
            !string.Equals(actual.TargetMonitorDeviceName, expected.TargetMonitorDeviceName, StringComparison.Ordinal) ||
            actual.ConnectionMethod != expected.ConnectionMethod ||
            actual.FormatVersion != expected.FormatVersion ||
            actual.Icons.Count != expected.Icons.Count)
        {
            return "Safety Snapshot 回读 metadata 与刚保存的快照不一致。 ";
        }

        for (var index = 0; index < expected.Icons.Count; index++)
        {
            if (expected.Icons[index] != actual.Icons[index])
                return $"Safety Snapshot 回读第 {index} 项与刚保存的快照不一致。 ";
        }
        return null;
    }

    private static string? VerifyPreparedJournalReadBack(
        DesktopWorkspaceExecutionJournal expected,
        DesktopWorkspaceExecutionJournal? actual)
    {
        if (actual is null)
            return "Prepared Journal 保存后回读返回 null。 ";
        if (actual.SessionId != expected.SessionId ||
            actual.SafetySnapshotId != expected.SafetySnapshotId ||
            actual.BlueprintId != expected.BlueprintId ||
            actual.BlueprintRevision != expected.BlueprintRevision ||
            actual.Lifecycle != DesktopWorkspaceExecutionJournalLifecycle.Prepared ||
            actual.Revision != expected.Revision ||
            !string.Equals(actual.CertificateFingerprint, expected.CertificateFingerprint, StringComparison.Ordinal) ||
            !string.Equals(
                actual.ExecutionPlan.ExecutionPlanFingerprint,
                expected.ExecutionPlan.ExecutionPlanFingerprint,
                StringComparison.Ordinal) ||
            actual.ExecutionPlan.Steps.Count != expected.ExecutionPlan.Steps.Count)
        {
            return "Prepared Journal 回读 identity / plan 与刚保存的 journal 不一致。 ";
        }
        return null;
    }
}
