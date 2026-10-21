using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;

public enum DesktopWorkspaceExecutionStepKind
{
    MoveToTarget = 0,
    MoveToTemporaryStaging = 1,
    MoveFromTemporaryStagingToTarget = 2,
}

public enum DesktopWorkspaceExecutionPlanIssueCode
{
    FreshCheckRejected = 0,
    InvalidFrozenPlan = 1,
    NonMovableMove = 2,
    TargetOutsideWorkArea = 3,
    FinalTargetCollision = 4,
    StationaryOccupantBlocksTarget = 5,
    NoSafeStagingSlot = 6,
    SimulationDeadlock = 7,
}

public sealed record DesktopWorkspaceExecutionPlanIssue(
    DesktopWorkspaceExecutionPlanIssueCode Code,
    string Message,
    string? StableId = null,
    string? DisplayRef = null);

/// <summary>
/// Workspace transaction 的一个精确 Writer step。
/// From / To 都冻结显示器与物理像素，因此同一份 journal 可以在失败时按完成步骤倒序恢复，
/// 不需要把多屏 Workspace 伪装成单屏 TargetDisplay restore。
/// </summary>
public sealed record DesktopWorkspaceExecutionStep
{
    public int StepIndex { get; }
    public string StableId { get; }
    public string DisplayName { get; }
    public DesktopWorkspaceExecutionStepKind Kind { get; }
    public string FromDisplayRef { get; }
    public PixelPoint FromPixelPosition { get; }
    public string ToDisplayRef { get; }
    public PixelPoint ToPixelPosition { get; }
    public string? ToSlotId { get; }

    public DesktopWorkspaceExecutionStep(
        int stepIndex,
        string stableId,
        string displayName,
        DesktopWorkspaceExecutionStepKind kind,
        string fromDisplayRef,
        PixelPoint fromPixelPosition,
        string toDisplayRef,
        PixelPoint toPixelPosition,
        string? toSlotId)
    {
        if (stepIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(stepIndex));
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

        StepIndex = stepIndex;
        StableId = stableId;
        DisplayName = displayName.Trim();
        Kind = kind;
        FromDisplayRef = fromDisplayRef.Trim();
        FromPixelPosition = fromPixelPosition;
        ToDisplayRef = toDisplayRef.Trim();
        ToPixelPosition = toPixelPosition;
        ToSlotId = toSlotId.Trim();
    }
}

/// <summary>
/// 用户确认并通过 fresh check 后形成的整个多屏 Workspace 施工计划。
/// 它是 execution preparation，不包含 Writer / Recovery store / COM authority。
/// </summary>
public sealed record DesktopWorkspaceExecutionPlan
{
    private readonly IReadOnlyList<DesktopWorkspaceExecutionStep> _steps;

    public Guid ConfirmationId { get; }
    public Guid PreviewId { get; }
    public Guid FreshObservationId { get; }
    public string PreviewFingerprint { get; }
    public string ExactPlanFingerprint { get; }
    public string ExecutionPlanFingerprint { get; }
    public IReadOnlyList<DesktopWorkspaceExecutionStep> Steps => _steps;
    public int MoveItemCount { get; }
    public bool UsesTemporaryStaging { get; }
    public bool IsNoOp => _steps.Count == 0;

    internal DesktopWorkspaceExecutionPlan(
        DesktopWorkspaceApplyConfirmation confirmation,
        Guid freshObservationId,
        string executionPlanFingerprint,
        IEnumerable<DesktopWorkspaceExecutionStep> steps,
        int moveItemCount)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (freshObservationId == Guid.Empty)
            throw new ArgumentException("FreshObservationId 不能为 Guid.Empty。", nameof(freshObservationId));
        if (string.IsNullOrWhiteSpace(executionPlanFingerprint))
            throw new ArgumentException("ExecutionPlanFingerprint 不能为空。", nameof(executionPlanFingerprint));
        ArgumentNullException.ThrowIfNull(steps);
        if (moveItemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(moveItemCount));

        var stepArray = steps.ToArray();
        if (stepArray.Any(static step => step is null))
            throw new ArgumentException("Steps 不能包含 null。", nameof(steps));
        for (var i = 0; i < stepArray.Length; i++)
        {
            if (stepArray[i].StepIndex != i)
                throw new ArgumentException("Steps 的 StepIndex 必须从 0 连续递增。", nameof(steps));
        }

        ConfirmationId = confirmation.ConfirmationId;
        PreviewId = confirmation.FrozenPreview.PreviewId;
        FreshObservationId = freshObservationId;
        PreviewFingerprint = confirmation.FrozenPreview.PreviewFingerprint;
        ExactPlanFingerprint = confirmation.FrozenPreview.ExactPlan.PlanFingerprint;
        ExecutionPlanFingerprint = executionPlanFingerprint.Trim();
        _steps = Array.AsReadOnly(stepArray);
        MoveItemCount = moveItemCount;
        UsesTemporaryStaging = stepArray.Any(static step =>
            step.Kind == DesktopWorkspaceExecutionStepKind.MoveToTemporaryStaging);
    }
}

public sealed record DesktopWorkspaceExecutionPlanResult
{
    private readonly IReadOnlyList<DesktopWorkspaceExecutionPlanIssue> _issues;

    public DesktopWorkspaceExecutionPlan? Plan { get; }
    public IReadOnlyList<DesktopWorkspaceExecutionPlanIssue> Issues => _issues;
    public bool Succeeded => Plan is not null && _issues.Count == 0;

    private DesktopWorkspaceExecutionPlanResult(
        DesktopWorkspaceExecutionPlan? plan,
        IEnumerable<DesktopWorkspaceExecutionPlanIssue> issues)
    {
        var issueArray = issues.ToArray();
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("Issues 不能包含 null。", nameof(issues));
        if ((plan is null) == (issueArray.Length == 0))
            throw new ArgumentException("ExecutionPlanResult 必须是完整 Success 或完整 Failure。 ");

        Plan = plan;
        _issues = Array.AsReadOnly(issueArray);
    }

    internal static DesktopWorkspaceExecutionPlanResult Success(DesktopWorkspaceExecutionPlan plan)
        => new(plan ?? throw new ArgumentNullException(nameof(plan)), []);

    internal static DesktopWorkspaceExecutionPlanResult Failure(
        params DesktopWorkspaceExecutionPlanIssue[] issues)
    {
        if (issues.Length == 0)
            throw new ArgumentException("Failure 至少需要一个 issue。", nameof(issues));
        return new(null, issues);
    }
}

/// <summary>
/// Frozen Preview + explicit Confirmation + fresh Windows facts → deterministic Workspace execution steps。
///
/// Planner 没有 AI / redesign authority，也没有 Writer authority。它会再次调用 FreshCheck，随后只对
/// 用户确认过的 exact targets 做碰撞模拟和必要的 native-slot staging；任何无法安全排序的情况 fail-closed。
/// </summary>
public static class DesktopWorkspaceExecutionPlanner
{
    public static DesktopWorkspaceExecutionPlanResult Create(
        DesktopWorkspaceApplyConfirmation confirmation,
        ExplorerWorkspaceGeometry freshGeometry,
        IEnumerable<DesktopWorkspaceCompilationItemFact> freshItemFacts)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(freshGeometry);
        ArgumentNullException.ThrowIfNull(freshItemFacts);

        var factArray = freshItemFacts.ToArray();
        var freshCheck = DesktopWorkspaceApplyFreshCheck.Evaluate(
            confirmation,
            freshGeometry,
            factArray);
        if (!freshCheck.IsReady)
        {
            return DesktopWorkspaceExecutionPlanResult.Failure(
                new DesktopWorkspaceExecutionPlanIssue(
                    DesktopWorkspaceExecutionPlanIssueCode.FreshCheckRejected,
                    string.Join("；", freshCheck.Issues)));
        }

        var preview = confirmation.FrozenPreview;
        var displayByRef = freshGeometry.Displays.ToDictionary(
            static display => display.DisplayRef,
            StringComparer.Ordinal);
        var factById = factArray.ToDictionary(static fact => fact.StableId, StringComparer.Ordinal);
        var previewById = preview.Items.ToDictionary(static item => item.StableId, StringComparer.Ordinal);

        var moving = preview.Items
            .Where(static item => item.IsPositionChange)
            .OrderBy(item => ItemOrder(preview.ExactPlan, item.StableId))
            .ThenBy(static item => item.StableId, StringComparer.Ordinal)
            .ToArray();

        foreach (var item in moving)
        {
            if (!item.IsMovable)
            {
                return DesktopWorkspaceExecutionPlanResult.Failure(
                    new DesktopWorkspaceExecutionPlanIssue(
                        DesktopWorkspaceExecutionPlanIssueCode.NonMovableMove,
                        $"项目 \"{item.DisplayName}\" 不可移动，但 Frozen Preview 要求改变位置。",
                        item.StableId,
                        item.SourceDisplayRef));
            }
            if (item.Disposition != DesktopWorkspaceCompiledItemDisposition.Place ||
                item.TargetDisplayRef is null ||
                item.TargetSlotId is null ||
                item.TargetPixelPosition is null)
            {
                return DesktopWorkspaceExecutionPlanResult.Failure(
                    new DesktopWorkspaceExecutionPlanIssue(
                        DesktopWorkspaceExecutionPlanIssueCode.InvalidFrozenPlan,
                        $"项目 \"{item.DisplayName}\" 的移动目标不完整。",
                        item.StableId));
            }

            var targetDisplay = displayByRef[item.TargetDisplayRef];
            var targetRect = DesktopIconLayoutGeometry.CreateRectChecked(
                item.TargetPixelPosition.Value,
                freshGeometry.Spacing);
            if (!DesktopIconLayoutGeometry.IsFullyContained(targetRect, targetDisplay.WorkArea))
            {
                return DesktopWorkspaceExecutionPlanResult.Failure(
                    new DesktopWorkspaceExecutionPlanIssue(
                        DesktopWorkspaceExecutionPlanIssueCode.TargetOutsideWorkArea,
                        $"项目 \"{item.DisplayName}\" 的 exact target 已越出当前 WorkArea。",
                        item.StableId,
                        item.TargetDisplayRef));
            }
        }

        var finalRects = BuildFinalRects(preview, freshGeometry.Spacing);
        var finalCollision = FindFinalTargetCollision(preview, finalRects);
        if (finalCollision is not null)
            return DesktopWorkspaceExecutionPlanResult.Failure(finalCollision);

        var movingIds = moving.Select(static item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var movingItem in moving)
        {
            var targetRect = finalRects[movingItem.StableId];
            foreach (var stationary in preview.Items.Where(item => !movingIds.Contains(item.StableId)))
            {
                var stationaryRect = finalRects[stationary.StableId];
                if (!DesktopIconLayoutGeometry.Overlaps(targetRect, stationaryRect))
                    continue;

                return DesktopWorkspaceExecutionPlanResult.Failure(
                    new DesktopWorkspaceExecutionPlanIssue(
                        DesktopWorkspaceExecutionPlanIssueCode.StationaryOccupantBlocksTarget,
                        $"项目 \"{movingItem.DisplayName}\" 的最终目标被不会移动的项目 \"{stationary.DisplayName}\" 占用。",
                        movingItem.StableId,
                        movingItem.TargetDisplayRef));
            }
        }

        var sim = factArray.ToDictionary(
            static fact => fact.StableId,
            static fact => new SimulatedItemState(fact.CurrentDisplayRef, fact.PixelPosition),
            StringComparer.Ordinal);
        var pending = moving.ToDictionary(static item => item.StableId, StringComparer.Ordinal);
        var staged = new HashSet<string>(StringComparer.Ordinal);
        var steps = new List<DesktopWorkspaceExecutionStep>();
        var maxSteps = Math.Max(1, moving.Length * 3);

        while (pending.Count > 0)
        {
            if (steps.Count >= maxSteps)
            {
                return DesktopWorkspaceExecutionPlanResult.Failure(
                    new DesktopWorkspaceExecutionPlanIssue(
                        DesktopWorkspaceExecutionPlanIssueCode.SimulationDeadlock,
                        "Workspace execution simulation 超过有界步骤数，已拒绝生成执行计划。"));
            }

            DesktopWorkspaceFrozenPreviewItem? direct = null;
            foreach (var item in moving.Where(item => pending.ContainsKey(item.StableId)))
            {
                var target = item.TargetPixelPosition!.Value;
                if (!IsOccupiedByOther(item.StableId, target, sim, freshGeometry.Spacing))
                {
                    direct = item;
                    break;
                }
            }

            if (direct is not null)
            {
                var current = sim[direct.StableId];
                var kind = staged.Remove(direct.StableId)
                    ? DesktopWorkspaceExecutionStepKind.MoveFromTemporaryStagingToTarget
                    : DesktopWorkspaceExecutionStepKind.MoveToTarget;
                steps.Add(new DesktopWorkspaceExecutionStep(
                    steps.Count,
                    direct.StableId,
                    direct.DisplayName,
                    kind,
                    current.DisplayRef,
                    current.PixelPosition,
                    direct.TargetDisplayRef!,
                    direct.TargetPixelPosition!.Value,
                    direct.TargetSlotId));
                sim[direct.StableId] = new SimulatedItemState(
                    direct.TargetDisplayRef!,
                    direct.TargetPixelPosition.Value);
                pending.Remove(direct.StableId);
                continue;
            }

            var stageItem = moving.FirstOrDefault(item =>
                pending.ContainsKey(item.StableId) && !staged.Contains(item.StableId));
            if (stageItem is null)
            {
                return DesktopWorkspaceExecutionPlanResult.Failure(
                    new DesktopWorkspaceExecutionPlanIssue(
                        DesktopWorkspaceExecutionPlanIssueCode.SimulationDeadlock,
                        "所有待移动项目都已进入 staging，但仍没有项目可以进入最终目标。"));
            }

            var staging = FindSafeStagingSlot(
                preview,
                freshGeometry,
                sim,
                finalRects,
                stageItem.StableId);
            if (staging is null)
            {
                return DesktopWorkspaceExecutionPlanResult.Failure(
                    new DesktopWorkspaceExecutionPlanIssue(
                        DesktopWorkspaceExecutionPlanIssueCode.NoSafeStagingSlot,
                        $"无法为 \"{stageItem.DisplayName}\" 找到不污染 Preserve / Reserved / 最终目标的安全 native staging slot。",
                        stageItem.StableId));
            }

            var from = sim[stageItem.StableId];
            steps.Add(new DesktopWorkspaceExecutionStep(
                steps.Count,
                stageItem.StableId,
                stageItem.DisplayName,
                DesktopWorkspaceExecutionStepKind.MoveToTemporaryStaging,
                from.DisplayRef,
                from.PixelPosition,
                staging.Value.DisplayRef,
                staging.Value.Slot.PixelPosition,
                staging.Value.Slot.SlotId));
            sim[stageItem.StableId] = new SimulatedItemState(
                staging.Value.DisplayRef,
                staging.Value.Slot.PixelPosition);
            staged.Add(stageItem.StableId);
        }

        foreach (var item in moving)
        {
            var final = sim[item.StableId];
            if (!string.Equals(final.DisplayRef, item.TargetDisplayRef, StringComparison.Ordinal) ||
                final.PixelPosition != item.TargetPixelPosition!.Value)
            {
                return DesktopWorkspaceExecutionPlanResult.Failure(
                    new DesktopWorkspaceExecutionPlanIssue(
                        DesktopWorkspaceExecutionPlanIssueCode.SimulationDeadlock,
                        $"项目 \"{item.DisplayName}\" 的模拟最终状态没有收敛到 Frozen Preview exact target。",
                        item.StableId));
            }
        }

        var fingerprint = ComputeFingerprint(confirmation, freshGeometry, steps);
        return DesktopWorkspaceExecutionPlanResult.Success(
            new DesktopWorkspaceExecutionPlan(
                confirmation,
                freshGeometry.ObservationId,
                fingerprint,
                steps,
                moving.Length));
    }

    private static Dictionary<string, PixelRect> BuildFinalRects(
        DesktopWorkspaceFrozenPreview preview,
        PixelPoint spacing)
    {
        var result = new Dictionary<string, PixelRect>(StringComparer.Ordinal);
        foreach (var item in preview.Items)
        {
            var finalPosition = item.Disposition == DesktopWorkspaceCompiledItemDisposition.Place
                ? item.TargetPixelPosition ?? throw new InvalidDataException("Place item 缺少 TargetPixelPosition。")
                : item.SourcePixelPosition;
            result.Add(item.StableId, DesktopIconLayoutGeometry.CreateRectChecked(finalPosition, spacing));
        }
        return result;
    }

    private static DesktopWorkspaceExecutionPlanIssue? FindFinalTargetCollision(
        DesktopWorkspaceFrozenPreview preview,
        IReadOnlyDictionary<string, PixelRect> finalRects)
    {
        var items = preview.Items.OrderBy(static item => item.StableId, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            for (var j = i + 1; j < items.Length; j++)
            {
                if (!DesktopIconLayoutGeometry.Overlaps(
                        finalRects[items[i].StableId],
                        finalRects[items[j].StableId]))
                    continue;

                if (!items[i].IsPositionChange && !items[j].IsPositionChange)
                    continue;

                return new DesktopWorkspaceExecutionPlanIssue(
                    DesktopWorkspaceExecutionPlanIssueCode.FinalTargetCollision,
                    $"Frozen Preview 的最终状态让 \"{items[i].DisplayName}\" 与 \"{items[j].DisplayName}\" 占用重叠区域。",
                    items[i].StableId);
            }
        }
        return null;
    }

    private static bool IsOccupiedByOther(
        string movingStableId,
        PixelPoint target,
        IReadOnlyDictionary<string, SimulatedItemState> simulation,
        PixelPoint spacing)
    {
        var targetRect = DesktopIconLayoutGeometry.CreateRectChecked(target, spacing);
        foreach (var (stableId, state) in simulation)
        {
            if (string.Equals(stableId, movingStableId, StringComparison.Ordinal))
                continue;
            var rect = DesktopIconLayoutGeometry.CreateRectChecked(state.PixelPosition, spacing);
            if (DesktopIconLayoutGeometry.Overlaps(targetRect, rect))
                return true;
        }
        return false;
    }

    private static StagingSlot? FindSafeStagingSlot(
        DesktopWorkspaceFrozenPreview preview,
        ExplorerWorkspaceGeometry geometry,
        IReadOnlyDictionary<string, SimulatedItemState> simulation,
        IReadOnlyDictionary<string, PixelRect> finalRects,
        string movingStableId)
    {
        var policyByDisplay = preview.Displays.ToDictionary(
            static display => display.DisplayRef,
            static display => display.Policy,
            StringComparer.Ordinal);
        var reservedByDisplay = preview.Displays.ToDictionary(
            static display => display.DisplayRef,
            static display => display.ReservedRegions,
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
                if (IsInsideReservedRegion(slot, reservedByDisplay[display.DisplayRef]))
                    continue;

                var candidateRect = DesktopIconLayoutGeometry.CreateRectChecked(
                    slot.PixelPosition,
                    geometry.Spacing);
                if (finalRects.Values.Any(finalRect =>
                        DesktopIconLayoutGeometry.Overlaps(candidateRect, finalRect)))
                    continue;

                var occupied = false;
                foreach (var (stableId, state) in simulation)
                {
                    if (string.Equals(stableId, movingStableId, StringComparison.Ordinal))
                        continue;
                    var currentRect = DesktopIconLayoutGeometry.CreateRectChecked(
                        state.PixelPosition,
                        geometry.Spacing);
                    if (DesktopIconLayoutGeometry.Overlaps(candidateRect, currentRect))
                    {
                        occupied = true;
                        break;
                    }
                }
                if (occupied)
                    continue;

                return new StagingSlot(display.DisplayRef, slot);
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
        for (var i = 0; i < plan.Items.Count; i++)
        {
            if (string.Equals(plan.Items[i].StableId, stableId, StringComparison.Ordinal))
                return i;
        }
        return int.MaxValue;
    }

    private static string ComputeFingerprint(
        DesktopWorkspaceApplyConfirmation confirmation,
        ExplorerWorkspaceGeometry geometry,
        IReadOnlyList<DesktopWorkspaceExecutionStep> steps)
    {
        var builder = new StringBuilder();
        builder.Append("preview=").Append(confirmation.FrozenPreview.PreviewFingerprint).Append('\n');
        builder.Append("geometry=").Append(geometry.GeometryFingerprint).Append('\n');
        foreach (var step in steps)
        {
            builder.Append(step.StepIndex).Append('|')
                .Append(step.StableId).Append('|')
                .Append((int)step.Kind).Append('|')
                .Append(step.FromDisplayRef).Append('|')
                .Append(step.FromPixelPosition.X).Append(',').Append(step.FromPixelPosition.Y).Append('|')
                .Append(step.ToDisplayRef).Append('|')
                .Append(step.ToPixelPosition.X).Append(',').Append(step.ToPixelPosition.Y).Append('|')
                .Append(step.ToSlotId).Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return $"workspace-execution-plan-v1:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private sealed record SimulatedItemState(string DisplayRef, PixelPoint PixelPosition);
    private readonly record struct StagingSlot(string DisplayRef, ExplorerNativeSlot Slot);
}
