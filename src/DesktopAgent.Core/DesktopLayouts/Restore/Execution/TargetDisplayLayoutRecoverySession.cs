namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 不可变的恢复会话模型。
/// 构造函数深度冻结所有输入集合。
/// </summary>
public sealed record TargetDisplayLayoutRecoverySession
{
    public Guid SessionId { get; }
    public Guid SafetySnapshotId { get; }
    public string TargetDeviceName { get; }
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>深度冻结的源恢复计划。</summary>
    public TargetDisplayLayoutRestorePlan SourceRestorePlan { get; }

    /// <summary>深度冻结的执行计划。</summary>
    public TargetDisplayLayoutExecutionPlan ExecutionPlan { get; }

    /// <summary>已完成的正向步骤索引（仅 ExactSuccess）。</summary>
    public IReadOnlyList<int> CompletedForwardStepIndexes { get; }

    /// <summary>已完成的回滚步骤的源步骤索引。</summary>
    public IReadOnlyList<int> CompletedRollbackSourceStepIndexes { get; }

    public int? LastFailedStepIndex { get; }
    public bool RequiresManualRecovery { get; }
    public bool IsPositionUncertain { get; }
    public string Status { get; }

    /// <summary>这次写入的来路（整理 / 撤销按检查点还原），落库时原样写进恢复会话行。</summary>
    public RecoverySessionOrigin Origin { get; }

    public TargetDisplayLayoutRecoverySession(
        Guid sessionId,
        Guid safetySnapshotId,
        string targetDeviceName,
        DateTimeOffset startedAtUtc,
        TargetDisplayLayoutRestorePlan sourceRestorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan,
        IReadOnlyList<int> completedForwardStepIndexes,
        IReadOnlyList<int> completedRollbackSourceStepIndexes,
        int? lastFailedStepIndex,
        bool requiresManualRecovery,
        bool isPositionUncertain,
        string status,
        RecoverySessionOrigin origin = RecoverySessionOrigin.Organize)
    {
        ArgumentNullException.ThrowIfNull(targetDeviceName);
        ArgumentNullException.ThrowIfNull(sourceRestorePlan);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(completedForwardStepIndexes);
        ArgumentNullException.ThrowIfNull(completedRollbackSourceStepIndexes);
        ArgumentNullException.ThrowIfNull(status);

        SessionId = sessionId;
        SafetySnapshotId = safetySnapshotId;
        TargetDeviceName = targetDeviceName;
        StartedAtUtc = startedAtUtc;

        // 深度冻结 SourceRestorePlan
        SourceRestorePlan = FreezeRestorePlan(sourceRestorePlan);

        // ExecutionPlan 已经防御性复制（Array.AsReadOnly），直接使用
        ExecutionPlan = executionPlan;

        CompletedForwardStepIndexes = Array.AsReadOnly(completedForwardStepIndexes.ToArray());
        CompletedRollbackSourceStepIndexes = Array.AsReadOnly(completedRollbackSourceStepIndexes.ToArray());
        LastFailedStepIndex = lastFailedStepIndex;
        RequiresManualRecovery = requiresManualRecovery;
        IsPositionUncertain = isPositionUncertain;
        Status = status;
        Origin = origin;
    }

    /// <summary>创建恢复计划的深度冻结副本。</summary>
    internal static TargetDisplayLayoutRestorePlan FreezeRestorePlan(TargetDisplayLayoutRestorePlan source)
    {
        var frozenItems = source.Items.Select(it => it).ToList().AsReadOnly();
        var frozenDeps = source.Dependencies.ToList().AsReadOnly();
        var frozenCycles = source.Cycles
            .Select(c => new TargetDisplayLayoutRestoreCycle(
                Array.AsReadOnly(c.StableIds.ToArray())))
            .ToList().AsReadOnly();
        IReadOnlyList<string>? frozenOrder = null;
        if (source.MoveExecutionOrder is not null)
            frozenOrder = Array.AsReadOnly(source.MoveExecutionOrder.ToArray());
        var frozenIssues = source.Issues.ToList().AsReadOnly();

        return new TargetDisplayLayoutRestorePlan(
            source.TargetDeviceName,
            source.SnapshotTargetIconCount,
            source.AlreadyAtTargetCount,
            source.MoveRequiredCount,
            source.MissingCount,
            source.BlockedCount,
            source.HasBlockingErrors,
            source.RequiresTemporaryStaging,
            source.IsNoOp,
            frozenItems,
            frozenDeps,
            frozenCycles,
            frozenIssues,
            frozenOrder);
    }
}
