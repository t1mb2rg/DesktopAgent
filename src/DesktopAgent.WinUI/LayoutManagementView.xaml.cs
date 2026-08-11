using System.Collections.ObjectModel;
using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Productization;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.DesktopLayouts.Templates.Rules;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.DesktopOverlays;
using DesktopAgent.WindowsInterop.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView : UserControl
{
    private const int LayoutListLimit = 100;
    private const double CanvasInset = 12d;

    private readonly SqliteDesktopLayoutTemplateStore _templateStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly SqliteDesktopLayoutIconRuleStore _ruleStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly JsonDesktopProductSettingsStore _settingsStore =
        new(DesktopAgentDataPaths.DefaultSettingsPath);
    private readonly StaThreadExecutor _staThreadExecutor = new();
    private readonly WindowsDesktopObservationSource _observationSource;
    private readonly DesktopZoneOverlayHost _overlayHost = new();

    private readonly ObservableCollection<LayoutTemplateListItem> _templateItems = [];
    private readonly ObservableCollection<LayoutZoneListItem> _zoneItems = [];
    private readonly ObservableCollection<LayoutPreviewListItem> _previewItems = [];

    private DesktopProductSettings _settings = DesktopProductSettings.CreateDefault();
    private DesktopLayoutEditDraft? _draft;
    private DesktopLayoutImpactPreview? _latestPreview;
    private bool _initialized;

    public LayoutManagementView()
    {
        InitializeComponent();

        _observationSource = new WindowsDesktopObservationSource(
            new NativeDisplayTopologyReader(),
            new DesktopShellIconReader(),
            new DesktopShellIconPositioningCapabilitiesReader(),
            _staThreadExecutor);

        LayoutTemplateList.ItemsSource = _templateItems;
        LayoutZoneList.ItemsSource = _zoneItems;
        LayoutPreviewList.ItemsSource = _previewItems;
        Loaded += LayoutManagementView_Loaded;
        Unloaded += LayoutManagementView_Unloaded;
    }

    private async void LayoutManagementView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        _initialized = true;
        try
        {
            await EnsureStoresAsync();
            await RefreshLayoutsAsync();
            RenderLayoutCanvas();
        }
        catch (Exception ex)
        {
            ShowError("无法初始化布局", ex.Message);
        }
    }

    private void LayoutManagementView_Unloaded(object sender, RoutedEventArgs e)
        => HideOverlay();

    private async Task EnsureStoresAsync()
    {
        await _templateStore.InitializeAsync();
        await _ruleStore.InitializeAsync();
        await _settingsStore.InitializeAsync();
    }

    private async void RefreshLayouts_Click(object sender, RoutedEventArgs e)
    {
        if (_zoneColorPreferencesDirty)
        {
            ShowInfo("分区颜色尚未应用", "先应用或还原当前颜色修改，再刷新布局列表。", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            await RefreshLayoutsAsync();
            ShowInfo("已刷新", "布局列表已更新。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError("无法刷新布局", ex.Message);
        }
    }

    private async Task RefreshLayoutsAsync(Guid? selectTemplateId = null)
    {
        await EnsureStoresAsync();
        _settings = await _settingsStore.GetAsync();
        var summaries = await _templateStore.ListAsync(LayoutListLimit);

        _templateItems.Clear();
        foreach (var summary in summaries)
        {
            _templateItems.Add(LayoutTemplateListItem.From(
                summary,
                _settings.ActiveLayoutTemplateId == summary.TemplateId));
        }

        LayoutSummaryText.Text = summaries.Count == 0
            ? "还没有保存的布局"
            : $"{summaries.Count} 个布局";

        var desiredId = selectTemplateId ?? _settings.ActiveLayoutTemplateId;
        if (desiredId.HasValue)
        {
            LayoutTemplateList.SelectedItem = _templateItems.FirstOrDefault(
                item => item.TemplateId == desiredId.Value);
        }
    }

    private void NewLayout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LayoutPresetCombo.SelectedIndex = (int)DesktopLayoutPresetKind.TwoColumns;
            _draft = DesktopLayoutPresetFactory.CreateNewDraft(
                "新布局",
                DesktopLayoutPresetKind.TwoColumns,
                TargetDisplayBinding.ForPrimaryDisplay());
            LoadDraftIntoEditor(_draft);
            ShowInfo("新布局已就绪", "先看看分区，需要时再保存。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowError("无法创建布局", ex.Message);
        }
    }

    private async void LoadLayout_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem selected)
        {
            ShowError("请选择布局", "先在左侧选一个布局。 ");
            return;
        }

        try
        {
            await EnsureStoresAsync();
            _settings = await _settingsStore.GetAsync();
            var template = await _templateStore.GetAsync(selected.TemplateId)
                ?? throw new InvalidOperationException("所选布局已不存在，请刷新列表。");
            var rules = await _ruleStore.GetAsync(selected.TemplateId)
                ?? throw new InvalidDataException("布局存在，但 RuleSet 缺失。拒绝进入半状态编辑。");

            _draft = DesktopLayoutEditDraft.FromPersisted(template, rules);
            LoadDraftIntoEditor(_draft);
            ShowInfo("已打开布局", "编辑期间自动化保持暂停。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowError("无法打开布局", ex.Message);
        }
    }

    private async Task<bool> EnsureDraftForSelectedLayoutAsync()
    {
        if (_draft is not null)
            return true;

        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem selected)
        {
            ShowError("请选择布局", "先在左侧选择一个布局，或新建一个布局。 ");
            return false;
        }

        await EnsureStoresAsync();
        _settings = await _settingsStore.GetAsync();
        var template = await _templateStore.GetAsync(selected.TemplateId)
            ?? throw new InvalidOperationException("所选布局已不存在，请刷新列表。");
        var rules = await _ruleStore.GetAsync(selected.TemplateId)
            ?? throw new InvalidDataException("布局存在，但 RuleSet 缺失。拒绝进入半状态编辑。");

        _draft = DesktopLayoutEditDraft.FromPersisted(template, rules);
        LoadDraftIntoEditor(_draft);
        return true;
    }

    private async void SetActiveLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_zoneColorPreferencesDirty)
        {
            ShowInfo("分区颜色尚未应用", "先应用或还原当前颜色修改，再切换当前布局。", InfoBarSeverity.Informational);
            return;
        }

        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem selected)
        {
            ShowError("请选择布局", "先在左侧选一个要使用的布局。 ");
            return;
        }

        try
        {
            await EnsureStoresAsync();
            _ = await _templateStore.GetAsync(selected.TemplateId)
                ?? throw new InvalidOperationException("所选布局已不存在，请刷新列表。");

            _settings = await _settingsStore.GetAsync();
            _settings = _settings.WithActiveLayoutTemplate(selected.TemplateId);
            await _settingsStore.SaveAsync(_settings);
            await RefreshLayoutsAsync(selected.TemplateId);

            ShowInfo("当前布局已切换", "这一步只切换偏好，不会移动桌面图标。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError("无法切换布局", ex.Message);
        }
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
        {
            ShowError("还没有布局", "先新建或打开一个布局。 ");
            return;
        }

        if (_zoneColorPreferencesDirty)
        {
            ShowInfo("分区颜色尚未应用", "先应用或还原颜色修改，再替换分区。", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            var draft = BuildDraftFromEditor();
            if (draft.RuleSet.Rules.Any(rule => rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride))
            {
                throw new InvalidOperationException(
                    "当前布局包含 ZoneOverride 规则。为避免旧规则指向失效分区，本阶段不会自动替换这些 Zone。 ");
            }

            if (LayoutPresetCombo.SelectedIndex is < 0 or > 3)
                throw new InvalidOperationException("请选择有效布局预设。 ");

            var preset = (DesktopLayoutPresetKind)LayoutPresetCombo.SelectedIndex;
            _draft = draft.WithZones(DesktopLayoutPresetFactory.CreateZones(preset));
            LoadDraftIntoEditor(_draft);
            ShowInfo("分区已更新", "画布已按新分区刷新。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowError("无法应用分区", ex.Message);
        }
    }

    private async void PreviewLayout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureDraftForSelectedLayoutAsync())
                return;

            var draft = BuildDraftFromEditor();
            LayoutCanvasStatusText.Text = "正在读取当前桌面…";
            var preview = await CapturePreviewAsync(draft);
            ApplyPreviewToUi(preview);
            _draft = draft;

            ShowInfo("预览已刷新", "画布展示的是刚刚观察到的桌面影响。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            LayoutCanvasStatusText.Text = "当前无法生成预览";
            ShowError("无法预览", ex.Message);
        }
    }

    private async void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayHost.IsVisible)
        {
            HideOverlay();
            ShowInfo("桌面线框已隐藏", "", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            if (!await EnsureDraftForSelectedLayoutAsync())
                return;

            var draft = BuildDraftFromEditor();
            LayoutCanvasStatusText.Text = "正在定位目标显示器…";
            var preview = await CapturePreviewAsync(draft);

            _settings = await _settingsStore.GetAsync();
            ApplyPreviewToUi(preview);
            _overlayHost.Show(
                preview.Plan.TargetDisplay,
                draft.Zones,
                _settings.OverlayAppearance,
                GetCurrentZoneColorOverridesSnapshot());
            _draft = draft;
            OverlayToggleButton.Content = "▣ 隐藏线框";
            OverlayStateText.Text = "线框显示中 · 鼠标穿透";

            ShowInfo(
                "桌面线框已显示",
                "它只是一层鼠标穿透的辅助框，不会触碰桌面图标。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            HideOverlay();
            ShowError("无法显示桌面线框", ex.Message);
        }
    }

    private async Task<DesktopLayoutImpactPreview> CapturePreviewAsync(DesktopLayoutEditDraft draft)
    {
        var selectedTargetDeviceName = draft.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget
            ? NormalizeRequiredDeviceName(LayoutDeviceNameTextBox.Text, "当前选择的显示器需要填写设备名。")
            : null;

        var capture = await _observationSource.CaptureAsync();
        if (!capture.IsCaptured || capture.Observation is null)
        {
            var details = string.Join("；", capture.Issues.Select(issue => issue.Message));
            throw new InvalidOperationException($"当前桌面事实不可用：{details}");
        }

        return DesktopLayoutImpactPreviewService.Create(
            draft,
            capture.Observation,
            selectedTargetDeviceName);
    }

    private void ApplyPreviewToUi(DesktopLayoutImpactPreview preview)
    {
        _latestPreview = preview;
        _previewItems.Clear();

        var plan = preview.Plan;
        MoveCountText.Text = plan.MoveRequiredCount.ToString();
        StayCountText.Text = plan.AlreadyAtTargetCount.ToString();
        TargetDisplayText.Text = plan.TargetDisplay.DeviceName;
        LayoutCanvasStatusText.Text = plan.IsComplete
            ? "预览已刷新 · 蓝点是预计位置"
            : "预览还有未规划项目";

        LayoutPreviewSummaryText.Text =
            $"输入 {plan.InputIconCount} · 移动 {plan.MoveRequiredCount} · 保持 {plan.AlreadyAtTargetCount} · " +
            $"排除 {plan.ExcludedCount} · 固定 {plan.PinnedCount} · 完整 {(plan.IsComplete ? "是" : "否")}";
        LayoutPreviewObservationText.Text =
            $"Observation {preview.ObservationId:D}\n{preview.ObservedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}";

        foreach (var item in plan.Items.Take(100))
            _previewItems.Add(LayoutPreviewListItem.From(item));

        LayoutPreviewDetails.Visibility = Visibility.Visible;
        RenderLayoutCanvas();
    }

    private async void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
        {
            ShowError("还没有布局", "先新建或打开一个布局。 ");
            return;
        }

        if (_zoneColorPreferencesDirty)
        {
            ShowInfo("分区颜色尚未应用", "先应用或还原颜色修改，再保存布局。", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            await EnsureStoresAsync();
            var draft = BuildDraftFromEditor();
            var template = draft.BuildNextPersistedTemplate();

            var result = await LayoutTemplateAndRulesSaveCoordinator.SaveAsync(
                _templateStore,
                _ruleStore,
                template,
                draft.RuleSet);

            if (!result.IsSuccess)
                throw new InvalidOperationException(result.Message ?? $"保存失败：{result.Outcome}");

            _draft = DesktopLayoutEditDraft.FromPersisted(template, draft.RuleSet);
            LoadDraftIntoEditor(_draft);
            await RefreshLayoutsAsync(template.TemplateId);

            ShowInfo("布局已保存", $"V{template.Version}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError("无法保存布局", ex.Message);
        }
    }

    private void CancelLayoutEdit_Click(object sender, RoutedEventArgs e)
    {
        _draft = null;
        _latestPreview = null;
        _zoneItems.Clear();
        _previewItems.Clear();
        ClearZoneColorPreferencesForView();
        LayoutEditorPanel.Visibility = Visibility.Collapsed;
        LayoutEmptyEditorState.Visibility = Visibility.Visible;
        EditingBadge.Visibility = Visibility.Collapsed;
        LayoutPreviewDetails.Visibility = Visibility.Collapsed;
        LayoutCanvasDraftNameText.Text = "桌面画布";
        LayoutCanvasStatusText.Text = "新建或加载一个布局开始编辑";
        ResetPreviewSummary();
        HideOverlay();
        RenderLayoutCanvas();
        ShowInfo("已退出编辑", "未保存的修改已丢弃。", InfoBarSeverity.Informational);
    }

    private DesktopLayoutEditDraft BuildDraftFromEditor()
    {
        var draft = _draft ?? throw new InvalidOperationException("当前没有布局草稿。 ");
        var binding = BuildTargetBindingFromEditor();
        return draft.WithMetadata(LayoutNameTextBox.Text, binding);
    }

    private TargetDisplayBinding BuildTargetBindingFromEditor()
        => LayoutTargetBindingCombo.SelectedIndex switch
        {
            0 => TargetDisplayBinding.ForPrimaryDisplay(),
            1 => TargetDisplayBinding.ForSelectedTarget(),
            2 => TargetDisplayBinding.ForExactDeviceName(
                NormalizeRequiredDeviceName(
                    LayoutDeviceNameTextBox.Text,
                    "固定设备名模式必须填写设备名。")),
            _ => throw new InvalidOperationException("请选择有效目标显示器绑定。"),
        };

    private static string NormalizeRequiredDeviceName(string value, string error)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
            throw new InvalidOperationException(error);
        return normalized;
    }

    private void LoadDraftIntoEditor(DesktopLayoutEditDraft draft)
    {
        HideOverlay();
        _latestPreview = null;
        _previewItems.Clear();
        ResetPreviewSummary();

        LayoutEditorPanel.Visibility = Visibility.Visible;
        LayoutEmptyEditorState.Visibility = Visibility.Collapsed;
        EditingBadge.Visibility = Visibility.Visible;
        LayoutPreviewDetails.Visibility = Visibility.Collapsed;

        LayoutNameTextBox.Text = draft.Name;
        LayoutDeviceNameTextBox.Text = draft.TargetDisplayBinding.DeviceName ?? string.Empty;
        LayoutTargetBindingCombo.SelectedIndex = draft.TargetDisplayBinding.Kind switch
        {
            TargetDisplayBindingKind.PrimaryDisplay => 0,
            TargetDisplayBindingKind.SelectedTarget => 1,
            TargetDisplayBindingKind.ExactDeviceName => 2,
            _ => 0,
        };

        LayoutCanvasDraftNameText.Text = draft.Name;
        LayoutCanvasStatusText.Text = $"{draft.Zones.Count} 个分区 · 尚未预览当前桌面";
        LayoutRuleSummaryText.Text = $"{draft.RuleSet.RuleCount} 条规则";

        _zoneItems.Clear();
        foreach (var zone in draft.Zones.OrderBy(zone => zone.SortOrder))
            _zoneItems.Add(LayoutZoneListItem.From(zone));

        InitializeZoneColorPreferencesForDraft(draft);
        RenderLayoutCanvas();
    }

    private void LayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => RenderLayoutCanvas();

    private void RenderLayoutCanvas()
    {
        LayoutCanvas.Children.Clear();
        var draft = _draft;
        LayoutCanvasEmptyState.Visibility = draft is null ? Visibility.Visible : Visibility.Collapsed;
        if (draft is null)
            return;

        var width = Math.Max(100d, LayoutCanvas.ActualWidth);
        var height = Math.Max(100d, LayoutCanvas.ActualHeight);
        var drawableWidth = Math.Max(1d, width - CanvasInset * 2);
        var drawableHeight = Math.Max(1d, height - CanvasInset * 2);

        var orderedZones = draft.Zones.OrderBy(zone => zone.SortOrder).ToArray();
        foreach (var zone in orderedZones)
        {
            var color = ResolveZoneCanvasColor(zone.ZoneId);
            var left = CanvasInset + drawableWidth * zone.Bounds.Left / NormalizedRect.Scale;
            var top = CanvasInset + drawableHeight * zone.Bounds.Top / NormalizedRect.Scale;
            var zoneWidth = drawableWidth * zone.Bounds.Width / NormalizedRect.Scale;
            var zoneHeight = drawableHeight * zone.Bounds.Height / NormalizedRect.Scale;

            var label = new TextBlock
            {
                Text = zone.Name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(10, 8, 8, 8),
            };

            var zoneBlock = new Border
            {
                Width = Math.Max(1d, zoneWidth),
                Height = Math.Max(1d, zoneHeight),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1.5),
                BorderBrush = Brush(155, color.R, color.G, color.B),
                Background = Brush(28, color.R, color.G, color.B),
                Child = label,
            };

            Canvas.SetLeft(zoneBlock, left);
            Canvas.SetTop(zoneBlock, top);
            LayoutCanvas.Children.Add(zoneBlock);
        }

        if (_latestPreview is null)
            return;

        var work = _latestPreview.Plan.TargetDisplay.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
            return;

        foreach (var item in _latestPreview.Plan.Items.Take(80))
        {
            if (!item.TargetPosition.HasValue)
                continue;

            var target = item.TargetPosition.Value;
            var targetX = CanvasInset + drawableWidth * (target.X - work.Left) / work.Width;
            var targetY = CanvasInset + drawableHeight * (target.Y - work.Top) / work.Height;
            if (targetX < CanvasInset || targetX > width - CanvasInset ||
                targetY < CanvasInset || targetY > height - CanvasInset)
            {
                continue;
            }

            var current = item.CurrentPosition;
            var currentX = CanvasInset + drawableWidth * (current.X - work.Left) / work.Width;
            var currentY = CanvasInset + drawableHeight * (current.Y - work.Top) / work.Height;
            var hasVisibleCurrent =
                currentX >= CanvasInset && currentX <= width - CanvasInset &&
                currentY >= CanvasInset && currentY <= height - CanvasInset;

            if (hasVisibleCurrent &&
                (Math.Abs(currentX - targetX) > 1d || Math.Abs(currentY - targetY) > 1d))
            {
                var path = new Line
                {
                    X1 = currentX,
                    Y1 = currentY,
                    X2 = targetX,
                    Y2 = targetY,
                    StrokeThickness = 1,
                    Stroke = Brush(70, 0x00, 0x78, 0xD4),
                };
                LayoutCanvas.Children.Add(path);
            }

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brush(225, 0x00, 0x78, 0xD4),
                Stroke = Brush(255, 0xFF, 0xFF, 0xFF),
                StrokeThickness = 1,
            };
            ToolTipService.SetToolTip(dot, item.Icon.DisplayName);
            Canvas.SetLeft(dot, targetX - 4);
            Canvas.SetTop(dot, targetY - 4);
            LayoutCanvas.Children.Add(dot);
        }
    }

    private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
        => new(Color.FromArgb(alpha, red, green, blue));

    private void ResetPreviewSummary()
    {
        MoveCountText.Text = "—";
        StayCountText.Text = "—";
        TargetDisplayText.Text = "—";
        LayoutPreviewSummaryText.Text = string.Empty;
        LayoutPreviewObservationText.Text = string.Empty;
    }

    private void HideOverlay()
    {
        _overlayHost.Hide();
        OverlayToggleButton.Content = "▣ 桌面线框";
        OverlayStateText.Text = "线框关闭";
    }

    private void ShowError(string title, string message)
        => ShowInfo(title, message, InfoBarSeverity.Error);

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        LayoutStatusBar.Severity = severity;
        LayoutStatusBar.Title = title;
        LayoutStatusBar.Message = message;
        LayoutStatusBar.IsOpen = true;
    }

    private sealed record LayoutTemplateListItem(
        Guid TemplateId,
        string Name,
        string SecondaryText,
        string ActiveText)
    {
        public static LayoutTemplateListItem From(
            DesktopLayoutTemplateSummary summary,
            bool isActive)
            => new(
                summary.TemplateId,
                summary.Name,
                $"V{summary.Version} · {summary.ZoneCount} 区",
                isActive ? "当前" : string.Empty);
    }

    private sealed record LayoutZoneListItem(
        string Name,
        string BoundsText,
        string FlowText)
    {
        public static LayoutZoneListItem From(DesktopLayoutZone zone)
            => new(
                zone.Name,
                $"{zone.Bounds.Width / 100d:0.#}% × {zone.Bounds.Height / 100d:0.#}%",
                zone.PlacementFlow == ZonePlacementFlow.ColumnMajor ? "列" : "行");
    }

    private sealed record LayoutPreviewListItem(
        string DisplayName,
        string StateText,
        string PositionText)
    {
        public string CompactText => $"{StateText} · {PositionText}";

        public static LayoutPreviewListItem From(DesktopLayoutRuleAwarePreviewItem item)
        {
            var target = item.TargetPosition.HasValue
                ? $"({item.TargetPosition.Value.X}, {item.TargetPosition.Value.Y})"
                : "—";
            return new LayoutPreviewListItem(
                item.Icon.DisplayName,
                $"{item.Disposition} / {item.PlacementState}",
                $"({item.CurrentPosition.X}, {item.CurrentPosition.Y}) → {target}");
        }
    }
}
