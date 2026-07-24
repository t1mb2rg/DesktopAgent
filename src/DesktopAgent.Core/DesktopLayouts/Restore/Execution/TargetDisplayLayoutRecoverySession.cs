namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 不可变的恢复会话模型，用于追踪已部分执行的正向和回滚步骤。
/// 纯数据，不含 COM/PIDL/HWND/WPF。
/// </summary>
public sealed record TargetDisplayLayoutRecoverySession
{
    /// <summary>唯一会话标识。</summary>
    public Guid SessionId { get; }

    /// <summary>安全快照标识。</summary>
    public Guid SafetySnapshotId { get; }

    /// <summary>目标显示器 DeviceName。</summary>
    public string TargetDeviceName { get; }

    /// <summary>会话开始 UTC 时间。</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>源恢复计划（只读副本）。</summary>
    public IReadOnlyList<TargetDisplayLayoutRestorePlanItem> SourceRestorePlanItems { get; }

    /// <summary>执行计划（只读副本）。</summary>
    public IReadOnlyList<TargetDisplayLayoutExecutionStep> ExecutionPlanSteps { get; }

    /// <summary>已完成的正向步骤索引。</summary>
    public IReadOnlyList<int> CompletedForwardSteps { get; }

    /// <summary>已完成的回滚步骤索引。</summary>
    public IReadOnlyList<int> CompletedRollbackSteps { get; }

    /// <summary>最后失败步骤的索引（无失败时为 null）。</summary>
    public int? LastFailedStepIndex { get; }

    /// <summary>是否需要人工恢复。</summary>
    public bool RequiresManualRecovery { get; }

    /// <summary>是否至少有一个图标位置不确定。</summary>
    public bool IsPositionUncertain { get; }

    /// <summary>恢复状态文本。</summary>
    public string Status { get; }

    public TargetDisplayLayoutRecoverySession(
        Guid sessionId,
        Guid safetySnapshotId,
        string targetDeviceName,
        DateTimeOffset startedAtUtc,
        IReadOnlyList<TargetDisplayLayoutRestorePlanItem> sourceRestorePlanItems,
        IReadOnlyList<TargetDisplayLayoutExecutionStep> executionPlanSteps,
        IReadOnlyList<int> completedForwardSteps,
        IReadOnlyList<int> completedRollbackSteps,
        int? lastFailedStepIndex,
        bool requiresManualRecovery,
        bool isPositionUncertain,
        string status)
    {
        ArgumentNullException.ThrowIfNull(targetDeviceName);
        ArgumentNullException.ThrowIfNull(sourceRestorePlanItems);
        ArgumentNullException.ThrowIfNull(executionPlanSteps);
        ArgumentNullException.ThrowIfNull(completedForwardSteps);
        ArgumentNullException.ThrowIfNull(completedRollbackSteps);
        ArgumentNullException.ThrowIfNull(status);

        SessionId = sessionId;
        SafetySnapshotId = safetySnapshotId;
        TargetDeviceName = targetDeviceName;
        StartedAtUtc = startedAtUtc;
        SourceRestorePlanItems = Array.AsReadOnly(sourceRestorePlanItems.ToArray());
        ExecutionPlanSteps = Array.AsReadOnly(executionPlanSteps.ToArray());
        CompletedForwardSteps = Array.AsReadOnly(completedForwardSteps.ToArray());
        CompletedRollbackSteps = Array.AsReadOnly(completedRollbackSteps.ToArray());
        LastFailedStepIndex = lastFailedStepIndex;
        RequiresManualRecovery = requiresManualRecovery;
        IsPositionUncertain = isPositionUncertain;
        Status = status;
    }
}
