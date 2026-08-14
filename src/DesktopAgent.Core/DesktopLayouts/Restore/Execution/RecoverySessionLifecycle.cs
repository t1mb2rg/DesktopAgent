namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 恢复会话的持久化历史摘要。它记录会话走到过什么阶段、上一次如何收敛，
/// 但不声明当前真实桌面是什么状态。
///
/// 是否需要自动对账、是否需要人工介入以及上一次安全分类，统一通过
/// RecoverySessionDisposition.FromLifecycle 投影；所有持久化路径仍使用本枚举，
/// 不得使用自由字符串。
/// </summary>
public enum RecoverySessionLifecycle
{
    /// <summary>会话已创建，Writer 尚未启动。</summary>
    Ready = 0,

    /// <summary>Writer 操作曾处于进行中；跨重启后必须重新观察事实并对账。</summary>
    Executing = 1,

    /// <summary>会话最终被验证为 Forward 目标已成立。</summary>
    Completed = 2,

    /// <summary>会话最终被验证为已恢复到 Original。</summary>
    FailedAndRolledBack = 3,

    /// <summary>上一次恢复结束在“已知但不完整”的人工处理状态。</summary>
    FailedRollbackIncomplete = 4,

    /// <summary>上一次恢复无法证明位置事实，因此以位置不确定安全分类停止。</summary>
    PositionUncertain = 5,

    /// <summary>
    /// 用户在明确看到当前真实桌面后，主动接受当前状态并放弃继续恢复到 Original / Desired。
    /// 该终态本身不执行 Writer；历史 Snapshot、Attempts、Verification 与失败证据继续保留。
    /// </summary>
    AcceptedCurrentState = 6,
}
