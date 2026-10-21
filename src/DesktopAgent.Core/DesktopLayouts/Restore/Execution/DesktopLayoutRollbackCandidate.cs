using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 这次「上次整理」是从哪条执行路径写到桌面上的。
///
/// 撤销入口必须<strong>同时看见</strong>两条路径留下的安全检查点，否则用户撤不掉其中一条：
/// 直接应用写恢复会话，工作区设计应用写 V10 动态执行 journal + 同一套安全检查点快照。
/// </summary>
public enum DesktopLayoutRollbackSourceKind
{
    /// <summary>「应用到桌面」：逐屏直接应用，恢复会话里冻结了双向计划与已完成步骤。</summary>
    RecoverySession = 0,

    /// <summary>工作区设计（V10 Dynamic Workspace）应用到桌面。</summary>
    WorkspaceApply = 1,
}

/// <summary>
/// 一次整理真正写下去的一个项目：它是谁、界面上叫什么、写完之后应该在哪。
/// 「应该在哪」来自当时冻结的计划（恢复会话的已完成步骤 / 动态执行 authority 的 move goal），
/// 是判定「桌面是否还是我们整理完的样子」的唯一依据，绝不现场推断。
/// </summary>
public sealed record DesktopLayoutRollbackWrittenItem
{
    public string StableId { get; }

    /// <summary>撤销文案里给人看的名字。</summary>
    public string DisplayName { get; }

    /// <summary>写盘后这个项目的物理像素终点。</summary>
    public PixelPoint ExpectedFinalPosition { get; }

    public DesktopLayoutRollbackWrittenItem(
        string stableId,
        string displayName,
        PixelPoint expectedFinalPosition)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));

        StableId = stableId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? stableId : displayName;
        ExpectedFinalPosition = expectedFinalPosition;
    }
}

/// <summary>
/// 「最近一次成功整理」的路径无关视图：撤销的判定与规划只依赖这些事实，
/// 不关心它来自恢复会话还是工作区设计应用。
/// </summary>
public sealed record DesktopLayoutRollbackCandidate(
    Guid SessionId,
    DesktopLayoutRollbackSourceKind SourceKind,
    Guid SafetySnapshotId,
    string? TargetDeviceName,
    DateTimeOffset StartedAtUtc,
    IReadOnlyList<DesktopLayoutRollbackWrittenItem> WrittenItems)
{
    public int WrittenItemCount => WrittenItems.Count;

    /// <summary>直接应用路径有唯一目标屏；工作区设计应用可能同时落在多块屏上。</summary>
    public bool HasTargetDisplay => !string.IsNullOrWhiteSpace(TargetDeviceName);

    /// <summary>恢复会话候选：写过的项目 = 已完成的前向步骤，终点 = 该步的 ToPosition。</summary>
    public static DesktopLayoutRollbackCandidate? TryFromRecoverySession(RecoverySessionRecord? session)
    {
        if (session is null)
            return null;

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in session.SourceRestorePlan.Items)
            names[item.StableId] = item.DisplayName;

        var steps = session.ExecutionPlan.Steps;
        var byStableId = new Dictionary<string, DesktopLayoutRollbackWrittenItem>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var stepIndex in session.CompletedForwardStepIndexes.OrderBy(index => index))
        {
            if (stepIndex < 0 || stepIndex >= steps.Count)
                continue;

            var step = steps[stepIndex];
            if (!byStableId.ContainsKey(step.StableId))
                order.Add(step.StableId);

            // 同一个项目可能有多步（staging：先挪到临时位、再挪到目标位），末步的 ToPosition 才是最终落点。
            byStableId[step.StableId] = new DesktopLayoutRollbackWrittenItem(
                step.StableId,
                names.TryGetValue(step.StableId, out var displayName) ? displayName : step.StableId,
                step.ToPosition);
        }

        return new DesktopLayoutRollbackCandidate(
            session.SessionId,
            DesktopLayoutRollbackSourceKind.RecoverySession,
            session.SafetySnapshotId,
            session.TargetDeviceName,
            session.StartedAtUtc,
            Array.AsReadOnly(order.Select(stableId => byStableId[stableId]).ToArray()));
    }

    /// <summary>
    /// 工作区设计应用候选：写过的项目 = authority 里获准移动的 move goals，
    /// 终点 = 该 goal 冻结的最终物理位置（执行器就是把 FrozenTargetPixelPosition 交给 Writer 的）。
    /// 只有 <c>Committed</c> journal 才算候选：<c>Restored</c> 表示这次写入已经被撤回过。
    /// </summary>
    public static DesktopLayoutRollbackCandidate? TryFromWorkspaceApply(
        DesktopWorkspaceDynamicExecutionJournal? journal)
    {
        if (journal is null)
            return null;

        if (journal.Lifecycle != DesktopWorkspaceExecutionJournalLifecycle.Committed)
            return null;

        var moves = journal.Authority.ItemGoals
            .Where(static goal => goal.IsMoveAuthorized)
            .Select(static goal => new DesktopLayoutRollbackWrittenItem(
                goal.StableId,
                goal.DisplayName,
                goal.FinalPixelPosition))
            .ToArray();

        return new DesktopLayoutRollbackCandidate(
            journal.SessionId,
            DesktopLayoutRollbackSourceKind.WorkspaceApply,
            journal.SafetySnapshotId,
            TargetDeviceName: null,
            journal.StartedAtUtc,
            Array.AsReadOnly(moves));
    }
}
