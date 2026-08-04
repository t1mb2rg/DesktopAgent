namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 恢复会话生命周期状态。所有持久化和恢复路径必须使用此枚举，
/// 不得使用自由字符串。
/// </summary>
public enum RecoverySessionLifecycle
{
    /// <summary>会话已创建，Writer 尚未启动。</summary>
    Ready = 0,

    /// <summary>Writer 操作进行中（可能在崩溃时残留）。</summary>
    Executing = 1,

    /// <summary>全部 Forward 步骤成功，独立验证通过。</summary>
    Completed = 2,

    /// <summary>Forward 失败或取消，全部已知步骤成功回滚，独立验证通过。</summary>
    FailedAndRolledBack = 3,

    /// <summary>Forward 失败或取消，回滚不完整或独立验证失败。</summary>
    FailedRollbackIncomplete = 4,

    /// <summary>Forward 或回滚后，位置不可信或独立验证无法确定。</summary>
    PositionUncertain = 5,
}
