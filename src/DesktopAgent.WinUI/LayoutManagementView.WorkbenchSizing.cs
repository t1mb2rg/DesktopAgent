using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

/// <summary>
/// 约束布局工作台的滚动职责：根页面直接消费父容器的有限高度；
/// 三栏工作区进一步使用显式可视高度上限，右侧属性栏只能在自身内部滚动，
/// 不能通过 DesiredSize 把左侧 footer 推出窗口。
/// </summary>
public sealed partial class LayoutManagementView
{
    private bool _workbenchSizingApplied;
    private bool _zoneColorAffordanceAdded;
    private Grid? _workbenchRootGrid;
    private ScrollViewer? _workbenchHorizontalScrollViewer;
    private Grid? _workbenchGrid;
    private FrameworkElement? _workbenchHeader;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_workbenchSizingApplied)
        {
            AddZoneColorAffordance();
            ApplyHardWorkbenchHeight();
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

        var header = rootGrid.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(child => Grid.GetRow(child) == 0);

        // 旧结构是 ScrollViewer -> RootGrid。即使禁用纵向滚动，ScrollViewer 仍可能
        // 让 RootGrid 按内容 DesiredSize 参与测量，右侧属性变长时会把整组三栏一起撑高。
        // 这里先重排视觉树为 RootGrid -> HorizontalScrollViewer -> WorkbenchGrid。
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

        _workbenchRootGrid = rootGrid;
        _workbenchHorizontalScrollViewer = workspaceScrollViewer;
        _workbenchGrid = workbenchGrid;
        _workbenchHeader = header;
        _workbenchSizingApplied = true;

        // 这次不再依赖“* 行应该会被约束”的隐式布局推断。
        // 直接把三栏工作台高度钉死在当前 UserControl 的可视高度里。
        SizeChanged -= LayoutManagementView_WorkbenchSizeChanged;
        SizeChanged += LayoutManagementView_WorkbenchSizeChanged;
        LayoutStatusBar.SizeChanged -= LayoutStatusBar_WorkbenchSizeChanged;
        LayoutStatusBar.SizeChanged += LayoutStatusBar_WorkbenchSizeChanged;
        Loaded -= LayoutManagementView_WorkbenchLoaded;
        Loaded += LayoutManagementView_WorkbenchLoaded;

        AddZoneColorAffordance();
        ApplyHardWorkbenchHeight();
    }

    private void LayoutManagementView_WorkbenchLoaded(object sender, RoutedEventArgs e)
        => ApplyHardWorkbenchHeight();

    private void LayoutManagementView_WorkbenchSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyHardWorkbenchHeight();

    private void LayoutStatusBar_WorkbenchSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyHardWorkbenchHeight();

    private void ApplyHardWorkbenchHeight()
    {
        if (!_workbenchSizingApplied ||
            _workbenchRootGrid is null ||
            _workbenchHorizontalScrollViewer is null ||
            _workbenchGrid is null ||
            ActualHeight <= 0)
        {
            return;
        }

        var headerHeight = _workbenchHeader?.ActualHeight ?? 0d;
        var statusHeight = LayoutStatusBar.ActualHeight;
        var spacing = _workbenchRootGrid.RowSpacing * 2d;

        // 8px 给横向滚动条/舍入误差留余量，防止因为 1px 测量差再次产生纵向溢出。
        var availableHeight = Math.Max(
            280d,
            ActualHeight - headerHeight - statusHeight - spacing - 8d);

        _workbenchHorizontalScrollViewer.Height = availableHeight;
        _workbenchHorizontalScrollViewer.MaxHeight = availableHeight;
        _workbenchGrid.Height = availableHeight;
        _workbenchGrid.MaxHeight = availableHeight;

        // 三栏都拿同一个明确高度。特别是左栏必须拥有有限高度，
        // 这样内部 Grid 的“列表 = * / footer = Auto”才能保证 footer 永远可见。
        foreach (var child in _workbenchGrid.Children.OfType<FrameworkElement>())
        {
            child.Height = availableHeight;
            child.MaxHeight = availableHeight;
            child.VerticalAlignment = VerticalAlignment.Stretch;
        }
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
