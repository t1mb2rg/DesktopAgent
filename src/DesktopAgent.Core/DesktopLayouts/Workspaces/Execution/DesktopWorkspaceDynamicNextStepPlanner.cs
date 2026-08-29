using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;

public enum DesktopWorkspaceDynamicNextStepStatus
{
    Complete = 0,
    MoveReady = 1,
    Blocked = 2,
}

public enum DesktopWorkspaceDynamicMoveKind
{
    MoveToFrozenTarget = 0,
    MoveToTemporaryStaging = 1,
}

public enum DesktopWorkspaceDynamicNextStepIssueCode
{
    GeometryChanged = 0,
    ItemSetChanged = 1,
    MovabilityChanged = 2,
    InvalidFrozenGoal = 3,
    StationaryItemDrifted = 4,
    TargetOutsideWorkArea = 5,
    NoSafeStagingSlot = 6,
}

public sealed record DesktopWorkspaceDynamicNextStepIssue(
    DesktopWorkspaceDynamicNextStepIssueCode Code,
    string Message,
    string? StableId = null,
    string? DisplayRef = null);

/// <summary>
/// Dynamic Workspace execution 的单次、事实驱动 move 决策。
/// From 只描述本次 fresh observation 中的真实当前位置，不是用户确认 authority；
/// Frozen target 仍然来自同一份 Frozen Preview，planner 无权重新设计或修改最终目标。
/// </summary>
public sealed record DesktopWorkspaceDynamicMove
{
    public int DecisionIndex { get; }
    public string StableId { get; }
    public string DisplayName { get; }
    public DesktopWorkspaceDynamicMoveKind Kind { get; }
    public string FromDisplayRef { get; }
    public PixelPoint FromPixelPosition { get; }
    public string ToDisplayRef { get; }
    public PixelPoint ToPixelPosition { get; }
    public string ToSlotId { get; }
    public string FrozenTargetDisplayRef { get; }
    public PixelPoint FrozenTargetPixelPosition { get; }
    public string FrozenTargetSlotId { get; }

    internal DesktopWorkspaceDynamicMove(
        int decisionIndex,
        string stableId,
        string displayName,
        DesktopWorkspaceDynamicMoveKind kind,
        string fromDisplayRef,
        PixelPoint fromPixelPosition,
        string toDisplayRef,
        PixelPoint toPixelPosition,
        string toSlotId,
        string frozenTargetDisplayRef,
        PixelPoint frozenTargetPixelPosition,
        string frozenTargetSlotId)
    {
        if (decisionIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(decisionIndex));
        if (string.IsNullOrEmpty(stableId))
            throw new ArgumentException("StableId 不能为空。", nameof(stableId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("DisplayName 不能为空。", nameof(displayName));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(fromDisplayRef))
            throw new ArgumentException("FromDisplayRef 不能为空。", nameof(fromDisplayRef));
        if (string.IsNullOrWhiteSpace(toDisplayRef))
            throw new ArgumentException("ToDisplayRef 不能为空。", nameof(toDisplayRef));
        if (string.IsNullOrWhiteSpace(toSlotId))
            throw new ArgumentException("ToSlotId 不能为空。", nameof(toSlotId));
        if (string.IsNullOrWhiteSpace(frozenTargetDisplayRef))
            throw new ArgumentException("FrozenTargetDisplayRef 不能为空。", nameof(frozenTargetDisplayRef));
        if (string.IsNullOrWhiteSpace(frozenTargetSlotId))
            throw new ArgumentException("FrozenTargetSlotId 不能为空。", nameof(frozenTargetSlotId));

        DecisionIndex = decisionIndex;
        StableId = stableId;
        DisplayName = displayName.Trim();
        Kind = kind;
        FromDisplayRef = fromDisplayRef.Trim();
        FromPixelPosition = fromPixelPosition;
        ToDisplayRef = toDisplayRef.Trim();
        ToPixelPosition = toPixelPosition;
        ToSlotId = toSlotId.Trim();
        FrozenTargetDisplayRef = frozenTargetDisplayRef.Trim();
        FrozenTargetPixelPosition = frozenTargetPixelPosition;
        FrozenTargetSlotId = frozenTargetSlotId.Trim();
    }
}

public sealed record DesktopWorkspaceDynamicNextStepResult
{
    private readonly IReadOnlyList<DesktopWorkspaceDynamicNextStepIssue> _issues;

    public DesktopWorkspaceDynamicNextStepStatus Status { get; }
    public DesktopWorkspaceDynamicMove? Move { get; }
    public IReadOnlyList<DesktopWorkspaceDynamicNextStepIssue> Issues => _issues;
    public int SatisfiedMovingGoalCount { get; }
    public int TotalMovingGoalCount { get; }

    public bool IsComplete => Status == DesktopWorkspaceDynamicNextStepStatus.Complete;
    public bool IsMoveReady => Status == DesktopWorkspaceDynamicNextStepStatus.MoveReady && Move is not null;

    private DesktopWorkspaceDynamicNextStepResult(
        DesktopWorkspaceDynamicNextStepStatus status,
        DesktopWorkspaceDynamicMove? move,
        IEnumerable<DesktopWorkspaceDynamicNextStepIssue>? issues,
        int satisfiedMovingGoalCount,
        int totalMovingGoalCount)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (satisfiedMovingGoalCount < 0 || totalMovingGoalCount < 0 ||
            satisfiedMovingGoalCount > totalMovingGoalCount)
            throw new ArgumentOutOfRangeException(nameof(satisfiedMovingGoalCount));

        var issueArray = (issues ?? Array.Empty<DesktopWorkspaceDynamicNextStepIssue>()).ToArray();
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("Issues 不能包含 null。", nameof(issues));
        if (status == DesktopWorkspaceDynamicNextStepStatus.MoveReady && move is null)
            throw new ArgumentException("MoveReady 必须提供 Move。", nameof(move));
        if (status != DesktopWorkspaceDynamicNextStepStatus.MoveReady && move is not null)
            throw new ArgumentException("只有 MoveReady 可以提供 Move。", nameof(move));
        if (status == DesktopWorkspaceDynamicNextStepStatus.Blocked && issueArray.Length == 0)
            throw new ArgumentException("Blocked 至少需要一个 issue。", nameof(issues));
        if (status != DesktopWorkspaceDynamicNextStepStatus.Blocked && issueArray.Length != 0)
            throw new ArgumentException("Complete / MoveReady 不得携带 issue。", nameof(issues));

        Status = status;
        Move = move;
        _issues = Array.AsReadOnly(issueArray);
        SatisfiedMovingGoalCount = satisfiedMovingGoalCount;
        TotalMovingGoalCount = totalMovingGoalCount;
    }

    internal static DesktopWorkspaceDynamicNextStepResult Complete(int satisfied, int total)
        => new(DesktopWorkspaceDynamicNextStepStatus.Complete, null, null, satisfied, total);

    internal static DesktopWorkspaceDynamicNextStepResult Ready(
        DesktopWorkspaceDynamicMove move,
        int satisfied,
        int total)
        => new(DesktopWorkspaceDynamicNextStepStatus.MoveReady, move, null, satisfied, total);

    internal static DesktopWorkspaceDynamicNextStepResult Blocked(
        int satisfied,
        int total,
        params DesktopWorkspaceDynamicNextStepIssue[] issues)
        => new(DesktopWorkspaceDynamicNextStepStatus.Blocked, null, issues, satisfied, total);
}

/// <summary>
/// 已确认 Frozen Preview + 每一步 fresh Windows facts → 恰好一个下一步 move。
///
/// 与 v1 一次性 ExecutionPlan 不同，这里只冻结最终目标，不冻结整个执行期间的 From 位置。
/// Explorer 若在前序 Writer 后被动改变仍待移动项目的位置，下一次决策会直接采用新的 fresh From；
/// 但 Preserve / Keep / Exclude / 原本无需移动的项目若漂移，仍然 fail-closed。
/// </summary>
public static class DesktopWorkspaceDynamicNextStepPlanner
{
    public static DesktopWorkspaceDynamicNextStepResult Decide(
        DesktopWorkspaceFrozenPreview preview,
        ExplorerWorkspaceGeometry freshGeometry,
        IEnumerable<DesktopWorkspaceCompilationItemFact> freshItemFacts,
        int decisionIndex)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(freshGeometry);
        ArgumentNullException.ThrowIfNull(freshItemFacts);
        if (decisionIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(decisionIndex));

        var movingGoals = preview.Items
            .Where(static item => item.IsPositionChange)
            .OrderBy(item => ItemOrder(preview.ExactPlan, item.StableId))
            .ThenBy(static item => item.StableId, StringComparer.Ordinal)
            .ToArray();
        var totalMoving = movingGoals.Length;

        if (!string.Equals(
                freshGeometry.GeometryFingerprint,
                preview.ExactPlan.GeometryFingerprint,
                StringComparison.Ordinal))
        {
            return DesktopWorkspaceDynamicNextStepResult.Blocked(
                0,
                totalMoving,
                new DesktopWorkspaceDynamicNextStepIssue(
                    DesktopWorkspaceDynamicNextStepIssueCode.GeometryChanged,
                    "Explorer geometry 已不同于 Frozen Preview，Dynamic execution 不会重新编译新的最终目标。"));
        }

        var factArray = freshItemFacts.ToArray();
        if (factArray.Any(static fact => fact is null))
        {
            return DesktopWorkspaceDynamicNextStepResult.Blocked(
                0,
                totalMoving,
                new DesktopWorkspaceDynamicNextStepIssue(
                    DesktopWorkspaceDynamicNextStepIssueCode.ItemSetChanged,
                    "fresh item facts 包含 null。"));
        }

        var duplicateId = factArray
            .GroupBy(static fact => fact.StableId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() != 1)?.Key;
        if (duplicateId is not null)
        {
            return DesktopWorkspaceDynamicNextStepResult.Blocked(
                0,
                totalMoving,
                new DesktopWorkspaceDynamicNextStepIssue(
                    DesktopWorkspaceDynamicNextStepIssueCode.ItemSetChanged,
                    $"fresh item facts 包含重复 StableId：{duplicateId}。",
                    duplicateId));
        }

        var previewIds = preview.Items.Select(static item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var freshIds = factArray.Select(static item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        if (!previewIds.SetEquals(freshIds))
        {
            return DesktopWorkspaceDynamicNextStepResult.Blocked(
                0,
                totalMoving,
                new DesktopWorkspaceDynamicNextStepIssue(
                    DesktopWorkspaceDynamicNextStepIssueCode.ItemSetChanged,
                    "执行期间桌面项目集合发生变化。"));
        }

        var freshById = factArray.ToDictionary(static fact => fact.StableId, StringComparer.Ordinal);
        foreach (var previewItem in preview.Items)
        {
            var fresh = freshById[previewItem.StableId];
            if (fresh.IsMovable != previewItem.IsMovable)
            {
                return DesktopWorkspaceDynamicNextStepResult.Blocked(
                    0,
                    totalMoving,
                    new DesktopWorkspaceDynamicNextStepIssue(
                        DesktopWorkspaceDynamicNextStepIssueCode.MovabilityChanged,
                        $"项目 \"{previewItem.DisplayName}\" 的 movable 状态在执行期间发生变化。",
                        previewItem.StableId,
                        fresh.CurrentDisplayRef));
            }
        }

        var displayByRef = freshGeometry.Displays.ToDictionary(
            static display => display.DisplayRef,
            StringComparer.Ordinal);

        foreach (var item in preview.Items)
        {
            var final = FinalPosition(item);
            if (final is null)
            {
                return DesktopWorkspaceDynamicNextStepResult.Blocked(
                    0,
                    totalMoving,
                    new DesktopWorkspaceDynamicNextStepIssue(
                        DesktopWorkspaceDynamicNextStepIssueCode.InvalidFrozenGoal,
                        $"项目 \"{item.DisplayName}\" 缺少 Frozen final position。",
                        item.StableId));
            }

            if (!displayByRef.TryGetValue(final.Value.DisplayRef, out var targetDisplay))
            {
                return DesktopWorkspaceDynamicNextStepResult.Blocked(
                    0,
                    totalMoving,
                    new DesktopWorkspaceDynamicNextStepIssue(
                        DesktopWorkspaceDynamicNextStepIssueCode.InvalidFrozenGoal,
                        $"项目 \"{item.DisplayName}\" 的 Frozen final display 已不存在。",
                        item.StableId,
                        final.Value.DisplayRef));
            }

            var finalRect = DesktopIconLayoutGeometry.CreateRectChecked(
                final.Value.PixelPosition,
                freshGeometry.Spacing);
            if (!DesktopIconLayoutGeometry.IsFullyContained(finalRect, targetDisplay.WorkArea))
            {
                return DesktopWorkspaceDynamicNextStepResult.Blocked(
                    0,
                    totalMoving,
                    new DesktopWorkspaceDynamicNextStepIssue(
                        DesktopWorkspaceDynamicNextStepIssueCode.TargetOutsideWorkArea,
                        $"项目 \"{item.DisplayName}\" 的 Frozen final target 已越出当前 WorkArea。",
                        item.StableId,
                        final.Value.DisplayRef));
            }
        }

        // 用户没有授权改变这些项目的最终位置。它们如果被动漂移，必须停机而不是顺手“修”。
        foreach (var item in preview.Items.Where(static item => !item.IsPositionChange))
        {
            var fresh = freshById[item.StableId];
            var final = FinalPosition(item)!.Value;
            if (!string.Equals(fresh.CurrentDisplayRef, final.DisplayRef, StringComparison.Ordinal) ||
                fresh.PixelPosition != final.PixelPosition)
            {
                return DesktopWorkspaceDynamicNextStepResult.Blocked(
                    0,
                    totalMoving,
                    new DesktopWorkspaceDynamicNextStepIssue(
                        DesktopWorkspaceDynamicNextStepIssueCode.StationaryItemDrifted,
                        $"本来不应移动的项目 \"{item.DisplayName}\" 在执行期间发生位置变化。",
                        item.StableId,
                        fresh.CurrentDisplayRef));
            }
        }

        var satisfied = movingGoals.Count(item => IsAtFrozenTarget(item, freshById[item.StableId]));
        if (satisfied == totalMoving)
            return DesktopWorkspaceDynamicNextStepResult.Complete(satisfied, totalMoving);

        foreach (var item in movingGoals)
        {
            var fresh = freshById[item.StableId];
            if (IsAtFrozenTarget(item, fresh))
                continue;

            var target = item.TargetPixelPosition!.Value;
            if (!IsOccupiedByOther(item.StableId, target, factArray, freshGeometry.Spacing))
            {
                return DesktopWorkspaceDynamicNextStepResult.Ready(
                    new DesktopWorkspaceDynamicMove(
                        decisionIndex,
                        item.StableId,
                        item.DisplayName,
                        DesktopWorkspaceDynamicMoveKind.MoveToFrozenTarget,
                        fresh.CurrentDisplayRef,
                        fresh.PixelPosition,
                        item.TargetDisplayRef!,
                        target,
                        item.TargetSlotId!,
                        item.TargetDisplayRef!,
                        target,
                        item.TargetSlotId!),
                    satisfied,
                    totalMoving);
            }
        }

        var stageItem = movingGoals.First(item => !IsAtFrozenTarget(item, freshById[item.StableId]));
        var staging = FindSafeStagingSlot(
            preview,
            freshGeometry,
            factArray,
            stageItem.StableId,
            freshById[stageItem.StableId]);
        if (staging is null)
        {
            return DesktopWorkspaceDynamicNextStepResult.Blocked(
                satisfied,
                totalMoving,
                new DesktopWorkspaceDynamicNextStepIssue(
                    DesktopWorkspaceDynamicNextStepIssueCode.NoSafeStagingSlot,
                    $"无法为 \"{stageItem.DisplayName}\" 找到不污染 Preserve / Reserved / Frozen final targets 的安全 native staging slot。",
                    stageItem.StableId));
        }

        return DesktopWorkspaceDynamicNextStepResult.Ready(
            new DesktopWorkspaceDynamicMove(
                decisionIndex,
                stageItem.StableId,
                stageItem.DisplayName,
                DesktopWorkspaceDynamicMoveKind.MoveToTemporaryStaging,
                freshById[stageItem.StableId].CurrentDisplayRef,
                freshById[stageItem.StableId].PixelPosition,
                staging.Value.DisplayRef,
                staging.Value.Slot.PixelPosition,
                staging.Value.Slot.SlotId,
                stageItem.TargetDisplayRef!,
                stageItem.TargetPixelPosition!.Value,
                stageItem.TargetSlotId!),
            satisfied,
            totalMoving);
    }

    private static bool IsAtFrozenTarget(
        DesktopWorkspaceFrozenPreviewItem item,
        DesktopWorkspaceCompilationItemFact fresh)
        => item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place &&
           item.TargetDisplayRef is not null &&
           item.TargetPixelPosition.HasValue &&
           string.Equals(fresh.CurrentDisplayRef, item.TargetDisplayRef, StringComparison.Ordinal) &&
           fresh.PixelPosition == item.TargetPixelPosition.Value;

    private static (string DisplayRef, PixelPoint PixelPosition)? FinalPosition(
        DesktopWorkspaceFrozenPreviewItem item)
    {
        if (item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place)
        {
            if (item.TargetDisplayRef is null || !item.TargetPixelPosition.HasValue)
                return null;
            return (item.TargetDisplayRef, item.TargetPixelPosition.Value);
        }

        return (item.SourceDisplayRef, item.SourcePixelPosition);
    }

    private static bool IsOccupiedByOther(
        string movingStableId,
        PixelPoint target,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact> facts,
        PixelPoint spacing)
    {
        var targetRect = DesktopIconLayoutGeometry.CreateRectChecked(target, spacing);
        foreach (var fact in facts)
        {
            if (string.Equals(fact.StableId, movingStableId, StringComparison.Ordinal))
                continue;
            var currentRect = DesktopIconLayoutGeometry.CreateRectChecked(fact.PixelPosition, spacing);
            if (DesktopIconLayoutGeometry.Overlaps(targetRect, currentRect))
                return true;
        }
        return false;
    }

    private static DynamicStagingSlot? FindSafeStagingSlot(
        DesktopWorkspaceFrozenPreview preview,
        ExplorerWorkspaceGeometry geometry,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact> freshFacts,
        string movingStableId,
        DesktopWorkspaceCompilationItemFact movingFact)
    {
        var policyByDisplay = preview.Displays.ToDictionary(
            static display => display.DisplayRef,
            static display => display.Policy,
            StringComparer.Ordinal);
        var reservedByDisplay = preview.Displays.ToDictionary(
            static display => display.DisplayRef,
            static display => display.ReservedRegions,
            StringComparer.Ordinal);
        var finalRects = preview.Items.ToDictionary(
            static item => item.StableId,
            item => DesktopIconLayoutGeometry.CreateRectChecked(
                FinalPosition(item)!.Value.PixelPosition,
                geometry.Spacing),
            StringComparer.Ordinal);

        foreach (var display in geometry.Displays
                     .Where(display => policyByDisplay[display.DisplayRef] == DesktopWorkspaceDisplayPolicyKind.Design)
                     .OrderBy(static display => display.IsPrimary ? 0 : 1)
                     .ThenBy(static display => display.DisplayRef, StringComparer.Ordinal))
        {
            foreach (var slot in display.Slots
                         .OrderBy(static slot => slot.Row)
                         .ThenBy(static slot => slot.Column)
                         .ThenBy(static slot => slot.SlotId, StringComparer.Ordinal))
            {
                if (string.Equals(display.DisplayRef, movingFact.CurrentDisplayRef, StringComparison.Ordinal) &&
                    slot.PixelPosition == movingFact.PixelPosition)
                    continue;
                if (IsInsideReservedRegion(slot, reservedByDisplay[display.DisplayRef]))
                    continue;

                var candidateRect = DesktopIconLayoutGeometry.CreateRectChecked(
                    slot.PixelPosition,
                    geometry.Spacing);
                if (finalRects.Values.Any(finalRect =>
                        DesktopIconLayoutGeometry.Overlaps(candidateRect, finalRect)))
                    continue;

                var occupied = false;
                foreach (var fact in freshFacts)
                {
                    if (string.Equals(fact.StableId, movingStableId, StringComparison.Ordinal))
                        continue;
                    var currentRect = DesktopIconLayoutGeometry.CreateRectChecked(
                        fact.PixelPosition,
                        geometry.Spacing);
                    if (DesktopIconLayoutGeometry.Overlaps(candidateRect, currentRect))
                    {
                        occupied = true;
                        break;
                    }
                }
                if (occupied)
                    continue;

                return new DynamicStagingSlot(display.DisplayRef, slot);
            }
        }

        return null;
    }

    private static bool IsInsideReservedRegion(
        ExplorerNativeSlot slot,
        IReadOnlyList<CompiledWorkspaceReservedRegion> reservedRegions)
        => reservedRegions.Any(region =>
            slot.Column >= region.NativeRect.FirstColumn &&
            slot.Column < region.NativeRect.ExclusiveColumn &&
            slot.Row >= region.NativeRect.FirstRow &&
            slot.Row < region.NativeRect.ExclusiveRow);

    private static int ItemOrder(CompiledWorkspacePlan plan, string stableId)
    {
        for (var index = 0; index < plan.Items.Count; index++)
        {
            if (string.Equals(plan.Items[index].StableId, stableId, StringComparison.Ordinal))
                return index;
        }
        return int.MaxValue;
    }

    private readonly record struct DynamicStagingSlot(string DisplayRef, ExplorerNativeSlot Slot);
}
