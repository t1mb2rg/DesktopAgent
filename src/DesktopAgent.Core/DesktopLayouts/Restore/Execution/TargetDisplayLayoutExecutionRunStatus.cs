namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 布局恢复执行运行结果状态。
/// </summary>
public enum TargetDisplayLayoutExecutionRunStatus
{
    /// <summary>所有步骤精确成功。</summary>
    Completed,

    /// <summary>无需移动（NoOp 计划）。</summary>
    CompletedNoOp,

    /// <summary>在第一次 Writer 调用前失败（快照、计划、验证等）。</summary>
    FailedBeforeWrite,

    /// <summary>正向步骤失败但全部回滚精确成功。</summary>
    FailedAndRolledBack,

    /// <summary>正向步骤失败且回滚未完成。</summary>
    FailedRollbackIncomplete,

    /// <summary>至少一个图标位置不确定。</summary>
    PositionUncertain,
}
