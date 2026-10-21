using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private bool _suppressTemplateSelectionChanged;
    private bool _isLayoutEditing;
    private bool _layoutSelectionChangeActive;

    private async void LayoutTemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateSelectionChanged || _layoutSelectionChangeActive)
            return;

        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem selected)
            return;

        if (_isLayoutEditing &&
            (_draft is not null || _logicalWorkspaceSession is not null || _workspaceFrozenPreview is not null))
        {
            if (_draft is not null && _draft.TemplateId == selected.TemplateId)
                return;

            RestoreSelectionToCurrentDraft();
            _layoutSelectionChangeActive = true;
            LayoutTemplateList.IsEnabled = false;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "切换布局？",
                    Content = _draft is not null
                        ? "当前布局尚在编辑。切换会放弃未保存的修改；取消后可以先保存。"
                        : "切换会关闭当前精确预览；再次应用前需要重新生成预览。",
                    PrimaryButtonText = "放弃当前编辑并切换",
                    CloseButtonText = "保留当前编辑",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                SetSelectedTemplateWithoutNotification(selected);
                if (await LoadSelectedLayoutForPassiveViewAsync(selected))
                    SetLayoutInteractionMode(isEditing: false);
            }
            catch (Exception ex)
            {
                RestoreSelectionToCurrentDraft();
                ShowError("无法切换布局", ex.Message);
            }
            finally
            {
                LayoutTemplateList.IsEnabled = true;
                _layoutSelectionChangeActive = false;
            }
            return;
        }

        if (_zoneColorPreferencesDirty && _draft is not null &&
            _draft.TemplateId != selected.TemplateId)
        {
            RestoreSelectionToCurrentDraft();
            ShowInfo("分区颜色尚未应用", "先应用或还原当前颜色修改，再切换到其他布局。", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            if (await LoadSelectedLayoutForPassiveViewAsync(selected))
                SetLayoutInteractionMode(isEditing: false);
        }
        catch (Exception ex)
        {
            ShowError("无法显示布局", ex.Message);
        }
    }

    private async void BeginEditSelectedLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_isLayoutEditing)
        {
            await OpenDesktopZoneEditorAsync();
            return;
        }

        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem selected)
        {
            ShowError("请选择布局", "先在左侧选择一个布局。");
            return;
        }

        try
        {
            if (_draft is null || _draft.TemplateId != selected.TemplateId)
            {
                if (!await LoadSelectedLayoutForPassiveViewAsync(selected))
                    return;
            }

            SetLayoutStage(DesktopLayoutStage.Design);
            SetLayoutInteractionMode(isEditing: true);
            ShowInfo(
                "可以编辑了",
                "再次点击“桌面编辑”可直接在目标桌面调整分区；编辑期间自动化保持暂停。",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowError("无法进入编辑", ex.Message);
        }
    }

    private void NewLayoutWithMode_Click(object sender, RoutedEventArgs e)
    {
        if (_zoneColorPreferencesDirty)
        {
            ShowInfo("分区颜色尚未应用", "先应用或还原当前颜色修改，再新建布局。", InfoBarSeverity.Informational);
            return;
        }

        ClearAiLogicalWorkspacePreview();
        ClearAiWorkspaceFrozenPreview();
        ClearAiMultiDisplayWorkspace();
        SetSelectedTemplateWithoutNotification(null);
        NewLayout_Click(sender, e);
        if (_draft is not null)
        {
            SetLayoutStage(DesktopLayoutStage.Design);
            SetLayoutInteractionMode(isEditing: true);
        }
    }

    private void CancelLayoutEditWithMode_Click(object sender, RoutedEventArgs e)
    {
        CancelLayoutEdit_Click(sender, e);
        ClearAiLogicalWorkspacePreview();
        ClearAiWorkspaceFrozenPreview();
        ClearAiMultiDisplayWorkspace();
        SetSelectedTemplateWithoutNotification(null);
        SetLayoutInteractionMode(isEditing: false);
    }

    private async Task<bool> LoadSelectedLayoutForPassiveViewAsync(LayoutTemplateListItem selected)
    {
        await EnsureStoresAsync();
        _settings = await _settingsStore.GetAsync();

        var template = await _templateStore.GetAsync(selected.TemplateId)
            ?? throw new InvalidOperationException("所选布局已不存在，请刷新列表。");
        var rules = await _ruleStore.GetAsync(selected.TemplateId)
            ?? throw new InvalidDataException("布局存在，但 RuleSet 缺失。拒绝进入半状态展示。");

        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem current ||
            current.TemplateId != selected.TemplateId)
        {
            return false;
        }

        ClearAiLogicalWorkspacePreview();
        ClearAiWorkspaceFrozenPreview();
        ClearAiMultiDisplayWorkspace();
        _draft = DesktopLayoutEditDraft.FromPersisted(template, rules);
        LoadDraftIntoEditor(_draft);
        LayoutCanvasStatusText.Text = $"{_draft.Zones.Count} 个分区 · 点击预览读取当前桌面";
        return true;
    }

    private void SetLayoutInteractionMode(bool isEditing)
    {
        ConfigureTargetDisplayUx();
        if (_draft is not null && LayoutTemplateList.SelectedItem is LayoutTemplateListItem &&
            _workspaceFrozenPreview is null && _logicalWorkspaceSession is null)
        {
            LibraryOverviewSurface.Visibility = Visibility.Collapsed;
            SharedDesignSurface.Visibility = Visibility.Collapsed;
            WorkspaceDetailSurface.Visibility = Visibility.Collapsed;
            LayoutWorkbenchSurface.Visibility = Visibility.Visible;
        }

        _isLayoutEditing = isEditing;
        var isLegacyLogicalWorkspacePreview = _logicalWorkspaceSession is not null;
        var isWorkspaceFrozenPreview = _workspaceFrozenPreview is not null;
        var isAnyFrozenWorkspacePreview = isLegacyLogicalWorkspacePreview || isWorkspaceFrozenPreview;
        var isAiMultiDisplayWorkspace = _activeAiWorkspaceIndex >= 0;
        EditingBadge.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        LayoutNameTextBox.IsReadOnly = !isEditing || isAnyFrozenWorkspacePreview;
        LayoutTargetBindingCombo.IsEnabled = isEditing && !isAiMultiDisplayWorkspace && !isAnyFrozenWorkspacePreview;
        LayoutDeviceNameTextBox.IsReadOnly = !isEditing || isAiMultiDisplayWorkspace || isAnyFrozenWorkspacePreview;
        LayoutPresetCombo.IsEnabled = isEditing && !isAnyFrozenWorkspacePreview;
        ApplyPresetButton.IsEnabled = isEditing && !isAnyFrozenWorkspacePreview;
        SaveLayoutButton.IsEnabled = isEditing && !isAnyFrozenWorkspacePreview;
        OverlayToggleButton.IsEnabled = !isLegacyLogicalWorkspacePreview;
        SetActiveLayoutButton.IsEnabled = !isEditing;
        LayoutSelectionHintText.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        LayoutSelectionHintText.Text = isAnyFrozenWorkspacePreview
            ? "当前是只读精确预览。可随时查看桌面线框；点选模板可确认关闭预览并切换。保存的工作区设计在上方设计库。"
            : "正在编辑：先保存或取消编辑；点选其他模板可确认放弃修改并切换。工作区设计在上方设计库。";
        PreviewLayoutButton.IsEnabled = !isAnyFrozenWorkspacePreview;
        CancelLayoutEditButton.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        EditLayoutButton.Content = isEditing ? "桌面编辑" : "编辑";
        EditLayoutButton.IsEnabled = !isAnyFrozenWorkspacePreview && (isEditing
            ? _draft is not null
            : LayoutTemplateList.SelectedItem is LayoutTemplateListItem);
    }

    private void ConfigureTargetDisplayUx()
    {
        if (LayoutTargetBindingCombo.Items.Count > 1 &&
            LayoutTargetBindingCombo.Items[1] is ComboBoxItem selectedTargetItem)
        {
            selectedTargetItem.Content = "临时指定";
        }

        LayoutDeviceNameTextBox.PlaceholderText = "请输入设备名，例如 \\.\\DISPLAY1";
    }

    private void RestoreSelectionToCurrentDraft()
    {
        LayoutTemplateListItem? item = null;
        if (_draft is { IsNew: false })
            item = _templateItems.FirstOrDefault(candidate => candidate.TemplateId == _draft.TemplateId);

        SetSelectedTemplateWithoutNotification(item);
    }

    private void SetSelectedTemplateWithoutNotification(LayoutTemplateListItem? item)
    {
        _suppressTemplateSelectionChanged = true;
        try
        {
            LayoutTemplateList.SelectedItem = item;
        }
        finally
        {
            _suppressTemplateSelectionChanged = false;
        }
    }
}
