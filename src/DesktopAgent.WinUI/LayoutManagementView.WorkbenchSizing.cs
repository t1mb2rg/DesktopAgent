using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

/// <summary>
/// 约束布局工作台的滚动职责：三栏工作区始终占用当前可视高度，
/// 右侧属性栏负责自己的纵向滚动，不能反过来把左侧操作按钮推到视口之外。
/// </summary>
public sealed partial class LayoutManagementView
{
    private bool _zoneColorAffordanceAdded;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (Content is not ScrollViewer workspaceScrollViewer ||
            workspaceScrollViewer.Content is not Grid rootGrid ||
            rootGrid.RowDefinitions.Count < 3)
        {
            return;
        }

        // 外层只负责窄窗口下的横向兜底。纵向高度由当前页面视口约束，
        // 右侧 Layout Properties 已拥有自己的 ScrollViewer。
        workspaceScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
        workspaceScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        workspaceScrollViewer.HorizontalScrollMode = ScrollMode.Auto;
        workspaceScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        workspaceScrollViewer.VerticalContentAlignment = VerticalAlignment.Stretch;
        workspaceScrollViewer.HorizontalContentAlignment = HorizontalAlignment.Stretch;

        rootGrid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);

        // 左侧列表必须消费剩余高度并在自身内部滚动，不能用固定最小高度
        // 把底部“编辑 / 使用”操作区推出当前视口。
        LayoutTemplateList.MinHeight = 0;

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
