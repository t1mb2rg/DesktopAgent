using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Execution;

/// <summary>
/// 两个 Candidate 的语义比较结果。
/// </summary>
public sealed record SemanticComparisonResult
{
    public bool IsEquivalent { get; }
    public IReadOnlyList<string> Differences { get; }

    public SemanticComparisonResult(bool isEquivalent, IReadOnlyList<string> differences)
    {
        IsEquivalent = isEquivalent;
        Differences = (differences ?? Array.Empty<string>()).ToList().AsReadOnly();
    }

    public static SemanticComparisonResult Equivalent()
        => new(true, Array.Empty<string>());

    public static SemanticComparisonResult Different(IReadOnlyList<string> differences)
        => new(false, differences);
}

/// <summary>
/// 纯逻辑语义比较器 — 比较两个 Candidate 在所有影响用户确认和真实执行的字段上是否等价。
/// 忽略 SnapshotId、CreatedAtUtc 和对象引用地址。
/// </summary>
public static class DesktopLayoutRuleAwareExecutionCandidateSemanticComparer
{
    public static SemanticComparisonResult Compare(
        DesktopLayoutRuleAwareExecutionCandidate confirmed,
        DesktopLayoutRuleAwareExecutionCandidate fresh)
    {
        if (confirmed is null) throw new ArgumentNullException(nameof(confirmed));
        if (fresh is null) throw new ArgumentNullException(nameof(fresh));

        var diffs = new List<string>();

        // ═══ Template ═══
        CompareTemplates(confirmed, fresh, diffs);

        // ═══ TargetDisplay / TargetDeviceName ═══
        CompareTargetDisplay(confirmed, fresh, diffs);

        // ═══ IconSpacing ═══
        if (confirmed.SourcePreviewPlan.IconSpacing != fresh.SourcePreviewPlan.IconSpacing)
            diffs.Add($"IconSpacing: confirmed=({confirmed.SourcePreviewPlan.IconSpacing.X},{confirmed.SourcePreviewPlan.IconSpacing.Y}), " +
                $"fresh=({fresh.SourcePreviewPlan.IconSpacing.X},{fresh.SourcePreviewPlan.IconSpacing.Y})。");

        // ═══ InputIconCount ═══
        if (confirmed.InputIconCount != fresh.InputIconCount)
            diffs.Add($"InputIconCount: {confirmed.InputIconCount} → {fresh.InputIconCount}。");

        // ═══ 当前输入图标 ═══
        CompareInputIcons(confirmed, fresh, diffs);

        // ═══ TargetSnapshot 图标 ═══
        CompareTargetSnapshots(confirmed, fresh, diffs);

        // ═══ RestorePlan Items ═══
        CompareRestorePlans(confirmed, fresh, diffs);

        // ═══ ExecutionPlan ═══
        CompareExecutionPlans(confirmed, fresh, diffs);

        return diffs.Count == 0
            ? SemanticComparisonResult.Equivalent()
            : SemanticComparisonResult.Different(diffs);
    }

    private static void CompareTemplates(
        DesktopLayoutRuleAwareExecutionCandidate a,
        DesktopLayoutRuleAwareExecutionCandidate b,
        List<string> diffs)
    {
        var ta = a.SourcePreviewPlan.Template;
        var tb = b.SourcePreviewPlan.Template;

        if (ta.TemplateId != tb.TemplateId)
            diffs.Add($"TemplateId: {ta.TemplateId} → {tb.TemplateId}。");
        if (ta.Version != tb.Version)
            diffs.Add($"TemplateVersion: {ta.Version} → {tb.Version}。");
        if (!string.Equals(ta.Name, tb.Name, StringComparison.Ordinal))
            diffs.Add($"TemplateName: \"{ta.Name}\" → \"{tb.Name}\"。");
    }

    private static void CompareTargetDisplay(
        DesktopLayoutRuleAwareExecutionCandidate a,
        DesktopLayoutRuleAwareExecutionCandidate b,
        List<string> diffs)
    {
        if (!string.Equals(a.TargetDeviceName, b.TargetDeviceName, StringComparison.Ordinal))
            diffs.Add($"TargetDeviceName: \"{a.TargetDeviceName}\" → \"{b.TargetDeviceName}\"。");

        var da = a.TargetDisplay;
        var db = b.TargetDisplay;
        if (da.Bounds != db.Bounds)
            diffs.Add($"TargetDisplay.Bounds: ({da.Bounds.Left},{da.Bounds.Top},{da.Bounds.Width},{da.Bounds.Height}) " +
                $"→ ({db.Bounds.Left},{db.Bounds.Top},{db.Bounds.Width},{db.Bounds.Height})。");
        if (da.WorkArea != db.WorkArea)
            diffs.Add($"TargetDisplay.WorkArea: ({da.WorkArea.Left},{da.WorkArea.Top},{da.WorkArea.Width},{da.WorkArea.Height}) " +
                $"→ ({db.WorkArea.Left},{db.WorkArea.Top},{db.WorkArea.Width},{db.WorkArea.Height})。");
        if (da.DpiX != db.DpiX || da.DpiY != db.DpiY)
            diffs.Add($"TargetDisplay.Dpi: ({da.DpiX}x{da.DpiY}) → ({db.DpiX}x{db.DpiY})。");
        if (da.IsPrimary != db.IsPrimary)
            diffs.Add($"TargetDisplay.IsPrimary: {da.IsPrimary} → {db.IsPrimary}。");
    }

    private static void CompareInputIcons(
        DesktopLayoutRuleAwareExecutionCandidate a,
        DesktopLayoutRuleAwareExecutionCandidate b,
        List<string> diffs)
    {
        var iconsA = a.SourcePreviewPlan.InputIcons;
        var iconsB = b.SourcePreviewPlan.InputIcons;

        if (iconsA.Count != iconsB.Count)
        {
            diffs.Add($"InputIcons.Count: {iconsA.Count} → {iconsB.Count}。");
            return;
        }

        for (var i = 0; i < iconsA.Count; i++)
        {
            var ia = iconsA[i];
            var ib = iconsB[i];

            if (!string.Equals(ia.StableId, ib.StableId, StringComparison.Ordinal))
                diffs.Add($"InputIcons[{i}].StableId: \"{ia.StableId}\" → \"{ib.StableId}\"。");
            if (!string.Equals(ia.DisplayName ?? "", ib.DisplayName ?? "", StringComparison.Ordinal))
                diffs.Add($"InputIcons[{i}] (\"{ia.StableId}\").DisplayName: \"{ia.DisplayName}\" → \"{ib.DisplayName}\"。");
            if (!string.Equals(ia.ParsingName ?? "", ib.ParsingName ?? "", StringComparison.Ordinal))
                diffs.Add($"InputIcons[{i}] (\"{ia.StableId}\").ParsingName 已变化。");
            if (ia.ItemKind != ib.ItemKind)
                diffs.Add($"InputIcons[{i}] (\"{ia.StableId}\").ItemKind: {ia.ItemKind} → {ib.ItemKind}。");
            if (ia.IsSystemItem != ib.IsSystemItem)
                diffs.Add($"InputIcons[{i}] (\"{ia.StableId}\").IsSystemItem: {ia.IsSystemItem} → {ib.IsSystemItem}。");
            if (ia.IsFileSystemItem != ib.IsFileSystemItem)
                diffs.Add($"InputIcons[{i}] (\"{ia.StableId}\").IsFileSystemItem: {ia.IsFileSystemItem} → {ib.IsFileSystemItem}。");
            if (ia.PixelPosition != ib.PixelPosition)
                diffs.Add($"InputIcons[{i}] (\"{ia.StableId}\").PixelPosition: " +
                    $"({ia.PixelPosition.X},{ia.PixelPosition.Y}) → ({ib.PixelPosition.X},{ib.PixelPosition.Y})。");
            if (!string.Equals(ia.MonitorDeviceName ?? "", ib.MonitorDeviceName ?? "", StringComparison.Ordinal))
                diffs.Add($"InputIcons[{i}] (\"{ia.StableId}\").MonitorDeviceName: " +
                    $"\"{ia.MonitorDeviceName}\" → \"{ib.MonitorDeviceName}\"。");
        }
    }

    private static void CompareTargetSnapshots(
        DesktopLayoutRuleAwareExecutionCandidate a,
        DesktopLayoutRuleAwareExecutionCandidate b,
        List<string> diffs)
    {
        var sa = a.TargetSnapshot;
        var sb = b.TargetSnapshot;

        if (sa.Icons.Count != sb.Icons.Count)
        {
            diffs.Add($"TargetSnapshot.Icons.Count: {sa.Icons.Count} → {sb.Icons.Count}。");
            return;
        }

        for (var i = 0; i < sa.Icons.Count; i++)
        {
            var ia = sa.Icons[i];
            var ib = sb.Icons[i];

            if (!string.Equals(ia.StableId, ib.StableId, StringComparison.Ordinal))
                diffs.Add($"TargetSnapshot[{i}].StableId: \"{ia.StableId}\" → \"{ib.StableId}\"。");
            if (ia.PixelPosition != ib.PixelPosition)
                diffs.Add($"TargetSnapshot[{i}] (\"{ia.StableId}\").PixelPosition: " +
                    $"({ia.PixelPosition.X},{ia.PixelPosition.Y}) → ({ib.PixelPosition.X},{ib.PixelPosition.Y})。");
            if (!string.Equals(ia.MonitorDeviceName ?? "", ib.MonitorDeviceName ?? "", StringComparison.Ordinal))
                diffs.Add($"TargetSnapshot[{i}] (\"{ia.StableId}\").MonitorDeviceName: " +
                    $"\"{ia.MonitorDeviceName}\" → \"{ib.MonitorDeviceName}\"。");
            if (ia.IsSystemItem != ib.IsSystemItem)
                diffs.Add($"TargetSnapshot[{i}] (\"{ia.StableId}\").IsSystemItem: {ia.IsSystemItem} → {ib.IsSystemItem}。");
            if (ia.IsFileSystemItem != ib.IsFileSystemItem)
                diffs.Add($"TargetSnapshot[{i}] (\"{ia.StableId}\").IsFileSystemItem: {ia.IsFileSystemItem} → {ib.IsFileSystemItem}。");
        }
    }

    private static void CompareRestorePlans(
        DesktopLayoutRuleAwareExecutionCandidate a,
        DesktopLayoutRuleAwareExecutionCandidate b,
        List<string> diffs)
    {
        var ra = a.RestorePlan;
        var rb = b.RestorePlan;

        if (ra.HasBlockingErrors != rb.HasBlockingErrors)
            diffs.Add($"RestorePlan.HasBlockingErrors: {ra.HasBlockingErrors} → {rb.HasBlockingErrors}。");
        if (ra.RequiresTemporaryStaging != rb.RequiresTemporaryStaging)
            diffs.Add($"RestorePlan.RequiresTemporaryStaging: {ra.RequiresTemporaryStaging} → {rb.RequiresTemporaryStaging}。");
        if (ra.IsNoOp != rb.IsNoOp)
            diffs.Add($"RestorePlan.IsNoOp: {ra.IsNoOp} → {rb.IsNoOp}。");
        if (ra.MoveRequiredCount != rb.MoveRequiredCount)
            diffs.Add($"RestorePlan.MoveRequiredCount: {ra.MoveRequiredCount} → {rb.MoveRequiredCount}。");
        if (ra.BlockedCount != rb.BlockedCount)
            diffs.Add($"RestorePlan.BlockedCount: {ra.BlockedCount} → {rb.BlockedCount}。");
        if (ra.MissingCount != rb.MissingCount)
            diffs.Add($"RestorePlan.MissingCount: {ra.MissingCount} → {rb.MissingCount}。");
        if (ra.AlreadyAtTargetCount != rb.AlreadyAtTargetCount)
            diffs.Add($"RestorePlan.AlreadyAtTargetCount: {ra.AlreadyAtTargetCount} → {rb.AlreadyAtTargetCount}。");
        if (ra.SnapshotTargetIconCount != rb.SnapshotTargetIconCount)
            diffs.Add($"RestorePlan.SnapshotTargetIconCount: {ra.SnapshotTargetIconCount} → {rb.SnapshotTargetIconCount}。");

        if (ra.Items.Count != rb.Items.Count)
        {
            diffs.Add($"RestorePlan.Items.Count: {ra.Items.Count} → {rb.Items.Count}。");
            return;
        }

        for (var i = 0; i < ra.Items.Count; i++)
        {
            var ia = ra.Items[i];
            var ib = rb.Items[i];

            if (!string.Equals(ia.StableId, ib.StableId, StringComparison.Ordinal))
                diffs.Add($"RestorePlan.Items[{i}].StableId: \"{ia.StableId}\" → \"{ib.StableId}\"。");
            if (ia.State != ib.State)
                diffs.Add($"RestorePlan.Items[{i}] (\"{ia.StableId}\").State: {ia.State} → {ib.State}。");
            if (ia.SnapshotTargetPosition != ib.SnapshotTargetPosition)
                diffs.Add($"RestorePlan.Items[{i}] (\"{ia.StableId}\").SnapshotTargetPosition: " +
                    $"({ia.SnapshotTargetPosition.X},{ia.SnapshotTargetPosition.Y}) → ({ib.SnapshotTargetPosition.X},{ib.SnapshotTargetPosition.Y})。");
            if (ia.CurrentPosition != ib.CurrentPosition)
                diffs.Add($"RestorePlan.Items[{i}] (\"{ia.StableId}\").CurrentPosition: " +
                    $"{FormatPos(ia.CurrentPosition)} → {FormatPos(ib.CurrentPosition)}。");
            if (!string.Equals(ia.CurrentMonitorDeviceName ?? "", ib.CurrentMonitorDeviceName ?? "", StringComparison.Ordinal))
                diffs.Add($"RestorePlan.Items[{i}] (\"{ia.StableId}\").CurrentMonitorDeviceName: " +
                    $"\"{ia.CurrentMonitorDeviceName}\" → \"{ib.CurrentMonitorDeviceName}\"。");
            if (ia.IsFileSystemItem != ib.IsFileSystemItem)
                diffs.Add($"RestorePlan.Items[{i}] (\"{ia.StableId}\").IsFileSystemItem: {ia.IsFileSystemItem} → {ib.IsFileSystemItem}。");
            if (ia.IsSystemItem != ib.IsSystemItem)
                diffs.Add($"RestorePlan.Items[{i}] (\"{ia.StableId}\").IsSystemItem: {ia.IsSystemItem} → {ib.IsSystemItem}。");
        }
    }

    private static void CompareExecutionPlans(
        DesktopLayoutRuleAwareExecutionCandidate a,
        DesktopLayoutRuleAwareExecutionCandidate b,
        List<string> diffs)
    {
        var ea = a.ExecutionPlan;
        var eb = b.ExecutionPlan;

        if (ea.IsExecutable != eb.IsExecutable)
            diffs.Add($"ExecutionPlan.IsExecutable: {ea.IsExecutable} → {eb.IsExecutable}。");
        if (ea.IsNoOp != eb.IsNoOp)
            diffs.Add($"ExecutionPlan.IsNoOp: {ea.IsNoOp} → {eb.IsNoOp}。");
        if (ea.UsesTemporaryStaging != eb.UsesTemporaryStaging)
            diffs.Add($"ExecutionPlan.UsesTemporaryStaging: {ea.UsesTemporaryStaging} → {eb.UsesTemporaryStaging}。");
        if (ea.StagingAssignments.Count != eb.StagingAssignments.Count)
            diffs.Add($"ExecutionPlan.StagingAssignments.Count: {ea.StagingAssignments.Count} → {eb.StagingAssignments.Count}。");
        if (ea.Steps.Count != eb.Steps.Count)
        {
            diffs.Add($"ExecutionPlan.Steps.Count: {ea.Steps.Count} → {eb.Steps.Count}。");
            return;
        }

        for (var i = 0; i < ea.Steps.Count; i++)
        {
            var sa = ea.Steps[i];
            var sb = eb.Steps[i];

            if (sa.StepIndex != sb.StepIndex)
                diffs.Add($"ExecutionPlan.Steps[{i}].StepIndex: {sa.StepIndex} → {sb.StepIndex}。");
            if (sa.Kind != sb.Kind)
                diffs.Add($"ExecutionPlan.Steps[{i}].Kind: {sa.Kind} → {sb.Kind}。");
            if (!string.Equals(sa.StableId, sb.StableId, StringComparison.Ordinal))
                diffs.Add($"ExecutionPlan.Steps[{i}].StableId: \"{sa.StableId}\" → \"{sb.StableId}\"。");
            if (sa.FromPosition != sb.FromPosition)
                diffs.Add($"ExecutionPlan.Steps[{i}] (\"{sa.StableId}\").FromPosition: " +
                    $"({sa.FromPosition.X},{sa.FromPosition.Y}) → ({sb.FromPosition.X},{sb.FromPosition.Y})。");
            if (sa.ToPosition != sb.ToPosition)
                diffs.Add($"ExecutionPlan.Steps[{i}] (\"{sa.StableId}\").ToPosition: " +
                    $"({sa.ToPosition.X},{sa.ToPosition.Y}) → ({sb.ToPosition.X},{sb.ToPosition.Y})。");
            if (!string.Equals(sa.ExpectedMonitorDeviceName, sb.ExpectedMonitorDeviceName, StringComparison.Ordinal))
                diffs.Add($"ExecutionPlan.Steps[{i}] (\"{sa.StableId}\").ExpectedMonitorDeviceName: " +
                    $"\"{sa.ExpectedMonitorDeviceName}\" → \"{sb.ExpectedMonitorDeviceName}\"。");
        }
    }

    private static string FormatPos(PixelPoint? pos) =>
        pos.HasValue ? $"({pos.Value.X},{pos.Value.Y})" : "null";
}
