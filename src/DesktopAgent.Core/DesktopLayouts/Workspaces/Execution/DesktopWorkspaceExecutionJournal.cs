using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;

public enum DesktopWorkspaceExecutionJournalLifecycle
{
    Prepared = 0,
    ForwardInProgress = 1,
    RollingBack = 2,
    Committed = 3,
    Restored = 4,
    NeedsAttention = 5,
}

public enum DesktopWorkspaceExecutionAttemptPhase
{
    Forward = 0,
    Rollback = 1,
}

public enum DesktopWorkspaceExecutionAttemptOutcome
{
    ExactSuccess = 0,
    InexactKnown = 1,
    StableIdMismatch = 2,
    PreviousPositionMismatch = 3,
    WriterException = 4,
}

/// <summary>
/// 跨重启恢复使用的不可变 Workspace execution plan 快照。
/// 它是已经获得 authority 的运行时 plan 的持久化证据，不提供重新规划或重新设计入口。
/// </summary>
public sealed record DesktopWorkspaceExecutionPlanSnapshot
{
    private readonly IReadOnlyList<DesktopWorkspaceExecutionStep> _steps;

    public Guid ConfirmationId { get; }
    public Guid PreviewId { get; }
    public Guid FreshObservationId { get; }
    public string PreviewFingerprint { get; }
    public string ExactPlanFingerprint { get; }
    public string ExecutionPlanFingerprint { get; }
    public IReadOnlyList<DesktopWorkspaceExecutionStep> Steps => _steps;
    public int MoveItemCount { get; }
    public bool UsesTemporaryStaging { get; }
    public bool IsNoOp => _steps.Count == 0;

    public DesktopWorkspaceExecutionPlanSnapshot(
        Guid confirmationId,
        Guid previewId,
        Guid freshObservationId,
        string previewFingerprint,
        string exactPlanFingerprint,
        string executionPlanFingerprint,
        IEnumerable<DesktopWorkspaceExecutionStep> steps,
        int moveItemCount,
        bool usesTemporaryStaging)
    {
        if (confirmationId == Guid.Empty)
            throw new ArgumentException("ConfirmationId 不能为 Guid.Empty。", nameof(confirmationId));
        if (previewId == Guid.Empty)
            throw new ArgumentException("PreviewId 不能为 Guid.Empty。", nameof(previewId));
        if (freshObservationId == Guid.Empty)
            throw new ArgumentException("FreshObservationId 不能为 Guid.Empty。", nameof(freshObservationId));
        if (string.IsNullOrWhiteSpace(previewFingerprint))
            throw new ArgumentException("PreviewFingerprint 不能为空。", nameof(previewFingerprint));
        if (string.IsNullOrWhiteSpace(exactPlanFingerprint))
            throw new ArgumentException("ExactPlanFingerprint 不能为空。", nameof(exactPlanFingerprint));
        if (string.IsNullOrWhiteSpace(executionPlanFingerprint))
            throw new ArgumentException("ExecutionPlanFingerprint 不能为空。", nameof(executionPlanFingerprint));
        ArgumentNullException.ThrowIfNull(steps);
        if (moveItemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(moveItemCount));

        var stepArray = steps.ToArray();
        if (stepArray.Any(static step => step is null))
            throw new ArgumentException("Steps 不能包含 null。", nameof(steps));
        for (var index = 0; index < stepArray.Length; index++)
        {
            if (stepArray[index].StepIndex != index)
                throw new ArgumentException("Steps 的 StepIndex 必须从 0 连续递增。", nameof(steps));
        }

        var calculatedUsesStaging = stepArray.Any(static step =>
            step.Kind == DesktopWorkspaceExecutionStepKind.MoveToTemporaryStaging);
        if (usesTemporaryStaging != calculatedUsesStaging)
            throw new ArgumentException("UsesTemporaryStaging 与 Steps 不一致。", nameof(usesTemporaryStaging));

        ConfirmationId = confirmationId;
        PreviewId = previewId;
        FreshObservationId = freshObservationId;
        PreviewFingerprint = previewFingerprint.Trim();
        ExactPlanFingerprint = exactPlanFingerprint.Trim();
        ExecutionPlanFingerprint = executionPlanFingerprint.Trim();
        _steps = Array.AsReadOnly(stepArray);
        MoveItemCount = moveItemCount;
        UsesTemporaryStaging = usesTemporaryStaging;
    }

    public static DesktopWorkspaceExecutionPlanSnapshot FromPlan(
        DesktopWorkspaceExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new DesktopWorkspaceExecutionPlanSnapshot(
            plan.ConfirmationId,
            plan.PreviewId,
            plan.FreshObservationId,
            plan.PreviewFingerprint,
            plan.ExactPlanFingerprint,
            plan.ExecutionPlanFingerprint,
            plan.Steps,
            plan.MoveItemCount,
            plan.UsesTemporaryStaging);
    }
}

/// <summary>
/// 单次 Workspace Writer 调用的持久化证据。
/// Attempt 只记录 Writer 报告与错误，不替代重启后的 fresh Windows facts。
/// </summary>
public sealed record DesktopWorkspaceExecutionAttemptEvidence
{
    public int AttemptIndex { get; }
    public int SourceStepIndex { get; }
    public DesktopWorkspaceExecutionAttemptPhase Phase { get; }
    public string ExpectedStableId { get; }
    public string ExpectedPreviousDisplayRef { get; }
    public PixelPoint ExpectedPreviousPixelPosition { get; }
    public string RequestedDisplayRef { get; }
    public PixelPoint RequestedPixelPosition { get; }
    public string? ReportedStableId { get; }
    public PixelPoint? ReportedPreviousPixelPosition { get; }
    public PixelPoint? ActualPixelPosition { get; }
    public bool HasTrustedActualPosition { get; }
    public bool IsExact { get; }
    public DesktopWorkspaceExecutionAttemptOutcome Outcome { get; }
    public ConnectionMethod? ConnectionMethod { get; }
    public string? ExceptionType { get; }
    public string? ErrorMessage { get; }
    public string? PrimaryPathExceptionType { get; }
    public string? PrimaryPathExceptionMessage { get; }

    public DesktopWorkspaceExecutionAttemptEvidence(
        int attemptIndex,
        int sourceStepIndex,
        DesktopWorkspaceExecutionAttemptPhase phase,
        string expectedStableId,
        string expectedPreviousDisplayRef,
        PixelPoint expectedPreviousPixelPosition,
        string requestedDisplayRef,
        PixelPoint requestedPixelPosition,
        string? reportedStableId,
        PixelPoint? reportedPreviousPixelPosition,
        PixelPoint? actualPixelPosition,
        bool hasTrustedActualPosition,
        bool isExact,
        DesktopWorkspaceExecutionAttemptOutcome outcome,
        ConnectionMethod? connectionMethod,
        string? exceptionType,
        string? errorMessage,
        string? primaryPathExceptionType,
        string? primaryPathExceptionMessage)
    {
        if (attemptIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(attemptIndex));
        if (sourceStepIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceStepIndex));
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (string.IsNullOrEmpty(expectedStableId))
            throw new ArgumentException("ExpectedStableId 不能为空。", nameof(expectedStableId));
        if (string.IsNullOrWhiteSpace(expectedPreviousDisplayRef))
            throw new ArgumentException("ExpectedPreviousDisplayRef 不能为空。", nameof(expectedPreviousDisplayRef));
        if (string.IsNullOrWhiteSpace(requestedDisplayRef))
            throw new ArgumentException("RequestedDisplayRef 不能为空。", nameof(requestedDisplayRef));
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (connectionMethod.HasValue && !Enum.IsDefined(connectionMethod.Value))
            throw new ArgumentOutOfRangeException(nameof(connectionMethod));
        if (isExact && outcome != DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess)
            throw new ArgumentException("IsExact=true 必须对应 ExactSuccess。", nameof(isExact));
        if (outcome == DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess && !isExact)
            throw new ArgumentException("ExactSuccess 必须 IsExact=true。", nameof(outcome));
        if (hasTrustedActualPosition != actualPixelPosition.HasValue)
            throw new ArgumentException("HasTrustedActualPosition 必须与 ActualPixelPosition 是否存在一致。", nameof(hasTrustedActualPosition));
        if (isExact && actualPixelPosition != requestedPixelPosition)
            throw new ArgumentException("ExactSuccess 的 ActualPixelPosition 必须等于 RequestedPixelPosition。", nameof(actualPixelPosition));

        AttemptIndex = attemptIndex;
        SourceStepIndex = sourceStepIndex;
        Phase = phase;
        ExpectedStableId = expectedStableId;
        ExpectedPreviousDisplayRef = expectedPreviousDisplayRef.Trim();
        ExpectedPreviousPixelPosition = expectedPreviousPixelPosition;
        RequestedDisplayRef = requestedDisplayRef.Trim();
        RequestedPixelPosition = requestedPixelPosition;
        ReportedStableId = reportedStableId;
        ReportedPreviousPixelPosition = reportedPreviousPixelPosition;
        ActualPixelPosition = actualPixelPosition;
        HasTrustedActualPosition = hasTrustedActualPosition;
        IsExact = isExact;
        Outcome = outcome;
        ConnectionMethod = connectionMethod;
        ExceptionType = exceptionType;
        ErrorMessage = errorMessage;
        PrimaryPathExceptionType = primaryPathExceptionType;
        PrimaryPathExceptionMessage = primaryPathExceptionMessage;
    }
}

/// <summary>
/// 多显示器 Workspace 的持久化 execution / recovery journal。
/// Forward 与 rollback 都引用同一份冻结 Steps；rollback 只按已完成 forward steps 倒序反演，
/// 不需要恢复时重新设计、重新 compile 或猜测过去发生了什么。
/// </summary>
public sealed record DesktopWorkspaceExecutionJournal
{
    private IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> _forwardAttempts =
        Array.Empty<DesktopWorkspaceExecutionAttemptEvidence>();
    private IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> _rollbackAttempts =
        Array.Empty<DesktopWorkspaceExecutionAttemptEvidence>();
    private IReadOnlyList<int> _completedForwardStepIndexes = Array.Empty<int>();
    private IReadOnlyList<int> _completedRollbackSourceStepIndexes = Array.Empty<int>();

    public Guid SessionId { get; init; }
    public Guid SafetySnapshotId { get; init; }
    public Guid BlueprintId { get; init; }
    public int BlueprintRevision { get; init; }
    public string CertificateFingerprint { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DesktopWorkspaceExecutionJournalLifecycle Lifecycle { get; init; }
    public DesktopWorkspaceExecutionPlanSnapshot ExecutionPlan { get; init; } = null!;
    public IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> ForwardAttempts
    {
        get => _forwardAttempts;
        init => _forwardAttempts = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    public IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> RollbackAttempts
    {
        get => _rollbackAttempts;
        init => _rollbackAttempts = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    public IReadOnlyList<int> CompletedForwardStepIndexes
    {
        get => _completedForwardStepIndexes;
        init => _completedForwardStepIndexes = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    public IReadOnlyList<int> CompletedRollbackSourceStepIndexes
    {
        get => _completedRollbackSourceStepIndexes;
        init => _completedRollbackSourceStepIndexes = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    public int? LastFailedStepIndex { get; init; }
    public string? ForwardFailureType { get; init; }
    public string? ForwardFailureMessage { get; init; }
    public string? RecoveryFailureType { get; init; }
    public string? RecoveryFailureMessage { get; init; }
    public int Revision { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public bool IsResolved =>
        Lifecycle is DesktopWorkspaceExecutionJournalLifecycle.Committed
            or DesktopWorkspaceExecutionJournalLifecycle.Restored;

    public static DesktopWorkspaceExecutionJournal CreatePrepared(
        Guid safetySnapshotId,
        DesktopWorkspaceApplyConfirmation confirmation,
        DesktopWorkspaceExecutionPlan plan,
        TimeProvider? timeProvider = null)
    {
        if (safetySnapshotId == Guid.Empty)
            throw new ArgumentException("SafetySnapshotId 不能为 Guid.Empty。", nameof(safetySnapshotId));
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(plan);

        var preview = confirmation.FrozenPreview;
        if (plan.ConfirmationId != confirmation.ConfirmationId ||
            plan.PreviewId != preview.PreviewId ||
            !string.Equals(plan.PreviewFingerprint, preview.PreviewFingerprint, StringComparison.Ordinal) ||
            !string.Equals(plan.ExactPlanFingerprint, preview.ExactPlan.PlanFingerprint, StringComparison.Ordinal))
        {
            throw new ArgumentException("ExecutionPlan 与用户确认的 Frozen Preview identity 不一致。", nameof(plan));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var result = new DesktopWorkspaceExecutionJournal
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = safetySnapshotId,
            BlueprintId = preview.Blueprint.BlueprintId,
            BlueprintRevision = preview.Blueprint.Revision,
            CertificateFingerprint = preview.RealizationCertificate.CertificateFingerprint,
            StartedAtUtc = now,
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Prepared,
            ExecutionPlan = DesktopWorkspaceExecutionPlanSnapshot.FromPlan(plan),
            ForwardAttempts = [],
            RollbackAttempts = [],
            CompletedForwardStepIndexes = [],
            CompletedRollbackSourceStepIndexes = [],
            Revision = 1,
            UpdatedAtUtc = now,
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        var errors = new List<string>();
        if (SessionId == Guid.Empty) errors.Add("SessionId 不能为 Guid.Empty。");
        if (SafetySnapshotId == Guid.Empty) errors.Add("SafetySnapshotId 不能为 Guid.Empty。");
        if (BlueprintId == Guid.Empty) errors.Add("BlueprintId 不能为 Guid.Empty。");
        if (BlueprintRevision <= 0) errors.Add("BlueprintRevision 必须 > 0。");
        if (string.IsNullOrWhiteSpace(CertificateFingerprint)) errors.Add("CertificateFingerprint 不能为空。");
        if (ExecutionPlan is null) errors.Add("ExecutionPlan 不能为 null。");
        if (!Enum.IsDefined(Lifecycle)) errors.Add("Lifecycle 无效。");
        if (Revision <= 0) errors.Add("Revision 必须 > 0。");
        if (StartedAtUtc.Offset != TimeSpan.Zero) errors.Add("StartedAtUtc 必须为 UTC。");
        if (UpdatedAtUtc.Offset != TimeSpan.Zero) errors.Add("UpdatedAtUtc 必须为 UTC。");
        if (UpdatedAtUtc < StartedAtUtc) errors.Add("UpdatedAtUtc 不能早于 StartedAtUtc。");

        if (ExecutionPlan is not null)
        {
            ValidateAttemptList(ForwardAttempts, DesktopWorkspaceExecutionAttemptPhase.Forward, ExecutionPlan, errors);
            ValidateAttemptList(RollbackAttempts, DesktopWorkspaceExecutionAttemptPhase.Rollback, ExecutionPlan, errors);

            var allAttempts = ForwardAttempts.Concat(RollbackAttempts)
                .OrderBy(static attempt => attempt.AttemptIndex)
                .ToArray();
            for (var index = 0; index < allAttempts.Length; index++)
            {
                if (allAttempts[index].AttemptIndex != index)
                    errors.Add($"AttemptIndex 不连续：位置 {index} 的值为 {allAttempts[index].AttemptIndex}。");
            }

            ValidateCompletedIndexes(
                ForwardAttempts,
                CompletedForwardStepIndexes,
                nameof(CompletedForwardStepIndexes),
                errors);
            ValidateCompletedIndexes(
                RollbackAttempts,
                CompletedRollbackSourceStepIndexes,
                nameof(CompletedRollbackSourceStepIndexes),
                errors);

            var completedForward = CompletedForwardStepIndexes.OrderBy(static index => index).ToArray();
            for (var index = 0; index < completedForward.Length; index++)
            {
                if (completedForward[index] != index)
                {
                    errors.Add("CompletedForwardStepIndexes 必须是从 0 开始的连续前缀。");
                    break;
                }
            }

            var completedRollback = CompletedRollbackSourceStepIndexes.OrderBy(static index => index).ToArray();
            if (completedRollback.Any(index => !completedForward.Contains(index)))
                errors.Add("Rollback 只能覆盖已经完成的 Forward step。");
            if (completedRollback.Length > 0)
            {
                var expectedSuffix = completedForward
                    .Skip(completedForward.Length - completedRollback.Length)
                    .ToArray();
                if (!expectedSuffix.SequenceEqual(completedRollback))
                    errors.Add("CompletedRollbackSourceStepIndexes 必须对应已完成 Forward steps 的连续后缀。");
            }

            if (LastFailedStepIndex is < 0 ||
                LastFailedStepIndex >= ExecutionPlan.Steps.Count)
                errors.Add("LastFailedStepIndex 超出 ExecutionPlan 范围。");

            if (Lifecycle == DesktopWorkspaceExecutionJournalLifecycle.Prepared &&
                (ForwardAttempts.Count > 0 || RollbackAttempts.Count > 0 ||
                 CompletedForwardStepIndexes.Count > 0 || CompletedRollbackSourceStepIndexes.Count > 0))
                errors.Add("Prepared journal 不得已有 Writer attempt 或 completed step。");

            if (Lifecycle == DesktopWorkspaceExecutionJournalLifecycle.ForwardInProgress &&
                RollbackAttempts.Count > 0)
                errors.Add("ForwardInProgress journal 不得已有 Rollback attempt。");

            if (Lifecycle == DesktopWorkspaceExecutionJournalLifecycle.Committed &&
                CompletedForwardStepIndexes.Count != ExecutionPlan.Steps.Count)
                errors.Add("Committed journal 必须完成全部 Forward steps。");

            if (Lifecycle == DesktopWorkspaceExecutionJournalLifecycle.Restored &&
                !completedForward.SequenceEqual(completedRollback))
                errors.Add("Restored journal 必须回滚全部已完成 Forward steps。");
        }

        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(" ", errors));
    }

    private static void ValidateAttemptList(
        IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> attempts,
        DesktopWorkspaceExecutionAttemptPhase expectedPhase,
        DesktopWorkspaceExecutionPlanSnapshot plan,
        List<string> errors)
    {
        if (attempts.Any(static attempt => attempt is null))
        {
            errors.Add($"{expectedPhase} Attempts 不能包含 null。");
            return;
        }

        foreach (var attempt in attempts)
        {
            if (attempt.Phase != expectedPhase)
                errors.Add($"Attempt #{attempt.AttemptIndex} Phase 与所在集合不匹配。");
            if (attempt.SourceStepIndex < 0 || attempt.SourceStepIndex >= plan.Steps.Count)
            {
                errors.Add($"Attempt #{attempt.AttemptIndex} SourceStepIndex 超出范围。");
                continue;
            }

            var step = plan.Steps[attempt.SourceStepIndex];
            if (!string.Equals(attempt.ExpectedStableId, step.StableId, StringComparison.Ordinal))
                errors.Add($"Attempt #{attempt.AttemptIndex} StableId 与 source step 不匹配。");

            var expectedPreviousDisplay = expectedPhase == DesktopWorkspaceExecutionAttemptPhase.Forward
                ? step.FromDisplayRef
                : step.ToDisplayRef;
            var expectedPreviousPixel = expectedPhase == DesktopWorkspaceExecutionAttemptPhase.Forward
                ? step.FromPixelPosition
                : step.ToPixelPosition;
            var requestedDisplay = expectedPhase == DesktopWorkspaceExecutionAttemptPhase.Forward
                ? step.ToDisplayRef
                : step.FromDisplayRef;
            var requestedPixel = expectedPhase == DesktopWorkspaceExecutionAttemptPhase.Forward
                ? step.ToPixelPosition
                : step.FromPixelPosition;

            if (!string.Equals(attempt.ExpectedPreviousDisplayRef, expectedPreviousDisplay, StringComparison.Ordinal) ||
                attempt.ExpectedPreviousPixelPosition != expectedPreviousPixel)
                errors.Add($"Attempt #{attempt.AttemptIndex} ExpectedPrevious 与 source step 不匹配。");
            if (!string.Equals(attempt.RequestedDisplayRef, requestedDisplay, StringComparison.Ordinal) ||
                attempt.RequestedPixelPosition != requestedPixel)
                errors.Add($"Attempt #{attempt.AttemptIndex} Requested target 与 source step 不匹配。");
        }
    }

    private static void ValidateCompletedIndexes(
        IReadOnlyList<DesktopWorkspaceExecutionAttemptEvidence> attempts,
        IReadOnlyList<int> completed,
        string fieldName,
        List<string> errors)
    {
        if (completed.Distinct().Count() != completed.Count)
            errors.Add($"{fieldName} 不能包含重复索引。");

        var expected = attempts
            .Where(static attempt => attempt.Outcome == DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess)
            .Select(static attempt => attempt.SourceStepIndex)
            .Distinct()
            .OrderBy(static index => index)
            .ToArray();
        var actual = completed.OrderBy(static index => index).ToArray();
        if (!expected.SequenceEqual(actual))
            errors.Add($"{fieldName} 与 ExactSuccess attempts 不匹配。");
    }
}

public interface IDesktopWorkspaceExecutionJournalStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DesktopWorkspaceExecutionJournal journal, CancellationToken cancellationToken = default);
    Task<DesktopWorkspaceExecutionJournal> UpdateAsync(
        DesktopWorkspaceExecutionJournal journal,
        CancellationToken cancellationToken = default);
    Task<DesktopWorkspaceExecutionJournal?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopWorkspaceExecutionJournal>> ListUnresolvedAsync(
        CancellationToken cancellationToken = default);
}
