using DesktopAgent.Core.DesktopLayouts.Workspaces;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>
/// 把同一次桌面理解的语义分类安全叠加到 Provider-safe 逻辑工作区。
/// 只通过本地 authority sidecar 绑定 StableId；不会按名称猜测，也不会把 StableId 复制到公开模型。
/// </summary>
public static class DesktopLogicalWorkspaceSemanticOverlay
{
    public static DesktopLogicalWorkspaceProjectionResult Apply(
        DesktopLogicalWorkspaceProjectionResult projection,
        DesktopUnderstandingDraft understanding)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(understanding);
        if (!projection.Succeeded)
            throw new ArgumentException("Semantic overlay 只能应用到成功的逻辑工作区投影。", nameof(projection));

        var workspace = projection.Workspace!;
        var authority = projection.Authority!;
        var normalizedCategoryKeys = understanding.Categories
            .Select(static category => category.Key.Trim())
            .ToArray();
        if (normalizedCategoryKeys.Distinct(StringComparer.Ordinal).Count() !=
            normalizedCategoryKeys.Length)
        {
            return DesktopLogicalWorkspaceProjectionResult.Failure(
            [
                new DesktopLogicalWorkspaceProjectionIssue(
                    DesktopLogicalWorkspaceProjectionIssueCode.InvalidSemanticCategoryCatalog,
                    "语义分类目录在本地正规化后包含重复 Key，已拒绝建立规划会话。"),
            ]);
        }

        var normalizedCategoryKeyByOriginal = understanding.Categories
            .ToDictionary(
                static category => category.Key,
                static category => category.Key.Trim(),
                StringComparer.Ordinal);
        var categories = understanding.Categories
            .Select(category => new DesktopObservedLogicalCategory(
                normalizedCategoryKeyByOriginal[category.Key],
                category.Name,
                category.Description))
            .ToArray();
        var categoryByItemRef = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var assignment in understanding.Assignments)
        {
            if (!authority.TryResolveItemByStableId(assignment.StableId, out var binding))
            {
                return DesktopLogicalWorkspaceProjectionResult.Failure(
                [
                    new DesktopLogicalWorkspaceProjectionIssue(
                        DesktopLogicalWorkspaceProjectionIssueCode.SemanticAssignmentOutsideObservation,
                        "语义分类引用了不属于当前桌面 Observation 的项目，已拒绝建立规划会话。"),
                ]);
            }

            categoryByItemRef.Add(
                binding!.ItemRef,
                normalizedCategoryKeyByOriginal[assignment.CategoryKey]);
        }

        var enrichedItems = workspace.Items
            .Select(item => new DesktopObservedLogicalItem(
                item.ItemRef,
                item.DisplayName,
                item.ItemKind,
                item.CurrentDisplayRef,
                item.CurrentCell,
                item.IsMovable,
                item.IsGridAligned,
                item.OccupiedCells,
                categoryByItemRef.GetValueOrDefault(item.ItemRef)))
            .ToArray();
        var enrichedWorkspace = new DesktopObservedLogicalWorkspace(
            workspace.ObservationId,
            workspace.TopologyFingerprint,
            workspace.GridFingerprint,
            workspace.Displays,
            enrichedItems,
            categories);

        return DesktopLogicalWorkspaceProjectionResult.Success(enrichedWorkspace, authority);
    }
}
