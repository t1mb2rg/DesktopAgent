namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 当前真实桌面与持久化 ExecutionPlan 合法执行前缀之间的关系。
/// </summary>
public enum RecoveryExecutionPrefixState
{
    NoWork = 0,
    Original = 1,
    Intermediate = 2,
    Committed = 3,
    NeedsAttention = 4,
}

public enum RecoveryExecutionPrefixIssueCode
{
    IncompleteDesktopRead = 0,
    PlanNotExecutable = 1,
    SourcePlanInconsistent = 2,
    ExecutionPlanInconsistent = 3,
    MissingObservedStableId = 4,
    DuplicateObservedStableId = 5,
    NoMatchingPrefix = 6,
    AmbiguousPrefixMatch = 7,
}

public sealed record RecoveryExecutionPrefixIssue(
    RecoveryExecutionPrefixIssueCode Code,
    string? StableId,
    string Message);

/// <summary>
/// 对 ExecutionPlan 合法执行前缀的 observation-first 对账结果。
/// AppliedStepCount 只在当前桌面唯一匹配某个前缀时存在：
/// 0 表示尚未应用任何步骤；Steps.Count 表示全部步骤已经生效。
/// </summary>
public sealed record RecoveryExecutionPrefixReconciliationResult
{
    public RecoveryExecutionPrefixState State { get; }
    public int? AppliedStepCount { get; }
    public int? NextRollbackStepIndex =>
        AppliedStepCount is > 0 ? AppliedStepCount.Value - 1 : null;
    public IReadOnlyList<RecoveryExecutionPrefixIssue> Issues { get; }

    public bool RequiresAttention => State == RecoveryExecutionPrefixState.NeedsAttention;
    public bool HasKnownPrefix => AppliedStepCount.HasValue;

    public RecoveryExecutionPrefixReconciliationResult(
        RecoveryExecutionPrefixState state,
        int? appliedStepCount,
        IReadOnlyList<RecoveryExecutionPrefixIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        if (state == RecoveryExecutionPrefixState.NeedsAttention && appliedStepCount is not null)
            throw new ArgumentException("NeedsAttention 不得携带 AppliedStepCount。", nameof(appliedStepCount));
        if (state != RecoveryExecutionPrefixState.NeedsAttention && appliedStepCount is null)
            throw new ArgumentException("可证明的前缀状态必须携带 AppliedStepCount。", nameof(appliedStepCount));
        if (appliedStepCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(appliedStepCount));

        State = state;
        AppliedStepCount = appliedStepCount;
        Issues = Array.AsReadOnly(issues.ToArray());
    }
}
