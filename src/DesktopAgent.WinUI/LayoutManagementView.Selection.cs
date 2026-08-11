using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private bool _suppressTemplateSelectionChanged;
    private bool _isLayoutEditing;

    private async void LayoutTemplateList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
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
            ShowInfo(
                "正在编辑布局",
                "先保存或取消当前编辑，再切换到其他布局。",
                InfoBarSeverity.Informational);
            return;
        }

        try
        {
            await LoadSelectedLayoutForPassiveViewAsync(selected);
            SetLayoutInteractionMode(isEditing: false);
        }
        catch (Exception ex)
        {
            ShowError("无法显示布局", ex.Message);
        }
    }

    private async void BeginEditSelectedLayout_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutTemplateList.SelectedItem is not LayoutTemplateListItem selected)
        {
            ShowError("请选择布局", "先在左侧选择一个布局。");
            return;
        }

        try
        {
            if (_draft is null || _draft.TemplateId != selected.TemplateId)
                await LoadSelectedLayoutForPassiveViewAsync(selected);

            SetLayoutInteractionMode(isEditing: true);
            ShowInfo(
                "可以编辑了",
                "修改完成后保存即可；编辑期间自动化保持暂停。",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowError("无法进入编辑", ex.Message);
        }
    }

    private void NewLayoutWithMode_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedTemplateWithoutNotification(null);
        NewLayout_Click(sender, e);
        if (_draft is not null)
            SetLayoutInteractionMode(isEditing: true);
    }

    private void CancelLayoutEditWithMode_Click(object sender, RoutedEventArgs e)
    {
        CancelLayoutEdit_Click(sender, e);
        _isLayoutEditing = false;
        SetSelectedTemplateWithoutNotification(null);
        EditLayoutButton.IsEnabled = false;
    }

    private async Task LoadSelectedLayoutForPassiveViewAsync(LayoutTemplateListItem selected)
    {
        await EnsureStoresAsync();

        var template = await _templateStore.GetAsync(selected.TemplateId)
            ?? throw new InvalidOperationException("所选布局已不存在，请刷新列表。");
        var rules = await _ruleStore.GetAsync(selected.TemplateId)
            ?? throw new InvalidDataException("布局存在，但 RuleSet 缺失。拒绝进入半状态展示。");

        // 若用户在异步读取期间已经切换了选择，旧请求不再覆盖新上下文。
        if (LayoutTemplateList.SelectedItem is LayoutTemplateListItem current &&
            current.TemplateId != selected.TemplateId)
        {
            return;
        }

        _draft = DesktopLayoutEditDraft.FromPersisted(template, rules);
        LoadDraftIntoEditor(_draft);
        LayoutCanvasStatusText.Text = $"{_draft.Zones.Count} 个分区 · 点击预览读取当前桌面";
    }

    private void SetLayoutInteractionMode(bool isEditing)
    {
        _isLayoutEditing = isEditing;

        EditingBadge.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        LayoutNameTextBox.IsReadOnly = !isEditing;
        LayoutTargetBindingCombo.IsEnabled = isEditing;
        LayoutDeviceNameTextBox.IsReadOnly = !isEditing;
        LayoutPresetCombo.IsEnabled = isEditing;
        ApplyPresetButton.IsEnabled = isEditing;
        SaveLayoutButton.IsEnabled = isEditing;
        CancelLayoutEditButton.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        EditLayoutButton.IsEnabled = !isEditing &&
                                     LayoutTemplateList.SelectedItem is LayoutTemplateListItem;
    }

    private void RestoreSelectionToCurrentDraft()
    {
        LayoutTemplateListItem? item = null;
        if (_draft is { IsNew: false })
        {
            item = _templateItems.FirstOrDefault(
                candidate => candidate.TemplateId == _draft.TemplateId);
        }

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
