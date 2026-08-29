using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
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
        IEnumerable<string>? verificationIssues)
    {
        Status = status;
        WriterStarted = writerStarted;
        SafetySnapshotId = safetySnapshotId;
        SessionId = sessionId;
        Journal = journal;
        ErrorType = errorType;
        ErrorMessage = errorMessage;
        _verificationIssues = Array.AsReadOnly((verificationIssues ?? Array.Empty<string>()).ToArray());
    }

    public static DesktopWorkspaceDynamicExecutionRunResult FailedBeforeWrite(
        string errorType,
        string errorMessage,
        Guid? safetySnapshotId = null,
        DesktopWorkspaceDynamicExecutionJournal? journal = null,
        IEnumerable<string>? verificationIssues = null)
        => new(
            DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite,
            false,
            safetySnapshotId,
            journal?.SessionId,
            journal,
            errorType,
            errorMessage,
            verificationIssues);

    public static DesktopWorkspaceDynamicExecutionRunResult FromJournal(
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
/// 新的 Workspace dynamic execution coordinator。
/// 第一次 Writer 前仍使用严格 Frozen Preview FreshCheck；Writer boundary 之后，
/// 每一刀都 fresh capture，再由 DynamicNextStepPlanner 只决定一个 move。
/// Frozen final targets 永远来自用户确认过的 Preview，From 则始终以当前 Windows 事实为准。
/// </summary>
public sealed class DesktopWorkspaceDynamicExecutionCoordinator
{
    private readonly IDesktopObservationSource _observationSource;
    private readonly IDesktopIconPositionWriter _positionWriter;
    private readonly StaThreadExecutor _staExecutor;
    private readonly ISafetySnapshotFactory _snapshotFactory;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;
    private readonly IDesktopWorkspaceExecutionJournalStore _staticJournalStore;
    private readonly IDesktopWorkspaceDynamicExecutionJournalStore _dynamicJournalStore;
    private readonly TimeProvider _timeProvider;

    public DesktopWorkspaceDynamicExecutionCoordinator(
        IDesktopObservationSource observationSource,
        IDesktopIconPositionWriter positionWriter,
        StaThreadExecutor staExecutor,
        ISafetySnapshotFactory snapshotFactory,
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
        cancellationToken.ThrowIfCancellationRequested();

        await _staticJournalStore.InitializeAsync(cancellationToken);
        var unresolvedStatic = await _staticJournalStore.ListUnresolvedAsync(cancellationToken);
        if (unresolvedStatic.Count != 0)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "UnresolvedWorkspaceExecution",
                $"检测到未解决 V9 Workspace execution journal {unresolvedStatic[0].SessionId:D}，Dynamic Writer 未启动。");
        }

        await _dynamicJournalStore.InitializeAsync(cancellationToken);
        var unresolvedDynamic = await _dynamicJournalStore.ListUnresolvedAsync(cancellationToken);
        if (unresolvedDynamic.Count != 0)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "UnresolvedDynamicWorkspaceExecution",
                $"检测到未解决 Dynamic Workspace execution journal {unresolvedDynamic[0].SessionId:D}，新的 Writer 未启动。");
        }

        DesktopObservationCaptureResult initialCapture;
        try
        {
            initialCapture = await _observationSource.CaptureAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "InitialObservationFailed",
                $"Fresh Observation 异常：{ex.GetType().Name}: {ex.Message}");
        }

        if (!initialCapture.IsCaptured)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "InitialObservationIncomplete",
                "无法取得完整 fresh DesktopObservation：" +
                string.Join("；", initialCapture.Issues.Select(static issue => issue.Message)));
        }

        var initialObservation = initialCapture.Observation!;
        var initialGeometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(initialObservation);
        if (!initialGeometryResult.Succeeded)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "InitialGeometryFailed",
                string.Join("；", initialGeometryResult.Issues.Select(static issue => issue.Message)));
        }

        IReadOnlyList<DesktopWorkspaceCompilationItemFact> initialFacts;
        try
        {
            initialFacts = DesktopWorkspaceLocalItemFactProjector.Project(
                initialObservation,
                initialGeometryResult.Geometry!);
        }
        catch (Exception ex)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "InitialRealityProjectionFailed",
                $"无法建立 local reality：{ex.GetType().Name}: {ex.Message}");
        }

        var freshCheck = DesktopWorkspaceApplyFreshCheck.Evaluate(
            confirmation,
            initialGeometryResult.Geometry!,
            initialFacts);
        if (!freshCheck.IsReady)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "WorkspaceFreshCheckRejected",
                string.Join("；", freshCheck.Issues));
        }

        var authority = DesktopWorkspaceDynamicExecutionAuthoritySnapshot.FromConfirmation(confirmation);
        if (authority.MoveGoalCount == 0)
        {
            return new DesktopWorkspaceDynamicExecutionRunResult(
                DesktopWorkspaceExecutionRunStatus.NoOp,
                false,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        DesktopLayoutSnapshot safetySnapshot;
        try
        {
            safetySnapshot = _snapshotFactory.Create(initialObservation);
            await _snapshotStore.InitializeAsync(cancellationToken);
            await _snapshotStore.SaveAsync(safetySnapshot, cancellationToken);
            var snapshotReadBack = await _snapshotStore.GetAsync(safetySnapshot.SnapshotId, cancellationToken);
            var snapshotIssue = VerifySnapshotReadBack(safetySnapshot, snapshotReadBack);
            if (snapshotIssue is not null)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "SafetySnapshotReadBackFailed",
                    snapshotIssue,
                    safetySnapshot.SnapshotId);
            }
        }
        catch (Exception ex)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "SafetySnapshotPersistFailed",
                $"Safety Snapshot 保存/回读失败：{ex.GetType().Name}: {ex.Message}");
        }

        var prepared = DesktopWorkspaceDynamicExecutionJournal.CreatePrepared(
            safetySnapshot.SnapshotId,
            confirmation,
            authority,
            _timeProvider);
        try
        {
            await _dynamicJournalStore.SaveAsync(prepared, cancellationToken);
            var preparedReadBack = await _dynamicJournalStore.GetAsync(prepared.SessionId, cancellationToken);
            if (preparedReadBack is null || preparedReadBack.Revision != prepared.Revision ||
                preparedReadBack.Lifecycle != DesktopWorkspaceExecutionJournalLifecycle.Prepared ||
                preparedReadBack.Authority.PreviewId != authority.PreviewId ||
                preparedReadBack.Authority.ConfirmationId != authority.ConfirmationId)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                    "DynamicPreparedJournalReadBackFailed",
                    "Dynamic Prepared journal 保存后回读 identity 不一致。",
                    safetySnapshot.SnapshotId,
                    prepared);
            }
            prepared = preparedReadBack;
        }
        catch (Exception ex)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "DynamicPreparedJournalPersistFailed",
                $"Dynamic Prepared journal 保存/回读失败：{ex.GetType().Name}: {ex.Message}",
                safetySnapshot.SnapshotId);
        }

        var journal = await TryTransitionAsync(
            prepared,
            prepared with
            {
                Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress,
            });
        if (journal is null)
        {
            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "DynamicForwardStartCheckpointFailed",
                "第一次 Writer 前无法持久化 ForwardInProgress checkpoint。",
                safetySnapshot.SnapshotId,
                prepared);
        }

        var writerStarted = false;
        try
        {
            return await ExecuteForwardLoopAsync(
                confirmation,
                journal,
                writerStarted,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            return await ContainUnexpectedExecutionExceptionAsync(
                journal,
                writerStarted,
                ex);
        }
    }

    private async Task<DesktopWorkspaceDynamicExecutionRunResult> ExecuteForwardLoopAsync(
        DesktopWorkspaceApplyConfirmation confirmation,
        DesktopWorkspaceDynamicExecutionJournal startingJournal,
        bool writerStarted,
        CancellationToken cancellationToken)
    {
        var journal = startingJournal;
        var attempts = journal.ForwardAttempts.ToList();
        var maxDecisions = Math.Max(32, journal.Authority.MoveGoalCount * 4);

        for (var decisionIndex = attempts.Count; decisionIndex < maxDecisions; decisionIndex++)
        {
            var capture = await _observationSource.CaptureAsync(cancellationToken);
            if (!capture.IsCaptured)
            {
                return await FailAfterBoundaryAsync(
                    journal,
                    writerStarted,
                    attempts,
                    decisionIndex,
                    "DynamicObservationIncomplete",
                    "Dynamic decision 前无法取得完整 DesktopObservation：" +
                    string.Join("；", capture.Issues.Select(static issue => issue.Message)));
            }

            var observation = capture.Observation!;
            var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(observation);
            if (!geometryResult.Succeeded)
            {
                return await FailAfterBoundaryAsync(
                    journal,
                    writerStarted,
                    attempts,
                    decisionIndex,
                    "DynamicGeometryFailed",
                    string.Join("；", geometryResult.Issues.Select(static issue => issue.Message)));
            }

            IReadOnlyList<DesktopWorkspaceCompilationItemFact> facts;
            try
            {
                facts = DesktopWorkspaceLocalItemFactProjector.Project(observation, geometryResult.Geometry!);
            }
            catch (Exception ex)
            {
                return await FailAfterBoundaryAsync(
                    journal,
                    writerStarted,
                    attempts,
                    decisionIndex,
                    "DynamicRealityProjectionFailed",
                    $"Dynamic local reality 失败：{ex.GetType().Name}: {ex.Message}");
            }

            var decision = DesktopWorkspaceDynamicNextStepPlanner.Decide(
                confirmation.FrozenPreview,
                geometryResult.Geometry!,
                facts,
                decisionIndex);

            if (decision.Status == DesktopWorkspaceDynamicNextStepStatus.Blocked)
            {
                return await FailAfterBoundaryAsync(
                    journal,
                    writerStarted,
                    attempts,
                    decisionIndex,
                    "DynamicPlannerBlocked",
                    string.Join("；", decision.Issues.Select(static issue => issue.Message)),
                    decision.SatisfiedMovingGoalCount);
            }

            if (decision.IsComplete)
            {
                var verificationIssues = await VerifyCompleteAsync(
                    confirmation,
                    cancellationToken);
                if (verificationIssues.Count != 0)
                {
                    return await FailAfterBoundaryAsync(
                        journal,
                        writerStarted,
                        attempts,
                        decisionIndex,
                        "DynamicFinalVerificationFailed",
                        string.Join("；", verificationIssues),
                        decision.SatisfiedMovingGoalCount,
                        verificationIssues);
                }

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
                        "DynamicCommitCheckpointFailed",
                        "最终 fresh verification 已通过，但 Committed checkpoint 持久化失败。",
                        verificationIssues);
                }

                return DesktopWorkspaceDynamicExecutionRunResult.FromJournal(
                    DesktopWorkspaceExecutionRunStatus.Committed,
                    writerStarted,
                    committed);
            }

            var move = decision.Move!;
            DesktopIconMoveResult moveResult;
            try
            {
                moveResult = await _staExecutor.RunAsync(() =>
                {
                    writerStarted = true;
                    return _positionWriter.MoveDesktopIcon(
                        move.StableId,
                        move.ToPixelPosition);
                });
            }
            catch (Exception ex)
            {
                var evidence = CreateWriterExceptionEvidence(
                    attempts.Count,
                    observation.ObservationId,
                    move,
                    decision.SatisfiedMovingGoalCount,
                    ex);
                attempts.Add(evidence);
                return await FailAfterBoundaryAsync(
                    journal,
                    writerStarted,
                    attempts,
                    decisionIndex,
                    "DynamicWriterException",
                    $"Decision #{decisionIndex} Writer 异常：{ex.GetType().Name}: {ex.Message}",
                    decision.SatisfiedMovingGoalCount);
            }

            var classified = ClassifyMoveResult(
                attempts.Count,
                observation.ObservationId,
                move,
                decision.SatisfiedMovingGoalCount,
                moveResult);
            attempts.Add(classified);

            if (classified.Outcome != DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess)
            {
                var (errorType, errorMessage) = DescribeInexactEvidence(
                    decisionIndex,
                    classified,
                    moveResult);
                return await FailAfterBoundaryAsync(
                    journal,
                    writerStarted,
                    attempts,
                    decisionIndex,
                    errorType,
                    errorMessage,
                    decision.SatisfiedMovingGoalCount);
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
                });
            if (checkpoint is null)
            {
                return DesktopWorkspaceDynamicExecutionRunResult.FromJournal(
                    DesktopWorkspaceExecutionRunStatus.NeedsAttention,
                    true,
                    journal,
                    "DynamicForwardCheckpointFailed",
                    $"Decision #{decisionIndex} 已 ExactSuccess，但 checkpoint 持久化失败。已停止新的 Writer。");
            }
            journal = checkpoint;
        }

        return await FailAfterBoundaryAsync(
            journal,
            writerStarted,
            attempts,
            attempts.Count,
            "DynamicDecisionBudgetExceeded",
            $"Dynamic execution 超过 {maxDecisions} 次有界决策仍未收敛，已停止新的 Writer。",
            journal.LastObservedSatisfiedGoalCount);
    }

    private async Task<DesktopWorkspaceDynamicExecutionRunResult> FailAfterBoundaryAsync(
        DesktopWorkspaceDynamicExecutionJournal current,
        bool writerStarted,
        IReadOnlyList<DesktopWorkspaceDynamicExecutionAttemptEvidence> attempts,
        int failedDecisionIndex,
        string errorType,
        string errorMessage,
        int? satisfiedGoalCount = null,
        IEnumerable<string>? verificationIssues = null)
    {
        var requested = current with
        {
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention,
            ForwardAttempts = attempts,
            LastObservedSatisfiedGoalCount = satisfiedGoalCount ?? current.LastObservedSatisfiedGoalCount,
            LastFailedDecisionIndex = failedDecisionIndex,
            ForwardFailureType = errorType,
            ForwardFailureMessage = errorMessage,
            RecoveryFailureType = null,
            RecoveryFailureMessage = null,
            FinalVerifiedAtUtc = null,
        };
        var terminal = await TryTransitionAsync(current, requested);
        if (terminal is not null)
            current = terminal;

        return DesktopWorkspaceDynamicExecutionRunResult.FromJournal(
            DesktopWorkspaceExecutionRunStatus.NeedsAttention,
            writerStarted,
            current,
            errorType,
            errorMessage,
            verificationIssues);
    }

    private async Task<DesktopWorkspaceDynamicExecutionRunResult> ContainUnexpectedExecutionExceptionAsync(
        DesktopWorkspaceDynamicExecutionJournal startingJournal,
        bool writerStarted,
        Exception exception)
    {
        var journal = startingJournal;
        try
        {
            var latest = await _dynamicJournalStore.GetAsync(startingJournal.SessionId, CancellationToken.None);
            if (latest is not null)
                journal = latest;
        }
        catch
        {
        }

        if (!writerStarted)
        {
            var restored = await TryTransitionAsync(
                journal,
                journal with
                {
                    Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Restored,
                    ForwardFailureType = null,
                    ForwardFailureMessage = null,
                    RecoveryFailureType = null,
                    RecoveryFailureMessage = null,
                    FinalVerifiedAtUtc = null,
                });
            if (restored is not null)
                journal = restored;

            return DesktopWorkspaceDynamicExecutionRunResult.FailedBeforeWrite(
                "UnhandledDynamicExecutionExceptionBeforeWrite",
                $"进入 dynamic execution boundary 后、第一次 Writer 前发生异常：{exception.GetType().Name}: {exception.Message}",
                journal.SafetySnapshotId,
                journal);
        }

        if (!journal.IsResolved && journal.Lifecycle != DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention)
        {
            var terminal = await TryTransitionAsync(
                journal,
                journal with
                {
                    Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention,
                    ForwardFailureType = "UnhandledDynamicExecutionException",
                    ForwardFailureMessage = $"{exception.GetType().Name}: {exception.Message}",
                    FinalVerifiedAtUtc = null,
                });
            if (terminal is not null)
                journal = terminal;
        }

        return DesktopWorkspaceDynamicExecutionRunResult.FromJournal(
            DesktopWorkspaceExecutionRunStatus.NeedsAttention,
            true,
            journal,
            "UnhandledDynamicExecutionException",
            $"Writer boundary 后发生未处理异常：{exception.GetType().Name}: {exception.Message}。已停止新的 Writer。");
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

    private async Task<IReadOnlyList<string>> VerifyCompleteAsync(
        DesktopWorkspaceApplyConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        var capture = await _observationSource.CaptureAsync(cancellationToken);
        if (!capture.IsCaptured)
        {
            return Array.AsReadOnly(new[]
            {
                "Dynamic final verification 无法取得完整 fresh DesktopObservation：" +
                string.Join("；", capture.Issues.Select(static issue => issue.Message)),
            });
        }

        var geometryResult = DesktopExplorerWorkspaceGeometryAdapter.Build(capture.Observation!);
        if (!geometryResult.Succeeded)
        {
            return Array.AsReadOnly(new[]
            {
                "Dynamic final verification 无法建立 fresh Explorer geometry：" +
                string.Join("；", geometryResult.Issues.Select(static issue => issue.Message)),
            });
        }

        IReadOnlyList<DesktopWorkspaceCompilationItemFact> facts;
        try
        {
            facts = DesktopWorkspaceLocalItemFactProjector.Project(
                capture.Observation!,
                geometryResult.Geometry!);
        }
        catch (Exception ex)
        {
            return Array.AsReadOnly(new[]
            {
                $"Dynamic final verification 无法建立 local reality：{ex.GetType().Name}: {ex.Message}",
            });
        }

        var decision = DesktopWorkspaceDynamicNextStepPlanner.Decide(
            confirmation.FrozenPreview,
            geometryResult.Geometry!,
            facts,
            decisionIndex: 0);
        if (decision.IsComplete)
            return Array.AsReadOnly(Array.Empty<string>());

        if (decision.Status == DesktopWorkspaceDynamicNextStepStatus.Blocked)
            return Array.AsReadOnly(decision.Issues.Select(static issue => issue.Message).ToArray());

        return Array.AsReadOnly(new[]
        {
            $"Dynamic final verification 仍需要 move：{decision.Move!.DisplayName} → " +
            $"({decision.Move.ToPixelPosition.X},{decision.Move.ToPixelPosition.Y})。",
        });
    }

    private static DesktopWorkspaceDynamicExecutionAttemptEvidence ClassifyMoveResult(
        int attemptIndex,
        Guid observationId,
        DesktopWorkspaceDynamicMove move,
        int satisfiedGoalCount,
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
            observationId,
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
            satisfiedGoalCount,
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
        Guid observationId,
        DesktopWorkspaceDynamicMove move,
        int satisfiedGoalCount,
        Exception exception)
        => new(
            attemptIndex,
            move.DecisionIndex,
            DesktopWorkspaceDynamicExecutionAttemptPhase.Forward,
            observationId,
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
            satisfiedGoalCount,
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
        int decisionIndex,
        DesktopWorkspaceDynamicExecutionAttemptEvidence evidence,
        DesktopIconMoveResult result)
        => evidence.Outcome switch
        {
            DesktopWorkspaceExecutionAttemptOutcome.StableIdMismatch => (
                "DynamicStableIdMismatch",
                $"Decision #{decisionIndex} Writer 报告 StableId \"{result.StableId}\"，期望 \"{evidence.StableId}\"。"),
            DesktopWorkspaceExecutionAttemptOutcome.PreviousPositionMismatch => (
                "DynamicPreviousPositionMismatch",
                $"Decision #{decisionIndex} Writer Previous/Requested 与本次 fresh decision identity 不一致。"),
            DesktopWorkspaceExecutionAttemptOutcome.InexactKnown => (
                "DynamicInexactKnownPosition",
                $"Decision #{decisionIndex} 实际落点 ({result.ActualPosition.X},{result.ActualPosition.Y}) " +
                $"不是请求位置 ({evidence.RequestedPixelPosition.X},{evidence.RequestedPixelPosition.Y})。"),
            _ => (
                "DynamicUnexpectedWriterEvidence",
                $"Decision #{decisionIndex} 返回非 ExactSuccess 证据 {evidence.Outcome}。"),
        };

    private static string? VerifySnapshotReadBack(
        DesktopLayoutSnapshot expected,
        DesktopLayoutSnapshot? actual)
    {
        if (actual is null)
            return "Safety Snapshot 保存后回读返回 null。";
        if (actual.SnapshotId != expected.SnapshotId ||
            actual.CreatedAtUtc != expected.CreatedAtUtc ||
            !string.Equals(actual.TargetMonitorDeviceName, expected.TargetMonitorDeviceName, StringComparison.Ordinal) ||
            actual.ConnectionMethod != expected.ConnectionMethod ||
            actual.FormatVersion != expected.FormatVersion ||
            actual.Icons.Count != expected.Icons.Count)
        {
            return "Safety Snapshot 回读 metadata 与刚保存的快照不一致。";
        }

        for (var index = 0; index < expected.Icons.Count; index++)
        {
            if (expected.Icons[index] != actual.Icons[index])
                return $"Safety Snapshot 回读第 {index} 项与刚保存的快照不一致。";
        }
        return null;
    }
}