namespace DesktopAgent.Core.DesktopLayouts.Restore;

/// <summary>
/// 目标显示器布局恢复计划。纯数据，不含 PIDL/COM/HWND/WPF。
/// </summary>
public sealed record TargetDisplayLayoutRestorePlan
{
    public string TargetDeviceName { get; }
    public int SnapshotTargetIconCount { get; }
    public int AlreadyAtTargetCount { get; }
    public int MoveRequiredCount { get; }
    public int MissingCount { get; }
    public int BlockedCount { get; }
    public bool HasBlockingErrors { get; }
    public bool RequiresTemporaryStaging { get; }
    public bool IsNoOp { get; }

    public IReadOnlyList<TargetDisplayLayoutRestorePlanItem> Items { get; }
    public IReadOnlyList<TargetDisplayLayoutRestoreDependency> Dependencies { get; }
    public IReadOnlyList<TargetDisplayLayoutRestoreCycle> Cycles { get; }
    public IReadOnlyList<TargetDisplayLayoutRestorePlanIssue> Issues { get; }
    public IReadOnlyList<string>? MoveExecutionOrder { get; }

    public TargetDisplayLayoutRestorePlan(
        string targetDeviceName,
        int snapshotTargetIconCount,
        int alreadyAtTargetCount,
        int moveRequiredCount,
        int missingCount,
        int blockedCount,
        bool hasBlockingErrors,
        bool requiresTemporaryStaging,
        bool isNoOp,
        IReadOnlyList<TargetDisplayLayoutRestorePlanItem> items,
        IReadOnlyList<TargetDisplayLayoutRestoreDependency> dependencies,
        IReadOnlyList<TargetDisplayLayoutRestoreCycle> cycles,
        IReadOnlyList<TargetDisplayLayoutRestorePlanIssue> issues,
        IReadOnlyList<string>? moveExecutionOrder)
    {
        TargetDeviceName = targetDeviceName;
        SnapshotTargetIconCount = snapshotTargetIconCount;
        AlreadyAtTargetCount = alreadyAtTargetCount;
        MoveRequiredCount = moveRequiredCount;
        MissingCount = missingCount;
        BlockedCount = blockedCount;
        HasBlockingErrors = hasBlockingErrors;
        RequiresTemporaryStaging = requiresTemporaryStaging;
        IsNoOp = isNoOp;
        Items = items;
        Dependencies = dependencies;
        Cycles = cycles;
        Issues = issues;
        MoveExecutionOrder = moveExecutionOrder;
    }
}
