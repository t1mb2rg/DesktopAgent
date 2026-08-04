using System.Collections.ObjectModel;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 不可变的恢复会话持久化聚合。
/// 包含跨重启恢复所需的全部证据：Session 标头、双向计划冻结副本、
/// 全部 Forward/Rollback 尝试、独立验证结果、失败证据以及并发控制 Revision。
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

    // ── 派生属性（由 Attempts 计算，非持久化列） ──

    /// <summary>从 ForwardAttempts 推导的已完成正向步骤索引（仅 ExactSuccess）。</summary>
    public IReadOnlyList<int> ComputedForwardStepIndexes =>
        ForwardAttempts
            .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
            .Select(a => a.SourceStepIndex)
            .Distinct()
            .OrderBy(x => x)
            .ToList().AsReadOnly();

    /// <summary>从 RollbackAttempts 推导的已完成回滚源步骤索引（仅 ExactSuccess）。</summary>
    public IReadOnlyList<int> ComputedRollbackSourceStepIndexes =>
        RollbackAttempts
            .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
            .Select(a => a.SourceStepIndex)
            .Distinct()
            .OrderBy(x => x)
            .ToList().AsReadOnly();

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

        // 推导生命周期
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
            ForwardFailureType = result.ForwardFailureType,
            ForwardFailureMessage = result.ForwardFailureMessage,
            RecoveryFailureType = result.RecoveryFailureType,
            RecoveryFailureMessage = result.RecoveryFailureMessage,
            VerificationResult = result.VerificationResult,
            Revision = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 从 RunStatus 推导生命周期状态。
    /// </summary>
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

            // FailedBeforeWrite / 其他 → 不应持久化（无 Session，Writer 未启动）
            _ => RecoverySessionLifecycle.Ready,
        };

    /// <summary>
    /// 从 Lifecycle 反向映射为 RecoverySession.Status 字符串。
    /// 用于从持久化 Record 重建 RecoverySession。
    /// </summary>
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

    /// <summary>
    /// 创建用于 Update 的新 Revision（原 Revision + 1），保持其他字段不变。
    /// 调用方负责设置更新的字段。
    /// </summary>
    public RecoverySessionRecord WithNextRevision()
    {
        return this with
        {
            Revision = Revision + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    // ═══════════════════════════════════════════════════
    //  完整性验证（不可变 8：跨模型一致性）
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 验证聚合的跨模型一致性。失败时抛出 InvalidDataException。
    /// </summary>
    public void Validate()
    {
        var errors = new List<string>();

        // 必填字段
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

        // 生命周期枚举
        if (!Enum.IsDefined(Lifecycle))
            errors.Add($"无效的 Lifecycle 值：{(int)Lifecycle}。");

        // 目标显示器一致性
        if (SourceRestorePlan is not null && ExecutionPlan is not null)
        {
            if (!string.Equals(SourceRestorePlan.TargetDeviceName, TargetDeviceName, StringComparison.Ordinal))
                errors.Add($"SourceRestorePlan.TargetDeviceName \"{SourceRestorePlan.TargetDeviceName}\" ≠ Record.TargetDeviceName \"{TargetDeviceName}\"。");
            if (!string.Equals(ExecutionPlan.TargetDeviceName, TargetDeviceName, StringComparison.Ordinal))
                errors.Add($"ExecutionPlan.TargetDeviceName \"{ExecutionPlan.TargetDeviceName}\" ≠ Record.TargetDeviceName \"{TargetDeviceName}\"。");
        }

        // Attempts 一致性
        ValidateAttempts(nameof(ForwardAttempts), ForwardAttempts,
            TargetDisplayLayoutExecutionAttemptPhase.Forward, errors);
        ValidateAttempts(nameof(RollbackAttempts), RollbackAttempts,
            TargetDisplayLayoutExecutionAttemptPhase.Rollback, errors);

        // AttemptIndex 连续唯一
        var allAttempts = ForwardAttempts.Concat(RollbackAttempts)
            .OrderBy(a => a.AttemptIndex).ToList();
        for (var i = 0; i < allAttempts.Count; i++)
        {
            if (allAttempts[i].AttemptIndex != i)
                errors.Add($"AttemptIndex 不连续：位置 {i} 的值是 {allAttempts[i].AttemptIndex}。");
        }

        // CompletedForwardStepIndexes 与 ExactSuccess Forward Attempts 一致
        if (ExecutionPlan is not null)
        {
            var actualFwd = ForwardAttempts
                .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                .Select(a => a.SourceStepIndex)
                .OrderBy(x => x).ToList();
            // 注意：Record 上没有显式的 CompletedForwardStepIndexes 字段；
            // 改为从 Attempts 推导并与 Session 的原始值交叉验证。
            // 此处仅验证 Attempt 级别的 SourceStepIndex 在范围内。
            ValidateStepIndexInRange(ForwardAttempts, ExecutionPlan.Steps.Count, errors);
            ValidateStepIndexInRange(RollbackAttempts, ExecutionPlan.Steps.Count, errors);
        }

        // LastFailedStepIndex 范围
        if (LastFailedStepIndex is not null && ExecutionPlan is not null)
        {
            if (LastFailedStepIndex < 0 || LastFailedStepIndex >= ExecutionPlan.Steps.Count)
                errors.Add($"LastFailedStepIndex {LastFailedStepIndex} 超出范围 [0, {ExecutionPlan.Steps.Count})。");
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
                errors.Add($"RestorePlan SnapshotTargetIconCount={SourceRestorePlan.SnapshotTargetIconCount}，" +
                    $"实际 Already+Move={actualAlready + actualMove}。");
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
        }

        // ── Attempt ExpectedStableId 与 ExecutionStep.StableId 交叉验证 ──
        if (ExecutionPlan is not null)
        {
            foreach (var a in ForwardAttempts.Concat(RollbackAttempts))
            {
                if (a.SourceStepIndex >= 0 && a.SourceStepIndex < ExecutionPlan.Steps.Count)
                {
                    var step = ExecutionPlan.Steps[a.SourceStepIndex];
                    if (!string.Equals(a.ExpectedStableId, step.StableId, StringComparison.Ordinal))
                        errors.Add(
                            $"Attempt #{a.AttemptIndex} ExpectedStableId \"{a.ExpectedStableId}\" " +
                            $"≠ Step[{a.SourceStepIndex}].StableId \"{step.StableId}\"。");
                }
            }
        }

        // 状态标志一致性
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

        // Verification 一致性
        if (VerificationResult is not null)
        {
            if (!Enum.IsDefined(VerificationResult.Phase))
                errors.Add($"无效的 VerificationResult.Phase：{(int)VerificationResult.Phase}。");
            if (!Enum.IsDefined(VerificationResult.Status))
                errors.Add($"无效的 VerificationResult.Status：{(int)VerificationResult.Status}。");
            if (VerificationResult.FailedItemCount < 0)
                errors.Add($"VerificationResult.FailedItemCount 不能为负数：{VerificationResult.FailedItemCount}。");

            // Phase 与 Lifecycle 一致性
            if (Lifecycle == RecoverySessionLifecycle.Completed
                && VerificationResult.Phase != ExecutionVerificationPhase.Forward)
                errors.Add($"Completed 状态下 VerificationPhase 必须为 Forward，实际为 {VerificationResult.Phase}。");

            if (Lifecycle is RecoverySessionLifecycle.FailedAndRolledBack
                or RecoverySessionLifecycle.FailedRollbackIncomplete)
            {
                if (VerificationResult.Phase != ExecutionVerificationPhase.Rollback)
                    errors.Add($"{Lifecycle} 状态下 VerificationPhase 必须为 Rollback，实际为 {VerificationResult.Phase}。");
            }
        }

        if (errors.Count > 0)
            throw new InvalidDataException(
                $"RecoverySessionRecord 验证失败 ({SessionId:D})：" +
                string.Join("; ", errors));
    }

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
                errors.Add($"{name} 中 Attempt #{a.AttemptIndex} 的 Phase 值无效：{(int)a.Phase}。");
            if (!Enum.IsDefined(a.Outcome))
                errors.Add($"{name} 中 Attempt #{a.AttemptIndex} 的 Outcome 值无效：{(int)a.Outcome}。");
            if (string.IsNullOrWhiteSpace(a.ExpectedStableId))
                errors.Add($"{name} 中 Attempt #{a.AttemptIndex} 的 ExpectedStableId 无效。");

            // Outcome 与字段组合合法性
            switch (a.Outcome)
            {
                case TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess:
                    if (!a.IsExact || !a.HasTrustedActualPosition)
                        errors.Add($"{name} 中 Attempt #{a.AttemptIndex} ExactSuccess 但 IsExact={a.IsExact}, HasTrusted={a.HasTrustedActualPosition}。");
                    break;
                case TargetDisplayLayoutExecutionAttemptOutcome.InexactKnown:
                    if (a.IsExact || !a.HasTrustedActualPosition)
                        errors.Add($"{name} 中 Attempt #{a.AttemptIndex} InexactKnown 但 IsExact={a.IsExact}, HasTrusted={a.HasTrustedActualPosition}。");
                    break;
                case TargetDisplayLayoutExecutionAttemptOutcome.WriterException:
                    if (string.IsNullOrWhiteSpace(a.ExceptionType))
                        errors.Add($"{name} 中 Attempt #{a.AttemptIndex} WriterException 但 ExceptionType 为空。");
                    break;
                 case TargetDisplayLayoutExecutionAttemptOutcome.StableIdMismatch:
                    if (a.HasTrustedActualPosition)
                        errors.Add($"{name} 中 Attempt #{a.AttemptIndex} StableIdMismatch 但 HasTrustedActualPosition=true。");
                    break;
            }
        }
    }

    private static void ValidateStepIndexInRange(
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> attempts,
        int stepCount,
        List<string> errors)
    {
        foreach (var a in attempts)
        {
            if (a.SourceStepIndex < 0 || a.SourceStepIndex >= stepCount)
                errors.Add($"Attempt #{a.AttemptIndex} SourceStepIndex={a.SourceStepIndex} 超出范围 [0, {stepCount})。");
        }
    }
}
