namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 将一个已经持久化的恢复终态投影为对外运行结果摘要。
///
/// Lifecycle 仍是唯一持久化语义源；RunStatus / IsSuccess / IsNoOp / 人工处理 /
/// 位置不确定等值只能由这里派生，不能由 Coordinator 各自手工拼装。
/// </summary>
public readonly record struct RecoveryTerminalProjection
{
    public RecoverySessionLifecycle Lifecycle { get; }
    public TargetDisplayLayoutExecutionRunStatus RunStatus { get; }
    public bool IsSuccess { get; }
    public bool IsNoOp { get; }
    public RecoverySessionDisposition Disposition { get; }

    public bool RequiresManualRecovery => Disposition.RequiresManualRecovery;
    public bool IsPositionUncertain => Disposition.IsPositionUncertain;

    private RecoveryTerminalProjection(
        RecoverySessionLifecycle lifecycle,
        TargetDisplayLayoutExecutionRunStatus runStatus,
        bool isSuccess,
        bool isNoOp,
        RecoverySessionDisposition disposition)
    {
        Lifecycle = lifecycle;
        RunStatus = runStatus;
        IsSuccess = isSuccess;
        IsNoOp = isNoOp;
        Disposition = disposition;
    }

    public static RecoveryTerminalProjection FromLifecycle(
        RecoverySessionLifecycle lifecycle,
        bool isNoOp = false)
    {
        var disposition = RecoverySessionDisposition.FromLifecycle(lifecycle);

        if (isNoOp && lifecycle != RecoverySessionLifecycle.Completed)
        {
            throw new ArgumentException(
                $"只有 Lifecycle.Completed 可以投影为 NoOp，实际为 {lifecycle}。",
                nameof(isNoOp));
        }

        return lifecycle switch
        {
            RecoverySessionLifecycle.Completed => new(
                lifecycle,
                isNoOp
                    ? TargetDisplayLayoutExecutionRunStatus.CompletedNoOp
                    : TargetDisplayLayoutExecutionRunStatus.Completed,
                isSuccess: true,
                isNoOp,
                disposition),

            RecoverySessionLifecycle.FailedAndRolledBack => new(
                lifecycle,
                TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack,
                isSuccess: false,
                isNoOp: false,
                disposition),

            RecoverySessionLifecycle.FailedRollbackIncomplete => new(
                lifecycle,
                TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
                isSuccess: false,
                isNoOp: false,
                disposition),

            RecoverySessionLifecycle.PositionUncertain => new(
                lifecycle,
                TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                isSuccess: false,
                isNoOp: false,
                disposition),

            RecoverySessionLifecycle.Ready
                or RecoverySessionLifecycle.Executing
                => throw new InvalidOperationException(
                    $"Lifecycle.{lifecycle} 不是恢复终态，不能投影为终态 RunResult。"),

            _ => throw new ArgumentOutOfRangeException(
                nameof(lifecycle), lifecycle, $"未定义的 RecoverySessionLifecycle 值：{(int)lifecycle}。"),
        };
    }
}
