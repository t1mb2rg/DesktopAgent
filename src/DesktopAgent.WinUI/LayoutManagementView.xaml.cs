using System.Collections.ObjectModel;
using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Productization;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.DesktopLayouts.Templates.Rules;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView : UserControl
{
    private const int LayoutListLimit = 100;

    private readonly SqliteDesktopLayoutTemplateStore _templateStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly SqliteDesktopLayoutIconRuleStore _ruleStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly JsonDesktopProductSettingsStore _settingsStore =
        new(DesktopAgentDataPaths.DefaultSettingsPath);
    private readonly StaThreadExecutor _staThreadExecutor = new();
    private readonly WindowsDesktopObservationSource _observationSource;

    private readonly ObservableCollection<LayoutTemplateListItem> _templateItems = [];
    private readonly ObservableCollection<LayoutZoneListItem> _zoneItems = [];
    private readonly ObservableCollection<LayoutPreviewListItem> _previewItems = [];

    private DesktopProductSettings _settings = DesktopProductSettings.CreateDefault();
    private DesktopLayoutEditDraft? _draft;
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
        }
        catch (Exception ex)
        {
            ShowError("无法初始化布局管理", ex.Message);
        }
    }

    private async Task EnsureStoresAsync()
    {
        await _templateStore.InitializeAsync();
        await _ruleStore.InitializeAsync();
        await _settingsStore.InitializeAsync();
    }

    private async void RefreshLayouts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshLayoutsAsync();
            ShowInfo("布局已刷新", "只读取本地模板元数据，没有观察或移动桌面。", InfoBarSeverity.Success);
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
            ? "还没有保存的布局。可以从一个常用预设开始。"
            : $"共 {summaries.Count} 个布局。设为当前只改变产品偏好，不会立即移动桌面图标。";

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
            ShowInfo("已创建内存草稿", "尚未写入 SQLite，也不会影响当前桌面。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowError("无法创建布局草稿", ex.Message);
        }
    }

    private async void LoadLayout_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem selected)
        {
            ShowError("请选择布局", "先在列表里选择一个布局。 ");
            return;
        }

        try
        {
            await EnsureStoresAsync();
            var template = await _templateStore.GetAsync(selected.TemplateId)
                ?? throw new InvalidOperationException("所选布局已不存在，请刷新列表。");
            var rules = await _ruleStore.GetAsync(selected.TemplateId)
                ?? throw new InvalidDataException("布局存在，但 RuleSet 缺失。拒绝进入半状态编辑。");

            _draft = DesktopLayoutEditDraft.FromPersisted(template, rules);
            LoadDraftIntoEditor(_draft);
            ShowInfo("已加载布局草稿", "编辑态 Automation Policy 仍为 BLOCK。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowError("无法加载布局", ex.Message);
        }
    }

    private async void SetActiveLayout_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem selected)
        {
            ShowError("请选择布局", "先在列表里选择要设为当前的布局。 ");
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

            ShowInfo(
                "已设为当前布局",
                "这里只保存 ActiveLayoutTemplateId。没有 Writer 调用，也没有立即应用到桌面。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError("无法设置当前布局", ex.Message);
        }
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
        {
            ShowError("没有布局草稿", "请先新建或加载一个布局。 ");
            return;
        }

        try
        {
            var draft = BuildDraftFromEditor();
            if (draft.RuleSet.Rules.Any(rule => rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride))
            {
                throw new InvalidOperationException(
                    "当前布局包含 ZoneOverride 规则。为避免规则悄悄指向失效 Zone，本阶段拒绝用预设替换分区。请在后续规则编辑器中显式调整。 ");
            }

            if (LayoutPresetCombo.SelectedIndex is < 0 or > 3)
                throw new InvalidOperationException("请选择有效布局预设。 ");

            var preset = (DesktopLayoutPresetKind)LayoutPresetCombo.SelectedIndex;
            _draft = draft.WithZones(DesktopLayoutPresetFactory.CreateZones(preset));
            LoadDraftIntoEditor(_draft);
            ShowInfo("预设已套用到草稿", "仍只在内存中，点击“原子保存草稿”前不会写入。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowError("无法套用预设", ex.Message);
        }
    }

    private async void PreviewLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
        {
            ShowError("没有布局草稿", "请先新建或加载一个布局。 ");
            return;
        }

        try
        {
            var draft = BuildDraftFromEditor();
            var selectedTargetDeviceName = draft.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget
                ? NormalizeRequiredDeviceName(LayoutDeviceNameTextBox.Text, "当前选择的显示器需要填写设备名才能预览。")
                : null;

            LayoutPreviewSummaryText.Text = "正在重新观察桌面并计算影响…";
            LayoutPreviewCard.Visibility = Visibility.Visible;
            _previewItems.Clear();

            var capture = await _observationSource.CaptureAsync();
            if (!capture.IsCaptured || capture.Observation is null)
            {
                var details = string.Join("；", capture.Issues.Select(issue => issue.Message));
                throw new InvalidOperationException($"当前桌面事实不可用：{details}");
            }

            var preview = DesktopLayoutImpactPreviewService.Create(
                draft,
                capture.Observation,
                selectedTargetDeviceName);
            var plan = preview.Plan;

            LayoutPreviewSummaryText.Text =
                $"目标 {plan.TargetDisplay.DeviceName} · 输入 {plan.InputIconCount} · " +
                $"目标屏 {plan.TargetDisplayIconCount} · 屏外 {plan.OutOfScopeIconCount} · " +
                $"需移动 {plan.MoveRequiredCount} · 已在目标 {plan.AlreadyAtTargetCount} · " +
                $"排除 {plan.ExcludedCount} · 固定 {plan.PinnedCount} · " +
                $"规划完整：{(plan.IsComplete ? "是" : "否")}。";

            LayoutPreviewObservationText.Text =
                $"Observation {preview.ObservationId:D} · {preview.ObservedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}。" +
                "此 Observation 只服务本次预览，不会保存成未来可重放命令。";

            foreach (var item in plan.Items.Take(100))
                _previewItems.Add(LayoutPreviewListItem.From(item));

            _draft = draft;
            ShowInfo(
                "影响预览已刷新",
                "每次点击预览都会重新观察；未来执行仍必须再次 Fresh Replan。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            LayoutPreviewSummaryText.Text = "影响预览不可用。";
            ShowError("无法生成影响预览", ex.Message);
        }
    }

    private async void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
        {
            ShowError("没有布局草稿", "请先新建或加载一个布局。 ");
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

            ShowInfo(
                "布局已原子保存",
                $"Template V{template.Version} 与 RuleSet 已回读验证。保存元数据不等于应用到桌面。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError("无法保存布局", ex.Message);
        }
    }

    private void CancelLayoutEdit_Click(object sender, RoutedEventArgs e)
    {
        _draft = null;
        _zoneItems.Clear();
        _previewItems.Clear();
        LayoutEditorCard.Visibility = Visibility.Collapsed;
        LayoutPreviewCard.Visibility = Visibility.Collapsed;
        ShowInfo("已离开布局编辑", "未保存的内存草稿已丢弃。", InfoBarSeverity.Informational);
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
        LayoutEditorCard.Visibility = Visibility.Visible;
        LayoutPreviewCard.Visibility = Visibility.Collapsed;
        LayoutNameTextBox.Text = draft.Name;
        LayoutDeviceNameTextBox.Text = draft.TargetDisplayBinding.DeviceName ?? string.Empty;
        LayoutTargetBindingCombo.SelectedIndex = draft.TargetDisplayBinding.Kind switch
        {
            TargetDisplayBindingKind.PrimaryDisplay => 0,
            TargetDisplayBindingKind.SelectedTarget => 1,
            TargetDisplayBindingKind.ExactDeviceName => 2,
            _ => 0,
        };

        LayoutRuleSummaryText.Text =
            $"规则：{draft.RuleSet.RuleCount}。本阶段保留既有规则，不提供隐式重写。";

        _zoneItems.Clear();
        foreach (var zone in draft.Zones.OrderBy(zone => zone.SortOrder))
            _zoneItems.Add(LayoutZoneListItem.From(zone));
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
        string VersionText,
        string ZoneText,
        string ActiveText)
    {
        public static LayoutTemplateListItem From(
            DesktopLayoutTemplateSummary summary,
            bool isActive)
            => new(
                summary.TemplateId,
                summary.Name,
                $"V{summary.Version}",
                $"{summary.ZoneCount} 区",
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
                $"[{zone.Bounds.Left / 100d:0.##}%, {zone.Bounds.Top / 100d:0.##}%] → " +
                $"[{zone.Bounds.Right / 100d:0.##}%, {zone.Bounds.Bottom / 100d:0.##}%]",
                zone.PlacementFlow == ZonePlacementFlow.ColumnMajor ? "列优先" : "行优先");
    }

    private sealed record LayoutPreviewListItem(
        string DisplayName,
        string StateText,
        string PositionText)
    {
        public static LayoutPreviewListItem From(
            Core.DesktopLayouts.Templates.Preview.DesktopLayoutRuleAwarePreviewItem item)
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
