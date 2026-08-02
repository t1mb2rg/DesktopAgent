using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

public partial class LayoutPreviewWindow : Window
{
    private readonly IDesktopLayoutTemplateStore _templateStore;
    private readonly IDisplayTopologyReader _displayTopologyReader;
    private readonly IDesktopIconReader _desktopIconReader;
    private readonly IDesktopIconPositionWriter _positioningCapabilitiesProvider;
    private readonly StaThreadExecutor _staExecutor;
    private readonly DesktopLayoutSnapshotFactory _snapshotFactory;

    // M2-03B-B 仅使用 GetCapabilities 的只读能力查询；本窗口永远不能调用 MoveDesktopIcon。
    private readonly ObservableCollection<LayoutPreviewTemplateRow> _templateRows = [];
    private readonly ObservableCollection<LayoutPreviewDisplayRow> _displayRows = [];
    private readonly ObservableCollection<LayoutPreviewItemRow> _itemRows = [];
    private readonly ObservableCollection<LayoutPreviewOutOfScopeRow> _oosRows = [];

    private bool _isBusy;
    private bool _storeReady;
    private DesktopLayoutTemplate? _loadedTemplate;
    private IReadOnlyList<DisplayDescriptor> _currentDisplays = Array.Empty<DisplayDescriptor>();
    private DesktopLayoutPreviewPlan? _previewPlan;
    private LayoutPreviewScene? _scene;
    private long _generation;

    public LayoutPreviewWindow(
        IDesktopLayoutTemplateStore templateStore,
        IDisplayTopologyReader displayTopologyReader,
        IDesktopIconReader desktopIconReader,
        IDesktopIconPositionWriter positioningCapabilitiesProvider,
        StaThreadExecutor staExecutor,
        DesktopLayoutSnapshotFactory snapshotFactory)
    {
        InitializeComponent();

        _templateStore = templateStore;
        _displayTopologyReader = displayTopologyReader;
        _desktopIconReader = desktopIconReader;
        _positioningCapabilitiesProvider = positioningCapabilitiesProvider;
        _staExecutor = staExecutor;
        _snapshotFactory = snapshotFactory;

        TemplateCombo.ItemsSource = _templateRows;
        ItemsGrid.ItemsSource = _itemRows;
        OutOfScopeGrid.ItemsSource = _oosRows;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _templateStore.InitializeAsync();
            _storeReady = true;
            await RefreshTemplatesAsync();
            ReadDisplays();
            StatusText.Text = "就绪。请选择模板并点击 生成预览。";
        }
        catch (Exception ex)
        {
            _storeReady = false;
            StatusText.Text = $"初始化失败：{ex.Message}";
        }
    }

    // ═══ 模板 ═══

    private async Task RefreshTemplatesAsync()
    {
        if (!_storeReady) return;
        var list = await _templateStore.ListAsync(limit: 200);
        _templateRows.Clear();
        foreach (var s in list) _templateRows.Add(new LayoutPreviewTemplateRow(s));
    }

    private async void TemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateCombo.SelectedItem is not LayoutPreviewTemplateRow row) return;
        InvalidatePreview();

        try
        {
            var template = await _templateStore.GetAsync(row.Summary.TemplateId);
            if (template is null)
            {
                StatusText.Text = $"模板 {row.Summary.TemplateId:D} 不存在。";
                _loadedTemplate = null;
                return;
            }
            _loadedTemplate = template;
            StatusText.Text = $"已加载模板：{template.Name} (v{template.Version})。点击 生成预览。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载模板失败：{ex.Message}";
            _loadedTemplate = null;
        }
    }

    // ═══ 显示器 ═══

    private void ReadDisplays()
    {
        try
        {
            _currentDisplays = _displayTopologyReader.GetDisplays();
            _displayRows.Clear();
            foreach (var d in _currentDisplays)
                _displayRows.Add(new LayoutPreviewDisplayRow(d));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取显示器失败：{ex.Message}";
        }
    }

    // ═══ 生成预览 ═══

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _loadedTemplate is null) return;
        InvalidatePreview();

        var gen = Interlocked.Increment(ref _generation);
        _isBusy = true;
        UpdateButtons();
        StatusText.Text = "正在读取桌面图标和显示器…";

        try
        {
            // 1. 读取显示器拓扑
            var displays1 = _displayTopologyReader.GetDisplays();

            // 2. 解析目标显示器
            string? selectedTargetName = null;
            var binding = _loadedTemplate.TargetDisplayBinding;
            DisplayDescriptor targetDisplay;
            try
            {
                targetDisplay = DesktopLayoutTargetDisplayResolver.Resolve(
                    binding, displays1, selectedTargetName);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"目标显示器解析失败：{ex.Message}";
                return;
            }

            // 3. STA 线程读取
            LayoutPreviewShellRead? shellRead;
            try
            {
                shellRead = await _staExecutor.RunAsync(() =>
                {
                    var capabilities = _positioningCapabilitiesProvider.GetCapabilities();
                    var iconRead = _desktopIconReader.ReadDesktopIcons();
                    return new LayoutPreviewShellRead(capabilities, iconRead);
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"桌面读取失败：{ex.Message}";
                return;
            }

            // 4. 再次读取拓扑，检查一致性
            var displays2 = _displayTopologyReader.GetDisplays();
            if (!TopologyConsistent(displays1, displays2))
            {
                StatusText.Text = "读取期间显示器拓扑发生变化，请重试。";
                return;
            }

            if (shellRead.IconRead.FailedItemCount > 0)
            {
                StatusText.Text = $"图标读取不完整：{shellRead.IconRead.FailedItemCount} 个失败。无法生成预览。";
                return;
            }

            // 5. 分配 MonitorDeviceName
            var assignedIcons = shellRead.IconRead.Icons.Select(icon =>
            {
                var monitor = DisplayAssignmentService.AssignMonitor(
                    icon.PixelPosition, displays2);
                return icon with { MonitorDeviceName = monitor?.DeviceName };
            }).ToList();

            // 6. 创建快照
            var snapshot = _snapshotFactory.Create(
                assignedIcons, targetDisplay.DeviceName, shellRead.IconRead.ConnectionMethod);

            // 7. 调用 PreviewPlanner
            var plan = DesktopLayoutPreviewPlanner.Plan(
                _loadedTemplate, displays2, selectedTargetName,
                shellRead.Capabilities.Spacing, snapshot.Icons);

            // 8. 创建 Transform 和 Scene
            var transform = new LayoutPreviewCanvasTransform(
                targetDisplay.WorkArea,
                PreviewCanvas.ActualWidth > 0 ? PreviewCanvas.ActualWidth : 800,
                PreviewCanvas.ActualHeight > 0 ? PreviewCanvas.ActualHeight : 600,
                20);

            _previewPlan = plan;
            _scene = LayoutPreviewSceneBuilder.Build(plan, transform);

            // 9. 检查 generation
            if (gen != Interlocked.Read(ref _generation)) return;

            // 10. 更新 UI
            RenderScene();
            _itemRows.Clear();
            foreach (var r in _scene.ItemRows) _itemRows.Add(r);
            _oosRows.Clear();
            foreach (var r in _scene.OutOfScopeRows) _oosRows.Add(r);

            SummaryText.Text = FormatSummary(plan, shellRead);
            DiagnosticsText.Text = FormatDiagnostics(shellRead);
            StatusText.Text = plan.IsComplete
                ? (plan.HasChanges ? "预览完成：有图标需要移动。" : "预览完成：所有图标已就位。")
                : "预览完成：容量不足，部分图标未分配。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"生成预览失败：{ex.Message}";
        }
        finally { _isBusy = false; UpdateButtons(); }
    }

    // ═══ 画布渲染 ═══

    private void RenderScene()
    {
        PreviewCanvas.Children.Clear();
        if (_scene is null || _previewPlan is null) return;

        var t = _scene.Transform;

        // WorkArea 外框
        var (waLeft, waTop, waW, waH) = t.Map(_previewPlan.TargetDisplay.WorkArea);
        var waRect = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            StrokeThickness = 1, Fill = Brushes.Transparent,
            Width = waW, Height = waH
        };
        Canvas.SetLeft(waRect, waLeft);
        Canvas.SetTop(waRect, waTop);
        PreviewCanvas.Children.Add(waRect);

        // Zone 矩形
        foreach (var zv in _scene.Zones)
        {
            var zRect = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x78, 0xD4)),
                Width = zv.CanvasWidth, Height = zv.CanvasHeight
            };
            Canvas.SetLeft(zRect, zv.CanvasLeft);
            Canvas.SetTop(zRect, zv.CanvasTop);
            PreviewCanvas.Children.Add(zRect);

            var zLabel = new TextBlock
            {
                Text = $"{zv.Name}  ({zv.AssignedCount}/{zv.EffectiveCapacity})",
                FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Background = new SolidColorBrush(Color.FromArgb(0x80, 0xFA, 0xFA, 0xFA))
            };
            Canvas.SetLeft(zLabel, zv.CanvasLeft + 2);
            Canvas.SetTop(zLabel, zv.CanvasTop + 2);
            PreviewCanvas.Children.Add(zLabel);
        }

        // 图标
        foreach (var iv in _scene.Icons)
        {
            // 当前位置：蓝色圆
            var curDot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                Stroke = Brushes.White, StrokeThickness = 1,
                ToolTip = iv.ToolTip
            };
            Canvas.SetLeft(curDot, iv.CurrentCanvasX - 4);
            Canvas.SetTop(curDot, iv.CurrentCanvasY - 4);
            PreviewCanvas.Children.Add(curDot);

            if (iv.State == DesktopLayoutPreviewItemState.MoveRequired
                && !double.IsNaN(iv.TargetCanvasX))
            {
                // 目标位置：红色方
                var tgtDot = new System.Windows.Shapes.Rectangle
                {
                    Width = 8, Height = 8,
                    Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)),
                    Stroke = Brushes.White, StrokeThickness = 1
                };
                Canvas.SetLeft(tgtDot, iv.TargetCanvasX - 4);
                Canvas.SetTop(tgtDot, iv.TargetCanvasY - 4);
                PreviewCanvas.Children.Add(tgtDot);

                // 连线
                var line = new Line
                {
                    X1 = iv.CurrentCanvasX, Y1 = iv.CurrentCanvasY,
                    X2 = iv.TargetCanvasX, Y2 = iv.TargetCanvasY,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)),
                    StrokeThickness = 1.5
                };
                PreviewCanvas.Children.Add(line);
            }
        }
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_previewPlan is null || _scene is null) return;
        if (PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0) return;

        // 仅重建 Transform 和 Scene，不重新读取
        var transform = new LayoutPreviewCanvasTransform(
            _previewPlan.TargetDisplay.WorkArea,
            PreviewCanvas.ActualWidth, PreviewCanvas.ActualHeight, 20);
        _scene = LayoutPreviewSceneBuilder.Build(_previewPlan, transform);
        RenderScene();
    }

    // ═══ 失效 ═══

    private void InvalidatePreview()
    {
        _previewPlan = null;
        _scene = null;
        PreviewCanvas.Children.Clear();
        _itemRows.Clear();
        _oosRows.Clear();
        SummaryText.Text = "无有效预览。";
        DiagnosticsText.Text = "—";
    }

    // ═══ 按钮 ═══

    private void UpdateButtons()
    {
        GenerateButton.IsEnabled = !_isBusy && _storeReady && _loadedTemplate is not null;
        RefreshButton.IsEnabled = !_isBusy && _storeReady;
        TemplateCombo.IsEnabled = !_isBusy;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        InvalidatePreview();
        ReadDisplays();
        try { await RefreshTemplatesAsync(); StatusText.Text = "已刷新。"; }
        catch (Exception ex) { StatusText.Text = $"刷新失败：{ex.Message}"; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isBusy)
        {
            e.Cancel = true;
            StatusText.Text = "操作正在进行中，请稍后再试。";
        }
    }

    // ═══ 格式化 ═══

    private static string FormatSummary(DesktopLayoutPreviewPlan plan, LayoutPreviewShellRead read)
    {
        var a = read.Capabilities.IsAutoArrangeEnabled
            ? "\n⚠ 自动排列已启用！本预览仅用于查看，执行时必须拒绝自动排列下的写入。\n" : "";
        return $"模板: {plan.Template.Name} v{plan.Template.Version}\n" +
            $"目标显示器: {plan.TargetDisplay.DeviceName}\n" +
            $"WorkArea: ({plan.TargetDisplay.WorkArea.Left},{plan.TargetDisplay.WorkArea.Top}) {plan.TargetDisplay.WorkArea.Width}×{plan.TargetDisplay.WorkArea.Height}\n" +
            $"间距: {read.Capabilities.Spacing.X}×{read.Capabilities.Spacing.Y}\n" +
            $"图标总数: {plan.InputIconCount}\n" +
            $"目标显示器: {plan.TargetDisplayIconCount}\n" +
            $"OutOfScope: {plan.OutOfScopeIconCount}\n" +
            $"已分配: {plan.AssignedCount}\n" +
            $"已就位: {plan.AlreadyAtTargetCount}\n" +
            $"需移动: {plan.MoveRequiredCount}\n" +
            $"未分配: {plan.UnassignedCount}\n" +
            $"IsComplete: {plan.IsComplete}  HasChanges: {plan.HasChanges}  IsNoOp: {plan.IsNoOp}\n" +
            $"耗时: {read.IconRead.ReadTime.TotalSeconds:F2}s  Connection: {read.IconRead.ConnectionMethod}{a}";
    }

    private static string FormatDiagnostics(LayoutPreviewShellRead read)
    {
        var a = read.Capabilities.IsAutoArrangeEnabled
            ? "⚠ 自动排列已启用（只读预览可继续）" : "自动排列: 关闭";
        return $"间距: {read.Capabilities.Spacing.X}×{read.Capabilities.Spacing.Y}\n" +
            $"{a}\n" +
            $"ConnectionMethod: {read.IconRead.ConnectionMethod}\n" +
            $"PrimaryPathException: {read.IconRead.PrimaryPathException?.Message ?? "无"}";
    }

    private static bool TopologyConsistent(
        IReadOnlyList<DisplayDescriptor> a, IReadOnlyList<DisplayDescriptor> b)
    {
        if (a.Count != b.Count) return false;
        var aSorted = a.OrderBy(d => d.DeviceName, StringComparer.Ordinal).ToList();
        var bSorted = b.OrderBy(d => d.DeviceName, StringComparer.Ordinal).ToList();
        for (var i = 0; i < aSorted.Count; i++)
        {
            var da = aSorted[i]; var db = bSorted[i];
            if (!StringComparer.Ordinal.Equals(da.DeviceName, db.DeviceName)) return false;
            if (da.Bounds != db.Bounds || da.WorkArea != db.WorkArea) return false;
            if (da.DpiX != db.DpiX || da.DpiY != db.DpiY) return false;
            if (da.IsPrimary != db.IsPrimary) return false;
        }
        return true;
    }
}

/// <summary>STA 线程读取结果。</summary>
internal sealed record LayoutPreviewShellRead(
    DesktopIconPositioningCapabilities Capabilities,
    DesktopIconReadResult IconRead);
