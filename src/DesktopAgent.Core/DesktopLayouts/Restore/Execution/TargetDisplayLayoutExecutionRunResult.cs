using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 不可变的布局恢复执行完整结果。
/// 纯数据，不含 COM/PIDL/HWND/WPF。
/// </summary>
public sealed record TargetDisplayLayoutExecutionRunResult
{
    /// <summary>执行状态。</summary>
    public TargetDisplayLayoutExecutionRunStatus Status { get; }

    /// <summary>是否成功（Completed 或 CompletedNoOp）。</summary>
    public bool IsSuccess { get; }

    /// <summary>是否为无操作执行。</summary>
    public bool IsNoOp { get; }

    /// <summary>恢复会话（可能为 null，当 FailedBeforeWrite 时）。</summary>
    public TargetDisplayLayoutRecoverySession? Session { get; }

    /// <summary>安全快照标识。</summary>
    public Guid? SafetySnapshotId { get; }

    /// <summary>正向步骤结果（只读）。</summary>
    public IReadOnlyList<DesktopIconMoveResult> ForwardStepResults { get; }

    /// <summary>回滚步骤结果（只读）。</summary>
    public IReadOnlyList<DesktopIconMoveResult> RollbackStepResults { get; }

    /// <summary>失败步骤索引（无失败时为 null）。</summary>
    public int? FailedStepIndex { get; }

    /// <summary>是否需要人工恢复。</summary>
    public bool RequiresManualRecovery { get; }

    /// <summary>是否至少有一个图标位置不确定。</summary>
    public bool IsPositionUncertain { get; }

    /// <summary>异常类型名（无异常时为 null）。</summary>
    public string? ExceptionType { get; }

    /// <summary>错误消息（无异常时为 null）。</summary>
    public string? ErrorMessage { get; }

    public TargetDisplayLayoutExecutionRunResult(
        TargetDisplayLayoutExecutionRunStatus status,
        bool isSuccess,
        bool isNoOp,
        TargetDisplayLayoutRecoverySession? session,
        Guid? safetySnapshotId,
        IReadOnlyList<DesktopIconMoveResult> forwardStepResults,
        IReadOnlyList<DesktopIconMoveResult> rollbackStepResults,
        int? failedStepIndex,
        bool requiresManualRecovery,
        bool isPositionUncertain,
        string? exceptionType,
        string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(forwardStepResults);
        ArgumentNullException.ThrowIfNull(rollbackStepResults);

        Status = status;
        IsSuccess = isSuccess;
        IsNoOp = isNoOp;
        Session = session;
        SafetySnapshotId = safetySnapshotId;
        ForwardStepResults = Array.AsReadOnly(forwardStepResults.ToArray());
        RollbackStepResults = Array.AsReadOnly(rollbackStepResults.ToArray());
        FailedStepIndex = failedStepIndex;
        RequiresManualRecovery = requiresManualRecovery;
        IsPositionUncertain = isPositionUncertain;
        ExceptionType = exceptionType;
        ErrorMessage = errorMessage;
    }

    /// <summary>快速创建失败结果（无 Writer 调用）。</summary>
    public static TargetDisplayLayoutExecutionRunResult FailedBeforeWrite(
        string? exceptionType = null, string? errorMessage = null) =>
        new(
            TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite,
            false, false, null, null,
            Array.Empty<DesktopIconMoveResult>(),
            Array.Empty<DesktopIconMoveResult>(),
            null, false, false,
            exceptionType, errorMessage);

    /// <summary>快速创建 NoOp 结果。</summary>
    public static TargetDisplayLayoutExecutionRunResult NoOp() =>
        new(
            TargetDisplayLayoutExecutionRunStatus.CompletedNoOp,
            true, true, null, null,
            Array.Empty<DesktopIconMoveResult>(),
            Array.Empty<DesktopIconMoveResult>(),
            null, false, false,
            null, null);
}
