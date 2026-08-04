namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 不可变的布局恢复执行完整结果。不含 Exception 对象。
/// </summary>
public sealed record TargetDisplayLayoutExecutionRunResult
{
    public TargetDisplayLayoutExecutionRunStatus Status { get; }
    public bool IsSuccess { get; }
    public bool IsNoOp { get; }
    public TargetDisplayLayoutRecoverySession? Session { get; }
    public Guid? SafetySnapshotId { get; }

    /// <summary>正向尝试结果（只读）。</summary>
    public IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> ForwardAttempts { get; }

    /// <summary>回滚尝试结果（只读）。</summary>
    public IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> RollbackAttempts { get; }

    public int? FailedStepIndex { get; }
    public bool RequiresManualRecovery { get; }
    public bool IsPositionUncertain { get; }

    /// <summary>正向失败异常类型（无失败时为 null）。</summary>
    public string? ForwardFailureType { get; }

    /// <summary>正向失败异常消息（无失败时为 null）。</summary>
    public string? ForwardFailureMessage { get; }

    /// <summary>回滚/恢复失败异常类型（无失败时为 null）。</summary>
    public string? RecoveryFailureType { get; }

    /// <summary>回滚/恢复失败异常消息（无失败时为 null）。</summary>
    public string? RecoveryFailureMessage { get; }

    /// <summary>执行后独立完整重读验证结果（无验证时为 null）。</summary>
    public TargetDisplayLayoutExecutionVerificationResult? VerificationResult { get; }

    public TargetDisplayLayoutExecutionRunResult(
        TargetDisplayLayoutExecutionRunStatus status,
        bool isSuccess,
        bool isNoOp,
        TargetDisplayLayoutRecoverySession? session,
        Guid? safetySnapshotId,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> forwardAttempts,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        int? failedStepIndex,
        bool requiresManualRecovery,
        bool isPositionUncertain,
        string? forwardFailureType,
        string? forwardFailureMessage,
        string? recoveryFailureType,
        string? recoveryFailureMessage,
        TargetDisplayLayoutExecutionVerificationResult? verificationResult = null)
    {
        ArgumentNullException.ThrowIfNull(forwardAttempts);
        ArgumentNullException.ThrowIfNull(rollbackAttempts);

        Status = status;
        IsSuccess = isSuccess;
        IsNoOp = isNoOp;
        Session = session;
        SafetySnapshotId = safetySnapshotId;
        ForwardAttempts = Array.AsReadOnly(forwardAttempts.ToArray());
        RollbackAttempts = Array.AsReadOnly(rollbackAttempts.ToArray());
        FailedStepIndex = failedStepIndex;
        RequiresManualRecovery = requiresManualRecovery;
        IsPositionUncertain = isPositionUncertain;
        ForwardFailureType = forwardFailureType;
        ForwardFailureMessage = forwardFailureMessage;
        RecoveryFailureType = recoveryFailureType;
        RecoveryFailureMessage = recoveryFailureMessage;
        VerificationResult = verificationResult;
    }

    public static TargetDisplayLayoutExecutionRunResult FailedBeforeWrite(
        string? exceptionType, string? errorMessage) =>
        new(
            TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite,
            false, false, null, null,
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            null, false, false,
            exceptionType, errorMessage, null, null);

    public static TargetDisplayLayoutExecutionRunResult NoOp() =>
        new(
            TargetDisplayLayoutExecutionRunStatus.CompletedNoOp,
            true, true, null, null,
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            null, false, false, null, null, null, null);
}
