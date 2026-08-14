namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    /// <summary>
    /// Deep Resident 只能释放没有交互工作状态的布局页。
    /// Draft、未应用的 Zone 颜色、可见 Overlay、显式 Desktop Zone Editor 或正在进行的
    /// Direct Apply 都必须继续保留在内存中，避免生命周期优化吞掉用户尚未完成的工作/执行状态。
    /// </summary>
    internal bool CanReleaseForDeepResident =>
        _draft is null &&
        !_zoneColorPreferencesDirty &&
        !_overlayHost.IsVisible &&
        _desktopZoneEditorWindow is null &&
        !_directApplyInProgress;
}
