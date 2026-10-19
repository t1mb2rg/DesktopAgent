using DesktopAgent.Application.DesktopLayouts.Workspaces;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

/// <summary>
/// 面板的三阶段：准备 / 设计 / 执行。
/// 这里只改"哪些面可见"和阶段按钮的呈现，不改任何判定 ——
/// "能不能应用"的唯一出处仍然是 <c>DesktopApplyReadiness</c>。
/// </summary>
public sealed partial class LayoutManagementView
{
    private DesktopLayoutStage _layoutStage = DesktopLayoutStage.Design;
    private bool _layoutStageInitialized;

    internal DesktopLayoutStage CurrentLayoutStage => _layoutStage;

    private void LayoutStageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string rawTag } ||
            !Enum.TryParse(rawTag, ignoreCase: false, out DesktopLayoutStage stage))
        {
            return;
        }

        SetLayoutStage(stage);
    }

    internal void SetLayoutStage(DesktopLayoutStage stage)
    {
        _layoutStage = stage;
        _layoutStageInitialized = true;
        ApplyLayoutStage(stage);
    }

    /// <summary>
    /// 进入面板时决定落在哪一阶段：一块"站定授权"都还没有的机器，第一步永远是准备。
    /// 以前这一步在界面上没有入口（校准按钮挂在"工作区设计库"卡片的角上），这就是"找不到入口"的根因。
    /// 读不到授权就不拦人：默认留在设计阶段。
    /// </summary>
    private async Task InitializeLayoutStageAsync()
    {
        if (_layoutStageInitialized)
            return;

        var stage = DesktopLayoutStage.Design;
        try
        {
            var snapshot = await ReadDisplayAuthorizationsAsync(DesktopAgentDataPaths.DefaultDatabasePath);
            if (snapshot is { IsAvailable: true, Authorizations.Count: 0 })
                stage = DesktopLayoutStage.Prepare;
        }
        catch (Exception)
        {
            // 读不到授权不该拦住面板。
        }

        // 异步读取期间用户可能通过首次设置入口明确选择了阶段。
        if (!_layoutStageInitialized)
            SetLayoutStage(stage);
    }

    private void ApplyLayoutStage(DesktopLayoutStage stage)
    {
        PrepareStagePanel.Visibility = StageVisibility(stage, "PrepareStagePanel");
        WorkspaceLibrarySection.Visibility = StageVisibility(stage, "WorkspaceLibrarySection");
        LayoutEditorSurface.Visibility = StageVisibility(stage, "LayoutEditorSurface");
        LayoutCanvasCard.Visibility = StageVisibility(stage, "LayoutCanvasCard");

        // 工作区详情面由详情流程自己开关（见 LayoutManagementView.WorkspaceDetail.cs）：
        // 阶段只保证"离开设计阶段一定收起"，并在它开着的时候让工作台让位。
        var detailIsOpen = WorkspaceDetailSurface.Visibility == Visibility.Visible;
        if (stage != DesktopLayoutStage.Design)
            WorkspaceDetailSurface.Visibility = Visibility.Collapsed;

        var workbenchIsGatedVisible = DesktopLayoutStagePresentation.IsVisibleInStage(stage, "LayoutWorkbenchSurface");
        LayoutWorkbenchSurface.Visibility = workbenchIsGatedVisible &&
            !(stage == DesktopLayoutStage.Design && detailIsOpen)
                ? Visibility.Visible
                : Visibility.Collapsed;

        UpdateWorkbenchSizing(stage);

        StagePrepareButton.Content = DesktopLayoutStagePresentation.StageButtonContent(
            DesktopLayoutStage.Prepare,
            stage == DesktopLayoutStage.Prepare);
        StageDesignButton.Content = DesktopLayoutStagePresentation.StageButtonContent(
            DesktopLayoutStage.Design,
            stage == DesktopLayoutStage.Design);
        StageExecuteButton.Content = DesktopLayoutStagePresentation.StageButtonContent(
            DesktopLayoutStage.Execute,
            stage == DesktopLayoutStage.Execute);
        StageHintText.Text = DesktopLayoutStagePresentation.Hint(stage);
    }

    private static Visibility StageVisibility(DesktopLayoutStage stage, string elementName)
        => DesktopLayoutStagePresentation.IsVisibleInStage(stage, elementName)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void LayoutWorkspaceSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateWorkbenchSizing(_layoutStage);

    private void UpdateWorkbenchSizing(DesktopLayoutStage stage)
    {
        var availableWidth = LayoutWorkspaceSurface.ActualWidth > 0
            ? LayoutWorkspaceSurface.ActualWidth : 1200;
        var stackInspector = stage == DesktopLayoutStage.Design && availableWidth < 900;
        LayoutWorkspaceSurface.ColumnDefinitions[0].Width = new GridLength(availableWidth < 1000 ? 220 : 268);
        LayoutWorkbenchSurface.ColumnDefinitions[0].MinWidth = stackInspector ? 240 : 320;
        LayoutWorkbenchSurface.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        LayoutWorkbenchSurface.ColumnDefinitions[1].MinWidth = 0;
        LayoutWorkbenchSurface.ColumnDefinitions[1].Width = stage == DesktopLayoutStage.Design && !stackInspector
            ? new GridLength(320) : new GridLength(0);
        Grid.SetRow(LayoutEditorSurface, stackInspector ? 1 : 0);
        Grid.SetColumn(LayoutEditorSurface, stackInspector ? 0 : 1);
        LayoutEditorSurface.MaxHeight = stackInspector ? 240 : double.PositiveInfinity;
    }
}
