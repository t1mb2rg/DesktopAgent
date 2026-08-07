using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 当前真实桌面中，一个执行计划项目相对 Original/Desired 的位置关系。
/// 该枚举描述观察事实，不描述历史上 Writer 是否成功。
/// </summary>
public enum RecoveryItemRelation
{
    AtOriginal = 0,
    AtDesired = 1,
    Elsewhere = 2,
    Missing = 3,
    Ambiguous = 4,
    ObservationUnavailable = 5,
}

/// <summary>
/// 一次纯对账的批次级摘要。它由 item-level 观察推导，而不是持久化历史的权威状态。
/// </summary>
public enum RecoveryReconciliationState
{
    NoWork = 0,
    Committed = 1,
    Restored = 2,
    MixedKnown = 3,
    NeedsAttention = 4,
}

public enum RecoveryReconciliationIssueCode
{
    TemporaryStagingUnsupported = 0,
    IncompleteDesktopRead = 1,
    DuplicatePlanStableId = 2,
    DuplicateObservedStableId = 3,
    MissingObservedStableId = 4,
    PositionOutsideKnownEndpoints = 5,
}

public sealed record RecoveryReconciliationIssue(
    RecoveryReconciliationIssueCode Code,
    string? StableId,
    string Message);

/// <summary>
/// 单个图标的当前观察结果。Original/Desired 来自持久化 ExecutionPlan，
/// ObservedPosition 只来自本次 fresh desktop observation。
/// </summary>
public sealed record RecoveryReconciliationItem(
    string StableId,
    PixelPoint OriginalPosition,
    PixelPoint DesiredPosition,
    PixelPoint? ObservedPosition,
    RecoveryItemRelation Relation);

/// <summary>
/// Observe → Compare 阶段的纯数据结果。本类型不包含任何 Writer/Store 操作。
/// </summary>
public sealed record RecoveryReconciliationResult
{
    public RecoveryReconciliationState State { get; }
    public IReadOnlyList<RecoveryReconciliationItem> Items { get; }
    public IReadOnlyList<RecoveryReconciliationIssue> Issues { get; }

    public bool RequiresAttention => State == RecoveryReconciliationState.NeedsAttention;

    public RecoveryReconciliationResult(
        RecoveryReconciliationState state,
        IReadOnlyList<RecoveryReconciliationItem> items,
        IReadOnlyList<RecoveryReconciliationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(issues);

        State = state;
        Items = Array.AsReadOnly(items.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
    }
}
