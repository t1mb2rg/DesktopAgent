using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.Productization;
using Microsoft.UI.Xaml.Controls;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    internal void HideDesktopOverlaysForEditing() => HideOverlay();

    private DesktopZoneEditorWindow? _desktopZoneEditorWindow;

    private async Task OpenDesktopZoneEditorAsync()
    {
        if (_desktopZoneEditorWindow is not null)
        {
            ShowInfo("桌面编辑已经打开", "先完成或取消当前桌面编辑。", InfoBarSeverity.Informational);
            return;
        }

        if (!_isLayoutEditing)
        {
            ShowError("请先进入布局编辑", "桌面分区编辑只在显式 Layout Editing 模式中开放。 ");
            return;
        }

        try
        {
            if (!await EnsureDraftForSelectedLayoutAsync())
                return;

            var draft = BuildDraftFromEditor();
            LayoutCanvasStatusText.Text = "正在定位桌面编辑目标…";
            var preview = await CapturePreviewAsync(draft);
            ApplyPreviewToUi(preview);

            _settings = await _settingsStore.GetAsync();
            var gridCapture = await ProductGridCalibration.Protect(_observationSource).CaptureAsync();
            if (!gridCapture.IsCaptured) throw new InvalidOperationException("当前实测网格不可用，请重新核验校准。");
            var gridResult = DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry.DesktopExplorerWorkspaceGeometryAdapter.Build(
                gridCapture.Observation!, await _workspaceGridFactReader.TryReadActiveAsync(gridCapture.Observation!));
            if (!gridResult.Succeeded) throw new InvalidOperationException("无法建立实测网格。");
            var editorGrid = gridResult.Geometry!.Displays.Single(display => display.DeviceName == preview.Plan.TargetDisplay.DeviceName);
            HideOverlay();

            var editor = new DesktopZoneEditorWindow(
                preview.Plan.TargetDisplay,
                draft,
                _settings,
                editorGrid,
                GetCurrentZoneColorOverridesSnapshot());
            editor.Completed += DesktopZoneEditor_Completed;
            editor.Cancelled += DesktopZoneEditor_Cancelled;
            _desktopZoneEditorWindow = editor;

            (Microsoft.UI.Xaml.Application.Current as App)?.ProductWindow?.HideForZoneEditor();
            editor.Activate();
        }
        catch (Exception ex)
        {
            RestoreProductWindowAfterDesktopEditor();
            _desktopZoneEditorWindow = null;
            LayoutCanvasStatusText.Text = "桌面编辑未打开";
            ShowError("无法进入桌面编辑", ex.Message);
        }
    }

    private async void DesktopZoneEditor_Completed(
        object? sender,
        DesktopZoneEditorCompletedEventArgs e)
    {
        DetachDesktopZoneEditor(sender as DesktopZoneEditorWindow);
        RestoreProductWindowAfterDesktopEditor();

        DesktopLayoutEditDraft? editedDraft = null;
        try
        {
            await EnsureStoresAsync();
            var previousDraft = BuildDraftFromEditor();
            editedDraft = previousDraft.WithZones(e.Zones);
            _draft = editedDraft;

            var template = editedDraft.BuildNextPersistedTemplate();
            var saveResult = await LayoutTemplateAndRulesSaveCoordinator.SaveAsync(
                _templateStore,
                _ruleStore,
                template,
                editedDraft.RuleSet);
            if (!saveResult.IsSuccess)
            {
                throw new InvalidOperationException(
                    saveResult.Message ?? $"保存失败：{saveResult.Outcome}");
            }

            string? colorWarning = null;
            try
            {
                var latestSettings = await _settingsStore.GetAsync();
                var preferences = latestSettings.OverlayZoneColors;
                var retainedZoneIds = e.Zones.Select(zone => zone.ZoneId).ToHashSet();

                foreach (var oldZone in previousDraft.Zones)
                {
                    if (!retainedZoneIds.Contains(oldZone.ZoneId))
                        preferences = preferences.WithoutOverride(oldZone.ZoneId);
                }

                foreach (var zone in e.Zones)
                {
                    preferences = e.ZoneColorOverrides.TryGetValue(zone.ZoneId, out var colorRgb)
                        ? preferences.WithOverride(zone.ZoneId, colorRgb)
                        : preferences.WithoutOverride(zone.ZoneId);
                }

                _settings = latestSettings.WithOverlayZoneColors(preferences);
                await _settingsStore.SaveAsync(_settings);
            }
            catch (Exception ex)
            {
                colorWarning = ex.Message;
                _settings = await _settingsStore.GetAsync();
            }

            _draft = RehydrateSavedDraft(template, editedDraft.RuleSet, editedDraft);
            LoadDraftIntoEditor(_draft);
            SetLayoutInteractionMode(isEditing: false);
            await RefreshLayoutsAsync(template.TemplateId);
            var recoveryError = TryPersistAiMultiDisplayWorkspace();

            if (colorWarning is null && recoveryError is null)
            {
                ShowInfo(
                    "桌面分区已保存",
                    $"V{template.Version} · 只更新布局空间与显示偏好，没有移动任何桌面图标。",
                    InfoBarSeverity.Success);
            }
            else
            {
                var warnings = new[]
                {
                    colorWarning is null ? null : $"颜色偏好：{colorWarning}",
                    recoveryError is null ? null : $"多屏草稿恢复状态：{recoveryError}",
                }.Where(static warning => warning is not null);
                ShowInfo(
                    "布局已保存，但附加状态未完全更新",
                    string.Join("；", warnings),
                    InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            if (editedDraft is not null)
            {
                _draft = editedDraft;
                LoadDraftIntoEditor(_draft);
                ApplyDesktopEditorColorsAsUnsaved(e.ZoneColorOverrides);
                SetLayoutInteractionMode(isEditing: true);
            }

            ShowError("无法保存桌面分区", ex.Message);
        }
    }

    private void DesktopZoneEditor_Cancelled(object? sender, EventArgs e)
    {
        DetachDesktopZoneEditor(sender as DesktopZoneEditorWindow);
        RestoreProductWindowAfterDesktopEditor();
        LayoutCanvasStatusText.Text = _draft is null
            ? "新建或加载一个布局开始编辑"
            : $"{_draft.Zones.Count} 个分区 · 桌面编辑已取消";
        ShowInfo("已取消桌面编辑", "布局草稿没有改变。", InfoBarSeverity.Informational);
    }

    private void DetachDesktopZoneEditor(DesktopZoneEditorWindow? editor)
    {
        var target = editor ?? _desktopZoneEditorWindow;
        if (target is not null)
        {
            target.Completed -= DesktopZoneEditor_Completed;
            target.Cancelled -= DesktopZoneEditor_Cancelled;
        }

        _desktopZoneEditorWindow = null;
    }

    private void RestoreProductWindowAfterDesktopEditor()
        => (Microsoft.UI.Xaml.Application.Current as App)?.ProductWindow?.ShowAfterZoneEditor();

    private void ApplyDesktopEditorColorsAsUnsaved(
        IReadOnlyDictionary<Guid, uint> zoneColorOverrides)
    {
        _workingZoneColorOverrides.Clear();
        foreach (var pair in zoneColorOverrides)
            _workingZoneColorOverrides[pair.Key] = pair.Value;

        _zoneColorPreferencesDirty = true;
        RenderLayoutCanvas();
    }
}
