using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>旧增量 Provider 的过渡适配；持续会话与新画布不再由旧 View 私有字段持有。</summary>
public static class DesktopWorkspaceConversationIntentAdapter
{
    public static DesktopWorkspaceConversationIntent FromLegacy(DesktopLayoutConversationState state)
        => new(state.IntentSummary, state.CategoryDisplayTargets, state.ItemDisplayTargets,
            state.DisplayLoadPreferences.ToDictionary(pair => pair.Key, pair => (DesktopWorkspaceDisplayLoad)(int)pair.Value),
            state.ReservedRegions.Select(region => new DesktopWorkspaceReservedIntent(region.Key, region.DisplayRef,
                region.PlacementHint, region.SizeHint, region.Purpose)));

    public static DesktopLayoutConversationState ToLegacy(DesktopWorkspaceConversationIntent state)
        => new(state.Summary, state.CategoryDisplayTargets, state.ItemDisplayTargets,
            state.DisplayLoads.ToDictionary(pair => pair.Key, pair => (DesktopDisplayLoadPreference)(int)pair.Value),
            state.ReservedRegions.Select(region => new DesktopReservedRegionConstraint(region.Key, region.DisplayRef,
                region.PlacementHint, region.SizeHint, region.Purpose)).ToArray());
}
