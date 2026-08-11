using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

/// <summary>
/// 约束布局工作台的滚动职责：根页面直接消费父容器的有限高度；
/// 外层 ScrollViewer 只复用于三栏工作区的横向兜底，右侧属性栏负责自身纵向滚动。
/// </summary>
public sealed partial class LayoutManagementView
{
    private bool _workbenchSizingApplied;
    private bool _zoneColorAffordanceAdded;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_workbenchSizingApplied)
        {
            AddZoneColorAffordance();
            return;
        }

        if (Content is not ScrollViewer workspaceScrollViewer ||
            workspaceScrollViewer.Content is not Grid rootGrid ||
            rootGrid.RowDefinitions.Count < 3)
        {
            return;
        }

        var workbenchGrid = rootGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(child => Grid.GetRow(child) == 2);
        if (workbenchGrid is null)
            return;

        // 旧结构是 ScrollViewer -> RootGrid。即使禁用纵向滚动，ScrollViewer 仍可能
        // 让 RootGrid 按内容 DesiredSize 参与测量，右侧属性变长时会把整组三栏一起撑高。
        // 这里直接重排视觉树为 RootGrid -> HorizontalScrollViewer -> WorkbenchGrid，
        // 让 RootGrid 真正拿到 MainWindow 给出的有限高度。
        rootGrid.Children.Remove(workbenchGrid);
        workspaceScrollViewer.Content = null;
        Content = null;

        workspaceScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
        workspaceScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        workspaceScrollViewer.HorizontalScrollMode = ScrollMode.Auto;
        workspaceScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        workspaceScrollViewer.VerticalContentAlignment = VerticalAlignment.Stretch;
        workspaceScrollViewer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        workspaceScrollViewer.Content = workbenchGrid;

        Grid.SetRow(workspaceScrollViewer, 2);
        rootGrid.Children.Add(workspaceScrollViewer);
        rootGrid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
        rootGrid.VerticalAlignment = VerticalAlignment.Stretch;

        // 左栏列表只消费按钮上方的剩余空间；项目多时由 ListView 自己滚。
        LayoutTemplateList.MinHeight = 0;

        Content = rootGrid;
        _workbenchSizingApplied = true;
        AddZoneColorAffordance();
    }

    private void AddZoneColorAffordance()
    {
        if (_zoneColorAffordanceAdded || LayoutZoneList.Parent is not StackPanel parent)
            return;

        var index = parent.Children.IndexOf(LayoutZoneList);
        if (index < 0)
            return;

        parent.Children.Insert(index, new TextBlock
        {
            Text = "🎨 点击分区可设置独立颜色",
            FontSize = 10,
            Opacity = 0.52,
            Margin = new Thickness(0, -2, 0, -2),
        });
        _zoneColorAffordanceAdded = true;
    }
}
