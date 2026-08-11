using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.Productization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    private readonly Dictionary<Guid, uint> _workingZoneColorOverrides = new();
    private Guid? _selectedZoneColorZoneId;
    private uint _selectedZoneWorkingColorRgb;
    private bool _suppressZoneColorUiEvents;
    private bool _zoneColorPreferencesDirty;

    private void InitializeZoneColorPreferencesForDraft(DesktopLayoutEditDraft draft)
    {
        _workingZoneColorOverrides.Clear();
        foreach (var zone in draft.Zones)
        {
            if (_settings.OverlayZoneColors.TryGetOverride(zone.ZoneId, out var colorRgb))
                _workingZoneColorOverrides[zone.ZoneId] = colorRgb;
        }

        _selectedZoneColorZoneId = null;
        _selectedZoneWorkingColorRgb = _settings.OverlayAppearance.ZoneColorRgb;
        _zoneColorPreferencesDirty = false;

        _suppressZoneColorUiEvents = true;
        try
        {
            LayoutZoneList.SelectedIndex = -1;
            ZoneColorEditorPanel.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressZoneColorUiEvents = false;
        }
    }

    private void ClearZoneColorPreferencesForView()
    {
        _workingZoneColorOverrides.Clear();
        _selectedZoneColorZoneId = null;
        _zoneColorPreferencesDirty = false;
        _suppressZoneColorUiEvents = true;
        try
        {
            LayoutZoneList.SelectedIndex = -1;
            ZoneColorEditorPanel.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressZoneColorUiEvents = false;
        }
    }

    private void LayoutZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressZoneColorUiEvents)
            return;

        RefreshZoneColorEditorFromSelection();
    }

    private void RefreshZoneColorEditorFromSelection()
    {
        var draft = _draft;
        if (draft is null || LayoutZoneList.SelectedIndex < 0)
        {
            _selectedZoneColorZoneId = null;
            ZoneColorEditorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var orderedZones = draft.Zones.OrderBy(zone => zone.SortOrder).ToArray();
        if (LayoutZoneList.SelectedIndex >= orderedZones.Length)
        {
            _selectedZoneColorZoneId = null;
            ZoneColorEditorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var zone = orderedZones[LayoutZoneList.SelectedIndex];
        var hasOverride = _workingZoneColorOverrides.TryGetValue(zone.ZoneId, out var overrideColor);
        var effectiveColor = hasOverride
            ? overrideColor
            : _settings.OverlayAppearance.ZoneColorRgb;

        _selectedZoneColorZoneId = zone.ZoneId;
        _selectedZoneWorkingColorRgb = effectiveColor;

        _suppressZoneColorUiEvents = true;
        try
        {
            ZoneColorEditorPanel.Visibility = Visibility.Visible;
            SelectedZoneColorNameText.Text = zone.Name;
            ZoneColorOverrideToggle.IsOn = hasOverride;
            SetZoneColorPickerColor(effectiveColor);
            UpdateSelectedZoneColorVisual(effectiveColor, hasOverride);

            var canPersist = !draft.IsNew;
            ZoneColorOverrideToggle.IsEnabled = canPersist;
            ZoneColorPickerButton.IsEnabled = canPersist;
            ApplyZoneColorsButton.IsEnabled = canPersist && _zoneColorPreferencesDirty;
            RevertZoneColorsButton.IsEnabled = canPersist && _zoneColorPreferencesDirty;
            ZoneColorHintText.Text = canPersist
                ? "关闭单独颜色时继承全局分区颜色。"
                : "先保存新布局，再为每个分区设置独立颜色。";
        }
        finally
        {
            _suppressZoneColorUiEvents = false;
        }
    }

    private void ZoneColorOverrideToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressZoneColorUiEvents ||
            _selectedZoneColorZoneId is not Guid zoneId || _draft is null || _draft.IsNew)
        {
            return;
        }

        if (ZoneColorOverrideToggle.IsOn)
        {
            _workingZoneColorOverrides[zoneId] = _selectedZoneWorkingColorRgb;
        }
        else
        {
            _workingZoneColorOverrides.Remove(zoneId);
            _selectedZoneWorkingColorRgb = _settings.OverlayAppearance.ZoneColorRgb;
            SetZoneColorPickerColor(_selectedZoneWorkingColorRgb);
        }

        MarkZoneColorPreferencesDirty();
        UpdateSelectedZoneColorVisual(
            ResolveZoneColorRgb(zoneId),
            ZoneColorOverrideToggle.IsOn);
        RenderLayoutCanvas();
        RefreshVisibleOverlayColors();
    }

    private void ZoneColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_initialized || _suppressZoneColorUiEvents ||
            _selectedZoneColorZoneId is not Guid zoneId || _draft is null || _draft.IsNew)
        {
            return;
        }

        var colorRgb = ToRgb(args.NewColor);
        _selectedZoneWorkingColorRgb = colorRgb;

        _suppressZoneColorUiEvents = true;
        try
        {
            ZoneColorOverrideToggle.IsOn = true;
        }
        finally
        {
            _suppressZoneColorUiEvents = false;
        }

        _workingZoneColorOverrides[zoneId] = colorRgb;
        MarkZoneColorPreferencesDirty();
        UpdateSelectedZoneColorVisual(colorRgb, hasOverride: true);
        RenderLayoutCanvas();
        RefreshVisibleOverlayColors();
    }

    private async void ApplyZoneColorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _draft.IsNew)
        {
            ShowError("还不能保存分区颜色", "先保存布局，再为它设置逐分区颜色。 ");
            return;
        }

        try
        {
            await EnsureStoresAsync();
            var latest = await _settingsStore.GetAsync();
            var preferences = latest.OverlayZoneColors;

            foreach (var zone in _draft.Zones)
            {
                preferences = _workingZoneColorOverrides.TryGetValue(zone.ZoneId, out var colorRgb)
                    ? preferences.WithOverride(zone.ZoneId, colorRgb)
                    : preferences.WithoutOverride(zone.ZoneId);
            }

            _settings = latest.WithOverlayZoneColors(preferences);
            await _settingsStore.SaveAsync(_settings);
            _zoneColorPreferencesDirty = false;
            RefreshZoneColorEditorFromSelection();

            ShowInfo("分区颜色已应用", "只保存显示偏好，不会改变布局规则或移动图标。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError("无法保存分区颜色", ex.Message);
        }
    }

    private async void RevertZoneColorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
            return;

        try
        {
            await EnsureStoresAsync();
            _settings = await _settingsStore.GetAsync();
            InitializeZoneColorPreferencesForDraft(_draft);
            RenderLayoutCanvas();
            RefreshVisibleOverlayColors();
            ShowInfo("颜色修改已还原", "已恢复这个布局上次应用的分区颜色。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowError("无法还原分区颜色", ex.Message);
        }
    }

    private void MarkZoneColorPreferencesDirty()
    {
        _zoneColorPreferencesDirty = true;
        ApplyZoneColorsButton.IsEnabled = _draft is { IsNew: false };
        RevertZoneColorsButton.IsEnabled = _draft is { IsNew: false };
    }

    private (byte R, byte G, byte B) ResolveZoneCanvasColor(Guid zoneId)
    {
        var rgb = ResolveZoneColorRgb(zoneId);
        return (
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
    }

    private uint ResolveZoneColorRgb(Guid zoneId)
        => _workingZoneColorOverrides.TryGetValue(zoneId, out var colorRgb)
            ? colorRgb
            : _settings.OverlayAppearance.ZoneColorRgb;

    private IReadOnlyDictionary<Guid, uint> GetCurrentZoneColorOverridesSnapshot()
        => _workingZoneColorOverrides.ToDictionary(pair => pair.Key, pair => pair.Value);

    private void RefreshVisibleOverlayColors()
    {
        if (!_overlayHost.IsVisible || _latestPreview is null || _draft is null)
            return;

        try
        {
            _overlayHost.Show(
                _latestPreview.Plan.TargetDisplay,
                _draft.Zones,
                _settings.OverlayAppearance,
                GetCurrentZoneColorOverridesSnapshot());
        }
        catch (Exception ex)
        {
            HideOverlay();
            ShowError("无法刷新桌面分区框颜色", ex.Message);
        }
    }

    private void UpdateSelectedZoneColorVisual(uint colorRgb, bool hasOverride)
    {
        var color = ToColor(colorRgb);
        SelectedZoneColorSwatch.Background = new SolidColorBrush(color);
        SelectedZoneColorHexText.Text = $"#{colorRgb:X6}";
        ZoneColorInheritanceText.Text = hasOverride
            ? "使用独立颜色"
            : $"继承全局 · #{_settings.OverlayAppearance.ZoneColorRgb:X6}";
    }

    private void SetZoneColorPickerColor(uint colorRgb)
    {
        var oldSuppress = _suppressZoneColorUiEvents;
        _suppressZoneColorUiEvents = true;
        try
        {
            ZoneColorPicker.Color = ToColor(colorRgb);
        }
        finally
        {
            _suppressZoneColorUiEvents = oldSuppress;
        }
    }

    private static Color ToColor(uint colorRgb)
        => Color.FromArgb(
            0xFF,
            (byte)((colorRgb >> 16) & 0xFF),
            (byte)((colorRgb >> 8) & 0xFF),
            (byte)(colorRgb & 0xFF));

    private static uint ToRgb(Color color)
        => ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
}
