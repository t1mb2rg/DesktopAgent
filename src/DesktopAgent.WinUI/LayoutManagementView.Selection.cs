using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private bool _suppressTemplateSelectionChanged;
    private bool _isLayoutEditing;

    private async void LayoutTemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateSelectionChanged)
            return;

        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem selected)
            return;

        if (_isLayoutEditing && _draft is not null)
        {
            if (_draft.TemplateId == selected.TemplateId)
                return;

            RestoreSelectionToCurrentDraft();
            ShowInfo("正在编辑布局", "先保存或取消当前编辑，再切换到其他布局。", InfoBarSeverity.Informational);
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

        SetSelectedTemplateWithoutNotification(null);
        NewLayout_Click(sender, e);
        if (_draft is not null)
            SetLayoutInteractionMode(isEditing: true);
    }

    private void CancelLayoutEditWithMode_Click(object sender, RoutedEventArgs e)
    {
        CancelLayoutEdit_Click(sender, e);
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

        _draft = DesktopLayoutEditDraft.FromPersisted(template, rules);
        LoadDraftIntoEditor(_draft);
        LayoutCanvasStatusText.Text = $"{_draft.Zones.Count} 个分区 · 点击预览读取当前桌面";
        return true;
    }

    private void SetLayoutInteractionMode(bool isEditing)
    {
        ConfigureTargetDisplayUx();

        _isLayoutEditing = isEditing;
        EditingBadge.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        LayoutNameTextBox.IsReadOnly = !isEditing;
        LayoutTargetBindingCombo.IsEnabled = isEditing;
        LayoutDeviceNameTextBox.IsReadOnly = !isEditing;
        LayoutPresetCombo.IsEnabled = isEditing;
        ApplyPresetButton.IsEnabled = isEditing;
        SaveLayoutButton.IsEnabled = isEditing;
        CancelLayoutEditButton.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        EditLayoutButton.Content = isEditing ? "桌面编辑" : "编辑";
        EditLayoutButton.IsEnabled = isEditing
            ? _draft is not null
            : LayoutTemplateList.SelectedItem is LayoutTemplateListItem;
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
