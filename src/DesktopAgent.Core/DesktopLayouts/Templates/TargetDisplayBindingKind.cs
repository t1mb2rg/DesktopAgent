namespace DesktopAgent.Core.DesktopLayouts.Templates;

/// <summary>
/// 目标显示器绑定模式。
/// </summary>
public enum TargetDisplayBindingKind
{
    /// <summary>使用用户在 WPF 中选择的目标显示器。</summary>
    SelectedTarget = 0,

    /// <summary>始终使用主显示器。</summary>
    PrimaryDisplay = 1,

    /// <summary>固定设备名精确匹配，使用 StringComparison.Ordinal。</summary>
    ExactDeviceName = 2
}
