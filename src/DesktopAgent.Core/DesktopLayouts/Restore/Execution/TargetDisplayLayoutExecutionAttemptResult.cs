using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 单次 Writer 调用的不可变执行尝试结果。
/// 不含 Exception 对象 — 仅保存类型名和消息。
/// </summary>
public sealed record TargetDisplayLayoutExecutionAttemptResult
{
    /// <summary>调用顺序索引（0 开始）。</summary>
    public int AttemptIndex { get; }

    /// <summary>对应的 ExecutionPlan 步骤索引。</summary>
    public int SourceStepIndex { get; }

    /// <summary>正向还是回滚。</summary>
    public TargetDisplayLayoutExecutionAttemptPhase Phase { get; }

    /// <summary>期望的 StableId。</summary>
    public string ExpectedStableId { get; }

    /// <summary>期望的 PreviousPosition。</summary>
    public PixelPoint? ExpectedPreviousPosition { get; }

    /// <summary>请求的目标位置。</summary>
    public PixelPoint RequestedPosition { get; }

    /// <summary>Writer 返回的 StableId（异常时为 null）。</summary>
    public string? ReportedStableId { get; }

    /// <summary>Writer 返回的 PreviousPosition（异常时为 null）。</summary>
    public PixelPoint? ReportedPreviousPosition { get; }

    /// <summary>Writer 返回的 ActualPosition（异常时为 null）。</summary>
    public PixelPoint? ActualPosition { get; }

    /// <summary>ActualPosition 是否可信。</summary>
    public bool HasTrustedActualPosition { get; }

    /// <summary>ActualPosition == RequestedPosition。</summary>
    public bool IsExact { get; }

    /// <summary>结果类别。</summary>
    public TargetDisplayLayoutExecutionAttemptOutcome Outcome { get; }

    /// <summary>Shell 连接方式。</summary>
    public ConnectionMethod? ConnectionMethod { get; }

    /// <summary>异常类型名（无异常时为 null）。</summary>
    public string? ExceptionType { get; }

    /// <summary>异常消息（无异常时为 null）。</summary>
    public string? ErrorMessage { get; }

    /// <summary>Writer PrimaryPathException 类型名。</summary>
    public string? PrimaryPathExceptionType { get; }

    /// <summary>Writer PrimaryPathException 消息。</summary>
    public string? PrimaryPathExceptionMessage { get; }

    public TargetDisplayLayoutExecutionAttemptResult(
        int attemptIndex,
        int sourceStepIndex,
        TargetDisplayLayoutExecutionAttemptPhase phase,
        string expectedStableId,
        PixelPoint? expectedPreviousPosition,
        PixelPoint requestedPosition,
        string? reportedStableId,
        PixelPoint? reportedPreviousPosition,
        PixelPoint? actualPosition,
        bool hasTrustedActualPosition,
        bool isExact,
        TargetDisplayLayoutExecutionAttemptOutcome outcome,
        ConnectionMethod? connectionMethod,
        string? exceptionType,
        string? errorMessage,
        string? primaryPathExceptionType,
        string? primaryPathExceptionMessage)
    {
        ArgumentNullException.ThrowIfNull(expectedStableId);

        AttemptIndex = attemptIndex;
        SourceStepIndex = sourceStepIndex;
        Phase = phase;
        ExpectedStableId = expectedStableId;
        ExpectedPreviousPosition = expectedPreviousPosition;
        RequestedPosition = requestedPosition;
        ReportedStableId = reportedStableId;
        ReportedPreviousPosition = reportedPreviousPosition;
        ActualPosition = actualPosition;
        HasTrustedActualPosition = hasTrustedActualPosition;
        IsExact = isExact;
        Outcome = outcome;
        ConnectionMethod = connectionMethod;
        ExceptionType = exceptionType;
        ErrorMessage = errorMessage;
        PrimaryPathExceptionType = primaryPathExceptionType;
        PrimaryPathExceptionMessage = primaryPathExceptionMessage;
    }

    /// <summary>从成功的 MoveResult 创建正向精确结果。</summary>
    public static TargetDisplayLayoutExecutionAttemptResult FromExactForward(
        int attemptIndex, int sourceStepIndex,
        TargetDisplayLayoutExecutionStep step, DesktopIconMoveResult mr) =>
        new(
            attemptIndex, sourceStepIndex,
            TargetDisplayLayoutExecutionAttemptPhase.Forward,
            step.StableId, step.FromPosition, step.ToPosition,
            mr.StableId, mr.PreviousPosition, mr.ActualPosition,
            true, true,
            TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess,
            mr.ConnectionMethod,
            null, null,
            mr.PrimaryPathException?.GetType().Name,
            mr.PrimaryPathException?.Message);

    /// <summary>从非精确 MoveResult 创建正向结果。</summary>
    public static TargetDisplayLayoutExecutionAttemptResult FromInexactForward(
        int attemptIndex, int sourceStepIndex,
        TargetDisplayLayoutExecutionStep step, DesktopIconMoveResult mr) =>
        new(
            attemptIndex, sourceStepIndex,
            TargetDisplayLayoutExecutionAttemptPhase.Forward,
            step.StableId, step.FromPosition, step.ToPosition,
            mr.StableId, mr.PreviousPosition, mr.ActualPosition,
            true, false,
            TargetDisplayLayoutExecutionAttemptOutcome.InexactKnown,
            mr.ConnectionMethod,
            null, null,
            mr.PrimaryPathException?.GetType().Name,
            mr.PrimaryPathException?.Message);

    /// <summary>从 Writer 异常创建正向结果。</summary>
    public static TargetDisplayLayoutExecutionAttemptResult FromWriterException(
        int attemptIndex, int sourceStepIndex,
        TargetDisplayLayoutExecutionStep step,
        Exception ex) =>
        new(
            attemptIndex, sourceStepIndex,
            TargetDisplayLayoutExecutionAttemptPhase.Forward,
            step.StableId, step.FromPosition, step.ToPosition,
            null, null, null,
            false, false,
            TargetDisplayLayoutExecutionAttemptOutcome.WriterException,
            null,
            ex.GetType().Name, ex.Message,
            null, null);

    /// <summary>从 StableId 不匹配创建结果。</summary>
    public static TargetDisplayLayoutExecutionAttemptResult FromStableIdMismatch(
        int attemptIndex, int sourceStepIndex,
        TargetDisplayLayoutExecutionStep step, DesktopIconMoveResult mr) =>
        new(
            attemptIndex, sourceStepIndex,
            TargetDisplayLayoutExecutionAttemptPhase.Forward,
            step.StableId, step.FromPosition, step.ToPosition,
            mr.StableId, mr.PreviousPosition, mr.ActualPosition,
            true, false, // ActualPosition 可信但 StableId 不匹配
            TargetDisplayLayoutExecutionAttemptOutcome.StableIdMismatch,
            mr.ConnectionMethod,
            null, null,
            mr.PrimaryPathException?.GetType().Name,
            mr.PrimaryPathException?.Message);

    /// <summary>从 PreviousPosition 不匹配创建结果。</summary>
    public static TargetDisplayLayoutExecutionAttemptResult FromPreviousPositionMismatch(
        int attemptIndex, int sourceStepIndex,
        TargetDisplayLayoutExecutionStep step, DesktopIconMoveResult mr) =>
        new(
            attemptIndex, sourceStepIndex,
            TargetDisplayLayoutExecutionAttemptPhase.Forward,
            step.StableId, step.FromPosition, step.ToPosition,
            mr.StableId, mr.PreviousPosition, mr.ActualPosition,
            true, false,
            TargetDisplayLayoutExecutionAttemptOutcome.PreviousPositionMismatch,
            mr.ConnectionMethod,
            null, null,
            mr.PrimaryPathException?.GetType().Name,
            mr.PrimaryPathException?.Message);

    /// <summary>从成功的 MoveResult 创建回滚精确结果。</summary>
    public static TargetDisplayLayoutExecutionAttemptResult FromExactRollback(
        int attemptIndex, int sourceStepIndex,
        TargetDisplayLayoutExecutionStep step, PixelPoint rollbackTarget,
        PixelPoint expectedPrevious, DesktopIconMoveResult mr) =>
        new(
            attemptIndex, sourceStepIndex,
            TargetDisplayLayoutExecutionAttemptPhase.Rollback,
            step.StableId, expectedPrevious, rollbackTarget,
            mr.StableId, mr.PreviousPosition, mr.ActualPosition,
            true, true,
            TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess,
            mr.ConnectionMethod,
            null, null,
            mr.PrimaryPathException?.GetType().Name,
            mr.PrimaryPathException?.Message);

    /// <summary>从非精确 MoveResult 创建回滚非精确结果。</summary>
    public static TargetDisplayLayoutExecutionAttemptResult FromInexactRollback(
        int attemptIndex, int sourceStepIndex,
        TargetDisplayLayoutExecutionStep step, PixelPoint rollbackTarget,
        PixelPoint expectedPrevious, DesktopIconMoveResult mr) =>
        new(
            attemptIndex, sourceStepIndex,
            TargetDisplayLayoutExecutionAttemptPhase.Rollback,
            step.StableId, expectedPrevious, rollbackTarget,
            mr.StableId, mr.PreviousPosition, mr.ActualPosition,
            true, false,
            TargetDisplayLayoutExecutionAttemptOutcome.InexactKnown,
            mr.ConnectionMethod,
            null, null,
            mr.PrimaryPathException?.GetType().Name,
            mr.PrimaryPathException?.Message);

    /// <summary>从回滚异常创建结果。</summary>
    public static TargetDisplayLayoutExecutionAttemptResult FromRollbackException(
        int attemptIndex, int sourceStepIndex,
        TargetDisplayLayoutExecutionStep step, PixelPoint rollbackTarget,
        PixelPoint expectedPrevious, Exception ex) =>
        new(
            attemptIndex, sourceStepIndex,
            TargetDisplayLayoutExecutionAttemptPhase.Rollback,
            step.StableId, expectedPrevious, rollbackTarget,
            null, null, null,
            false, false,
            TargetDisplayLayoutExecutionAttemptOutcome.WriterException,
            null,
            ex.GetType().Name, ex.Message,
            null, null);
}
