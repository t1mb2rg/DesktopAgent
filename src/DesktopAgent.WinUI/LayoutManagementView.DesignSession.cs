using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private readonly ProductWorkspaceSession _designSession;

    internal event Action? DesignWorkspaceRequested;

    private void OpenDesignWorkspace_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => DesignWorkspaceRequested?.Invoke();


    private async void EditWorkspaceDesign_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (WorkspaceRevisionList.SelectedItem is not WorkspaceRevisionListItem selected)
            return;
        var snapshot = _designSession.Session.Snapshot();
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑这个设计版本？",
            Content = "将此版本载入当前设计草稿，替换工作台中的草稿。已保存的设计和桌面图标不会改变；可以撤销草稿替换。",
            PrimaryButtonText = "载入并编辑",
            CloseButtonText = "取消",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            return;
        var result = ApplyWorkspaceDesignCommands(snapshot.Revision,
            [new ReplaceWorkspaceDraft(DesktopWorkspaceDesignDraft.FromBlueprint(selected.Record.Blueprint))]);
        if (!result.Applied)
        {
            ShowError("未载入设计", "当前草稿已经变化，请重试。");
            return;
        }
        InvalidateDesignRevisionEvidence();
        OpenSharedDesign_Click(sender, e);
    }

    // 后续聊天拓扑与网格拖动共用这个入口，不维护另一份 AI 专用草稿。
    internal DesktopWorkspaceEditResult ApplyWorkspaceDesignCommands(long baseRevision,
        IReadOnlyList<DesktopWorkspaceDesignCommand> commands)
        => _designSession.Session.TryApply(baseRevision, commands);

    internal void InvalidateDesignRevisionEvidence()
    {
        ClearAiLogicalWorkspacePreview();
        ClearAiWorkspaceFrozenPreview();
        InvalidateDirectApplyEvidence("设计已修改 · 请重新生成精确预览");
    }
}
