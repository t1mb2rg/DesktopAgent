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
    }

    [Fact]
    public void CustomValues_AreValidatedAndPreserved()
    {
        var actual = new DesktopOverlayAppearanceSettings(
            DesktopOverlayAppearancePreset.Custom,
            borderThicknessDip: 5.5d,
            borderOpacity: 0.8d,
            fillOpacity: 0.2d,
            cornerRadiusDip: 24d);

        Assert.Equal(DesktopOverlayAppearancePreset.Custom, actual.Preset);
        Assert.Equal(5.5d, actual.BorderThicknessDip);
        Assert.Equal(0.8d, actual.BorderOpacity);
        Assert.Equal(0.2d, actual.FillOpacity);
        Assert.Equal(24d, actual.CornerRadiusDip);
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
}
