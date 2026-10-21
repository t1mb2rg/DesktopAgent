using DesktopAgent.Core.Productization;

namespace DesktopAgent.Tests.Productization;

public sealed class DesktopOverlayAppearanceSettingsTests
{
    [Theory]
    [InlineData(DesktopOverlayAppearancePreset.Light, 2d, 0.34d, 0.035d, 14d)]
    [InlineData(DesktopOverlayAppearancePreset.Standard, 3d, 0.52d, 0.075d, 16d)]
    [InlineData(DesktopOverlayAppearancePreset.Strong, 4d, 0.74d, 0.14d, 18d)]
    public void Presets_AreStable(
        DesktopOverlayAppearancePreset preset,
        double thickness,
        double borderOpacity,
        double fillOpacity,
        double cornerRadius)
    {
        var actual = DesktopOverlayAppearanceSettings.FromPreset(preset);

        Assert.Equal(preset, actual.Preset);
        Assert.Equal(thickness, actual.BorderThicknessDip);
        Assert.Equal(borderOpacity, actual.BorderOpacity);
        Assert.Equal(fillOpacity, actual.FillOpacity);
        Assert.Equal(cornerRadius, actual.CornerRadiusDip);
        Assert.Equal(DesktopOverlayAppearanceSettings.DefaultZoneColorRgb, actual.ZoneColorRgb);
    }

    [Fact]
    public void CustomValues_AreValidatedAndPreserved()
    {
        const uint customColor = 0x8A4FFFu;
        var actual = new DesktopOverlayAppearanceSettings(
            DesktopOverlayAppearancePreset.Custom,
            borderThicknessDip: 5.5d,
            borderOpacity: 0.8d,
            fillOpacity: 0.2d,
            cornerRadiusDip: 24d,
            zoneColorRgb: customColor);

        Assert.Equal(DesktopOverlayAppearancePreset.Custom, actual.Preset);
        Assert.Equal(5.5d, actual.BorderThicknessDip);
        Assert.Equal(0.8d, actual.BorderOpacity);
        Assert.Equal(0.2d, actual.FillOpacity);
        Assert.Equal(24d, actual.CornerRadiusDip);
        Assert.Equal(customColor, actual.ZoneColorRgb);
    }

    [Fact]
    public void ChangingColor_MarksAppearanceCustom_AndPreservesGeometry()
    {
        var standard = DesktopOverlayAppearanceSettings.Standard;

        var actual = standard.WithZoneColor(0xFFB900u);

        Assert.Equal(DesktopOverlayAppearancePreset.Custom, actual.Preset);
        Assert.Equal(0xFFB900u, actual.ZoneColorRgb);
        Assert.Equal(standard.BorderThicknessDip, actual.BorderThicknessDip);
        Assert.Equal(standard.BorderOpacity, actual.BorderOpacity);
        Assert.Equal(standard.FillOpacity, actual.FillOpacity);
        Assert.Equal(standard.CornerRadiusDip, actual.CornerRadiusDip);
    }

    [Theory]
    [InlineData(0.5d, 0.5d, 0.1d, 16d)]
    [InlineData(9d, 0.5d, 0.1d, 16d)]
    [InlineData(3d, 0.05d, 0.1d, 16d)]
    [InlineData(3d, 1.1d, 0.1d, 16d)]
    [InlineData(3d, 0.5d, -0.1d, 16d)]
    [InlineData(3d, 0.5d, 0.5d, 16d)]
    [InlineData(3d, 0.5d, 0.1d, 40d)]
    public void OutOfRangeValues_AreRejected(
        double thickness,
        double borderOpacity,
        double fillOpacity,
        double cornerRadius)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopOverlayAppearanceSettings(
                DesktopOverlayAppearancePreset.Custom,
                thickness,
                borderOpacity,
                fillOpacity,
                cornerRadius));
    }

    [Fact]
    public void ColorOutside24BitRgb_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopOverlayAppearanceSettings(
                DesktopOverlayAppearancePreset.Custom,
                3d,
                0.5d,
                0.1d,
                16d,
                0x01_000000u));
    }
}
