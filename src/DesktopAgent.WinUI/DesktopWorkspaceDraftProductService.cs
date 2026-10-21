using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Persistence.Intelligence;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.WinUI;

/// <summary>操作卡片只消费当前草稿；生成精确预览不再请求模型重建设计。</summary>
internal sealed class DesktopWorkspaceDraftProductService
{
    internal async Task<int> SaveAsync(DesktopWorkspaceDesignDraft draft, string? name, string? userIntent)
    {
        if (draft.DisplayPolicies.Count == 0)
        {
            using var sta = new StaThreadExecutor();
            var source = new WindowsDesktopObservationSource(new NativeDisplayTopologyReader(),
                new DesktopShellIconReader(), new DesktopShellIconPositioningCapabilitiesReader(), sta);
            var capture = await ProductGridCalibration.Protect(source).CaptureAsync();
            if (!capture.IsCaptured) throw new InvalidOperationException("保存前无法核验当前显示器，请重新检查桌面。");
            var map = DesktopAgent.Application.DesktopLayouts.Templates.Editing.DesktopSemanticDisplayMap.Create(capture.Observation!);
            draft = DesktopWorkspaceWholeDesktopPolicy.CompleteIfAbsent(draft, map.Entries.Select(entry => entry.DisplayRef));
        }
        var store = new SqliteDesktopWorkspaceBlueprintStore(DesktopAgentDataPaths.DefaultDatabasePath);
        await store.InitializeAsync();
        var previous = await store.GetCurrentRevisionAsync(draft.DesignId);
        var revision = checked((previous?.Blueprint.Revision ?? 0) + 1);
        var blueprint = CompleteLabel(draft).ToBlueprint(revision, previous?.Blueprint.Revision);
        var record = new DesktopWorkspaceBlueprintRevisionRecord(blueprint, DateTimeOffset.UtcNow,
            sourceUserIntent: string.IsNullOrWhiteSpace(userIntent) ? null : userIntent);
        if (previous is null)
            await store.CreateAsync(string.IsNullOrWhiteSpace(name) ? blueprint.DesignSummary : name.Trim(), record);
        else
            await store.AppendRevisionAsync(record);
        return revision;
    }

    internal async Task<DesktopWorkspaceFrozenPreview> PreviewAsync(DesktopWorkspaceDesignDraft draft, CancellationToken ct)
    {
        if (draft.ItemOverrides.Count > 0)
            throw new InvalidOperationException("这份历史设计包含项目例外，请先在设计库重新确认项目绑定，再生成预览。");
        using var sta = new StaThreadExecutor();
        var source = new WindowsDesktopObservationSource(new NativeDisplayTopologyReader(),
            new DesktopShellIconReader(), new DesktopShellIconPositioningCapabilitiesReader(), sta);
        var capture = await ProductGridCalibration.Protect(source).CaptureAsync(ct);
        if (!capture.IsCaptured)
            throw new InvalidOperationException(string.Join("；", capture.Issues.Select(issue => issue.Message)));
        var observation = capture.Observation!;
        var facts = new DesktopExplorerGridFactReader(new SqliteExplorerGridMeasurementStore(DesktopAgentDataPaths.DefaultDatabasePath));
        var geometry = DesktopExplorerWorkspaceGeometryAdapter.Build(observation, await facts.TryReadActiveAsync(observation, ct));
        if (!geometry.Succeeded)
            throw new InvalidOperationException(string.Join("；", geometry.Issues.Select(issue => issue.Message)));
        var semanticStore = new SqliteDesktopSemanticUnderstandingStore(DesktopAgentDataPaths.DefaultDatabasePath);
        await semanticStore.InitializeAsync(ct);
        var understanding = await semanticStore.GetLatestAsync(ct)
            ?? throw new InvalidOperationException("请先完成桌面理解。");
        draft = DesktopWorkspaceWholeDesktopPolicy.CompleteIfAbsent(draft, geometry.Geometry!.Displays.Select(display => display.DisplayRef));
        draft = DesktopWorkspaceZonePresentation.WithLabelReservations(draft, geometry.Geometry!);
        return DesktopWorkspaceSavedDesignPreviewFactory.Create(CompleteLabel(draft).ToBlueprint(),
            understanding, observation, geometry.Geometry!);
    }

    private static DesktopWorkspaceDesignDraft CompleteLabel(DesktopWorkspaceDesignDraft draft)
        => string.IsNullOrWhiteSpace(draft.Summary) ? draft.With(summary: "桌面布局") : draft;
}
