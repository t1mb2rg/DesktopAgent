using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private sealed class AiWorkspaceDraftState
    {
        public DisplayDescriptor Display { get; }
        public string DisplayRef { get; }
        public string DeviceName { get; }
        public bool IsPrimary { get; }
        public int ObservedIconCount { get; }
        public int SourceAssignmentCount { get; }
        public int MappedAssignmentCount { get; }
        public DesktopLayoutEditDraft Draft { get; set; }

        public AiWorkspaceDraftState(
            DesktopUnderstandingMultiDisplayWorkspaceCompiler.DisplayCompilation compilation)
        {
            Display = compilation.Display;
            DisplayRef = compilation.DisplayRef;
            DeviceName = compilation.Display.DeviceName;
            IsPrimary = compilation.Display.IsPrimary;
            ObservedIconCount = compilation.ObservedIconCount;
            SourceAssignmentCount = compilation.SourceAssignmentCount;
            MappedAssignmentCount = compilation.MappedAssignmentCount;
            Draft = compilation.Draft;
        }

        public AiWorkspaceDraftState(DesktopLayoutWorkspaceDraftRecoveryState recovery)
        {
            Display = recovery.Display;
            DisplayRef = recovery.DisplayRef;
            DeviceName = recovery.Display.DeviceName;
            IsPrimary = recovery.Display.IsPrimary;
            ObservedIconCount = recovery.ObservedIconCount;
            SourceAssignmentCount = recovery.SourceAssignmentCount;
            MappedAssignmentCount = recovery.MappedAssignmentCount;
            Draft = new DesktopLayoutEditDraft(
                recovery.TemplateId,
                recovery.SourceVersion,
                recovery.Name,
                DesktopAgent.Core.DesktopLayouts.Templates.TargetDisplayBinding.ForExactDeviceName(
                    recovery.Display.DeviceName),
                recovery.Zones,
                recovery.RuleSet,
                recovery.ExplicitInboundStableIds);
        }

        public string DisplayText =>
            $"{(IsPrimary ? "主屏" : "副屏")} · {ShortDeviceName(DeviceName)} · {SourceAssignmentCount} 项";
    }

    private readonly List<AiWorkspaceDraftState> _aiWorkspaceDrafts = [];
    private readonly List<DesktopCrossDisplayMigrationIntent> _aiWorkspaceMigrations = [];
    private MenuFlyout? _crossDisplayMigrationFlyout;
    private bool _suppressAiWorkspaceSelectionChanged;
    private int _activeAiWorkspaceIndex = -1;

    private bool IsAiMultiDisplayWorkspaceActive =>
        _activeAiWorkspaceIndex >= 0 && _activeAiWorkspaceIndex < _aiWorkspaceDrafts.Count;

    internal bool TryLoadAiImportedWorkspace(
        DesktopUnderstandingMultiDisplayWorkspaceCompiler.WorkspaceCompilationResult workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (_isLayoutEditing && _draft is not null)
        {
            ShowInfo(
                "已有布局正在编辑",
                "AI 多屏工作区没有覆盖当前未保存修改。请先保存或取消当前编辑，再从 AI 助手重新导入。",
                InfoBarSeverity.Warning);
            return false;
        }

        if (_zoneColorPreferencesDirty)
        {
            ShowInfo(
                "分区颜色尚未应用",
                "AI 多屏工作区没有覆盖当前颜色修改。请先应用或还原颜色，再重新导入。",
                InfoBarSeverity.Warning);
            return false;
        }

        if (workspace.Displays.Count == 0)
        {
            ShowInfo(
                "没有可导入的显示器草稿",
                "本次 AI 结果没有生成任何安全的单显示器布局草稿。",
                InfoBarSeverity.Warning);
            return false;
        }

        _aiWorkspaceDrafts.Clear();
        _aiWorkspaceDrafts.AddRange(workspace.Displays.Select(static compilation =>
            new AiWorkspaceDraftState(compilation)));
        _aiWorkspaceMigrations.Clear();

        PopulateAiWorkspaceDisplaySelector(selectedIndex: 0);

        ConfigureCrossDisplayMigrationUi();

        _activeAiWorkspaceIndex = 0;
        SetSelectedTemplateWithoutNotification(null);
        LoadAiWorkspaceDraft(_activeAiWorkspaceIndex);
        var recoveryError = TryPersistAiMultiDisplayWorkspace();

        var displaySummary = string.Join(
            " · ",
            _aiWorkspaceDrafts.Select(state =>
                $"{ShortDeviceName(state.DeviceName)} {state.MappedAssignmentCount}/{state.SourceAssignmentCount}"));
        var migrationHint = _aiWorkspaceDrafts.Count > 1
            ? " 预览后可在“详细影响”中选中项目并右键，显式迁移到另一块显示器。"
            : string.Empty;
        ShowInfo(
            "AI 多显示器草案已导入",
            $"{workspace.Displays.Count} 块显示器 · {workspace.MappedAssignmentCount}/{workspace.SourceAssignmentCount} 个 Assignment 已映射。" +
            $" {displaySummary}。AI 默认保持当前屏幕归属；只有用户显式迁移的项目才允许跨屏。" +
            $"{migrationHint} 所有草稿仍未保存、未应用。" +
            (recoveryError is null ? string.Empty : $" 草稿恢复状态未能保存：{recoveryError}"),
            recoveryError is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        return true;
    }

    private void ConfigureCrossDisplayMigrationUi()
    {
        if (_aiWorkspaceDrafts.Count <= 1)
        {
            LayoutPreviewList.SelectionMode = ListViewSelectionMode.None;
            LayoutPreviewList.ContextFlyout = null;
            _crossDisplayMigrationFlyout = null;
            return;
        }

        LayoutPreviewList.SelectionMode = ListViewSelectionMode.Single;
        var flyout = new MenuFlyout();
        var migrate = new MenuFlyoutItem
        {
            Text = "迁移到另一显示器…",
        };
        migrate.Click += CrossDisplayMigration_Click;
        flyout.Items.Add(migrate);
        LayoutPreviewList.ContextFlyout = flyout;
        _crossDisplayMigrationFlyout = flyout;
    }

    private async void CrossDisplayMigration_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAiMultiDisplayWorkspaceActive || _aiWorkspaceDrafts.Count <= 1)
            return;

        if (_latestPreview is null)
        {
            ShowInfo("请先预览", "先对当前显示器生成 fresh 预览，再选择要迁移的项目。", InfoBarSeverity.Informational);
            return;
        }

        var selectedIndex = LayoutPreviewList.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= _latestPreview.Plan.Items.Count || selectedIndex >= 100)
        {
            ShowInfo("请选择项目", "在“详细影响”列表中先选中一个项目，再执行跨屏迁移。", InfoBarSeverity.Informational);
            return;
        }

        var previewItem = _latestPreview.Plan.Items[selectedIndex];
        var stableId = previewItem.Icon.StableId;
        if (_aiWorkspaceMigrations.Any(item => string.Equals(item.StableId, stableId, StringComparison.Ordinal)))
        {
            ShowInfo(
                "已有待执行跨屏迁移",
                "这个项目已经有一笔待执行迁移。v1 不允许在真正 Apply 前再次改投第三块屏。",
                InfoBarSeverity.Warning);
            return;
        }

        var sourceState = _aiWorkspaceDrafts[_activeAiWorkspaceIndex];
        var sourceRule = sourceState.Draft.RuleSet.Rules.SingleOrDefault(rule =>
            string.Equals(rule.StableId, stableId, StringComparison.Ordinal));
        if (sourceRule is null || sourceRule.Kind != DesktopLayoutIconRuleKind.ZoneOverride)
        {
            ShowInfo(
                "当前项目不能直接跨屏迁移",
                "v1 只允许迁移已经由当前 AI 草稿明确分配到 Zone 的项目；Pin、Exclude 或默认管理项目不会被静默改权。",
                InfoBarSeverity.Warning);
            return;
        }

        var targetDisplayCombo = new ComboBox
        {
            Header = "目标显示器",
            MinWidth = 360,
        };
        foreach (var pair in _aiWorkspaceDrafts.Select((state, index) => (state, index)))
        {
            if (pair.index == _activeAiWorkspaceIndex)
                continue;
            targetDisplayCombo.Items.Add(new ComboBoxItem
            {
                Content = pair.state.DisplayText,
                Tag = pair.index,
            });
        }

        var targetZoneCombo = new ComboBox
        {
            Header = "目标分区",
            MinWidth = 360,
        };

        void PopulateTargetZones()
        {
            targetZoneCombo.Items.Clear();
            if (targetDisplayCombo.SelectedItem is not ComboBoxItem displayItem
                || displayItem.Tag is not int targetIndex
                || targetIndex < 0
                || targetIndex >= _aiWorkspaceDrafts.Count)
            {
                return;
            }

            foreach (var zone in _aiWorkspaceDrafts[targetIndex].Draft.Zones.OrderBy(zone => zone.SortOrder))
            {
                targetZoneCombo.Items.Add(new ComboBoxItem
                {
                    Content = zone.Name,
                    Tag = zone.ZoneId,
                });
            }
            if (targetZoneCombo.Items.Count > 0)
                targetZoneCombo.SelectedIndex = 0;
        }

        targetDisplayCombo.SelectionChanged += (_, _) => PopulateTargetZones();
        if (targetDisplayCombo.Items.Count > 0)
            targetDisplayCombo.SelectedIndex = 0;

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text =
                $"项目：{previewItem.Icon.DisplayName}\n" +
                $"StableId：{stableId}\n\n" +
                "这一步只修改内存中的多屏布局草稿，Writer 不会启动。" +
                "迁移后请在目标屏重新预览并明确确认后，才会真正移动桌面图标。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        });
        content.Children.Add(targetDisplayCombo);
        content.Children.Add(targetZoneCombo);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "建立跨屏迁移草稿？",
            Content = content,
            PrimaryButtonText = "建立迁移草稿",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
            return;

        if (targetDisplayCombo.SelectedItem is not ComboBoxItem selectedDisplay
            || selectedDisplay.Tag is not int destinationIndex
            || targetZoneCombo.SelectedItem is not ComboBoxItem selectedZone
            || selectedZone.Tag is not Guid targetZoneId)
        {
            ShowError("无法建立跨屏迁移", "目标显示器或目标分区选择无效。");
            return;
        }

        var destination = _aiWorkspaceDrafts[destinationIndex];
        try
        {
            ReassignAiWorkspaceItem(
                stableId,
                sourceState.DeviceName,
                destination.DeviceName,
                targetZoneId);

            FocusAiWorkspaceDisplay(destinationIndex);
            var recoveryError = TryPersistAiMultiDisplayWorkspace();
            if (recoveryError is not null)
            {
                ShowInfo("多屏草稿恢复状态未保存", recoveryError, InfoBarSeverity.Warning);
                return;
            }
            ShowInfo(
                "跨屏迁移草稿已建立",
                $"{previewItem.Icon.DisplayName}：{ShortDeviceName(sourceState.DeviceName)} → " +
                $"{ShortDeviceName(destination.DeviceName)} / {selectedZone.Content}。" +
                " 当前仍是 0 Writer；请在目标屏重新预览。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError("无法建立跨屏迁移", ex.Message);
        }
    }

    private void AiWorkspaceDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAiWorkspaceSelectionChanged)
            return;

        if (_logicalWorkspaceSession is not null)
        {
            FocusAiLogicalWorkspaceDisplay(AiWorkspaceDisplayCombo.SelectedIndex);
            return;
        }

        var targetIndex = AiWorkspaceDisplayCombo.SelectedIndex;
        if (targetIndex < 0 || targetIndex >= _aiWorkspaceDrafts.Count)
            return;
        if (targetIndex == _activeAiWorkspaceIndex)
            return;

        if (_zoneColorPreferencesDirty)
        {
            RestoreAiWorkspaceSelection();
            ShowInfo(
                "分区颜色尚未应用",
                "先应用或还原当前显示器的颜色修改，再切换到另一块显示器。",
                InfoBarSeverity.Informational);
            return;
        }

        try
        {
            PersistCurrentAiWorkspaceDraftFromEditor();
            _activeAiWorkspaceIndex = targetIndex;
            LoadAiWorkspaceDraft(targetIndex);
            var recoveryError = TryPersistAiMultiDisplayWorkspace();
            if (recoveryError is not null)
                ShowInfo("多屏草稿恢复状态未保存", recoveryError, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            RestoreAiWorkspaceSelection();
            ShowError("无法切换显示器草稿", ex.Message);
        }
    }

    private void FocusAiWorkspaceDisplay(int index)
    {
        if (index < 0 || index >= _aiWorkspaceDrafts.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        _activeAiWorkspaceIndex = index;
        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.SelectedIndex = index;
        }
        finally
        {
            _suppressAiWorkspaceSelectionChanged = false;
        }
        LoadAiWorkspaceDraft(index);
    }

    private void LoadAiWorkspaceDraft(int index)
    {
        var state = _aiWorkspaceDrafts[index];
        _draft = state.Draft;
        LoadDraftIntoEditor(_draft);
        SetLayoutInteractionMode(isEditing: true);

        LayoutTargetBindingCombo.IsEnabled = false;
        LayoutDeviceNameTextBox.IsReadOnly = true;

        var inboundCount = state.Draft.ExplicitInboundStableIds.Count;
        LayoutCanvasStatusText.Text =
            $"AI 多屏草案 · {ShortDeviceName(state.DeviceName)} · {_draft.Zones.Count} 个分区 · " +
            $"{state.MappedAssignmentCount}/{state.SourceAssignmentCount} 个初始项目已映射" +
            (inboundCount > 0 ? $" · {inboundCount} 个显式跨屏授权" : string.Empty) +
            " · 尚未预览";
    }

    private void PersistCurrentAiWorkspaceDraftFromEditor()
    {
        if (!IsAiMultiDisplayWorkspaceActive || _draft is null)
            return;

        var edited = BuildDraftFromEditor();
        var expectedDevice = _aiWorkspaceDrafts[_activeAiWorkspaceIndex].DeviceName;
        if (edited.TargetDisplayBinding.Kind != DesktopAgent.Core.DesktopLayouts.Templates.TargetDisplayBindingKind.ExactDeviceName
            || !string.Equals(
                edited.TargetDisplayBinding.DeviceName,
                expectedDevice,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AI 多屏工作区中的单屏草稿必须保持固定设备绑定。若要改到其他显示器，请使用显式跨屏迁移，而不是修改 TargetDisplayBinding。");
        }

        _aiWorkspaceDrafts[_activeAiWorkspaceIndex].Draft = edited;
    }

    private void ReassignAiWorkspaceItem(
        string stableId,
        string sourceDeviceName,
        string targetDeviceName,
        Guid targetZoneId)
    {
        PersistCurrentAiWorkspaceDraftFromEditor();

        var edit = DesktopCrossDisplayWorkspaceEditor.Reassign(
            _aiWorkspaceDrafts.Select(static state => state.Draft).ToArray(),
            _aiWorkspaceMigrations,
            stableId,
            sourceDeviceName,
            targetDeviceName,
            targetZoneId);

        var draftByDevice = edit.Drafts.ToDictionary(
            static draft => draft.TargetDisplayBinding.DeviceName!,
            StringComparer.Ordinal);
        foreach (var state in _aiWorkspaceDrafts)
        {
            if (!draftByDevice.TryGetValue(state.DeviceName, out var draft))
                throw new InvalidOperationException($"跨屏编辑结果缺少显示器 {state.DeviceName} 的 Draft。");
            state.Draft = draft;
        }

        _aiWorkspaceMigrations.Clear();
        _aiWorkspaceMigrations.AddRange(edit.Migrations);

        InvalidateDirectApplyEvidence("跨屏迁移已修改 · 请重新预览");
    }

    private void RestoreAiWorkspaceSelection()
    {
        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.SelectedIndex = _activeAiWorkspaceIndex;
        }
        finally
        {
            _suppressAiWorkspaceSelectionChanged = false;
        }
    }

    internal void ClearAiMultiDisplayWorkspace()
    {
        ClearAiLogicalWorkspacePreview();
        _aiWorkspaceDrafts.Clear();
        _aiWorkspaceMigrations.Clear();
        _activeAiWorkspaceIndex = -1;
        _suppressAiWorkspaceSelectionChanged = true;
        try
        {
            AiWorkspaceDisplayCombo.Items.Clear();
            AiWorkspaceDisplayCombo.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressAiWorkspaceSelectionChanged = false;
        }

        LayoutPreviewList.SelectionMode = ListViewSelectionMode.None;
        LayoutPreviewList.ContextFlyout = null;
        _crossDisplayMigrationFlyout = null;
        DeleteAiMultiDisplayWorkspaceRecovery();

        if (_isLayoutEditing)
        {
            LayoutTargetBindingCombo.IsEnabled = true;
            LayoutDeviceNameTextBox.IsReadOnly = false;
        }
    }

    private static string ShortDeviceName(string deviceName)
    {
        const string prefix = @"\\.\";
        return deviceName.StartsWith(prefix, StringComparison.Ordinal)
            ? deviceName[prefix.Length..]
            : deviceName;
    }
}
