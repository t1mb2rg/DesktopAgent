using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 把当前真实桌面与 ExecutionPlan 的所有合法执行前缀做精确比较。
///
/// 本类只接受当前观察、SourceRestorePlan 和 ExecutionPlan；不读取 Attempts、
/// Completed* indexes 或 Lifecycle，因此不会根据 Journal 猜测“执行到了哪一步”。
/// 对 temporary staging 来说，StagingPosition 是计划明确授权的中间事实，只有当前桌面
/// 唯一匹配某个完整前缀时，才认为该执行进度可证明。
/// </summary>
public static class RecoveryExecutionPrefixReconciler
{
    public static RecoveryExecutionPrefixReconciliationResult Reconcile(
        TargetDisplayLayoutRestorePlan sourceRestorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan,
        DesktopIconReadResult observedDesktop)
    {
        ArgumentNullException.ThrowIfNull(sourceRestorePlan);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(observedDesktop);

        var validationIssues = ValidateAndBuildPrefixes(
            sourceRestorePlan,
            executionPlan,
            out var relevantStableIds,
            out var prefixes);

        if (validationIssues.Count > 0)
            return NeedsAttention(validationIssues);

        if (observedDesktop.FailedItemCount != 0)
        {
            return NeedsAttention(new[]
            {
                new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.IncompleteDesktopRead,
                    null,
                    $"当前桌面读取不完整：FailedItemCount={observedDesktop.FailedItemCount}。")
            });
        }

        var observedByStableId = observedDesktop.Icons
            .Where(icon => relevantStableIds.Contains(icon.StableId))
            .GroupBy(icon => icon.StableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var observationIssues = new List<RecoveryExecutionPrefixIssue>();
        var observedFacts = new Dictionary<string, PrefixIconFact>(StringComparer.Ordinal);

        foreach (var stableId in relevantStableIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!observedByStableId.TryGetValue(stableId, out var matches) || matches.Length == 0)
            {
                observationIssues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.MissingObservedStableId,
                    stableId,
                    $"当前桌面中未找到计划相关 StableId “{stableId}”。"));
                continue;
            }

            if (matches.Length != 1)
            {
                observationIssues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.DuplicateObservedStableId,
                    stableId,
                    $"当前桌面中 StableId “{stableId}”出现 {matches.Length} 次，无法建立唯一当前事实。"));
                continue;
            }

            observedFacts[stableId] = new PrefixIconFact(
                matches[0].PixelPosition,
                matches[0].MonitorDeviceName);
        }

        if (observationIssues.Count > 0)
            return NeedsAttention(observationIssues);

        var matchedPrefixes = new List<int>();
        for (var prefixIndex = 0; prefixIndex < prefixes.Count; prefixIndex++)
        {
            if (PrefixMatches(prefixes[prefixIndex], observedFacts))
                matchedPrefixes.Add(prefixIndex);
        }

        if (matchedPrefixes.Count == 0)
        {
            return NeedsAttention(new[]
            {
                new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.NoMatchingPrefix,
                    null,
                    "当前真实桌面不匹配 ExecutionPlan 的任何合法执行前缀；拒绝根据 Journal 猜测历史。")
            });
        }

        if (matchedPrefixes.Count > 1)
        {
            return NeedsAttention(new[]
            {
                new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.AmbiguousPrefixMatch,
                    null,
                    $"当前真实桌面同时匹配多个执行前缀：{string.Join(", ", matchedPrefixes)}；无法唯一证明执行进度。")
            });
        }

        var appliedStepCount = matchedPrefixes[0];
        var state = executionPlan.Steps.Count == 0
            ? RecoveryExecutionPrefixState.NoWork
            : appliedStepCount == 0
                ? RecoveryExecutionPrefixState.Original
                : appliedStepCount == executionPlan.Steps.Count
                    ? RecoveryExecutionPrefixState.Committed
                    : RecoveryExecutionPrefixState.Intermediate;

        return new RecoveryExecutionPrefixReconciliationResult(
            state,
            appliedStepCount,
            Array.Empty<RecoveryExecutionPrefixIssue>());
    }

    private static IReadOnlyList<RecoveryExecutionPrefixIssue> ValidateAndBuildPrefixes(
        TargetDisplayLayoutRestorePlan sourceRestorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan,
        out HashSet<string> relevantStableIds,
        out IReadOnlyList<IReadOnlyDictionary<string, PrefixIconFact>> prefixes)
    {
        relevantStableIds = new HashSet<string>(StringComparer.Ordinal);
        prefixes = Array.Empty<IReadOnlyDictionary<string, PrefixIconFact>>();
        var issues = new List<RecoveryExecutionPrefixIssue>();

        if (!executionPlan.IsExecutable)
        {
            issues.Add(new RecoveryExecutionPrefixIssue(
                RecoveryExecutionPrefixIssueCode.PlanNotExecutable,
                null,
                "ExecutionPlan.IsExecutable 为 false，不能建立恢复前缀。"));
            return issues;
        }

        if (!string.Equals(
                sourceRestorePlan.TargetDeviceName,
                executionPlan.TargetDeviceName,
                StringComparison.Ordinal))
        {
            issues.Add(new RecoveryExecutionPrefixIssue(
                RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                null,
                "SourceRestorePlan 与 ExecutionPlan 的 TargetDeviceName 不一致（Ordinal）。"));
        }

        if (sourceRestorePlan.RequiresTemporaryStaging != executionPlan.UsesTemporaryStaging)
        {
            issues.Add(new RecoveryExecutionPrefixIssue(
                RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                null,
                $"SourceRestorePlan.RequiresTemporaryStaging={sourceRestorePlan.RequiresTemporaryStaging}，" +
                $"但 ExecutionPlan.UsesTemporaryStaging={executionPlan.UsesTemporaryStaging}。"));
        }

        var moveItems = sourceRestorePlan.Items
            .Where(item => item.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
            .ToArray();

        var itemByStableId = new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(StringComparer.Ordinal);
        foreach (var item in moveItems)
        {
            if (string.IsNullOrWhiteSpace(item.StableId))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.SourcePlanInconsistent,
                    item.StableId,
                    "SourceRestorePlan 存在空白 StableId 的 MoveRequired item。"));
                continue;
            }

            if (!itemByStableId.TryAdd(item.StableId, item))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.SourcePlanInconsistent,
                    item.StableId,
                    $"SourceRestorePlan 中 MoveRequired StableId “{item.StableId}”重复。"));
                continue;
            }

            relevantStableIds.Add(item.StableId);

            if (item.CurrentPosition is null)
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.SourcePlanInconsistent,
                    item.StableId,
                    $"MoveRequired “{item.StableId}”缺少 CurrentPosition，无法建立 prefix 0。"));
            }

            if (string.IsNullOrWhiteSpace(item.CurrentMonitorDeviceName))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.SourcePlanInconsistent,
                    item.StableId,
                    $"MoveRequired “{item.StableId}”缺少 CurrentMonitorDeviceName，无法建立 prefix 0。"));
            }
        }

        if (executionPlan.IsNoOp)
        {
            if (executionPlan.Steps.Count != 0 || moveItems.Length != 0)
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    null,
                    "IsNoOp 计划必须同时满足 Steps.Count=0 且 MoveRequiredCount=0。"));
            }
        }
        else if (executionPlan.Steps.Count == 0)
        {
            issues.Add(new RecoveryExecutionPrefixIssue(
                RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                null,
                "可执行非 NoOp 计划的 Steps 不能为空。"));
        }

        var steppedStableIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < executionPlan.Steps.Count; i++)
        {
            var step = executionPlan.Steps[i];
            if (step.StepIndex != i)
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    step.StableId,
                    $"Steps[{i}].StepIndex={step.StepIndex}，预期 {i}。"));
            }

            if (string.IsNullOrWhiteSpace(step.StableId))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    step.StableId,
                    $"Steps[{i}] StableId 为空白。"));
                continue;
            }

            if (!itemByStableId.ContainsKey(step.StableId))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    step.StableId,
                    $"Steps[{i}] StableId “{step.StableId}”不能唯一映射到 MoveRequired item。"));
            }
            else
            {
                steppedStableIds.Add(step.StableId);
            }

            if (step.FromPosition == step.ToPosition)
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    step.StableId,
                    $"Steps[{i}] FromPosition == ToPosition。"));
            }

            if (string.IsNullOrWhiteSpace(step.ExpectedMonitorDeviceName))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    step.StableId,
                    $"Steps[{i}] 缺少 ExpectedMonitorDeviceName。"));
            }

            if (!Enum.IsDefined(step.Kind))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    step.StableId,
                    $"Steps[{i}] 包含未知 StepKind：{step.Kind}。"));
            }
        }

        foreach (var item in moveItems)
        {
            if (!steppedStableIds.Contains(item.StableId))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    item.StableId,
                    $"MoveRequired “{item.StableId}”没有任何执行步骤。"));
            }
        }

        ValidateStagingAssignments(executionPlan, issues);

        if (issues.Count > 0)
            return issues;

        var current = moveItems.ToDictionary(
            item => item.StableId,
            item => new PrefixIconFact(item.CurrentPosition!.Value, item.CurrentMonitorDeviceName),
            StringComparer.Ordinal);

        var builtPrefixes = new List<IReadOnlyDictionary<string, PrefixIconFact>>
        {
            Freeze(current)
        };

        for (var i = 0; i < executionPlan.Steps.Count; i++)
        {
            var step = executionPlan.Steps[i];
            var fact = current[step.StableId];

            if (fact.Position != step.FromPosition)
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    step.StableId,
                    $"Steps[{i}] FromPosition={step.FromPosition}，但 prefix {i} 中当前事实为 {fact.Position}。"));
                continue;
            }

            current[step.StableId] = new PrefixIconFact(
                step.ToPosition,
                step.ExpectedMonitorDeviceName);
            builtPrefixes.Add(Freeze(current));
        }

        if (issues.Count == 0)
        {
            foreach (var item in moveItems)
            {
                var finalFact = current[item.StableId];
                if (finalFact.Position != item.SnapshotTargetPosition
                    || !string.Equals(
                        finalFact.MonitorDeviceName,
                        sourceRestorePlan.TargetDeviceName,
                        StringComparison.Ordinal))
                {
                    issues.Add(new RecoveryExecutionPrefixIssue(
                        RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                        item.StableId,
                        $"执行完全部步骤后 “{item.StableId}” 的事实 " +
                        $"({finalFact.Position.X},{finalFact.Position.Y}, {finalFact.MonitorDeviceName ?? "<null>"}) " +
                        $"不等于目标 ({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y}, " +
                        $"{sourceRestorePlan.TargetDeviceName})。"));
                }
            }
        }

        if (issues.Count > 0)
            return issues;

        prefixes = Array.AsReadOnly(builtPrefixes.ToArray());
        return issues;
    }

    private static void ValidateStagingAssignments(
        TargetDisplayLayoutExecutionPlan executionPlan,
        ICollection<RecoveryExecutionPrefixIssue> issues)
    {
        var enterSteps = executionPlan.Steps
            .Where(step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging)
            .ToArray();
        var exitSteps = executionPlan.Steps
            .Where(step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget)
            .ToArray();

        if (!executionPlan.UsesTemporaryStaging)
        {
            if (executionPlan.StagingAssignments.Count != 0 || exitSteps.Length != 0)
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    null,
                    "非 staging ExecutionPlan 不得包含 StagingAssignments 或 staging exit step。"));
            }
            return;
        }

        if (enterSteps.Length != executionPlan.StagingAssignments.Count)
        {
            issues.Add(new RecoveryExecutionPrefixIssue(
                RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                null,
                $"staging enter steps 数量 {enterSteps.Length} 与 StagingAssignments 数量 " +
                $"{executionPlan.StagingAssignments.Count} 不一致。"));
        }

        if (exitSteps.Length != executionPlan.StagingAssignments.Count)
        {
            issues.Add(new RecoveryExecutionPrefixIssue(
                RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                null,
                $"staging exit steps 数量 {exitSteps.Length} 与 StagingAssignments 数量 " +
                $"{executionPlan.StagingAssignments.Count} 不一致。"));
        }

        var assignmentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in executionPlan.StagingAssignments)
        {
            if (!assignmentIds.Add(assignment.StableId))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    assignment.StableId,
                    $"StagingAssignments 中 StableId “{assignment.StableId}”重复。"));
                continue;
            }

            if (assignment.EnterStepIndex < 0
                || assignment.ExitStepIndex < 0
                || assignment.EnterStepIndex >= executionPlan.Steps.Count
                || assignment.ExitStepIndex >= executionPlan.Steps.Count
                || assignment.EnterStepIndex >= assignment.ExitStepIndex)
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    assignment.StableId,
                    $"StagingAssignment “{assignment.StableId}”的 Enter/Exit index 非法：" +
                    $"{assignment.EnterStepIndex}/{assignment.ExitStepIndex}。"));
                continue;
            }

            var enter = executionPlan.Steps[assignment.EnterStepIndex];
            var exit = executionPlan.Steps[assignment.ExitStepIndex];

            if (enter.Kind != TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging
                || !string.Equals(enter.StableId, assignment.StableId, StringComparison.Ordinal)
                || enter.ToPosition != assignment.StagingPosition)
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    assignment.StableId,
                    $"StagingAssignment “{assignment.StableId}”与 enter step 不一致。"));
            }

            if (exit.Kind != TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget
                || !string.Equals(exit.StableId, assignment.StableId, StringComparison.Ordinal)
                || exit.FromPosition != assignment.StagingPosition)
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    assignment.StableId,
                    $"StagingAssignment “{assignment.StableId}”与 exit step 不一致。"));
            }
        }

        foreach (var enter in enterSteps)
        {
            if (!assignmentIds.Contains(enter.StableId))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    enter.StableId,
                    $"staging enter step “{enter.StableId}”没有对应 StagingAssignment。"));
            }
        }

        foreach (var exit in exitSteps)
        {
            if (!assignmentIds.Contains(exit.StableId))
            {
                issues.Add(new RecoveryExecutionPrefixIssue(
                    RecoveryExecutionPrefixIssueCode.ExecutionPlanInconsistent,
                    exit.StableId,
                    $"staging exit step “{exit.StableId}”没有对应 StagingAssignment。"));
            }
        }
    }

    private static bool PrefixMatches(
        IReadOnlyDictionary<string, PrefixIconFact> prefix,
        IReadOnlyDictionary<string, PrefixIconFact> observed)
    {
        if (prefix.Count != observed.Count)
            return false;

        foreach (var (stableId, expected) in prefix)
        {
            if (!observed.TryGetValue(stableId, out var actual))
                return false;
            if (expected.Position != actual.Position)
                return false;
            if (!string.Equals(
                    expected.MonitorDeviceName,
                    actual.MonitorDeviceName,
                    StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, PrefixIconFact> Freeze(
        IReadOnlyDictionary<string, PrefixIconFact> source)
        => new Dictionary<string, PrefixIconFact>(source, StringComparer.Ordinal);

    private static RecoveryExecutionPrefixReconciliationResult NeedsAttention(
        IReadOnlyList<RecoveryExecutionPrefixIssue> issues)
        => new(
            RecoveryExecutionPrefixState.NeedsAttention,
            null,
            issues);

    private sealed record PrefixIconFact(
        PixelPoint Position,
        string? MonitorDeviceName);
}
