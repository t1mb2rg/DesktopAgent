using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// Observation-first 的跨重启恢复对账器。
/// 只比较“当前真实桌面”与持久化的 Original/Desired，不根据 Attempts 或完成索引猜测过去发生了什么。
/// </summary>
public static class RecoveryReconciler
{
    public static RecoveryReconciliationResult Reconcile(
        TargetDisplayLayoutExecutionPlan executionPlan,
        DesktopIconReadResult observedDesktop)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(observedDesktop);

        if (executionPlan.UsesTemporaryStaging)
        {
            return NeedsAttention(
                Array.Empty<RecoveryReconciliationItem>(),
                new RecoveryReconciliationIssue(
                    RecoveryReconciliationIssueCode.TemporaryStagingUnsupported,
                    null,
                    "Observation-first recovery R1 暂不支持含临时中转的执行计划。"));
        }

        var duplicatePlanIds = executionPlan.Steps
            .GroupBy(s => s.StableId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        if (duplicatePlanIds.Length > 0)
        {
            var issues = duplicatePlanIds
                .Select(id => new RecoveryReconciliationIssue(
                    RecoveryReconciliationIssueCode.DuplicatePlanStableId,
                    id,
                    $"ExecutionPlan 中 StableId \"{id}\" 出现多次，无法建立唯一 Original/Desired 对。"))
                .ToArray();
            return new RecoveryReconciliationResult(
                RecoveryReconciliationState.NeedsAttention,
                Array.Empty<RecoveryReconciliationItem>(),
                issues);
        }

        if (observedDesktop.FailedItemCount != 0)
        {
            var items = executionPlan.Steps
                .Select(step => new RecoveryReconciliationItem(
                    step.StableId,
                    step.FromPosition,
                    step.ToPosition,
                    null,
                    RecoveryItemRelation.ObservationUnavailable))
                .ToArray();

            return NeedsAttention(
                items,
                new RecoveryReconciliationIssue(
                    RecoveryReconciliationIssueCode.IncompleteDesktopRead,
                    null,
                    $"当前桌面读取不完整：FailedItemCount={observedDesktop.FailedItemCount}。"));
        }

        var observedByStableId = observedDesktop.Icons
            .GroupBy(icon => icon.StableId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var itemsResult = new List<RecoveryReconciliationItem>(executionPlan.Steps.Count);
        var issuesResult = new List<RecoveryReconciliationIssue>();

        foreach (var step in executionPlan.Steps)
        {
            if (!observedByStableId.TryGetValue(step.StableId, out var matches) || matches.Length == 0)
            {
                itemsResult.Add(new RecoveryReconciliationItem(
                    step.StableId,
                    step.FromPosition,
                    step.ToPosition,
                    null,
                    RecoveryItemRelation.Missing));
                issuesResult.Add(new RecoveryReconciliationIssue(
                    RecoveryReconciliationIssueCode.MissingObservedStableId,
                    step.StableId,
                    $"当前桌面中未找到 StableId \"{step.StableId}\"。"));
                continue;
            }

            if (matches.Length > 1)
            {
                itemsResult.Add(new RecoveryReconciliationItem(
                    step.StableId,
                    step.FromPosition,
                    step.ToPosition,
                    null,
                    RecoveryItemRelation.Ambiguous));
                issuesResult.Add(new RecoveryReconciliationIssue(
                    RecoveryReconciliationIssueCode.DuplicateObservedStableId,
                    step.StableId,
                    $"当前桌面中 StableId \"{step.StableId}\" 出现 {matches.Length} 次，观察结果不唯一。"));
                continue;
            }

            var observedPosition = matches[0].PixelPosition;
            var relation = observedPosition == step.ToPosition
                ? RecoveryItemRelation.AtDesired
                : observedPosition == step.FromPosition
                    ? RecoveryItemRelation.AtOriginal
                    : RecoveryItemRelation.Elsewhere;

            itemsResult.Add(new RecoveryReconciliationItem(
                step.StableId,
                step.FromPosition,
                step.ToPosition,
                observedPosition,
                relation));

            if (relation == RecoveryItemRelation.Elsewhere)
            {
                issuesResult.Add(new RecoveryReconciliationIssue(
                    RecoveryReconciliationIssueCode.PositionOutsideKnownEndpoints,
                    step.StableId,
                    $"StableId \"{step.StableId}\" 当前位于 {observedPosition}，既不是 Original={step.FromPosition} 也不是 Desired={step.ToPosition}。"));
            }
        }

        var state = DeriveState(itemsResult);
        return new RecoveryReconciliationResult(state, itemsResult, issuesResult);
    }

    private static RecoveryReconciliationState DeriveState(
        IReadOnlyList<RecoveryReconciliationItem> items)
    {
        if (items.Count == 0)
            return RecoveryReconciliationState.NoWork;

        if (items.Any(item => item.Relation is
            RecoveryItemRelation.Elsewhere or
            RecoveryItemRelation.Missing or
            RecoveryItemRelation.Ambiguous or
            RecoveryItemRelation.ObservationUnavailable))
        {
            return RecoveryReconciliationState.NeedsAttention;
        }

        if (items.All(item => item.Relation == RecoveryItemRelation.AtDesired))
            return RecoveryReconciliationState.Committed;

        if (items.All(item => item.Relation == RecoveryItemRelation.AtOriginal))
            return RecoveryReconciliationState.Restored;

        return RecoveryReconciliationState.MixedKnown;
    }

    private static RecoveryReconciliationResult NeedsAttention(
        IReadOnlyList<RecoveryReconciliationItem> items,
        RecoveryReconciliationIssue issue)
        => new(
            RecoveryReconciliationState.NeedsAttention,
            items,
            new[] { issue });
}
