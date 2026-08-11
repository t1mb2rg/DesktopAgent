using DesktopAgent.Application.DesktopLayouts.Templates.Editing;
using DesktopAgent.Core.Productization;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.DesktopOverlays;
using DesktopAgent.WindowsInterop.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DesktopAgent.WinUI;

public sealed partial class OverlayAppearanceSettingsView : UserControl
{
    private readonly JsonDesktopProductSettingsStore _settingsStore =
        new(DesktopAgentDataPaths.DefaultSettingsPath);
    private readonly NativeDisplayTopologyReader _displayTopologyReader = new();
    private readonly DesktopZoneOverlayHost _desktopPreviewHost = new();

    private DesktopOverlayAppearanceSettings _persistedAppearance =
        DesktopOverlayAppearanceSettings.Standard;
    private DesktopOverlayAppearancePreset _workingPreset =
        DesktopOverlayAppearancePreset.Standard;
    private bool _initialized;
    private bool _suppressUiEvents;

    public OverlayAppearanceSettingsView()
    {
        InitializeComponent();
    }

    private async void OverlayAppearanceSettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        _initialized = true;
        try
        {
            await _settingsStore.InitializeAsync();
            var settings = await _settingsStore.GetAsync();
            _persistedAppearance = settings.OverlayAppearance;
            DesktopZoneOverlayHost.DefaultAppearance = _persistedAppearance;
            ApplyAppearanceToControls(_persistedAppearance);
        }
        catch (Exception ex)
        {
            ShowStatus("无法读取分区框设置", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OverlayAppearanceSettingsView_Unloaded(object sender, RoutedEventArgs e)
        => HideDesktopPreview();

    private void LightPresetButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
            return;
        ApplyPreset(DesktopOverlayAppearancePreset.Light);
    }

    private void StandardPresetButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
            return;
        ApplyPreset(DesktopOverlayAppearancePreset.Standard);
    }

    private void StrongPresetButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
            return;
        ApplyPreset(DesktopOverlayAppearancePreset.Strong);
    }

    private void ApplyPreset(DesktopOverlayAppearancePreset preset)
    {
        if (_suppressUiEvents)
            return;

        var appearance = DesktopOverlayAppearanceSettings.FromPreset(preset);
        ApplyAppearanceToControls(appearance);
    }

    private void AppearanceSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized || _suppressUiEvents)
            return;

        _workingPreset = DesktopOverlayAppearancePreset.Custom;
        SetPresetSelection(DesktopOverlayAppearancePreset.Custom);
        UpdateValueLabelsAndPreview();

        // 真实桌面预览也跟随当前参数，但只在用户已经主动打开它时刷新。
        if (_desktopPreviewHost.IsVisible)
            RefreshDesktopPreview();
    }

    private void ApplyAppearanceToControls(DesktopOverlayAppearanceSettings appearance)
    {
        _suppressUiEvents = true;
        try
        {
            _workingPreset = appearance.Preset;
            BorderThicknessSlider.Value = appearance.BorderThicknessDip;
            BorderOpacitySlider.Value = appearance.BorderOpacity * 100d;
            FillOpacitySlider.Value = appearance.FillOpacity * 100d;
            CornerRadiusSlider.Value = appearance.CornerRadiusDip;
            SetPresetSelection(appearance.Preset);
        }
        finally
        {
            _suppressUiEvents = false;
        }

        UpdateValueLabelsAndPreview();
    }

    private void SetPresetSelection(DesktopOverlayAppearancePreset preset)
    {
        var oldSuppress = _suppressUiEvents;
        _suppressUiEvents = true;
        try
        {
            LightPresetButton.IsChecked = preset == DesktopOverlayAppearancePreset.Light;
            StandardPresetButton.IsChecked = preset == DesktopOverlayAppearancePreset.Standard;
            StrongPresetButton.IsChecked = preset == DesktopOverlayAppearancePreset.Strong;
        }
        finally
        {
            _suppressUiEvents = oldSuppress;
        }
    }

    private DesktopOverlayAppearanceSettings ReadWorkingAppearance()
        => new(
            _workingPreset,
            BorderThicknessSlider.Value,
            BorderOpacitySlider.Value / 100d,
            FillOpacitySlider.Value / 100d,
            CornerRadiusSlider.Value);

    private void UpdateValueLabelsAndPreview()
    {
        BorderThicknessValueText.Text = $"{BorderThicknessSlider.Value:0.0} px";
        BorderOpacityValueText.Text = $"{BorderOpacitySlider.Value:0}%";
        FillOpacityValueText.Text = $"{FillOpacitySlider.Value:0}%";
        CornerRadiusValueText.Text = $"{CornerRadiusSlider.Value:0} px";
        AppearanceModeText.Text = _workingPreset switch
        {
            DesktopOverlayAppearancePreset.Light => "轻量预设",
            DesktopOverlayAppearancePreset.Standard => "标准预设",
            DesktopOverlayAppearancePreset.Strong => "明显预设",
            _ => "自定义",
        };

        var appearance = ReadWorkingAppearance();
        var borderBrush = new SolidColorBrush(Color.FromArgb(
            ToAlpha(appearance.BorderOpacity),
            0x00,
            0x78,
            0xD4));
        var fillBrush = new SolidColorBrush(Color.FromArgb(
            ToAlpha(appearance.FillOpacity),
            0x00,
            0x78,
            0xD4));
        var thickness = new Thickness(appearance.BorderThicknessDip);
        var radius = new CornerRadius(appearance.CornerRadiusDip);

        ApplyPreviewZoneStyle(PreviewLeftZone, borderBrush, fillBrush, thickness, radius);
        ApplyPreviewZoneStyle(PreviewRightZone, borderBrush, fillBrush, thickness, radius);
    }

    private static void ApplyPreviewZoneStyle(
        Border zone,
        Brush borderBrush,
        Brush fillBrush,
        Thickness thickness,
        CornerRadius radius)
    {
        zone.BorderBrush = borderBrush;
        zone.Background = fillBrush;
        zone.BorderThickness = thickness;
        zone.CornerRadius = radius;
    }

    private async void ApplyAppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var appearance = ReadWorkingAppearance();
            var latest = await _settingsStore.GetAsync();
            var updated = latest.WithOverlayAppearance(appearance);
            await _settingsStore.SaveAsync(updated);

            _persistedAppearance = appearance;
            DesktopZoneOverlayHost.DefaultAppearance = appearance;
            ShowStatus(
                "分区框外观已应用",
                "只更新显示偏好，不会移动图标或改变自动化权限。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus("无法保存分区框设置", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void CancelAppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyAppearanceToControls(_persistedAppearance);
        HideDesktopPreview();
        ShowStatus("已恢复", "未应用的外观修改已撤销。", InfoBarSeverity.Informational);
    }

    private void DesktopPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_desktopPreviewHost.IsVisible)
        {
            HideDesktopPreview();
            return;
        }

        RefreshDesktopPreview();
    }

    private void RefreshDesktopPreview()
    {
        try
        {
            var display = _displayTopologyReader.GetDisplays().SingleOrDefault(item => item.IsPrimary)
                ?? throw new InvalidOperationException("当前没有唯一主显示器，无法展示桌面示例框。 ");
            var zones = DesktopLayoutPresetFactory.CreateZones(DesktopLayoutPresetKind.TwoColumns);
            _desktopPreviewHost.Show(display, zones, ReadWorkingAppearance());
            DesktopPreviewButton.Content = "隐藏桌面预览";
            ShowStatus(
                "桌面预览已显示",
                "这是两栏示例，只验证外观；鼠标穿透，也不会触碰桌面图标。",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            HideDesktopPreview();
            ShowStatus("无法显示桌面预览", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void HideDesktopPreview()
    {
        _desktopPreviewHost.Hide();
        DesktopPreviewButton.Content = "在桌面上预览";
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        AppearanceStatusBar.Title = title;
        AppearanceStatusBar.Message = message;
        AppearanceStatusBar.Severity = severity;
        AppearanceStatusBar.IsOpen = true;
    }

    private static byte ToAlpha(double opacity)
        => (byte)Math.Clamp(
            (int)Math.Round(opacity * byte.MaxValue, MidpointRounding.AwayFromZero),
            byte.MinValue,
            byte.MaxValue);
}
