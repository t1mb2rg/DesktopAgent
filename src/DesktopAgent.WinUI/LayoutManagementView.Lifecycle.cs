namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    /// <summary>
    /// Deep Resident 只能释放没有交互工作状态的布局页。
    /// Draft、未应用的 Zone 颜色或可见 Overlay 都必须继续保留在内存中，
    /// 避免生命周期优化吞掉用户尚未保存/确认的工作。
    /// </summary>
    internal bool CanReleaseForDeepResident =>
        _draft is null &&
        !_zoneColorPreferencesDirty &&
        !_overlayHost.IsVisible;
}
