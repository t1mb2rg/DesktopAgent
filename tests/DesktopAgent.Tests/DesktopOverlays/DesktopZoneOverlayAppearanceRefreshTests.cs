using DesktopAgent.Core.Productization;
using DesktopAgent.WindowsInterop.DesktopOverlays;

namespace DesktopAgent.Tests.DesktopOverlays;

/// <summary>
/// 设置页改了分区框外观后，已经把线框显示出来的窗口必须一起更新；
/// 否则用户只能在「关闭线框 → 重新打开」之后才看得到新样式。
/// </summary>
public sealed class DesktopZoneOverlayAppearanceRefreshTests
{
    [Fact]
    public void DefaultAppearanceChange_NotifiesOnlyWhenTheValueActuallyDiffers()
    {
        var original = DesktopZoneOverlayHost.DefaultAppearance;
        var raised = new List<DesktopOverlayAppearanceSettings>();

        void Handler(object? sender, DesktopOverlayAppearanceSettings appearance)
            => raised.Add(appearance);

        DesktopZoneOverlayHost.DefaultAppearanceChanged += Handler;
        try
        {
            DesktopZoneOverlayHost.DefaultAppearance =
                DesktopOverlayAppearanceSettings.FromPreset(DesktopOverlayAppearancePreset.Strong);
            var afterFirstApply = Assert.Single(raised);
            Assert.Equal(DesktopOverlayAppearancePreset.Strong, afterFirstApply.Preset);

            // 同一份外观再应用一次不应该重建线框（避免设置页重复点「应用」时闪烁）。
            DesktopZoneOverlayHost.DefaultAppearance =
                DesktopOverlayAppearanceSettings.FromPreset(DesktopOverlayAppearancePreset.Strong);
            Assert.Single(raised);

            DesktopZoneOverlayHost.DefaultAppearance =
                DesktopOverlayAppearanceSettings.FromPreset(DesktopOverlayAppearancePreset.Light);
            Assert.Equal(2, raised.Count);
            Assert.Equal(DesktopOverlayAppearancePreset.Light, raised[1].Preset);
            Assert.Equal(
                DesktopOverlayAppearanceSettings.FromPreset(DesktopOverlayAppearancePreset.Light).BorderThicknessDip,
                raised[1].BorderThicknessDip);
        }
        finally
        {
            DesktopZoneOverlayHost.DefaultAppearanceChanged -= Handler;
            DesktopZoneOverlayHost.DefaultAppearance = original;
        }
    }

    [Fact]
    public void CustomAppearanceChange_IsNotifiedEvenWhenThePresetLabelIsUnchanged()
    {
        var original = DesktopZoneOverlayHost.DefaultAppearance;
        var raised = 0;

        void Handler(object? sender, DesktopOverlayAppearanceSettings appearance) => raised++;

        DesktopZoneOverlayHost.DefaultAppearanceChanged += Handler;
        try
        {
            var custom = DesktopOverlayAppearanceSettings.Standard.WithCustomValues(
                borderThicknessDip: 6d,
                borderOpacity: 0.9d,
                fillOpacity: 0.2d,
                cornerRadiusDip: 4d);
            DesktopZoneOverlayHost.DefaultAppearance = custom;
            Assert.Equal(1, raised);

            // preset 仍为 Custom，但厚度不同，仍要让线框重建。
            var thicker = custom.WithCustomValues(
                borderThicknessDip: 8d,
                borderOpacity: 0.9d,
                fillOpacity: 0.2d,
                cornerRadiusDip: 4d);
            DesktopZoneOverlayHost.DefaultAppearance = thicker;
            Assert.Equal(2, raised);
        }
        finally
        {
            DesktopZoneOverlayHost.DefaultAppearanceChanged -= Handler;
            DesktopZoneOverlayHost.DefaultAppearance = original;
        }
    }
}
