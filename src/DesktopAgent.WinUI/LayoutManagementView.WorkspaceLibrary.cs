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
        WorkspaceLibraryStatusText.Text = "正在读取 Workspace Library…";

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
                        $"Workspace \"{summary.Name}\" 的 current revision 缺失。");

                if (current.Blueprint.Revision != summary.CurrentRevision)
                {
                    throw new InvalidDataException(
                        $"Workspace \"{summary.Name}\" 的资产指针与 immutable revision 不一致。");
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
            WorkspaceLibraryStatusText.Text = items.Count == 0
                ? "还没有保存的 Workspace Blueprint"
                : $"{items.Count} 个 Workspace · 点击查看详情 · 只读设计资产";

            if (showSuccess)
            {
                ShowInfo(
                    "Workspace Library 已刷新",
                    items.Count == 0
                        ? "当前还没有 durable Blueprint。"
                        : $"已读取 {items.Count} 个 Workspace Blueprint；没有恢复 Frozen Preview 或执行权限。",
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            WorkspaceLibraryStatusText.Text = "Workspace Library 当前不可读取";
            WorkspaceBlueprintList.Visibility = Visibility.Collapsed;
            WorkspaceLibraryEmptyText.Visibility = Visibility.Visible;
            WorkspaceLibraryEmptyText.Text = "Library 数据未通过完整性检查。";
            ShowInfo(
                "无法读取 Workspace Library",
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
                summary.Name,
                $"Blueprint v{summary.CurrentRevision}",
                $"{blueprint.DisplayPolicies.Count} 块显示器 · {blueprint.Zones.Count} Zones · " +
                $"{blueprint.ReservedRegions.Count} Reserved",
                blueprint.DesignSummary,
                $"更新于 {summary.UpdatedAtUtc.ToLocalTime():g}");
    }
}
