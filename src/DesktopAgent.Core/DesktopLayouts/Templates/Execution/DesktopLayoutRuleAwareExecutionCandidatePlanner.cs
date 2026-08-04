using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Execution;

using static DesktopLayoutRuleAwareExecutionCandidateIssueCode;

public static class DesktopLayoutRuleAwareExecutionCandidatePlanner
{
    public static DesktopLayoutRuleAwareExecutionCandidateBuildResult Build(
        DesktopLayoutRuleAwarePreviewPlan previewPlan,
        DesktopIconReadResult currentReadResult,
        IReadOnlyList<DisplayDescriptor> currentDisplays,
        DesktopIconPositioningCapabilities capabilities,
        DesktopLayoutSnapshotFactory snapshotFactory)
    {
        if (previewPlan is null) throw new ArgumentNullException(nameof(previewPlan));
        if (currentReadResult is null) throw new ArgumentNullException(nameof(currentReadResult));
        if (currentDisplays is null) throw new ArgumentNullException(nameof(currentDisplays));
        if (capabilities is null) throw new ArgumentNullException(nameof(capabilities));
        if (snapshotFactory is null) throw new ArgumentNullException(nameof(snapshotFactory));

        var issues = new List<DesktopLayoutRuleAwareExecutionCandidateIssue>();

        // ═══ Preview 不完整 / Unassigned ═══
        if (!previewPlan.IsComplete)
        {
            issues.Add(new(PreviewIncomplete, "PreviewPlan 不完整。"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }
        if (previewPlan.DefaultUnassignedCount > 0 || previewPlan.OverrideUnassignedCount > 0)
        {
            issues.Add(new(PreviewContainsUnassigned,
                $"PreviewPlan 包含 Unassigned 图标 (Override={previewPlan.OverrideUnassignedCount}, Default={previewPlan.DefaultUnassignedCount})。"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }

        // ═══ 当前读取完整 ═══
        if (currentReadResult.FailedItemCount != 0)
        {
            issues.Add(new(CurrentReadIncomplete,
                $"当前读取不完整 ({currentReadResult.FailedItemCount} 个失败)。"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }

        // ═══ 自动排列 ═══
        if (capabilities.IsAutoArrangeEnabled)
        {
            issues.Add(new(AutoArrangeEnabled, "自动排列图标已开启。"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }

        // ═══ IconSpacing ═══
        if (capabilities.Spacing != previewPlan.IconSpacing)
        {
            issues.Add(new(IconSpacingChanged,
                $"IconSpacing 已变化: Preview={previewPlan.IconSpacing} Capabilities={capabilities.Spacing}。"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }

        // ═══ 目标显示器 ═══
        var targetDeviceName = previewPlan.TargetDisplay.DeviceName;
        var matchingDisplays = currentDisplays
            .Where(d => string.Equals(d.DeviceName, targetDeviceName, StringComparison.Ordinal))
            .ToList();
        if (matchingDisplays.Count == 0)
        {
            issues.Add(new(TargetDisplayMissing, $"目标显示器 \"{targetDeviceName}\" 在当前拓扑中不存在。"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }
        if (matchingDisplays.Count > 1)
        {
            issues.Add(new(TargetDisplayDuplicated,
                $"目标显示器 \"{targetDeviceName}\" 出现 {matchingDisplays.Count} 次。"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }
        if (!ReferenceEquals(matchingDisplays[0], previewPlan.TargetDisplay))
        {
            issues.Add(new(TargetDisplayReferenceChanged,
                "TargetDisplay 引用已变化（等值但非原引用）。"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }

        // ═══ 图标数量 ═══
        if (currentReadResult.Icons.Count != previewPlan.InputIconCount)
        {
            issues.Add(new(CurrentIconCountChanged,
                $"当前图标数 ({currentReadResult.Icons.Count}) != Preview ({previewPlan.InputIconCount})。"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }

        // ═══ 逐项精确对比 ═══
        for (var i = 0; i < previewPlan.InputIcons.Count; i++)
        {
            var current = currentReadResult.Icons[i];
            var preview = previewPlan.InputIcons[i];

            if (!StringComparer.Ordinal.Equals(current.StableId, preview.StableId))
            {
                issues.Add(new(CurrentIconIdentityChanged,
                    $"当前图标[{i}] StableId \"{current.StableId}\" != Preview \"{preview.StableId}\"。",
                    current.StableId));
                return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
            }

            if (current.DisplayName != preview.DisplayName
                || current.ParsingName != preview.ParsingName
                || current.ItemKind != preview.ItemKind
                || current.IsSystemItem != preview.IsSystemItem
                || current.IsFileSystemItem != preview.IsFileSystemItem)
            {
                issues.Add(new(CurrentIconMetadataChanged,
                    $"当前图标[{i}] (\"{current.StableId}\") 元数据已变化。",
                    current.StableId));
                return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
            }

            if (current.PixelPosition != preview.PixelPosition)
            {
                issues.Add(new(CurrentIconPositionChanged,
                    $"当前图标[{i}] (\"{current.StableId}\") 位置已变化: " +
                    $"({preview.PixelPosition.X},{preview.PixelPosition.Y}) → ({current.PixelPosition.X},{current.PixelPosition.Y})。",
                    current.StableId));
                return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
            }

            if (!string.Equals(current.MonitorDeviceName ?? "", preview.MonitorDeviceName ?? "",
                    StringComparison.Ordinal))
            {
                issues.Add(new(CurrentIconMetadataChanged,
                    $"当前图标[{i}] (\"{current.StableId}\") MonitorDeviceName 已变化。",
                    current.StableId));
                return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
            }
        }

        // ═══ 创建目标快照 ═══
        DesktopLayoutSnapshot targetSnapshot;
        try
        {
            targetSnapshot = DesktopLayoutRuleAwareTargetSnapshotFactory.Create(
                previewPlan, currentReadResult, snapshotFactory);
        }
        catch (Exception ex)
        {
            issues.Add(new(TargetSnapshotVerificationFailed, $"目标快照创建失败：{ex.Message}"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }

        // ═══ 复用 Restore Planner ═══
        DesktopAgent.Core.DesktopLayouts.Restore.TargetDisplayLayoutRestorePlan restorePlan;
        try
        {
            restorePlan = DesktopAgent.Core.DesktopLayouts.Restore.TargetDisplayLayoutRestorePlanner
                .CreatePlan(targetSnapshot, currentReadResult, currentDisplays, capabilities,
                    targetDeviceName);
        }
        catch (Exception ex)
        {
            issues.Add(new(RestorePlanBlocked, $"RestorePlan 创建失败：{ex.Message}"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }

        // ═══ 复用 Execution Planner ═══
        DesktopAgent.Core.DesktopLayouts.Restore.Execution.TargetDisplayLayoutExecutionPlan executionPlan;
        try
        {
            executionPlan = DesktopAgent.Core.DesktopLayouts.Restore.Execution.TargetDisplayLayoutExecutionPlanner
                .CreatePlan(restorePlan, currentReadResult, currentDisplays, capabilities);
        }
        catch (Exception ex)
        {
            issues.Add(new(ExecutionPlanNotExecutable, $"ExecutionPlan 创建失败：{ex.Message}"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }

        // ═══ 组装 Candidate ═══
        try
        {
            var candidate = new DesktopLayoutRuleAwareExecutionCandidate(
                previewPlan, targetSnapshot, restorePlan, executionPlan,
                previewPlan.TargetDisplay);
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Success(candidate);
        }
        catch (Exception ex)
        {
            issues.Add(new(ExecutionPlanNotExecutable, $"Candidate 构造失败：{ex.Message}"));
            return DesktopLayoutRuleAwareExecutionCandidateBuildResult.Rejected(issues);
        }
    }
}
