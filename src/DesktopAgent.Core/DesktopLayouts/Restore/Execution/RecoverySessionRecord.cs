namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 不可变的恢复会话持久化聚合。
/// 包含跨重启恢复所需的全部证据：Session 标头、双向计划冻结副本、
/// 全部 Forward/Rollback 尝试、已完成步骤索引、独立验证结果、
/// 失败证据以及并发控制 Revision。
///
/// 不含 Exception 对象、COM/PIDL/HWND/WPF 引用或 Persistence 项目依赖。
/// </summary>
public sealed record RecoverySessionRecord
{
    // ── Session 标识 ──
    public Guid SessionId { get; init; }
    public Guid SafetySnapshotId { get; init; }
    public string TargetDeviceName { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }

    // ── 生命周期与标志 ──
    public RecoverySessionLifecycle Lifecycle { get; init; }
    public bool RequiresManualRecovery { get; init; }
    public bool IsPositionUncertain { get; init; }
    public int? LastFailedStepIndex { get; init; }

    // ── 计划冻结副本 ──
    public TargetDisplayLayoutRestorePlan SourceRestorePlan { get; init; } = null!;
    public TargetDisplayLayoutExecutionPlan ExecutionPlan { get; init; } = null!;

    // ── 尝试结果 ──
    public IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> ForwardAttempts { get; init; } =
        Array.Empty<TargetDisplayLayoutExecutionAttemptResult>();

    public IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> RollbackAttempts { get; init; } =
        Array.Empty<TargetDisplayLayoutExecutionAttemptResult>();

    // ── 已完成步骤索引（持久化，非派生） ──
    public IReadOnlyList<int> CompletedForwardStepIndexes { get; init; } =
        Array.Empty<int>();

    public IReadOnlyList<int> CompletedRollbackSourceStepIndexes { get; init; } =
        Array.Empty<int>();

    // ── 失败证据（无 Exception 对象） ──
    public string? ForwardFailureType { get; init; }
    public string? ForwardFailureMessage { get; init; }
    public string? RecoveryFailureType { get; init; }
    public string? RecoveryFailureMessage { get; init; }

    // ── 独立验证结果 ──
    public TargetDisplayLayoutExecutionVerificationResult? VerificationResult { get; init; }

    // ── 并发控制 ──
    public int Revision { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }

    // ═══════════════════════════════════════════════════
    //  冻结（防御性复制全部集合）
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 返回一个新的 Record，其中全部集合均已深度复制并冻结。
    /// 原 Record 的集合引用被修改后，冻结副本不受影响。
    /// </summary>
    public RecoverySessionRecord Freeze()
    {
        return this with
        {
            ForwardAttempts = Array.AsReadOnly(ForwardAttempts.ToArray()),
            RollbackAttempts = Array.AsReadOnly(RollbackAttempts.ToArray()),
            CompletedForwardStepIndexes = Array.AsReadOnly(CompletedForwardStepIndexes.ToArray()),
            CompletedRollbackSourceStepIndexes = Array.AsReadOnly(CompletedRollbackSourceStepIndexes.ToArray()),
            SourceRestorePlan = TargetDisplayLayoutRecoverySession.FreezeRestorePlan(SourceRestorePlan),
        };
    }

    /// <summary>验证 DateTimeOffset 的 Offset 为 UTC (Offset=0)。</summary>
    public static void RequireUtc(DateTimeOffset value, string fieldName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException(
                $"{fieldName} 必须为 UTC (Offset=0)，实际 Offset={value.Offset}。");
    }

    // ═══════════════════════════════════════════════════
    //  工厂与序列化
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 从现有 RecoverySession 和 RunResult 创建完整持久化聚合。
    /// 所有集合深度复制。
    /// </summary>
    public static RecoverySessionRecord FromExecutionResult(
        TargetDisplayLayoutRecoverySession session,
        TargetDisplayLayoutExecutionRunResult result)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);

        var lifecycle = DeriveLifecycle(result.Status);

        return new RecoverySessionRecord
        {
            SessionId = session.SessionId,
            SafetySnapshotId = session.SafetySnapshotId,
            TargetDeviceName = session.TargetDeviceName,
            StartedAtUtc = session.StartedAtUtc,
            Lifecycle = lifecycle,
            RequiresManualRecovery = result.RequiresManualRecovery,
            IsPositionUncertain = result.IsPositionUncertain,
            LastFailedStepIndex = result.FailedStepIndex,
            SourceRestorePlan = session.SourceRestorePlan,
            ExecutionPlan = session.ExecutionPlan,
            ForwardAttempts = Array.AsReadOnly(result.ForwardAttempts.ToArray()),
            RollbackAttempts = Array.AsReadOnly(result.RollbackAttempts.ToArray()),
            CompletedForwardStepIndexes = Array.AsReadOnly(
                session.CompletedForwardStepIndexes.ToArray()),
            CompletedRollbackSourceStepIndexes = Array.AsReadOnly(
                session.CompletedRollbackSourceStepIndexes.ToArray()),
            ForwardFailureType = result.ForwardFailureType,
            ForwardFailureMessage = result.ForwardFailureMessage,
            RecoveryFailureType = result.RecoveryFailureType,
            RecoveryFailureMessage = result.RecoveryFailureMessage,
            VerificationResult = result.VerificationResult,
            Revision = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static RecoverySessionLifecycle DeriveLifecycle(TargetDisplayLayoutExecutionRunStatus status) =>
        status switch
        {
            TargetDisplayLayoutExecutionRunStatus.Completed
                or TargetDisplayLayoutExecutionRunStatus.CompletedNoOp
                => RecoverySessionLifecycle.Completed,

            TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack
                => RecoverySessionLifecycle.FailedAndRolledBack,

            TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete
                => RecoverySessionLifecycle.FailedRollbackIncomplete,

            TargetDisplayLayoutExecutionRunStatus.PositionUncertain
                => RecoverySessionLifecycle.PositionUncertain,

            TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite
                => throw new ArgumentException(
                    $"RunStatus.{status} 无对应 RecoverySession，不应调用 DeriveLifecycle。"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(status), status, $"未定义的 RunStatus 值：{status}。"),
        };

    public static string DeriveStatus(RecoverySessionLifecycle lifecycle) =>
        lifecycle switch
        {
            RecoverySessionLifecycle.Ready => "Ready",
            RecoverySessionLifecycle.Executing => "Executing",
            RecoverySessionLifecycle.Completed => "Completed",
            RecoverySessionLifecycle.FailedAndRolledBack => "FailedAndRolledBack",
            RecoverySessionLifecycle.FailedRollbackIncomplete => "FailedRollbackIncomplete",
            RecoverySessionLifecycle.PositionUncertain => "PositionUncertain",
            _ => "Unknown",
        };

    // ═══════════════════════════════════════════════════
    //  完整性验证
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 验证聚合的跨模型一致性。失败时抛出 InvalidDataException。
    /// </summary>
    public void Validate()
    {
        var errors = new List<string>();

        // ── 必填字段 ──
        if (SessionId == Guid.Empty)
            errors.Add("SessionId 不能为 Guid.Empty。");
        if (SafetySnapshotId == Guid.Empty)
            errors.Add("SafetySnapshotId 不能为 Guid.Empty。");
        if (string.IsNullOrWhiteSpace(TargetDeviceName))
            errors.Add("TargetDeviceName 不能为 null 或空白。");
        if (SourceRestorePlan is null)
            errors.Add("SourceRestorePlan 不能为 null。");
        if (ExecutionPlan is null)
            errors.Add("ExecutionPlan 不能为 null。");

        // ── UTC 时间校验 ──
        try { RequireUtc(StartedAtUtc, nameof(StartedAtUtc)); }
        catch (ArgumentException ex) { errors.Add(ex.Message); }
        try { RequireUtc(UpdatedAtUtc, nameof(UpdatedAtUtc)); }
        catch (ArgumentException ex) { errors.Add(ex.Message); }
        if (VerificationResult is not null)
        {
            try { RequireUtc(VerificationResult.VerifiedAtUtc, "VerificationResult.VerifiedAtUtc"); }
            catch (ArgumentException ex) { errors.Add(ex.Message); }
        }

        // ── Lifecycle 枚举 ──
        if (!Enum.IsDefined(Lifecycle))
            errors.Add($"无效的 Lifecycle 值：{(int)Lifecycle}。");

        // ── Revision ──
        if (Revision <= 0)
            errors.Add($"Revision 必须 > 0，实际为 {Revision}。");

        // ── 目标显示器一致性 ──
        if (SourceRestorePlan is not null && ExecutionPlan is not null)
        {
            if (!string.Equals(SourceRestorePlan.TargetDeviceName, TargetDeviceName, StringComparison.Ordinal))
                errors.Add($"SourceRestorePlan.TargetDeviceName 不匹配。");
            if (!string.Equals(ExecutionPlan.TargetDeviceName, TargetDeviceName, StringComparison.Ordinal))
                errors.Add($"ExecutionPlan.TargetDeviceName 不匹配。");
        }

        // ── StepIndex 连续 ──
        if (ExecutionPlan is not null)
        {
            for (var i = 0; i < ExecutionPlan.Steps.Count; i++)
            {
                if (ExecutionPlan.Steps[i].StepIndex != i)
                    errors.Add($"ExecutionPlan.Steps[{i}].StepIndex={ExecutionPlan.Steps[i].StepIndex}，预期 {i}。");
            }
        }

        // ── Attempts 一致性 ──
        ValidateAttempts(nameof(ForwardAttempts), ForwardAttempts,
            TargetDisplayLayoutExecutionAttemptPhase.Forward, errors);
        ValidateAttempts(nameof(RollbackAttempts), RollbackAttempts,
            TargetDisplayLayoutExecutionAttemptPhase.Rollback, errors);

        // ── AttemptIndex 连续 ──
        var allAttempts = ForwardAttempts.Concat(RollbackAttempts)
            .OrderBy(a => a.AttemptIndex).ToList();
        for (var i = 0; i < allAttempts.Count; i++)
        {
            if (allAttempts[i].AttemptIndex != i)
                errors.Add($"AttemptIndex 不连续：位置 {i} 的值是 {allAttempts[i].AttemptIndex}。");
        }

        // ── Attempt 坐标与 Step 交叉验证 ──
        if (ExecutionPlan is not null)
        {
            foreach (var a in allAttempts)
            {
                if (a.SourceStepIndex < 0 || a.SourceStepIndex >= ExecutionPlan.Steps.Count)
                {
                    errors.Add($"Attempt #{a.AttemptIndex} SourceStepIndex={a.SourceStepIndex} 超出范围 [0, {ExecutionPlan.Steps.Count})。");
                    continue;
                }
                var step = ExecutionPlan.Steps[a.SourceStepIndex];

                // StableId 交叉验证
                if (!string.Equals(a.ExpectedStableId, step.StableId, StringComparison.Ordinal))
                    errors.Add($"Attempt #{a.AttemptIndex} ExpectedStableId \"{a.ExpectedStableId}\" ≠ Step[{a.SourceStepIndex}].StableId \"{step.StableId}\"。");

                // 坐标验证
                if (a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward)
                {
                    if (a.ExpectedPreviousPosition != step.FromPosition)
                        errors.Add($"Forward Attempt #{a.AttemptIndex} ExpectedPrevious={a.ExpectedPreviousPosition} ≠ Step[{a.SourceStepIndex}].From={step.FromPosition}。");
                    if (a.RequestedPosition != step.ToPosition)
                        errors.Add($"Forward Attempt #{a.AttemptIndex} RequestedPosition={a.RequestedPosition} ≠ Step[{a.SourceStepIndex}].To={step.ToPosition}。");
                }
                else // Rollback
                {
                    if (a.RequestedPosition != step.FromPosition)
                        errors.Add($"Rollback Attempt #{a.AttemptIndex} RequestedPosition={a.RequestedPosition} ≠ Step[{a.SourceStepIndex}].From={step.FromPosition}。");
                }

                // Outcome 坐标一致性
                if (a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                {
                    if (a.ActualPosition != a.RequestedPosition)
                        errors.Add($"Attempt #{a.AttemptIndex} ExactSuccess 但 ActualPosition={a.ActualPosition} ≠ RequestedPosition={a.RequestedPosition}。");
                }
            }
        }

        // ── 已完成步骤索引与 Attempts 交叉验证 ──
        if (ExecutionPlan is not null)
        {
            var expectedFwd = ForwardAttempts
                .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                .Select(a => a.SourceStepIndex)
                .Distinct().OrderBy(x => x).ToList();
            if (!expectedFwd.SequenceEqual(CompletedForwardStepIndexes.OrderBy(x => x)))
                errors.Add("CompletedForwardStepIndexes 与 ExactSuccess Forward Attempts 不匹配。");

            var expectedRb = RollbackAttempts
                .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                .Select(a => a.SourceStepIndex)
                .Distinct().OrderBy(x => x).ToList();
            if (!expectedRb.SequenceEqual(CompletedRollbackSourceStepIndexes.OrderBy(x => x)))
                errors.Add("CompletedRollbackSourceStepIndexes 与 ExactSuccess Rollback Attempts 不匹配。");
        }

        // ── LastFailedStepIndex 范围 ──
        if (LastFailedStepIndex is not null && ExecutionPlan is not null)
        {
            if (LastFailedStepIndex < 0 || LastFailedStepIndex >= ExecutionPlan.Steps.Count)
                errors.Add($"LastFailedStepIndex {LastFailedStepIndex} 超出范围。");
        }

        // ── RestorePlan 摘要计数一致性 ──
        if (SourceRestorePlan is not null)
        {
            var items = SourceRestorePlan.Items;
            var actualAlready = items.Count(it => it.State == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget);
            var actualMove = items.Count(it => it.State == TargetDisplayLayoutRestoreItemState.MoveRequired);
            var actualMissing = items.Count(it => it.State == TargetDisplayLayoutRestoreItemState.MissingFromCurrent);
            var actualBlocked = items.Count(it =>
                it.State == TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon
                || it.State == TargetDisplayLayoutRestoreItemState.NotFileSystemItem
                || it.State == TargetDisplayLayoutRestoreItemState.SystemItem);

            if (SourceRestorePlan.AlreadyAtTargetCount != actualAlready)
                errors.Add($"RestorePlan AlreadyAtTargetCount={SourceRestorePlan.AlreadyAtTargetCount}，实际={actualAlready}。");
            if (SourceRestorePlan.MoveRequiredCount != actualMove)
                errors.Add($"RestorePlan MoveRequiredCount={SourceRestorePlan.MoveRequiredCount}，实际={actualMove}。");
            if (SourceRestorePlan.MissingCount != actualMissing)
                errors.Add($"RestorePlan MissingCount={SourceRestorePlan.MissingCount}，实际={actualMissing}。");
            if (SourceRestorePlan.BlockedCount != actualBlocked)
                errors.Add($"RestorePlan BlockedCount={SourceRestorePlan.BlockedCount}，实际={actualBlocked}。");
            if (SourceRestorePlan.SnapshotTargetIconCount != actualAlready + actualMove)
                errors.Add($"RestorePlan SnapshotTargetIconCount={SourceRestorePlan.SnapshotTargetIconCount}，实际={actualAlready + actualMove}。");
        }

        // ── ExecutionPlan 步骤分类计数一致性 ──
        if (ExecutionPlan is not null)
        {
            var actualDirect = ExecutionPlan.Steps.Count(
                s => s.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTarget);
            var actualStagingIn = ExecutionPlan.Steps.Count(
                s => s.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging);
            var actualStagingOut = ExecutionPlan.Steps.Count(
                s => s.Kind == TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget);

            if (ExecutionPlan.DirectMoveStepCount != actualDirect)
                errors.Add($"ExecutionPlan DirectMoveStepCount={ExecutionPlan.DirectMoveStepCount}，实际={actualDirect}。");
            if (ExecutionPlan.StagingMoveStepCount != actualStagingIn)
                errors.Add($"ExecutionPlan StagingMoveStepCount={ExecutionPlan.StagingMoveStepCount}，实际={actualStagingIn}。");
            if (ExecutionPlan.TargetMoveStepCount != actualStagingOut)
                errors.Add($"ExecutionPlan TargetMoveStepCount={ExecutionPlan.TargetMoveStepCount}，实际={actualStagingOut}。");
            if (ExecutionPlan.UsesTemporaryStaging != (actualStagingIn > 0))
                errors.Add($"ExecutionPlan UsesTemporaryStaging={ExecutionPlan.UsesTemporaryStaging}，实际 staging steps={actualStagingIn}。");

            // ── Staging Assignments 验证 ──
            foreach (var sa in ExecutionPlan.StagingAssignments)
            {
                if (sa.EnterStepIndex < 0 || sa.EnterStepIndex >= ExecutionPlan.Steps.Count)
                    errors.Add($"StagingAssignment StableId=\"{sa.StableId}\" EnterStepIndex={sa.EnterStepIndex} 超出范围。");
                if (sa.ExitStepIndex < 0 || sa.ExitStepIndex >= ExecutionPlan.Steps.Count)
                    errors.Add($"StagingAssignment StableId=\"{sa.StableId}\" ExitStepIndex={sa.ExitStepIndex} 超出范围。");

                if (sa.EnterStepIndex >= sa.ExitStepIndex)
                    errors.Add($"StagingAssignment StableId=\"{sa.StableId}\" EnterStepIndex={sa.EnterStepIndex} ≥ ExitStepIndex={sa.ExitStepIndex}。");

                if (sa.EnterStepIndex >= 0 && sa.EnterStepIndex < ExecutionPlan.Steps.Count)
                {
                    var enterStep = ExecutionPlan.Steps[sa.EnterStepIndex];
                    if (!string.Equals(enterStep.StableId, sa.StableId, StringComparison.Ordinal))
                        errors.Add($"StagingAssignment 引用 StableId=\"{sa.StableId}\"，Enter step #{sa.EnterStepIndex} StableId=\"{enterStep.StableId}\"。");
                    if (enterStep.Kind != TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging)
                        errors.Add($"StagingAssignment Enter step #{sa.EnterStepIndex} Kind={enterStep.Kind}，期望 MoveToTemporaryStaging。");
                    if (enterStep.ToPosition != sa.StagingPosition)
                        errors.Add($"StagingAssignment Enter step.To={enterStep.ToPosition} ≠ StagingPosition={sa.StagingPosition}。");
                }

                if (sa.ExitStepIndex >= 0 && sa.ExitStepIndex < ExecutionPlan.Steps.Count)
                {
                    var exitStep = ExecutionPlan.Steps[sa.ExitStepIndex];
                    if (!string.Equals(exitStep.StableId, sa.StableId, StringComparison.Ordinal))
                        errors.Add($"StagingAssignment 引用 StableId=\"{sa.StableId}\"，Exit step #{sa.ExitStepIndex} StableId=\"{exitStep.StableId}\"。");
                    if (exitStep.Kind != TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget)
                        errors.Add($"StagingAssignment Exit step #{sa.ExitStepIndex} Kind={exitStep.Kind}，期望 MoveFromTemporaryStagingToTarget。");
                    if (exitStep.FromPosition != sa.StagingPosition)
                        errors.Add($"StagingAssignment Exit step.From={exitStep.FromPosition} ≠ StagingPosition={sa.StagingPosition}。");
                }
            }

            // Staging StableId 唯一性
            var stagingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sa in ExecutionPlan.StagingAssignments)
            {
                if (!stagingIds.Add(sa.StableId))
                    errors.Add($"StagingAssignment StableId=\"{sa.StableId}\" 重复。");
            }

            // 所有 staging steps 被 assignment 引用
            var assignedStagingIds = new HashSet<string>(
                ExecutionPlan.StagingAssignments.Select(sa => sa.StableId), StringComparer.Ordinal);
            foreach (var step in ExecutionPlan.Steps.Where(
                s => s.Kind is TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging
                    or TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget))
            {
                if (!assignedStagingIds.Contains(step.StableId))
                    errors.Add($"Staging step #{step.StepIndex} StableId=\"{step.StableId}\" 未被任何 StagingAssignment 引用。");
            }
        }

        // ── 状态标志一致性 ──
        if (Lifecycle == RecoverySessionLifecycle.Ready
            && (RequiresManualRecovery || IsPositionUncertain))
            errors.Add("Ready 状态下 RequiresManualRecovery 和 IsPositionUncertain 必须为 false。");

        if (Lifecycle == RecoverySessionLifecycle.Executing)
        {
            if (RequiresManualRecovery)
                errors.Add("Executing 状态下 RequiresManualRecovery 必须为 false。");
            if (IsPositionUncertain)
                errors.Add("Executing 状态下 IsPositionUncertain 必须为 false。");
        }

        if (Lifecycle == RecoverySessionLifecycle.Completed
            && (RequiresManualRecovery || IsPositionUncertain))
            errors.Add("Completed 状态下 RequiresManualRecovery 和 IsPositionUncertain 必须为 false。");

        if (Lifecycle == RecoverySessionLifecycle.FailedAndRolledBack
            && (RequiresManualRecovery || IsPositionUncertain))
            errors.Add("FailedAndRolledBack 状态下 RequiresManualRecovery 和 IsPositionUncertain 必须为 false。");

        if (Lifecycle == RecoverySessionLifecycle.FailedRollbackIncomplete)
        {
            if (!RequiresManualRecovery)
                errors.Add("FailedRollbackIncomplete 状态下 RequiresManualRecovery 必须为 true。");
            if (IsPositionUncertain)
                errors.Add("FailedRollbackIncomplete 状态下 IsPositionUncertain 必须为 false。");
        }

        if (Lifecycle == RecoverySessionLifecycle.PositionUncertain)
        {
            if (!RequiresManualRecovery || !IsPositionUncertain)
                errors.Add("PositionUncertain 状态下 RequiresManualRecovery 和 IsPositionUncertain 必须都为 true。");
        }

        // ── 失败证据 ──
        if (Lifecycle is RecoverySessionLifecycle.FailedRollbackIncomplete
            or RecoverySessionLifecycle.PositionUncertain)
        {
            if (string.IsNullOrWhiteSpace(ForwardFailureType))
                errors.Add($"{Lifecycle} 状态下 ForwardFailureType 必须非空。");
        }

        // ── Verification 一致性 ──
        if (VerificationResult is not null)
        {
            if (!Enum.IsDefined(VerificationResult.Phase))
                errors.Add($"无效的 VerificationResult.Phase：{(int)VerificationResult.Phase}。");
            if (!Enum.IsDefined(VerificationResult.Status))
                errors.Add($"无效的 VerificationResult.Status：{(int)VerificationResult.Status}。");

            if (VerificationResult.FailedItemCount < 0)
                errors.Add($"VerificationResult.FailedItemCount 不能为负数。");

            if (VerificationResult.Status == ExecutionVerificationStatus.Passed
                && VerificationResult.FailedItemCount != 0)
                errors.Add($"Verification Passed 但 FailedItemCount={VerificationResult.FailedItemCount}。");
            if (VerificationResult.Status == ExecutionVerificationStatus.Passed
                && VerificationResult.Issues.Count > 0)
                errors.Add($"Verification Passed 但 Issues.Count={VerificationResult.Issues.Count}。");
            if (VerificationResult.Status == ExecutionVerificationStatus.Failed
                && VerificationResult.FailedItemCount <= 0
                && VerificationResult.Issues.Count == 0)
                errors.Add("Verification Failed 但无证据。");

            foreach (var issue in VerificationResult.Issues)
            {
                if (string.IsNullOrWhiteSpace(issue))
                    errors.Add("VerificationResult.Issues 包含 null 或空白条目。");
            }

            if (Lifecycle == RecoverySessionLifecycle.Completed
                && VerificationResult.Phase != ExecutionVerificationPhase.Forward)
                errors.Add($"Completed 状态下 VerificationPhase 必须为 Forward。");

            if (Lifecycle is RecoverySessionLifecycle.FailedAndRolledBack
                or RecoverySessionLifecycle.FailedRollbackIncomplete)
            {
                if (VerificationResult.Phase != ExecutionVerificationPhase.Rollback)
                    errors.Add($"{Lifecycle} 状态下 VerificationPhase 必须为 Rollback。");
            }
        }

        if (errors.Count > 0)
            throw new InvalidDataException(
                $"RecoverySessionRecord 验证失败 ({SessionId:D})：" +
                string.Join("; ", errors));
    }

    // ═══════════════════════════════════════════════════
    //  Attempt 验证
    // ═══════════════════════════════════════════════════

    private static void ValidateAttempts(
        string name,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> attempts,
        TargetDisplayLayoutExecutionAttemptPhase expectedPhase,
        List<string> errors)
    {
        foreach (var a in attempts)
        {
            if (a.Phase != expectedPhase)
                errors.Add($"{name} 中 Attempt #{a.AttemptIndex} Phase={a.Phase}，期望 {expectedPhase}。");
            if (!Enum.IsDefined(a.Phase))
                errors.Add($"{name} 中 Attempt #{a.AttemptIndex} Phase 值无效：{(int)a.Phase}。");
            if (!Enum.IsDefined(a.Outcome))
                errors.Add($"{name} 中 Attempt #{a.AttemptIndex} Outcome 值无效：{(int)a.Outcome}。");
            if (string.IsNullOrWhiteSpace(a.ExpectedStableId))
                errors.Add($"{name} 中 Attempt #{a.AttemptIndex} ExpectedStableId 无效。");

            switch (a.Outcome)
            {
                case TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess:
                    if (!a.IsExact || !a.HasTrustedActualPosition)
                        errors.Add($"{name} #{a.AttemptIndex} ExactSuccess 但 IsExact={a.IsExact}, HasTrusted={a.HasTrustedActualPosition}。");
                    if (a.ExceptionType is not null || a.ErrorMessage is not null)
                        errors.Add($"{name} #{a.AttemptIndex} ExactSuccess 但 ExceptionType/ErrorMessage 非 null。");
                    if (a.ReportedStableId is null || a.ActualPosition is null)
                        errors.Add($"{name} #{a.AttemptIndex} ExactSuccess 但 ReportedStableId/ActualPosition 为 null。");
                    break;

                case TargetDisplayLayoutExecutionAttemptOutcome.InexactKnown:
                    if (a.IsExact || !a.HasTrustedActualPosition)
                        errors.Add($"{name} #{a.AttemptIndex} InexactKnown 但 IsExact={a.IsExact}, HasTrusted={a.HasTrustedActualPosition}。");
                    if (a.ReportedStableId is null || a.ActualPosition is null)
                        errors.Add($"{name} #{a.AttemptIndex} InexactKnown 但 ReportedStableId/ActualPosition 为 null。");
                    break;

                case TargetDisplayLayoutExecutionAttemptOutcome.WriterException:
                    if (string.IsNullOrWhiteSpace(a.ExceptionType))
                        errors.Add($"{name} #{a.AttemptIndex} WriterException 但 ExceptionType 为空。");
                    if (a.HasTrustedActualPosition || a.IsExact)
                        errors.Add($"{name} #{a.AttemptIndex} WriterException 但 HasTrusted={a.HasTrustedActualPosition}, IsExact={a.IsExact}。");
                    if (a.ReportedStableId is not null || a.ActualPosition is not null)
                        errors.Add($"{name} #{a.AttemptIndex} WriterException 但 ReportedStableId/ActualPosition 非 null。");
                    break;

                case TargetDisplayLayoutExecutionAttemptOutcome.StableIdMismatch:
                    if (a.HasTrustedActualPosition)
                        errors.Add($"{name} #{a.AttemptIndex} StableIdMismatch 但 HasTrustedActualPosition=true。");
                    if (a.ReportedStableId is null)
                        errors.Add($"{name} #{a.AttemptIndex} StableIdMismatch 但 ReportedStableId 为 null。");
                    break;

                case TargetDisplayLayoutExecutionAttemptOutcome.PreviousPositionMismatch:
                    if (!a.HasTrustedActualPosition)
                        errors.Add($"{name} #{a.AttemptIndex} PreviousPositionMismatch 但 HasTrustedActualPosition=false。");
                    if (a.ReportedPreviousPosition is null)
                        errors.Add($"{name} #{a.AttemptIndex} PreviousPositionMismatch 但 ReportedPreviousPosition 为 null。");
                    break;

                default:
                    errors.Add($"{name} #{a.AttemptIndex} 未定义的 Outcome：{(int)a.Outcome}。");
                    break;
            }
        }
    }
}
