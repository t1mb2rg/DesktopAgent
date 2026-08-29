using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Workspaces.Execution;

/// <summary>
/// Dynamic Workspace execution 的运行结果。产品切流前先保持独立结果类型，
/// 避免把 V10 dynamic journal 假装成 V9 static journal。
/// </summary>
public sealed record DesktopWorkspaceDynamicExecutionRunResult
{
    private readonly IReadOnlyList<string> _verificationIssues;

    public DesktopWorkspaceExecutionRunStatus Status { get; }
    public bool WriterStarted { get; }
    public Guid? SafetySnapshotId { get; }
    public Guid? SessionId { get; }
    public DesktopWorkspaceDynamicExecutionJournal? Journal { get; }
    public string? ErrorType { get; }
    public string? ErrorMessage { get; }
    public IReadOnlyList<string> VerificationIssues => _verificationIssues;

    private DesktopWorkspaceDynamicExecutionRunResult(
        DesktopWorkspaceExecutionRunStatus status,
        bool writerStarted,
        Guid? safetySnapshotId,
        Guid? sessionId,
        DesktopWorkspaceDynamicExecutionJournal? journal,
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
        _verificationIssues = Array.AsReadOnly((verificationIssues ?? []).ToArray());
    }

    internal static DesktopWorkspaceDynamicExecutionRunResult FailedBeforeWrite(
        string errorType,
        string errorMessage,
        Guid? safetySnapshotId = null,
        DesktopWorkspaceDynamicExecutionJournal? journal = null)
        => new(
            DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite,
            writerStarted: false,
            safetySnapshotId,
            journal?.SessionId,
            journal,
            errorType,
            errorMessage);

    internal static DesktopWorkspaceDynamicExecutionRunResult NoOp()
        => new(
            DesktopWorkspaceExecutionRunStatus.NoOp,
            writerStarted: false,
            safetySnapshotId: null,
            sessionId: null,
            journal: null,
            errorType: null,
            errorMessage: null);

    internal static DesktopWorkspaceDynamicExecutionRunResult FromJournal(
        DesktopWorkspaceExecutionRunStatus status,
        bool writerStarted,
        DesktopWorkspaceDynamicExecutionJournal journal,
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
/// 用户确认 Frozen Preview 后的 fresh-driven Workspace 执行器。
///
/// 第一次 Writer 前仍执行严格 FreshCheck；进入 Writer boundary 后只冻结最终目标 authority，
/// 每次 Writer 前重新观察当前 Windows facts，并由 DynamicNextStepPlanner 只决定下一刀。
/// AI / redesign / compile 均不在此类型中。
/// </summary>
public sealed class DesktopWorkspaceDynamicExecutionCoordinator
{
    private readonly IDesktopObservationSource _observationSource;
    private readonly IDesktopIconPositionWriter _positionWriter;
    private readonly StaThreadExecutor _staExecutor;
    private readonly IDesktopLayoutSafetySnapshotFactory _snapshotFactory;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;
    private readonly IDesktopWorkspaceExecutionJournalStore _staticJournalStore;
    private readonly IDesktopWorkspaceDynamicExecutionJournalStore _dynamicJournalStore;
    private readonly TimeProvider _timeProvider;

    public DesktopWorkspaceDynamicExecutionCoordinator(
        IDesktopObservationSource observationSource,
        IDesktopIconPositionWriter positionWriter,
        StaThreadExecutor staExecutor,
        IDesktopLayoutSafetySnapshotFactory snapshotFactory,
        IDesktopLayoutSnapshotStore snapshotStore,
        IDesktopWorkspaceExecutionJournalStore staticJournalStore,
        IDesktopWorkspaceDynamicExecutionJournalStore dynamicJournalStore,
        TimeProvider? timeProvider = null)
    {
        _observationSource = observationSource ?? throw new ArgumentNullException(nameof(observationSource));
        _positionWriter = positionWriter ?? throw new ArgumentNullException(nameof(positionWriter));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _staticJournalStore = staticJournalStore ?? throw new ArgumentNullException(nameof(staticJournalStore));
        _dynamicJournalStore = dynamicJournalStore ?? throw new ArgumentNullException(nameof(dynamicJournalStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DesktopWorkspaceDynamicExecutionRunResult> ExecuteAsync(
        DesktopWorkspaceApplyConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        DesktopLayoutSnapshot? safetySnapshot = null;
        DesktopWorkspaceDynamicExecutionJournal? journal = null;
        var writerStarted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _snapshotStore.InitializeAsync(cancellationToken);
            await _staticJournalStore.InitializeAsync(cancellationToken);
            await _dynamicJournalStore.InitializeAsync(cancellationToken);

            var unresolvedStatic = await _staticJournalStore.ListUnresolvedAsync(cancellationToken);
            if (unresolvedStatic.Count > 0)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "UnresolvedStaticWorkspaceExecution",
                    $"存在 {unresolvedStatic.Count} 个尚未收敛的 V9 Workspace execution journal，拒绝开始新的 Dynamic Writer。 ");
            }

            var unresolvedDynamic = await _dynamicJournalStore.ListUnresolvedAsync(cancellationToken);
            if (unresolvedDynamic.Count > 0)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "UnresolvedDynamicWorkspaceExecution",
                    $"存在 {unresolvedDynamic.Count} 个尚未收敛的 Dynamic Workspace execution journal，拒绝开始新的 Writer。 ");
            }

            var initialFresh = await CaptureFreshFactsAsync(cancellationToken);
            if (!initialFresh.Succeeded)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    initialFresh.ErrorType!,
                    initialFresh.ErrorMessage!);
            }

            var strictFreshCheck = DesktopWorkspaceApplyFreshCheck.Evaluate(
                confirmation,
                initialFresh.Geometry!,
                initialFresh.Facts!);
            if (!strictFreshCheck.IsReady)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "WorkspaceFreshCheckRejected",
                    $"Strict FreshCheck={strictFreshCheck.Status}：{string.Join("；", strictFreshCheck.Issues)}");
            }

            var initialDecision = DesktopWorkspaceDynamicNextStepPlanner.Decide(
                confirmation.FrozenPreview,
                initialFresh.Geometry!,
                initialFresh.Facts!,
                decisionIndex: 0);
            if (initialDecision.Status == DesktopWorkspaceDynamicNextStepStatus.Blocked)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "DynamicPlanningBlockedBeforeWrite",
                    DescribePlanningIssues(initialDecision));
            }
            if (initialDecision.IsComplete)
                return DesktopWorkspaceDynamicExecutionRunResult.NoOp();

            cancellationToken.ThrowIfCancellationRequested();

            safetySnapshot = _snapshotFactory.Create(
                initialFresh.Observation!.Icons,
                targetMonitorDeviceName: null,
                initialFresh.Observation.IconReadConnectionMethod);
            await _snapshotStore.SaveAsync(safetySnapshot, cancellationToken);

            var snapshotReadBack = await _snapshotStore.GetAsync(
                safetySnapshot.SnapshotId,
                cancellationToken);
            var snapshotIssue = VerifySnapshotReadBack(safetySnapshot, snapshotReadBack);
            if (snapshotIssue is not null)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "SafetySnapshotVerificationFailed",
                    snapshotIssue,
                    safetySnapshot.SnapshotId);
            }

            var authority = DesktopWorkspaceDynamicExecutionAuthoritySnapshot.FromConfirmation(confirmation);
            journal = DesktopWorkspaceDynamicExecutionJournal.CreatePrepared(
                safetySnapshot.SnapshotId,
                confirmation,
                authority,
                _timeProvider);
            await _dynamicJournalStore.SaveAsync(journal, cancellationToken);

            var journalReadBack = await _dynamicJournalStore.GetAsync(journal.SessionId, cancellationToken);
            var journalIssue = VerifyPreparedJournalReadBack(journal, journalReadBack);
            if (journalIssue is not null)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "PreparedDynamicJournalVerificationFailed",
                    journalIssue,
                    safetySnapshot.SnapshotId,
                    journal);
            }
            journal = journalReadBack!;

            if (cancellationToken.IsCancellationRequested)
            {
                var restored = await TryTransitionAsync(
                    journal,
                    journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored });
                if (restored is not null)
                    journal = restored;

                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "OperationCanceledBeforeWrite",
                    "Dynamic Prepared Journal 已建立，但第一次 Writer 前检测到取消。 ",
                    safetySnapshot.SnapshotId,
                    journal);
            }

            var forwardStarted = await TryTransitionAsync(
                journal,
                journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress });
            if (forwardStarted is null)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "ForwardStartCheckpointFailed",
                    "无法在第一次 Dynamic Writer 前持久化 ForwardInProgress checkpoint。 ",
                    safetySnapshot.SnapshotId,
                    journal);
            }
            journal = forwardStarted;

            return await ExecuteDynamicLoopAsync(
                confirmation,
                journal,
                () => writerStarted = true,
                cancellationToken);
        }
        catch (OperationCanceledException ex) when (!writerStarted)
        {
            if (journal is not null && !journal.IsResolved)
            {
                var restored = await TryTransitionAsync(
                    journal,
                    journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored });
                if (restored is not null)
                    journal = restored;
            }

            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "OperationCanceledBeforeWrite",
                $"Writer 未启动前操作取消：{ex.Message}",
                safetySnapshot?.SnapshotId,
                journal);
        }
        catch (Exception ex)
        {
            return await ContainUnexpectedExecutionExceptionAsync(
                journal,
                safetySnapshot?.SnapshotId,
                writerStarted,
                ex);
        }
    }

    private async Task<DesktopWorkspaceDynamicExecutionRunResult> ExecuteDynamicLoopAsync(
        DesktopWorkspaceApplyConfirmation confirmation,
        DesktopWorkspaceDynamicExecutionJournal journal,
        Action onWriterStarting,
        CancellationToken cancellationToken)
    {
        var attempts = journal.ForwardAttempts.ToList();
        var writerStarted = attempts.Count > 0;
        var maxDecisionCount = Math.Max(32, checked(journal.Authority.MoveGoalCount * 4));

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (!writerStarted)
                {
                    var restored = await TryTransitionAsync(
                        journal,
                        journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored });
                    if (restored is not null)
                        journal = restored;
                    return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                        "OperationCanceledBeforeWrite",
                        "第一次 Dynamic Writer 前检测到取消。",
                        journal.SafetySnapshotId,
                        journal);
                }

                return await PersistNeedsAttentionResultAsync(
                    journal,
                    attempts,
                    lastFailedDecisionIndex: attempts.Count,
                    "OperationCanceledAfterWrite",
                    "至少一次 Writer 已完成后检测到取消；Dynamic execution 不盲目反演旧路线，已停止新的 Writer。 ");
            }

            if (attempts.Count >= maxDecisionCount)
            {
                return await PersistNeedsAttentionResultAsync(
                    journal,
                    attempts,
                    lastFailedDecisionIndex: attempts.Count,
                    "DynamicMoveBudgetExceeded",
                    $"Dynamic execution 已达到 {maxDecisionCount} 次 Writer 决策上限，仍未稳定收敛。 ");
            }

            var fresh = await CaptureFreshFactsAsync(CancellationToken.None);
            if (!fresh.Succeeded)
            {
                if (!writerStarted)
                {
                    var restored = await TryTransitionAsync(
                        journal,
                        journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored });
                    if (restored is not null)
                        journal = restored;
                    return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                        fresh.ErrorType!,
                        fresh.ErrorMessage!,
                        journal.SafetySnapshotId,
                        journal);
                }

                return await PersistNeedsAttentionResultAsync(
                    journal,
                    attempts,
                    attempts.Count,
                    fresh.ErrorType!,
                    fresh.ErrorMessage!);
            }

            var decision = DesktopWorkspaceDynamicNextStepPlanner.Decide(
                confirmation.FrozenPreview,
                fresh.Geometry!,
                fresh.Facts!,
                attempts.Count);

            if (decision.Status == DesktopWorkspaceDynamicNextStepStatus.Blocked)
            {
                if (!writerStarted)
                {
                    var restored = await TryTransitionAsync(
                        journal,
                        journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored });
                    if (restored is not null)
                        journal = restored;
                    return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                        "DynamicPlanningBlockedBeforeWrite",
                        DescribePlanningIssues(decision),
                        journal.SafetySnapshotId,
                        journal);
                }

                return await PersistNeedsAttentionResultAsync(
                    journal,
                    attempts,
                    attempts.Count,
                    "DynamicPlanningBlockedAfterWrite",
                    DescribePlanningIssues(decision));
            }

            if (decision.IsComplete)
            {
                var verification = await VerifyFinalStateAsync(
                    confirmation,
                    attempts.Count,
                    CancellationToken.None);
                if (verification.Status == DynamicFinalVerificationStatus.Complete)
                {
                    var committed = await TryTransitionAsync(
                        journal,
                        journal with
                        {
                            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Committed,
                            ForwardAttempts = attempts,
                            LastObservedSatisfiedGoalCount = journal.Authority.MoveGoalCount,
                            LastFailedDecisionIndex = null,
                            ForwardFailureType = null,
                            ForwardFailureMessage = null,
                            RecoveryFailureType = null,
                            RecoveryFailureMessage = null,
                            FinalVerifiedAtUtc = _timeProvider.GetUtcNow(),
                        });
                    if (committed is null)
                    {
                        return DesktopWorkspaceDynamicExecutionRunResult.FromJournal(
                            DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                            writerStarted,
                            journal,
                            "CommitCheckpointPersistFailed",
                            "最终独立验证已通过，但 Dynamic Committed 终态持久化失败。 ");
                    }

                    return DesktopWorkspaceDynamicExecutionRunResult.FromJournal(
                        DesktopWorkspaceExecutionRunStatus.Committed,
                        writerStarted,
                        committed);
                }

                if (verification.Status == DynamicFinalVerificationStatus.MoveReady)
                {
                    // Explorer 在两次独立 fresh capture 之间再次改变了已获授权项目的位置。
                    // 不使用上一份 observation，回到循环顶部重新读取事实。
                    continue;
                }

                return await PersistNeedsAttentionResultAsync(
                    journal,
                    attempts,
                    attempts.Count,
                    "FinalVerificationBlocked",
                    verification.Message ?? "最终独立验证被本地安全门阻断。");
            }

            var move = decision.Move!;
            DesktopIconMoveResult moveResult;
            try
            {
                moveResult = await _staExecutor.RunAsync(() =>
                {
                    onWriterStarting();
                    writerStarted = true;
                    return _positionWriter.MoveDesktopIcon(
                        move.StableId,
                        move.ToPixelPosition);
                });
            }
            catch (Exception ex)
            {
                attempts.Add(CreateWriterExceptionEvidence(
                    attempts.Count,
                    fresh.Observation!.ObservationId,
                    move,
                    decision.SatisfiedMovingGoalCount,
                    ex));

                return await PersistNeedsAttentionResultAsync(
                    journal,
                    attempts,
                    move.DecisionIndex,
                    ex.GetType().Name,
                    $"Dynamic decision #{move.DecisionIndex} Writer 异常：{ex.Message}；是否产生副作用未知，已停止新的 Writer。 ");
            }

            var evidence = ClassifyMoveResult(
                attempts.Count,
                fresh.Observation!.ObservationId,
                move,
                decision.SatisfiedMovingGoalCount,
                moveResult);
            attempts.Add(evidence);

            if (evidence.Outcome != DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess)
            {
                var (errorType, errorMessage) = DescribeInexactEvidence(move, evidence, moveResult);
                return await PersistNeedsAttentionResultAsync(
                    journal,
                    attempts,
                    move.DecisionIndex,
                    errorType,
                    errorMessage);
            }

            var checkpoint = await TryTransitionAsync(
                journal,
                journal with
                {
                    Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress,
                    ForwardAttempts = attempts,
                    LastObservedSatisfiedGoalCount = decision.SatisfiedMovingGoalCount,
                    LastFailedDecisionIndex = null,
                    ForwardFailureType = null,
                    ForwardFailureMessage = null,
                    RecoveryFailureType = null,
                    RecoveryFailureMessage = null,
                });
            if (checkpoint is null)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FromJournal(
                    DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                    writerStarted: true,
                    journal,
                    "ForwardCheckpointPersistFailed",
                    $"Dynamic decision #{move.DecisionIndex} 已精确完成，但 checkpoint 持久化失败。已停止新的 Writer。 ");
            }
            journal = checkpoint;
        }
    }

    private async Task<DynamicFinalVerification> VerifyFinalStateAsync(
        DesktopWorkspaceApplyConfirmation confirmation,
        int decisionIndex,
        CancellationToken cancellationToken)
    {
        var fresh = await CaptureFreshFactsAsync(cancellationToken);
        if (!fresh.Succeeded)
            return DynamicFinalVerification.Blocked(fresh.ErrorMessage!);

        var decision = DesktopWorkspaceDynamicNextStepPlanner.Decide(
            confirmation.FrozenPreview,
            fresh.Geometry!,
            fresh.Facts!,
            decisionIndex);
        return decision.Status switch
        {
            DesktopWorkspaceDynamicNextStepStatus.Complete => DynamicFinalVerification.Complete(),
            DesktopWorkspaceDynamicNextStepStatus.MoveReady => DynamicFinalVerification.MoveReady(),
            _ => DynamicFinalVerification.Blocked(DescribePlanningIssues(decision)),
        };
    }

    private async Task<DesktopWorkspaceDynamicExecutionRunResult> PersistNeedsAttentionResultAsync(
        DesktopWorkspaceDynamicExecutionJournal journal,
        IReadOnlyList<DesktopWorkspaceDynamicExecutionAttemptEvidence> attempts,
        int? lastFailedDecisionIndex,
        string errorType,
        string errorMessage)
    {
        var terminal = await TryTransitionAsync(
            journal,
            journal with
            {
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention,
                ForwardAttempts = attempts,
                LastFailedDecisionIndex = lastFailedDecisionIndex,
                ForwardFailureType = errorType,
                ForwardFailureMessage = errorMessage,
                RecoveryFailureType = null,
                RecoveryFailureMessage = null,
                FinalVerifiedAtUtc = null,
            });
        if (terminal is not null)
            journal = terminal;

        return DesktopWorkspaceDynamicExecutionRunResult.FromJournal(
            DesktopWorkspaceExecutionRunStatus.NeedsAttention,
            writerStarted: true,
            journal,
            errorType,
            errorMessage);
    }

    private async Task<DesktopWorkspaceDynamicExecutionRunResult> ContainUnexpectedExecutionExceptionAsync(
        DesktopWorkspaceDynamicExecutionJournal? startingJournal,
        Guid? safetySnapshotId,
        bool writerStarted,
        Exception exception)
    {
        if (startingJournal is null)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                exception.GetType().Name,
                $"Dynamic execution preparation 失败：{exception.Message}",
                safetySnapshotId);
        }

        var journal = startingJournal;
        try
        {
            var latest = await _dynamicJournalStore.GetAsync(startingJournal.SessionId, CancellationToken.None);
            if (latest is not null)
                journal = latest;
        }
        catch
        {
            // 当前内存 journal 仍是最保守的持久化锚点。
        }

        if (!writerStarted)
        {
            if (!journal.IsResolved)
            {
                var restored = await TryTransitionAsync(
                    journal,
                    journal with { Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored });
                if (restored is not null)
                    journal = restored;
            }

            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "UnhandledDynamicExecutionExceptionBeforeWrite",
                $"第一次 Writer 前发生未处理异常：{exception.GetType().Name}: {exception.Message}",
                journal.SafetySnapshotId,
                journal);
        }

        if (journal.Lifecycle != DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention && !journal.IsResolved)
        {
            var terminal = await TryTransitionAsync(
                journal,
                journal with
                {
                    Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention,
                    LastFailedDecisionIndex = journal.ForwardAttempts.Count,
                    ForwardFailureType = "UnhandledDynamicExecutionException",
                    ForwardFailureMessage = $"{exception.GetType().Name}: {exception.Message}",
                });
            if (terminal is not null)
                journal = terminal;
        }

        return DesktopWorkspaceDynamicExecutionRunResult.FromJournal(
            journal.Lifecycle == DesktopWorkspaceExecutionJournalLifecycle.Committed
                ? DesktopWorkspaceExecutionRunStatus.Committed
                : DesktopWorkspaceExecutionRunStatus.NeedsAttention,
            writerStarted: true,
            journal,
            "UnhandledDynamicExecutionException",
            $"Writer boundary 之后发生未处理异常：{exception.GetType().Name}: {exception.Message}。已停止新的 Writer。 ");
    }

    private async Task<DesktopWorkspaceDynamicExecutionJournal?> TryTransitionAsync(
        DesktopWorkspaceDynamicExecutionJournal current,
        DesktopWorkspaceDynamicExecutionJournal requested)
    {
        try
        {
            if (requested.SessionId != current.SessionId || requested.Revision != current.Revision)
                return null;
            requested.Validate();
            var updated = await _dynamicJournalStore.UpdateAsync(requested, CancellationToken.None);
            var readBack = await _dynamicJournalStore.GetAsync(updated.SessionId, CancellationToken.None);
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

    private async Task<FreshFactsCapture> CaptureFreshFactsAsync(CancellationToken cancellationToken)
    {
        DesktopObservationCaptureResult capture;
        try
        {
            capture = await _observationSource.CaptureAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return FreshFactsCapture.Failed(
                "FreshObservationException",
                $"fresh DesktopObservation 抛出异常：{ex.GetType().Name}: {ex.Message}");
        }

        if (!capture.IsCaptured)
        {
            return FreshFactsCapture.Failed(
                "FreshObservationUnavailable",
                string.Join("；", capture.Issues.Select(static issue => issue.Message)));
        }

        var observation = capture.Observation!;
        var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);
        if (!geometryResult.Succeeded)
        {
            return FreshFactsCapture.Failed(
                "FreshGeometryUnavailable",
                string.Join("；", geometryResult.Issues.Select(static issue => issue.Message)));
        }

        try
        {
            var facts = DesktopWorkspaceLocalItemFactProjector.Project(
                observation,
                geometryResult.Geometry!);
            return FreshFactsCapture.Success(
                observation,
                geometryResult.Geometry!,
                facts);
        }
        catch (Exception ex)
        {
            return FreshFactsCapture.Failed(
                "FreshLocalRealityUnavailable",
                $"无法建立 fresh Workspace local reality：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static DesktopWorkspaceDynamicExecutionAttemptEvidence ClassifyMoveResult(
        int attemptIndex,
        Guid freshObservationId,
        DesktopWorkspaceDynamicMove move,
        int satisfiedGoalCountBeforeDecision,
        DesktopIconMoveResult result)
    {
        var stableIdMatches = string.Equals(result.StableId, move.StableId, StringComparison.Ordinal);
        var previousMatches = result.PreviousPosition == move.FromPixelPosition;
        var requestedMatches = result.RequestedPosition == move.ToPixelPosition;

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

        return new DesktopWorkspaceDynamicExecutionAttemptEvidence(
            attemptIndex,
            move.DecisionIndex,
            DesktopWorkspaceDynamicExecutionAttemptPhase.Forward,
            freshObservationId,
            move.StableId,
            move.Kind,
            move.FromDisplayRef,
            move.FromPixelPosition,
            move.ToDisplayRef,
            move.ToPixelPosition,
            move.ToSlotId,
            move.FrozenTargetDisplayRef,
            move.FrozenTargetPixelPosition,
            move.FrozenTargetSlotId,
            satisfiedGoalCountBeforeDecision,
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

    private static DesktopWorkspaceDynamicExecutionAttemptEvidence CreateWriterExceptionEvidence(
        int attemptIndex,
        Guid freshObservationId,
        DesktopWorkspaceDynamicMove move,
        int satisfiedGoalCountBeforeDecision,
        Exception exception)
        => new(
            attemptIndex,
            move.DecisionIndex,
            DesktopWorkspaceDynamicExecutionAttemptPhase.Forward,
            freshObservationId,
            move.StableId,
            move.Kind,
            move.FromDisplayRef,
            move.FromPixelPosition,
            move.ToDisplayRef,
            move.ToPixelPosition,
            move.ToSlotId,
            move.FrozenTargetDisplayRef,
            move.FrozenTargetPixelPosition,
            move.FrozenTargetSlotId,
            satisfiedGoalCountBeforeDecision,
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

    private static (string ErrorType, string ErrorMessage) DescribeInexactEvidence(
        DesktopWorkspaceDynamicMove move,
        DesktopWorkspaceDynamicExecutionAttemptEvidence evidence,
        DesktopIconMoveResult result)
        => evidence.Outcome switch
        {
            DesktopWorkspaceExecutionAttemptOutcome.StableIdMismatch => (
                "StableIdMismatch",
                $"Dynamic decision #{move.DecisionIndex} Writer 报告 StableId \"{result.StableId}\"，期望 \"{move.StableId}\"。 "),
            DesktopWorkspaceExecutionAttemptOutcome.PreviousPositionMismatch => (
                "PreviousPositionMismatch",
                $"Dynamic decision #{move.DecisionIndex} Writer Previous/Requested 与本次 fresh decision 不一致。" +
                $" fresh From=({move.FromPixelPosition.X},{move.FromPixelPosition.Y})，" +
                $"Writer Previous=({result.PreviousPosition.X},{result.PreviousPosition.Y})。 "),
            DesktopWorkspaceExecutionAttemptOutcome.InexactKnown => (
                "InexactKnownPosition",
                $"Dynamic decision #{move.DecisionIndex} 实际落点 ({result.ActualPosition.X},{result.ActualPosition.Y}) " +
                $"不是请求位置 ({move.ToPixelPosition.X},{move.ToPixelPosition.Y})。 "),
            _ => (
                "UnexpectedWriterEvidence",
                $"Dynamic decision #{move.DecisionIndex} 返回非 ExactSuccess 证据 {evidence.Outcome}。 "),
        };

    private static string DescribePlanningIssues(DesktopWorkspaceDynamicNextStepResult decision)
        => string.Join("；", decision.Issues.Select(static issue => $"{issue.Code}: {issue.Message}"));

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
            return "Safety Snapshot 回读 metadata 与刚保存的快照不一致。 ";

        for (var index = 0; index < expected.Icons.Count; index++)
        {
            if (expected.Icons[index] != actual.Icons[index])
                return $"Safety Snapshot 回读第 {index} 项与刚保存的快照不一致。 ";
        }
        return null;
    }

    private static string? VerifyPreparedJournalReadBack(
        DesktopWorkspaceDynamicExecutionJournal expected,
        DesktopWorkspaceDynamicExecutionJournal? actual)
    {
        if (actual is null)
            return "Prepared Dynamic Journal 保存后回读返回 null。 ";
        if (actual.SessionId != expected.SessionId ||
            actual.SafetySnapshotId != expected.SafetySnapshotId ||
            actual.BlueprintId != expected.BlueprintId ||
            actual.BlueprintRevision != expected.BlueprintRevision ||
            actual.Lifecycle != DesktopWorkspaceExecutionJournalLifecycle.Prepared ||
            actual.Revision != expected.Revision ||
            !string.Equals(actual.CertificateFingerprint, expected.CertificateFingerprint, StringComparison.Ordinal) ||
            actual.Authority.ConfirmationId != expected.Authority.ConfirmationId ||
            actual.Authority.PreviewId != expected.Authority.PreviewId ||
            !string.Equals(
                actual.Authority.PreviewFingerprint,
                expected.Authority.PreviewFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                actual.Authority.ExactPlanFingerprint,
                expected.Authority.ExactPlanFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                actual.Authority.GeometryFingerprint,
                expected.Authority.GeometryFingerprint,
                StringComparison.Ordinal) ||
            actual.Authority.ItemGoals.Count != expected.Authority.ItemGoals.Count)
            return "Prepared Dynamic Journal 回读 identity / authority 与刚保存的 journal 不一致。 ";
        return null;
    }

    private sealed record FreshFactsCapture(
        bool Succeeded,
        DesktopObservation? Observation,
        ExplorerWorkspaceGeometry? Geometry,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact>? Facts,
        string? ErrorType,
        string? ErrorMessage)
    {
        internal static FreshFactsCapture Success(
            DesktopObservation observation,
            ExplorerWorkspaceGeometry geometry,
            IReadOnlyList<DesktopWorkspaceCompilationItemFact> facts)
            => new(true, observation, geometry, facts, null, null);

        internal static FreshFactsCapture Failed(string errorType, string errorMessage)
            => new(false, null, null, null, errorType, errorMessage);
    }

    private enum DynamicFinalVerificationStatus
    {
        Complete = 0,
        MoveReady = 1,
        Blocked = 2,
    }

    private sealed record DynamicFinalVerification(
        DynamicFinalVerificationStatus Status,
        string? Message)
    {
        internal static DynamicFinalVerification Complete()
            => new(DynamicFinalVerificationStatus.Complete, null);
        internal static DynamicFinalVerification MoveReady()
            => new(DynamicFinalVerificationStatus.MoveReady, null);
        internal static DynamicFinalVerification Blocked(string message)
            => new(DynamicFinalVerificationStatus.Blocked, message);
    }
}
