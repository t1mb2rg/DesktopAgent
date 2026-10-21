using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>复用保存的设计，以 fresh 事实重新编译和独立验算；不恢复历史执行权限。</summary>
public static class DesktopWorkspaceSavedDesignPreviewFactory
{
    public static DesktopWorkspaceFrozenPreview Create(
        DesktopWorkspaceBlueprint blueprint,
        DesktopSemanticUnderstandingSnapshot understanding,
        DesktopObservation observation,
        ExplorerWorkspaceGeometry geometry,
        IEnumerable<DesktopWorkspaceCompilationItemRefBinding>? confirmedBindings = null)
    {
        if (!DesktopSemanticUnderstandingFreshnessEvaluator.Evaluate(understanding, observation).IsCurrent)
            throw new InvalidDataException("保存的桌面理解已过期，请先在 AI 助手重新理解桌面，再复用此设计。");

        var facts = DesktopWorkspaceLocalItemFactProjector.Project(observation, geometry);
        var bindings = (confirmedBindings ?? []).ToArray();
        var compilation = DesktopWorkspaceCompiler.Compile(blueprint, understanding.Understanding, geometry, facts, bindings);
        if (!compilation.Succeeded)
            throw new InvalidDataException("此设计无法在当前桌面完整实现：" +
                string.Join("；", compilation.Issues.Take(3).Select(static issue => issue.Message)));

        var equivalence = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            blueprint, geometry, compilation.Plan!, facts, bindings);
        if (!equivalence.Equivalent)
            throw new InvalidDataException("此设计未通过独立等价检查，没有生成预览。");

        var source = new DesktopWorkspacePreviewSourceSnapshot(observation.ObservationId, facts,
            observation.Icons.Select(static icon => new DesktopWorkspacePreviewItemPresentation(icon.StableId, icon.DisplayName)));
        return DesktopWorkspaceFrozenPreviewFactory.Create(blueprint, geometry, compilation.Plan!,
            equivalence.Certificate!, source, bindings);
    }
}
