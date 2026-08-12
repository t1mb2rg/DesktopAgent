using DesktopAgent.Core.Productization;

namespace DesktopAgent.WinUI;

public sealed partial class OverlayAppearanceSettingsView
{
    /// <summary>
    /// 只有当前工作值已经与持久化值一致、且桌面预览没有显示时，
    /// 才允许 Deep Resident 丢弃设置页的重型预览资源。
    /// </summary>
    internal bool CanReleaseForDeepResident
    {
        get
        {
            if (_desktopPreviewHost.IsVisible)
                return false;

            var working = ReadWorkingAppearance();
            return AppearanceEquals(working, _persistedAppearance);
        }
    }

    private static bool AppearanceEquals(
        DesktopOverlayAppearanceSettings left,
        DesktopOverlayAppearanceSettings right)
        => left.Preset == right.Preset &&
           left.BorderThicknessDip.Equals(right.BorderThicknessDip) &&
           left.BorderOpacity.Equals(right.BorderOpacity) &&
           left.FillOpacity.Equals(right.FillOpacity) &&
           left.CornerRadiusDip.Equals(right.CornerRadiusDip) &&
           left.ZoneColorRgb == right.ZoneColorRgb;
}
