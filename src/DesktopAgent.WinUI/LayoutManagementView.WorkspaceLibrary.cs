using System.Collections.ObjectModel;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Blueprints;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private const int WorkspaceLibraryLimit = 50;

    private readonly SqliteDesktopWorkspaceBlueprintStore _workspaceBlueprintStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly ObservableCollection<WorkspaceBlueprintLibraryItem> _workspaceBlueprintItems = [];
    private bool _workspaceLibraryBound;
    private bool _workspaceLibraryRefreshActive;

    private async void WorkspaceLibrarySection_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_workspaceLibraryBound)
        {
            WorkspaceBlueprintList.ItemsSource = _workspaceBlueprintItems;
            WorkspaceBlueprintList.IsItemClickEnabled = true;
            WorkspaceBlueprintList.ItemClick += WorkspaceBlueprintList_ItemClick;
            _workspaceLibraryBound = true;
        }

        if (_workspaceLibraryRefreshActive)
            return;

        await RefreshWorkspaceLibraryAsync(showSuccess: false);
    }

    private async void RefreshWorkspaceLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceLibraryRefreshActive)
            return;

        await RefreshWorkspaceLibraryAsync(showSuccess: true);
    }

    private async Task RefreshWorkspaceLibraryAsync(bool showSuccess)
    {
        _workspaceLibraryRefreshActive = true;
        WorkspaceLibraryRefreshButton.IsEnabled = false;
        WorkspaceLibraryStatusText.Text = "正在读取工作区设计库…";

        try
        {
            await _workspaceBlueprintStore.InitializeAsync();
            var summaries = await _workspaceBlueprintStore.ListAsync(
                includeArchived: false,
                limit: WorkspaceLibraryLimit);

            var items = new List<WorkspaceBlueprintLibraryItem>(summaries.Count);
            foreach (var summary in summaries)
            {
                var current = await _workspaceBlueprintStore.GetCurrentRevisionAsync(summary.BlueprintId)
                    ?? throw new InvalidDataException(
                        $"工作区 \"{summary.Name}\" 的当前设计版本缺失。");

                if (current.Blueprint.Revision != summary.CurrentRevision)
                {
                    throw new InvalidDataException(
                        $"工作区 \"{summary.Name}\" 的资产指针与不可变设计版本不一致。");
                }

                items.Add(WorkspaceBlueprintLibraryItem.From(summary, current.Blueprint));
            }

            _workspaceBlueprintItems.Clear();
            foreach (var item in items)
                _workspaceBlueprintItems.Add(item);

            WorkspaceBlueprintList.Visibility = items.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            WorkspaceLibraryEmptyText.Visibility = items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            WorkspaceLibraryEmptyText.Text = "还没有工作区设计。到 AI 助手描述你希望怎样安排桌面，生成后会保存在这里。";
            WorkspaceLibraryStatusText.Text = items.Count == 0
                ? "还没有保存的工作区设计"
                : $"{items.Count} 个设计 · 点击查看分区 · 只读设计资产";
            if (_workspaceDetailAsset is null && items.Count > 0 &&
                SharedDesignSurface.Visibility != Visibility.Visible &&
                _workspaceFrozenPreview is null && !_isLayoutEditing && !_sharedDesignActionBusy)
                await OpenWorkspaceBlueprintDetailAsync(items[0]);

            if (showSuccess)
            {
                ShowInfo(
                    "工作区设计库已刷新",
                    items.Count == 0
                        ? "当前还没有保存的工作区设计。"
                        : $"已读取 {items.Count} 个工作区设计；没有恢复精确预览或执行权限。",
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            WorkspaceLibraryStatusText.Text = "工作区设计库当前不可读取";
            WorkspaceBlueprintList.Visibility = Visibility.Collapsed;
            WorkspaceLibraryEmptyText.Visibility = Visibility.Visible;
            WorkspaceLibraryEmptyText.Text = "设计库数据未通过完整性检查。";
            ShowInfo(
                "无法读取工作区设计库",
                ex.Message,
                InfoBarSeverity.Warning);
        }
        finally
        {
            _workspaceLibraryRefreshActive = false;
            WorkspaceLibraryRefreshButton.IsEnabled = true;
        }
    }

    private sealed record WorkspaceBlueprintLibraryItem(
        Guid BlueprintId,
        string Name,
        string RevisionText,
        string StructureText,
        string DesignSummary,
        string UpdatedText)
    {
        internal static WorkspaceBlueprintLibraryItem From(
            DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints.DesktopWorkspaceBlueprintAssetSummary summary,
            DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints.DesktopWorkspaceBlueprint blueprint)
            => new(
                summary.BlueprintId,
                FormatWorkspaceTitle(summary.Name, blueprint),
                $"设计版本 v{summary.CurrentRevision}",
                $"{blueprint.DisplayPolicies.Count} 块显示器 · {blueprint.Zones.Count} 个分区 · " +
                $"{blueprint.ReservedRegions.Count} 个保留区域",
                FormatBlueprintSummary(blueprint),
                $"更新于 {summary.UpdatedAtUtc.ToLocalTime():g}");
    }
}
