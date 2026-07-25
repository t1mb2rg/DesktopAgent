namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 单次执行尝试的结果类别。
/// </summary>
public enum TargetDisplayLayoutExecutionAttemptOutcome
{
    /// <summary>精确移动到请求位置。</summary>
    ExactSuccess,

    /// <summary>已知非精确结果（位置已记录）。</summary>
    InexactKnown,

    /// <summary>Writer 抛异常（位置未知）。</summary>
    WriterException,

    /// <summary>Writer 返回错误 StableId。</summary>
    StableIdMismatch,

    /// <summary>Writer 返回 PreviousPosition 与预期不一致。</summary>
    PreviousPositionMismatch,
}
