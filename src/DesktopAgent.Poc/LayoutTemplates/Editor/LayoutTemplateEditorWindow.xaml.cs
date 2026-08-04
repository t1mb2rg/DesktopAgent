using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Poc.LayoutTemplates.Editor;

public partial class LayoutTemplateEditorWindow : Window
{
    private readonly IDesktopLayoutTemplateStore _templateStore;
    private readonly IDesktopLayoutIconRuleStore _ruleStore;
    private readonly string[] _knownDeviceNames;
    private bool _operationInProgress;
    private bool _templateStoreReady;
    private bool _ruleStoreReady;
    private LayoutTemplateEditorDraft _draft;
    private readonly ObservableCollection<TemplateListEntry> _templateList = [];
    private bool _suppressTemplateSelectionChanged;

    // Zone 事件订阅（Window 层）
    private readonly HashSet<LayoutZoneEditorDraft> _windowSubscribedZones =
        new(ReferenceEqualityComparer.Instance);

    // 画布拖动状态
    private LayoutZoneEditorDraft? _draggingZone;
    private NormalizedRect? _dragOriginalBounds;
    private double _dragAccumulatedPixelX;
    private double _dragAccumulatedPixelY;
    private bool _updatingDraftFromCanvas;

    // Zone 可视化缓存
    private readonly Dictionary<LayoutZoneEditorDraft, ZoneVisualData> _zoneVisuals = [];

    public LayoutTemplateEditorWindow(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        string[] knownDeviceNames)
    {
        InitializeComponent();

        _templateStore = templateStore;
        _ruleStore = ruleStore;
        _knownDeviceNames = knownDeviceNames;

        ExactDeviceNameCombo.ItemsSource = _knownDeviceNames;
        TemplateListBox.ItemsSource = _templateList;

        _draft = LayoutTemplateEditorDraft.CreateNew();
        BindDraft();

        Loaded += OnLoaded;
    }

    // ═══ 初始化 ═══

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _operationInProgress = true;
        UpdateButtons();

        // 阶段 A：初始化持久化
        try
        {
            await _templateStore.InitializeAsync();
            _templateStoreReady = true;
        }
        catch (Exception ex)
        {
            _templateStoreReady = false;
            StatusBarText.Text = $"持久化初始化失败：{ex.Message}。可继续编辑草稿，但保存/加载/删除不可用。";
            _operationInProgress = false;
            UpdateButtons();
            return;
        }

        // 阶段 A2：初始化规则 Store
        try
        {
            await _ruleStore.InitializeAsync();
            _ruleStoreReady = true;
        }
        catch (Exception ex)
        {
            _ruleStoreReady = false;
            StatusBarText.Text = $"规则 Store 初始化失败：{ex.Message}。可编辑未持久化草稿，但 SAVE / RELOAD / DELETE / LOAD 已禁用。";
            _operationInProgress = false;
            UpdateButtons();
            return;
        }

        // 阶段 B：加载模板列表
        try
        {
            await RefreshTemplateListAsync();
            StatusBarText.Text = "就绪。请选择模板或新建。";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"模板列表加载失败：{ex.Message}。保存与刷新功能仍可使用。";
        }
        finally { _operationInProgress = false; UpdateButtons(); }
    }

    // ═══ Window 层 Zone 订阅 ═══

    private void SyncWindowZoneSubscriptions()
    {
        var toRemove = new List<LayoutZoneEditorDraft>();
        foreach (var existing in _windowSubscribedZones)
        {
            var found = false;
            foreach (var z in _draft.Zones)
            {
                if (ReferenceEquals(z, existing)) { found = true; break; }
            }
            if (!found) toRemove.Add(existing);
        }
        foreach (var item in toRemove)
        {
            item.PropertyChanged -= OnZonePropertyChanged;
            _windowSubscribedZones.Remove(item);
        }

        foreach (var zone in _draft.Zones)
        {
            if (_windowSubscribedZones.Add(zone))
                zone.PropertyChanged += OnZonePropertyChanged;
        }
    }

    // ═══ 草稿绑定 ═══

    private void BindDraft()
    {
        DataContext = _draft;
        TemplateIdText.Text = _draft.TemplateId.ToString("D");
        ZoneListBox.ItemsSource = _draft.Zones;

        _draft.PropertyChanged += OnDraftPropertyChanged;
        _draft.Zones.CollectionChanged += OnZonesCollectionChanged;
        SyncWindowZoneSubscriptions();

        UpdateTitle();
        RebuildCanvas();
    }

    private void UnbindDraft()
    {
        _draft.PropertyChanged -= OnDraftPropertyChanged;
        _draft.Zones.CollectionChanged -= OnZonesCollectionChanged;

        foreach (var zone in _windowSubscribedZones)
            zone.PropertyChanged -= OnZonePropertyChanged;
        _windowSubscribedZones.Clear();

        DataContext = null;
        ZoneListBox.ItemsSource = null;
        _zoneVisuals.Clear();
        ZoneCanvas.Children.Clear();
    }

    private void OnDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateTitle();
        TemplateIdText.Text = _draft.TemplateId.ToString("D");
    }

    private void OnZonesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncWindowZoneSubscriptions();
        UpdateButtons();
        RebuildCanvas();
        UpdateTitle();
    }

    private void OnZonePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingDraftFromCanvas) return;
        RebuildCanvas();
        UpdateTitle();
        UpdateButtons();
    }

    private void UpdateTitle()
    {
        var dirty = _draft.IsDirty ? " *" : "";
        Title = $"布局模板编辑器 — {_draft.Name}{dirty}  |  M2-03A";
    }

    // ═══ 模板列表 ═══

    private async Task RefreshTemplateListAsync()
    {
        if (!_templateStoreReady) return;
        var list = await _templateStore.ListAsync(limit: 200);
        _templateList.Clear();
        foreach (var s in list)
            _templateList.Add(new TemplateListEntry(s));

        if (_draft.IsPersisted)
            SelectTemplateInList(_draft.TemplateId);
    }

    private sealed class TemplateListEntry
    {
        public DesktopLayoutTemplateSummary Summary { get; }
        public string DisplayText { get; }
        public TemplateListEntry(DesktopLayoutTemplateSummary s)
        {
            Summary = s;
            var idShort = s.TemplateId.ToString("D")[..8] + "...";
            var kind = s.TargetDisplayBinding.Kind.ToString();
            DisplayText = $"{s.Name} (v{s.Version}) [{kind}] {s.ZoneCount}z  {idShort}";
        }
    }

    private void RestoreTemplateSelectionForCurrentDraft()
    {
        _suppressTemplateSelectionChanged = true;
        if (_draft.IsPersisted)
            SelectTemplateInList(_draft.TemplateId);
        else
            TemplateListBox.SelectedItem = null;
        _suppressTemplateSelectionChanged = false;
    }

    private async void TemplateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateSelectionChanged) return;
        if (_operationInProgress) return;
        if (TemplateListBox.SelectedItem is not TemplateListEntry entry) return;

        if (_draft.IsDirty)
        {
            var dr = MessageBox.Show(
                "放弃未保存更改并切换模板？", "未保存的更改",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (dr != MessageBoxResult.Yes)
            {
                RestoreTemplateSelectionForCurrentDraft();
                return;
            }
        }

        if (!await LoadTemplateAsync(entry.Summary.TemplateId))
            RestoreTemplateSelectionForCurrentDraft();
    }

    private async Task<bool> LoadTemplateAsync(Guid templateId)
    {
        if (!_templateStoreReady || !_ruleStoreReady) return false;
        _operationInProgress = true;
        UpdateButtons();
        try
        {
            // 先读入局部变量
            var template = await _templateStore.GetAsync(templateId);
            if (template is null)
            {
                StatusBarText.Text = $"模板 {templateId:D} 不存在。";
                await RefreshTemplateListAsync();
                return false;
            }

            var ruleSet = await _ruleStore.GetAsync(templateId);
            if (ruleSet is null)
            {
                StatusBarText.Text = $"模板 {templateId:D} 的规则集不存在（数据不一致）。";
                return false;
            }
            if (ruleSet.TemplateId != template.TemplateId)
            {
                StatusBarText.Text = $"模板 {templateId:D} 的规则集 TemplateId 不一致。";
                return false;
            }

            // 全部成功后才替换 Draft
            UnbindDraft();
            _draft = LayoutTemplateEditorDraft.FromTemplateAndRuleSet(template, ruleSet);
            BindDraft();
            SelectTemplateInList(templateId);
            StatusBarText.Text = $"已加载：{template.Name} (v{template.Version})，{_draft.Rules.Count} 条规则";
            return true;
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"加载失败：{ex.Message}";
            return false;
        }
        finally { _operationInProgress = false; UpdateButtons(); }
    }

    private void SelectTemplateInList(Guid templateId)
    {
        _suppressTemplateSelectionChanged = true;
        foreach (var item in _templateList)
        {
            if (item.Summary.TemplateId == templateId)
            {
                TemplateListBox.SelectedItem = item;
                TemplateListBox.ScrollIntoView(item);
                _suppressTemplateSelectionChanged = false;
                return;
            }
        }
        TemplateListBox.SelectedItem = null;
        _suppressTemplateSelectionChanged = false;
    }

    // ═══ 工具栏 ═══

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (_draft.IsDirty)
        {
            var dr = MessageBox.Show("放弃未保存更改并新建？", "未保存的更改",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (dr != MessageBoxResult.Yes) return;
        }

        UnbindDraft();
        _draft = LayoutTemplateEditorDraft.CreateNew();
        BindDraft();
        _suppressTemplateSelectionChanged = true;
        TemplateListBox.SelectedIndex = -1;
        _suppressTemplateSelectionChanged = false;
        StatusBarText.Text = "已创建新模板。";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || !_templateStoreReady || !_ruleStoreReady) return;

        var element = Keyboard.FocusedElement as UIElement;
        if (element is TextBox tb)
        {
            var binding = BindingOperations.GetBindingExpression(tb, TextBox.TextProperty);
            binding?.UpdateSource();
        }
        Keyboard.ClearFocus();

        if (HasValidationError(this))
        {
            StatusBarText.Text = "存在无法解析的输入，请修正后再保存。";
            return;
        }

        _operationInProgress = true;
        UpdateButtons();
        try
        {
            var buildResult = _draft.TryBuild();
            if (!buildResult.IsValid)
            {
                StatusBarText.Text = $"保存失败：{string.Join("；", buildResult.Errors)}";
                return;
            }
            var template = buildResult.Template!;

            var ruleBuildResult = _draft.TryBuildRuleSet();
            if (!ruleBuildResult.IsValid)
            {
                StatusBarText.Text = $"规则保存失败：{string.Join("；", ruleBuildResult.Errors)}";
                return;
            }
            var ruleSet = ruleBuildResult.RuleSet!;

            // 门禁：保存期间 Draft 未被修改
            if (_draft.TemplateId != template.TemplateId)
            {
                StatusBarText.Text = "保存失败：保存期间草稿身份已变化。";
                return;
            }

            // 使用协调器保存
            var coordResult = await LayoutTemplateAndRulesSaveCoordinator.SaveAsync(
                _templateStore, _ruleStore, template, ruleSet);
            StatusBarText.Text = coordResult.Message!;
            if (!coordResult.IsSuccess) return;

            _draft.MarkSaved();
            StatusBarText.Text = $"保存成功：{template.Name} (v{template.Version})，{ruleSet.RuleCount} 条规则。";

            try
            {
                await RefreshTemplateListAsync();
                SelectTemplateInList(template.TemplateId);
            }
            catch (Exception refreshEx)
            {
                StatusBarText.Text = $"保存成功，但模板列表刷新失败：{refreshEx.Message}。可点击刷新重试。";
            }
            UpdateTitle();
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"保存失败：{ex.Message}";
        }
        finally { _operationInProgress = false; UpdateButtons(); }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || !_templateStoreReady) return;
        if (!_draft.IsPersisted)
        {
            StatusBarText.Text = "仅对已保存的模板可用。";
            return;
        }
        if (_draft.IsDirty)
        {
            var dr = MessageBox.Show("放弃未保存更改并重新加载？", "未保存的更改",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (dr != MessageBoxResult.Yes) return;
        }
        await LoadTemplateAsync(_draft.TemplateId);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || !_templateStoreReady || !_ruleStoreReady || !_draft.IsPersisted) return;

        var templateId = _draft.TemplateId;
        var dr = MessageBox.Show(
            $"确认删除模板？\n\n名称：{_draft.Name}\nTemplateId：{templateId:D}",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (dr != MessageBoxResult.Yes) return;

        _operationInProgress = true;
        UpdateButtons();
        try
        {
            var deleted = await _templateStore.DeleteAsync(templateId);
            var check = await _templateStore.GetAsync(templateId);
            var ruleCheck = await _ruleStore.GetAsync(templateId);

            if (check is not null || ruleCheck is not null)
            {
                StatusBarText.Text = $"删除验证失败：Template={check is not null}，Rules={ruleCheck is not null}。未切换草稿。";
                return;
            }

            UnbindDraft();
            _draft = LayoutTemplateEditorDraft.CreateNew();
            BindDraft();
            StatusBarText.Text = deleted ? "已删除。" : "记录原本已不存在。";

            try { await RefreshTemplateListAsync(); }
            catch (Exception refreshEx)
            { StatusBarText.Text = (deleted ? "已删除" : "记录原本已不存在") + $"，但模板列表刷新失败：{refreshEx.Message}。"; }
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"删除失败：{ex.Message}";
        }
        finally { _operationInProgress = false; UpdateButtons(); }
    }

    private async void RefreshList_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || !_templateStoreReady) return;
        _operationInProgress = true;
        UpdateButtons();
        try { await RefreshTemplateListAsync(); StatusBarText.Text = "模板列表已刷新。"; }
        catch (Exception ex) { StatusBarText.Text = $"刷新失败：{ex.Message}"; }
        finally { _operationInProgress = false; UpdateButtons(); }
    }

    // ═══ Zone 列表交互 ═══

    private void ZoneListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = ZoneListBox.SelectedItem is not null;
        SelectedZonePanel.IsEnabled = hasSelection;
        if (!_operationInProgress)
            RemoveZoneButton.IsEnabled = hasSelection && _draft.Zones.Count > 1;
        RebuildCanvas();
    }

    private void AddZone_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;

        var existing = new List<NormalizedRect>();
        foreach (var z in _draft.Zones)
        {
            if (z.TryGetBounds(out var b, out _) && b is not null)
                existing.Add(b);
        }

        if (!NormalizedZoneEditorGeometry.TryFindFreeRect(existing, out var rect) || rect is null)
        {
            StatusBarText.Text = "无法找到空闲区域添加新分区。";
            return;
        }

        int maxSort = _draft.Zones.Count > 0 ? _draft.Zones.Max(z => z.SortOrder) : -1;
        int newSort;
        try { newSort = checked(maxSort + 1); }
        catch (OverflowException)
        {
            StatusBarText.Text = $"SortOrder 已达到 int.MaxValue ({maxSort})，无法添加更多分区。";
            return;
        }

        var zone = new LayoutZoneEditorDraft(
            Guid.NewGuid(),
            $"分区 {_draft.Zones.Count + 1}",
            rect.Left, rect.Top, rect.Right, rect.Bottom,
            newSort,
            ZonePlacementFlow.ColumnMajor,
            null);

        _draft.Zones.Add(zone);
        ZoneListBox.SelectedItem = zone;
        StatusBarText.Text = $"已添加：{zone.Name}。";
    }

    private void RemoveZone_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (_draft.Zones.Count <= 1)
        {
            StatusBarText.Text = "至少保留一个分区。";
            return;
        }
        if (ZoneListBox.SelectedItem is not LayoutZoneEditorDraft selected) return;

        var dr = MessageBox.Show(
            $"确认删除分区 \"{selected.Name}\"？", "确认删除分区",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (dr != MessageBoxResult.Yes) return;

        var idx = _draft.Zones.IndexOf(selected);
        _draft.Zones.Remove(selected);
        if (_draft.Zones.Count > 0)
            ZoneListBox.SelectedIndex = Math.Min(idx, _draft.Zones.Count - 1);
        StatusBarText.Text = $"已删除：{selected.Name}。";
    }

    // ═══ 画布 ═══

    private void RebuildCanvas()
    {
        ZoneCanvas.Children.Clear();
        _zoneVisuals.Clear();

        var selectedZone = ZoneListBox.SelectedItem as LayoutZoneEditorDraft;

        foreach (var zone in _draft.Zones)
        {
            var isSelected = ReferenceEquals(zone, selectedZone);
            var visualData = CreateZoneVisual(zone, isSelected);
            _zoneVisuals[zone] = visualData;
            ZoneCanvas.Children.Add(visualData.Root);
        }

        UpdateAllZonePositions();
    }

    private ZoneVisualData CreateZoneVisual(LayoutZoneEditorDraft zone, bool isSelected)
    {
        var root = new Grid();

        var bgColor = isSelected
            ? Color.FromArgb(0x60, 0x00, 0x78, 0xD4)
            : Color.FromArgb(0x30, 0x00, 0x78, 0xD4);
        var borderBrush = isSelected
            ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        var borderThickness = isSelected ? new Thickness(2) : new Thickness(1);
        var isValid = zone.TryGetBounds(out _, out _);

        var border = new Border
        {
            Background = new SolidColorBrush(isValid ? bgColor : Color.FromArgb(0x30, 0xE0, 0x00, 0x00)),
            BorderBrush = isValid ? borderBrush : new SolidColorBrush(Colors.Red),
            BorderThickness = isValid ? borderThickness : new Thickness(2)
        };

        var content = new StackPanel();
        var nameLabel = new TextBlock
        {
            Text = zone.Name,
            FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(4, 2, 4, 0)
        };
        var soLabel = new TextBlock
        {
            Text = $"SO: {zone.SortOrder}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            Margin = new Thickness(4, 0, 4, 2)
        };
        content.Children.Add(nameLabel);
        content.Children.Add(soLabel);
        border.Child = content;
        root.Children.Add(border);

        var mainThumb = new Thumb
        {
            Cursor = Cursors.SizeAll, Tag = zone,
            Opacity = 0, Background = Brushes.Transparent
        };
        mainThumb.DragDelta += ZoneThumb_DragDelta;
        mainThumb.DragStarted += ZoneThumb_DragStarted;
        mainThumb.DragCompleted += ZoneThumb_DragCompleted;
        mainThumb.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (!_operationInProgress)
                ZoneListBox.SelectedItem = zone;
        };
        root.Children.Add(mainThumb);

        if (isSelected)
        {
            AddResizeCorner(root, zone, ZoneResizeHandle.TopLeft, Cursors.SizeNWSE);
            AddResizeCorner(root, zone, ZoneResizeHandle.TopRight, Cursors.SizeNESW);
            AddResizeCorner(root, zone, ZoneResizeHandle.BottomLeft, Cursors.SizeNESW);
            AddResizeCorner(root, zone, ZoneResizeHandle.BottomRight, Cursors.SizeNWSE);
        }

        root.HorizontalAlignment = HorizontalAlignment.Left;
        root.VerticalAlignment = VerticalAlignment.Top;

        return new ZoneVisualData { Root = root, NameLabel = nameLabel, SoLabel = soLabel, IsValid = isValid };
    }

    private void AddResizeCorner(Grid root, LayoutZoneEditorDraft zone, ZoneResizeHandle handle, Cursor cursor)
    {
        var thumb = new Thumb
        {
            Width = 12, Height = 12, Cursor = cursor,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            BorderThickness = new Thickness(1), Opacity = 0.9
        };
        thumb.DragDelta += (_, e2) => ResizeThumb_DragDelta(zone, handle, e2);
        thumb.DragStarted += (_, _) => ResizeThumb_DragStarted(zone, handle);
        thumb.DragCompleted += (_, _) => ResizeThumb_DragCompleted(zone);

        switch (handle)
        {
            case ZoneResizeHandle.TopLeft:
                thumb.HorizontalAlignment = HorizontalAlignment.Left;
                thumb.VerticalAlignment = VerticalAlignment.Top;
                thumb.Margin = new Thickness(-6, -6, 0, 0); break;
            case ZoneResizeHandle.TopRight:
                thumb.HorizontalAlignment = HorizontalAlignment.Right;
                thumb.VerticalAlignment = VerticalAlignment.Top;
                thumb.Margin = new Thickness(0, -6, -6, 0); break;
            case ZoneResizeHandle.BottomLeft:
                thumb.HorizontalAlignment = HorizontalAlignment.Left;
                thumb.VerticalAlignment = VerticalAlignment.Bottom;
                thumb.Margin = new Thickness(-6, 0, 0, -6); break;
            case ZoneResizeHandle.BottomRight:
                thumb.HorizontalAlignment = HorizontalAlignment.Right;
                thumb.VerticalAlignment = VerticalAlignment.Bottom;
                thumb.Margin = new Thickness(0, 0, -6, -6); break;
        }
        root.Children.Add(thumb);
    }

    private void UpdateZoneCanvasPosition(ZoneVisualData visual, LayoutZoneEditorDraft zone)
    {
        if (ZoneCanvas.ActualWidth <= 0 || ZoneCanvas.ActualHeight <= 0) return;
        if (double.IsNaN(ZoneCanvas.ActualWidth) || double.IsInfinity(ZoneCanvas.ActualWidth)
            || double.IsNaN(ZoneCanvas.ActualHeight) || double.IsInfinity(ZoneCanvas.ActualHeight)) return;

        var cw = ZoneCanvas.ActualWidth;
        var ch = ZoneCanvas.ActualHeight;
        long w = (long)zone.Right - zone.Left;
        long h = (long)zone.Bottom - zone.Top;
        var x = zone.Left / 10000.0 * cw;
        var y = zone.Top / 10000.0 * ch;
        var cw2 = w / 10000.0 * cw;
        var ch2 = h / 10000.0 * ch;

        if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)
            || double.IsNaN(cw2) || double.IsInfinity(cw2) || double.IsNaN(ch2) || double.IsInfinity(ch2)) return;

        visual.Root.Width = Math.Max(1, cw2);
        visual.Root.Height = Math.Max(1, ch2);
        Canvas.SetLeft(visual.Root, x);
        Canvas.SetTop(visual.Root, y);
    }

    private void UpdateAllZonePositions()
    {
        foreach (var (zone, visual) in _zoneVisuals)
            UpdateZoneCanvasPosition(visual, zone);
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAllZonePositions();

    // ═══ 安全 delta 转换 ═══

    private bool TryGetSnappedNormalizedDelta(out int deltaX, out int deltaY, out string? error)
    {
        deltaX = 0; deltaY = 0; error = null;

        if (ZoneCanvas.ActualWidth <= 0 || ZoneCanvas.ActualHeight <= 0)
        { error = "画布尺寸无效。"; return false; }
        if (double.IsNaN(ZoneCanvas.ActualWidth) || double.IsNaN(ZoneCanvas.ActualHeight)
            || double.IsInfinity(ZoneCanvas.ActualWidth) || double.IsInfinity(ZoneCanvas.ActualHeight))
        { error = "画布尺寸不是有限数值。"; return false; }

        if (double.IsNaN(_dragAccumulatedPixelX) || double.IsInfinity(_dragAccumulatedPixelX)
            || double.IsNaN(_dragAccumulatedPixelY) || double.IsInfinity(_dragAccumulatedPixelY))
        { error = "累计拖动差值为非有限数值。"; return false; }

        var normX = _dragAccumulatedPixelX / ZoneCanvas.ActualWidth * 10000;
        var normY = _dragAccumulatedPixelY / ZoneCanvas.ActualHeight * 10000;

        if (double.IsNaN(normX) || double.IsInfinity(normX)
            || double.IsNaN(normY) || double.IsInfinity(normY))
        { error = "归一化差值为非有限数值。"; return false; }

        var roundedX = Math.Round(normX, MidpointRounding.AwayFromZero);
        var roundedY = Math.Round(normY, MidpointRounding.AwayFromZero);

        if (double.IsNaN(roundedX) || double.IsInfinity(roundedX)
            || double.IsNaN(roundedY) || double.IsInfinity(roundedY))
        { error = "舍入值为非有限数值。"; return false; }

        if (roundedX < int.MinValue || roundedX > int.MaxValue
            || roundedY < int.MinValue || roundedY > int.MaxValue)
        { error = $"舍入值超出 int 范围：({roundedX}, {roundedY})。"; return false; }

        try
        {
            var rawX = checked((int)roundedX);
            var rawY = checked((int)roundedY);
            deltaX = NormalizedZoneEditorGeometry.SnapDelta(rawX, NormalizedZoneEditorGeometry.DefaultSnapStep);
            deltaY = NormalizedZoneEditorGeometry.SnapDelta(rawY, NormalizedZoneEditorGeometry.DefaultSnapStep);
            return true;
        }
        catch (OverflowException)
        { error = "归一化差值溢出。"; return false; }
    }

    // ═══ 拖动：移动 ═══

    private void ZoneThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (_operationInProgress) return;
        if (sender is not Thumb thumb || thumb.Tag is not LayoutZoneEditorDraft zone) return;
        _draggingZone = zone;
        if (zone.TryGetBounds(out var b, out _) && b is not null)
            _dragOriginalBounds = b;
        _dragAccumulatedPixelX = 0;
        _dragAccumulatedPixelY = 0;
    }

    private void ZoneThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        try
        {
            if (_draggingZone is null || _dragOriginalBounds is null) return;
            _dragAccumulatedPixelX += e.HorizontalChange;
            _dragAccumulatedPixelY += e.VerticalChange;

            if (!TryGetSnappedNormalizedDelta(out var dx, out var dy, out var error))
            {
                if (error is not null) StatusBarText.Text = error;
                return;
            }

            var candidate = NormalizedZoneEditorGeometry.Move(_dragOriginalBounds, dx, dy);

            var others = new List<NormalizedRect>();
            foreach (var z in _draft.Zones)
            {
                if (ReferenceEquals(z, _draggingZone)) continue;
                if (z.TryGetBounds(out var ob, out _) && ob is not null)
                    others.Add(ob);
            }

            if (NormalizedZoneEditorGeometry.OverlapsAny(candidate, others))
            {
                StatusBarText.Text = "分区重叠，变更已拒绝。";
                return;
            }

            _updatingDraftFromCanvas = true;
            try
            {
                _draggingZone.Left = candidate.Left;
                _draggingZone.Top = candidate.Top;
                _draggingZone.Right = candidate.Right;
                _draggingZone.Bottom = candidate.Bottom;
            }
            finally { _updatingDraftFromCanvas = false; }

            UpdateAllZonePositions();
            StatusBarText.Text = "";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"拖动处理异常：{ex.Message}";
        }
    }

    private void ZoneThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _draggingZone = null;
        _dragOriginalBounds = null;
        _dragAccumulatedPixelX = 0;
        _dragAccumulatedPixelY = 0;
    }

    // ═══ 拖动：缩放 ═══

    private void ResizeThumb_DragStarted(LayoutZoneEditorDraft zone, ZoneResizeHandle handle)
    {
        if (_operationInProgress) return;
        _draggingZone = zone;
        if (zone.TryGetBounds(out var b, out _) && b is not null)
            _dragOriginalBounds = b;
        _dragAccumulatedPixelX = 0;
        _dragAccumulatedPixelY = 0;
    }

    private void ResizeThumb_DragDelta(LayoutZoneEditorDraft zone, ZoneResizeHandle handle, DragDeltaEventArgs e)
    {
        try
        {
            if (_dragOriginalBounds is null) return;
            _dragAccumulatedPixelX += e.HorizontalChange;
            _dragAccumulatedPixelY += e.VerticalChange;

            if (!TryGetSnappedNormalizedDelta(out var dx, out var dy, out var error))
            {
                if (error is not null) StatusBarText.Text = error;
                return;
            }

            var candidate = NormalizedZoneEditorGeometry.Resize(
                _dragOriginalBounds, handle, dx, dy,
                NormalizedZoneEditorGeometry.DefaultMinimumSize);

            var others = new List<NormalizedRect>();
            foreach (var z in _draft.Zones)
            {
                if (ReferenceEquals(z, zone)) continue;
                if (z.TryGetBounds(out var ob, out _) && ob is not null)
                    others.Add(ob);
            }

            if (NormalizedZoneEditorGeometry.OverlapsAny(candidate, others))
            {
                StatusBarText.Text = "分区重叠，变更已拒绝。";
                return;
            }

            _updatingDraftFromCanvas = true;
            try
            {
                zone.Left = candidate.Left;
                zone.Top = candidate.Top;
                zone.Right = candidate.Right;
                zone.Bottom = candidate.Bottom;
            }
            finally { _updatingDraftFromCanvas = false; }

            UpdateAllZonePositions();
            StatusBarText.Text = "";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"缩放处理异常：{ex.Message}";
        }
    }

    private void ResizeThumb_DragCompleted(LayoutZoneEditorDraft zone)
    {
        _draggingZone = null;
        _dragOriginalBounds = null;
        _dragAccumulatedPixelX = 0;
        _dragAccumulatedPixelY = 0;
    }

    // ═══ 按钮状态 ═══

    private void UpdateButtons()
    {
        var canAct = !_operationInProgress;
        EditorWorkspace.IsEnabled = canAct;
        SaveButton.IsEnabled = canAct && _templateStoreReady;
        DeleteButton.IsEnabled = canAct && _templateStoreReady && _draft.IsPersisted;
        ReloadButton.IsEnabled = canAct && _templateStoreReady && _draft.IsPersisted;
        RefreshListButton.IsEnabled = canAct && _templateStoreReady;
        NewButton.IsEnabled = canAct;
        AddZoneButton.IsEnabled = canAct;
        RemoveZoneButton.IsEnabled = canAct && ZoneListBox.SelectedItem is not null && _draft.Zones.Count > 1;
    }

    // ═══ 关闭保护 ═══

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operationInProgress)
        {
            StatusBarText.Text = "当前操作尚未完成，请稍后再试。";
            e.Cancel = true;
            return;
        }

        if (_draft.IsDirty)
        {
            var dr = MessageBox.Show(
                "存在未保存的模板修改。确定放弃并关闭吗？",
                "未保存的更改", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (dr != MessageBoxResult.Yes) { e.Cancel = true; }
        }
    }

    // ═══ 验证辅助 ═══

    private static bool HasValidationError(DependencyObject obj)
    {
        if (Validation.GetHasError(obj)) return true;
        foreach (var child in LogicalTreeHelper.GetChildren(obj).OfType<DependencyObject>())
            if (HasValidationError(child)) return true;
        return false;
    }

    // ═══ 语义比较 ═══

    private static string? CompareTemplates(DesktopLayoutTemplate a, DesktopLayoutTemplate b)
    {
        if (a.TemplateId != b.TemplateId) return "TemplateId 不匹配";
        if (!StringComparer.Ordinal.Equals(a.Name, b.Name)) return $"Name 不匹配";
        if (a.Version != b.Version) return "Version 不匹配";
        if (a.TargetDisplayBinding.Kind != b.TargetDisplayBinding.Kind) return "BindingKind 不匹配";
        if (!StringComparer.Ordinal.Equals(a.TargetDisplayBinding.DeviceName ?? "", b.TargetDisplayBinding.DeviceName ?? "")) return "DeviceName 不匹配";
        if (a.Zones.Count != b.Zones.Count) return $"Zones 数量不匹配";

        for (var i = 0; i < a.Zones.Count; i++)
        {
            var za = a.Zones[i]; var zb = b.Zones[i];
            if (za.ZoneId != zb.ZoneId) return $"Zone[{i}] ZoneId 不匹配";
            if (!StringComparer.Ordinal.Equals(za.Name, zb.Name)) return $"Zone[{i}] Name 不匹配";
            if (za.Bounds.Left != zb.Bounds.Left || za.Bounds.Top != zb.Bounds.Top
                || za.Bounds.Right != zb.Bounds.Right || za.Bounds.Bottom != zb.Bounds.Bottom)
                return $"Zone[{i}] Bounds 不匹配";
            if (za.SortOrder != zb.SortOrder) return $"Zone[{i}] SortOrder 不匹配";
            if (za.PlacementFlow != zb.PlacementFlow) return $"Zone[{i}] PlacementFlow 不匹配";
            if (za.MaxItemCount != zb.MaxItemCount) return $"Zone[{i}] MaxItemCount 不匹配";
        }
        return null;
    }
}

internal sealed class ZoneVisualData
{
    public required Grid Root { get; init; }
    public required TextBlock NameLabel { get; init; }
    public required TextBlock SoLabel { get; init; }
    public bool IsValid { get; set; }
}
