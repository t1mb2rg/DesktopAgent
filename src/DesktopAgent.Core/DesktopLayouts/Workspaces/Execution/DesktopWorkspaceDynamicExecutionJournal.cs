using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;

public sealed record DesktopWorkspaceDynamicExecutionItemGoal
{
    public string StableId { get; }
    public string DisplayName { get; }
    public DesktopWorkspaceCompiledItemDisposition Disposition { get; }
    public bool IsMovable { get; }
    public bool IsMoveAuthorized { get; }
    public string FinalDisplayRef { get; }
    public PixelPoint FinalPixelPosition { get; }
    public string? FinalSlotId { get; }

    public DesktopWorkspaceDynamicExecutionItemGoal(
        string stableId,
        string displayName,
        DesktopWorkspaceCompiledItemDisposition disposition,
        bool isMovable,
        bool isMoveAuthorized,
        string finalDisplayRef,
        PixelPoint finalPixelPosition,
        string? finalSlotId)
    {
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("DisplayName 不能为空。", nameof(displayName));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition));
        if (string.IsNullOrWhiteSpace(finalDisplayRef))
            throw new ArgumentException("FinalDisplayRef 不能为空。", nameof(finalDisplayRef));
        if (isMoveAuthorized && !isMovable)
            throw new ArgumentException("不可移动项目不能获得 Workspace move authority。", nameof(isMoveAuthorized));
        if (disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
            string.IsNullOrWhiteSpace(finalSlotId))
            throw new ArgumentException("Place 项目的 Frozen final state 必须包含 SlotId。", nameof(finalSlotId));
        if (disposition != DesktopWorkspaceCompiledItemDisposition.Place && finalSlotId is not null)
            throw new ArgumentException("Keep / Exclude 的 Frozen final state 不应携带 SlotId。", nameof(finalSlotId));

        StableId = stableId;
        DisplayName = displayName.Trim();
        Disposition = disposition;
        IsMovable = isMovable;
        IsMoveAuthorized = isMoveAuthorized;
        FinalDisplayRef = finalDisplayRef.Trim();
        FinalPixelPosition = finalPixelPosition;
        FinalSlotId = finalSlotId?.Trim();
    }
}

/// <summary>
/// Dynamic Workspace execution 跨重启保存的不可变 authority 快照。
/// 它只冻结用户确认过的最终状态与执行 identity，不冻结整个执行期间的 From 路线。
/// </summary>
public sealed record DesktopWorkspaceDynamicExecutionAuthoritySnapshot
{
    private readonly IReadOnlyList<DesktopWorkspaceDynamicExecutionItemGoal> _itemGoals;

    public Guid ConfirmationId { get; }
    public Guid PreviewId { get; }
    public string PreviewFingerprint { get; }
    public string ExactPlanFingerprint { get; }
    public string GeometryFingerprint { get; }
    public IReadOnlyList<DesktopWorkspaceDynamicExecutionItemGoal> ItemGoals => _itemGoals;
    public int MoveGoalCount { get; }

    public DesktopWorkspaceDynamicExecutionAuthoritySnapshot(
        Guid confirmationId,
        Guid previewId,
        string previewFingerprint,
        string exactPlanFingerprint,
        string geometryFingerprint,
        IEnumerable<DesktopWorkspaceDynamicExecutionItemGoal> itemGoals)
    {
        if (confirmationId == Guid.Empty)
            throw new ArgumentException("ConfirmationId 不能为 Guid.Empty。", nameof(confirmationId));
        if (previewId == Guid.Empty)
            throw new ArgumentException("PreviewId 不能为 Guid.Empty。", nameof(previewId));
        if (string.IsNullOrWhiteSpace(previewFingerprint))
            throw new ArgumentException("PreviewFingerprint 不能为空。", nameof(previewFingerprint));
        if (string.IsNullOrWhiteSpace(exactPlanFingerprint))
            throw new ArgumentException("ExactPlanFingerprint 不能为空。", nameof(exactPlanFingerprint));
        if (string.IsNullOrWhiteSpace(geometryFingerprint))
            throw new ArgumentException("GeometryFingerprint 不能为空。", nameof(geometryFingerprint));
        ArgumentNullException.ThrowIfNull(itemGoals);

        var goals = itemGoals.ToArray();
        if (goals.Any(static goal => goal is null))
            throw new ArgumentException("ItemGoals 不能包含 null。", nameof(itemGoals));
        if (goals.Length == 0)
            throw new ArgumentException("ItemGoals 不能为空。", nameof(itemGoals));
        if (goals.Select(static goal => goal.StableId).Distinct(StringComparer.Ordinal).Count() != goals.Length)
            throw new ArgumentException("ItemGoals StableId 不能重复。", nameof(itemGoals));

        ConfirmationId = confirmationId;
        PreviewId = previewId;
        PreviewFingerprint = previewFingerprint.Trim();
        ExactPlanFingerprint = exactPlanFingerprint.Trim();
        GeometryFingerprint = geometryFingerprint.Trim();
        _itemGoals = Array.AsReadOnly(goals);
        MoveGoalCount = goals.Count(static goal => goal.IsMoveAuthorized);
    }

    public static DesktopWorkspaceDynamicExecutionAuthoritySnapshot FromConfirmation(
        DesktopWorkspaceApplyConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var preview = confirmation.FrozenPreview;
        var goals = preview.Items.Select(static item =>
        {
            if (item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place)
            {
                if (item.TargetDisplayRef is null ||
                    !item.TargetPixelPosition.HasValue ||
                    item.TargetSlotId is null)
                    throw new InvalidDataException($"Place 项目 {item.StableId} 缺少 Frozen final target。 ");

                return new DesktopWorkspaceDynamicExecutionItemGoal(
                    item.StableId,
                    item.DisplayName,
                    item.Disposition,
                    item.IsMovable,
                    item.IsPositionChange,
                    item.TargetDisplayRef,
                    item.TargetPixelPosition.Value,
                    item.TargetSlotId);
            }

            return new DesktopWorkspaceDynamicExecutionItemGoal(
                item.StableId,
                item.DisplayName,
                item.Disposition,
                item.IsMovable,
                isMoveAuthorized: false,
                item.SourceDisplayRef,
                item.SourcePixelPosition,
                finalSlotId: null);
        });

        return new DesktopWorkspaceDynamicExecutionAuthoritySnapshot(
            confirmation.ConfirmationId,
            preview.PreviewId,
            preview.PreviewFingerprint,
            preview.ExactPlan.PlanFingerprint,
            preview.ExactPlan.GeometryFingerprint,
            goals);
    }
}

public enum DesktopWorkspaceDynamicExecutionAttemptPhase
{
    Forward = 0,
    Recovery = 1,
}

/// <summary>
/// Dynamic execution 的一次真实 Writer 调用证据。Fresh From 属于本次 observation 事实，
/// FrozenTarget 属于用户确认 authority，两者故意分离。
/// </summary>
public sealed record DesktopWorkspaceDynamicExecutionAttemptEvidence
{
    public int AttemptIndex { get; }
    public int DecisionIndex { get; }
    public DesktopWorkspaceDynamicExecutionAttemptPhase Phase { get; }
    public Guid FreshObservationId { get; }
    public string StableId { get; }
    public DesktopWorkspaceDynamicMoveKind MoveKind { get; }
    public string FreshFromDisplayRef { get; }
    public PixelPoint FreshFromPixelPosition { get; }
    public string RequestedDisplayRef { get; }
    public PixelPoint RequestedPixelPosition { get; }
    public string RequestedSlotId { get; }
    public string FrozenTargetDisplayRef { get; }
    public PixelPoint FrozenTargetPixelPosition { get; }
    public string FrozenTargetSlotId { get; }
    public int SatisfiedGoalCountBeforeDecision { get; }
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

    public DesktopWorkspaceDynamicExecutionAttemptEvidence(
        int attemptIndex,
        int decisionIndex,
        DesktopWorkspaceDynamicExecutionAttemptPhase phase,
        Guid freshObservationId,
        string stableId,
        DesktopWorkspaceDynamicMoveKind moveKind,
        string freshFromDisplayRef,
        PixelPoint freshFromPixelPosition,
        string requestedDisplayRef,
        PixelPoint requestedPixelPosition,
        string requestedSlotId,
        string frozenTargetDisplayRef,
        PixelPoint frozenTargetPixelPosition,
        string frozenTargetSlotId,
        int satisfiedGoalCountBeforeDecision,
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
        if (decisionIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(decisionIndex));
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (freshObservationId == Guid.Empty)
            throw new ArgumentException("FreshObservationId 不能为 Guid.Empty。", nameof(freshObservationId));
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (!Enum.IsDefined(moveKind))
            throw new ArgumentOutOfRangeException(nameof(moveKind));
        if (string.IsNullOrWhiteSpace(freshFromDisplayRef))
            throw new ArgumentException("FreshFromDisplayRef 不能为空。", nameof(freshFromDisplayRef));
        if (string.IsNullOrWhiteSpace(requestedDisplayRef))
            throw new ArgumentException("RequestedDisplayRef 不能为空。", nameof(requestedDisplayRef));
        if (string.IsNullOrWhiteSpace(requestedSlotId))
            throw new ArgumentException("RequestedSlotId 不能为空。", nameof(requestedSlotId));
        if (string.IsNullOrWhiteSpace(frozenTargetDisplayRef))
            throw new ArgumentException("FrozenTargetDisplayRef 不能为空。", nameof(frozenTargetDisplayRef));
        if (string.IsNullOrWhiteSpace(frozenTargetSlotId))
            throw new ArgumentException("FrozenTargetSlotId 不能为空。", nameof(frozenTargetSlotId));
        if (satisfiedGoalCountBeforeDecision < 0)
            throw new ArgumentOutOfRangeException(nameof(satisfiedGoalCountBeforeDecision));
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (connectionMethod.HasValue && !Enum.IsDefined(connectionMethod.Value))
            throw new ArgumentOutOfRangeException(nameof(connectionMethod));
        if (hasTrustedActualPosition && !actualPixelPosition.HasValue)
            throw new ArgumentException("可信 ActualPosition 必须存在。", nameof(actualPixelPosition));
        if (isExact && (!hasTrustedActualPosition || actualPixelPosition != requestedPixelPosition ||
                       outcome != DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess))
            throw new ArgumentException("ExactSuccess 必须具有可信且精确等于 Requested 的 ActualPosition。", nameof(isExact));
        if (outcome == DesktopWorkspaceExecutionAttemptOutcome.ExactSuccess && !isExact)
            throw new ArgumentException("ExactSuccess 必须 IsExact=true。", nameof(outcome));

        AttemptIndex = attemptIndex;
        DecisionIndex = decisionIndex;
        Phase = phase;
        FreshObservationId = freshObservationId;
        StableId = stableId;
        MoveKind = moveKind;
        FreshFromDisplayRef = freshFromDisplayRef.Trim();
        FreshFromPixelPosition = freshFromPixelPosition;
        RequestedDisplayRef = requestedDisplayRef.Trim();
        RequestedPixelPosition = requestedPixelPosition;
        RequestedSlotId = requestedSlotId.Trim();
        FrozenTargetDisplayRef = frozenTargetDisplayRef.Trim();
        FrozenTargetPixelPosition = frozenTargetPixelPosition;
        FrozenTargetSlotId = frozenTargetSlotId.Trim();
        SatisfiedGoalCountBeforeDecision = satisfiedGoalCountBeforeDecision;
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
/// V10 Dynamic Workspace execution journal。V9 static journal 保持原样用于既有历史证据；
/// V10 只记录 immutable final-target authority 与每次 fresh decision / Writer 事实。
/// </summary>
public sealed record DesktopWorkspaceDynamicExecutionJournal
{
    private readonly IReadOnlyList<DesktopWorkspaceDynamicExecutionAttemptEvidence> _forwardAttempts;

    public Guid SessionId { get; init; }
    public Guid SafetySnapshotId { get; init; }
    public Guid BlueprintId { get; init; }
    public int BlueprintRevision { get; init; }
    public string CertificateFingerprint { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DesktopWorkspaceExecutionJournalLifecycle Lifecycle { get; init; }
    public DesktopWorkspaceDynamicExecutionAuthoritySnapshot Authority { get; init; } = null!;
    public IReadOnlyList<DesktopWorkspaceDynamicExecutionAttemptEvidence> ForwardAttempts
    {
        get => _forwardAttempts;
        init => _forwardAttempts = Array.AsReadOnly((value ?? []).ToArray());
    }
    public int LastObservedSatisfiedGoalCount { get; init; }
    public int? LastFailedDecisionIndex { get; init; }
    public string? ForwardFailureType { get; init; }
    public string? ForwardFailureMessage { get; init; }
    public string? RecoveryFailureType { get; init; }
    public string? RecoveryFailureMessage { get; init; }
    public DateTimeOffset? FinalVerifiedAtUtc { get; init; }
    public int Revision { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public bool IsResolved => Lifecycle is DesktopWorkspaceExecutionJournalLifecycle.Committed
        or DesktopWorkspaceExecutionJournalLifecycle.Restored;

    public DesktopWorkspaceDynamicExecutionJournal()
    {
        _forwardAttempts = Array.AsReadOnly(Array.Empty<DesktopWorkspaceDynamicExecutionAttemptEvidence>());
    }

    public static DesktopWorkspaceDynamicExecutionJournal CreatePrepared(
        Guid safetySnapshotId,
        DesktopWorkspaceApplyConfirmation confirmation,
        DesktopWorkspaceDynamicExecutionAuthoritySnapshot authority,
        TimeProvider? timeProvider = null)
    {
        if (safetySnapshotId == Guid.Empty)
            throw new ArgumentException("SafetySnapshotId 不能为 Guid.Empty。", nameof(safetySnapshotId));
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.ConfirmationId != confirmation.ConfirmationId ||
            authority.PreviewId != confirmation.FrozenPreview.PreviewId ||
            !string.Equals(authority.PreviewFingerprint, confirmation.FrozenPreview.PreviewFingerprint, StringComparison.Ordinal) ||
            !string.Equals(authority.ExactPlanFingerprint, confirmation.FrozenPreview.ExactPlan.PlanFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Dynamic authority snapshot 与 confirmation 不一致。", nameof(authority));

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var journal = new DesktopWorkspaceDynamicExecutionJournal
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = safetySnapshotId,
            BlueprintId = confirmation.FrozenPreview.Blueprint.BlueprintId,
            BlueprintRevision = confirmation.FrozenPreview.Blueprint.Revision,
            CertificateFingerprint = confirmation.FrozenPreview.RealizationCertificate.CertificateFingerprint,
            StartedAtUtc = now,
            Lifecycle = DesktopWorkspaceExecutionJournalLifecycle.Prepared,
            Authority = authority,
            ForwardAttempts = [],
            LastObservedSatisfiedGoalCount = 0,
            Revision = 1,
            UpdatedAtUtc = now,
        };
        journal.Validate();
        return journal;
    }

    public void Validate()
    {
        var errors = new List<string>();
        if (SessionId == Guid.Empty) errors.Add("SessionId 不能为 Guid.Empty。");
        if (SafetySnapshotId == Guid.Empty) errors.Add("SafetySnapshotId 不能为 Guid.Empty。");
        if (BlueprintId == Guid.Empty) errors.Add("BlueprintId 不能为 Guid.Empty。");
        if (BlueprintRevision <= 0) errors.Add("BlueprintRevision 必须 > 0。");
        if (string.IsNullOrWhiteSpace(CertificateFingerprint)) errors.Add("CertificateFingerprint 不能为空。");
        if (!Enum.IsDefined(Lifecycle)) errors.Add("Lifecycle 无效。");
        if (Authority is null) errors.Add("Authority 不能为空。");
        if (StartedAtUtc.Offset != TimeSpan.Zero) errors.Add("StartedAtUtc 必须为 UTC。");
        if (UpdatedAtUtc.Offset != TimeSpan.Zero) errors.Add("UpdatedAtUtc 必须为 UTC。");
        if (UpdatedAtUtc < StartedAtUtc) errors.Add("UpdatedAtUtc 不能早于 StartedAtUtc。");
        if (Revision <= 0) errors.Add("Revision 必须 > 0。");

        if (Authority is not null)
        {
            if (LastObservedSatisfiedGoalCount < 0 ||
                LastObservedSatisfiedGoalCount > Authority.MoveGoalCount)
                errors.Add("LastObservedSatisfiedGoalCount 超出 MoveGoalCount。 ");

            var goals = Authority.ItemGoals.ToDictionary(static goal => goal.StableId, StringComparer.Ordinal);
            for (var index = 0; index < ForwardAttempts.Count; index++)
            {
                var attempt = ForwardAttempts[index];
                if (attempt.AttemptIndex != index)
                    errors.Add($"Forward AttemptIndex 不连续：位置 {index} 的值为 {attempt.AttemptIndex}。");
                if (attempt.DecisionIndex != index)
                    errors.Add($"Forward DecisionIndex 不连续：位置 {index} 的值为 {attempt.DecisionIndex}。");
                if (attempt.Phase != DesktopWorkspaceDynamicExecutionAttemptPhase.Forward)
                    errors.Add($"Forward attempt #{index} Phase 必须为 Forward。 ");
                if (!goals.TryGetValue(attempt.StableId, out var goal))
                {
                    errors.Add($"Forward attempt #{index} StableId 不属于 Frozen authority。 ");
                    continue;
                }
                if (!goal.IsMoveAuthorized)
                    errors.Add($"Forward attempt #{index} 试图移动未获得 move authority 的项目。 ");
                if (!string.Equals(attempt.FrozenTargetDisplayRef, goal.FinalDisplayRef, StringComparison.Ordinal) ||
                    attempt.FrozenTargetPixelPosition != goal.FinalPixelPosition ||
                    !string.Equals(attempt.FrozenTargetSlotId, goal.FinalSlotId, StringComparison.Ordinal))
                    errors.Add($"Forward attempt #{index} Frozen target 与 authority 不一致。 ");
                if (attempt.SatisfiedGoalCountBeforeDecision > Authority.MoveGoalCount)
                    errors.Add($"Forward attempt #{index} SatisfiedGoalCountBeforeDecision 超出范围。 ");
                if (attempt.MoveKind == DesktopWorkspaceDynamicMoveKind.MoveToFrozenTarget &&
                    (!string.Equals(attempt.RequestedDisplayRef, goal.FinalDisplayRef, StringComparison.Ordinal) ||
                     attempt.RequestedPixelPosition != goal.FinalPixelPosition ||
                     !string.Equals(attempt.RequestedSlotId, goal.FinalSlotId, StringComparison.Ordinal)))
                    errors.Add($"Forward attempt #{index} MoveToFrozenTarget 请求偏离 Frozen authority。 ");
            }

            if (Lifecycle == DesktopWorkspaceExecutionJournalLifecycle.Prepared && ForwardAttempts.Count > 0)
                errors.Add("Prepared dynamic journal 不得已有 Writer attempt。 ");
            if (Lifecycle == DesktopWorkspaceExecutionJournalLifecycle.Committed)
            {
                if (FinalVerifiedAtUtc is null)
                    errors.Add("Committed dynamic journal 必须记录 FinalVerifiedAtUtc。 ");
                if (LastObservedSatisfiedGoalCount != Authority.MoveGoalCount)
                    errors.Add("Committed dynamic journal 必须观测到全部 move goals 已满足。 ");
            }
            else if (FinalVerifiedAtUtc is not null)
            {
                errors.Add("只有 Committed dynamic journal 可以记录 FinalVerifiedAtUtc。 ");
            }
        }

        if (LastFailedDecisionIndex is < 0)
            errors.Add("LastFailedDecisionIndex 不能为负数。 ");
        if (LastFailedDecisionIndex.HasValue && LastFailedDecisionIndex.Value > ForwardAttempts.Count)
            errors.Add("LastFailedDecisionIndex 超出当前 decision 范围。 ");

        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(" ", errors));
    }
}

public interface IDesktopWorkspaceDynamicExecutionJournalStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DesktopWorkspaceDynamicExecutionJournal journal, CancellationToken cancellationToken = default);
    Task<DesktopWorkspaceDynamicExecutionJournal> UpdateAsync(
        DesktopWorkspaceDynamicExecutionJournal journal,
        CancellationToken cancellationToken = default);
    Task<DesktopWorkspaceDynamicExecutionJournal?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DesktopWorkspaceDynamicExecutionJournal>> ListUnresolvedAsync(
        CancellationToken cancellationToken = default);
}
