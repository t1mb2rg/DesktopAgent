using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>全桌面设计入口的默认参与范围。仅补齐完全缺失的旧策略，绝不猜测或改写已声明的范围。</summary>
public static class DesktopWorkspaceWholeDesktopPolicy
{
    public static DesktopWorkspaceDesignDraft CompleteIfAbsent(DesktopWorkspaceDesignDraft draft, IEnumerable<string> displayRefs)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(displayRefs);
        if (draft.DisplayPolicies.Count > 0) return draft;
        var refs = displayRefs.ToArray();
        if (refs.Length == 0 || refs.Any(string.IsNullOrWhiteSpace) || refs.Distinct(StringComparer.Ordinal).Count() != refs.Length)
            throw new ArgumentException("当前显示器引用为空或重复。", nameof(displayRefs));
        if (draft.Zones.Any(zone => !refs.Contains(zone.DisplayRef, StringComparer.Ordinal)))
            throw new InvalidOperationException("设计引用了当前不存在的显示器，请先调整设计，不能自动扩大参与范围。");
        return draft.With(displayPolicies: refs.Select(displayRef =>
            new DesktopWorkspaceBlueprintDisplayPolicy(displayRef, DesktopWorkspaceDisplayPolicyKind.Design)));
    }
}
