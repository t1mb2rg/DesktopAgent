namespace DesktopAgent.Core.Displays;

/// <summary>
/// 目标显示器的纯逻辑选择规则。
/// 不依赖 WPF、Win32 或 WindowsInterop；仅操作 <see cref="DisplayDescriptor"/> 列表。
/// </summary>
public static class TargetDisplaySelector
{
    /// <summary>
    /// 从显示器列表中选择当前目标显示器。
    /// </summary>
    /// <param name="displays">当前枚举到的所有显示器。</param>
    /// <param name="previousDeviceName">
    /// 上次选择的目标显示器 DeviceName。
    /// 传入非 null 值时，若该设备仍在列表中则保留；
    /// 否则自动回退到主显示器。
    /// </param>
    /// <returns>选中的目标显示器；列表为空时返回 null。</returns>
    public static DisplayDescriptor? SelectTarget(
        IReadOnlyList<DisplayDescriptor> displays,
        string? previousDeviceName = null)
    {
        if (displays.Count == 0)
        {
            return null;
        }

        // 优先保留上次选择的目标设备（按 DeviceName 匹配）。
        if (previousDeviceName is not null)
        {
            var preserved = displays.FirstOrDefault(d => d.DeviceName == previousDeviceName);
            if (preserved is not null)
            {
                return preserved;
            }
        }

        // 回退到主显示器。
        var primary = displays.FirstOrDefault(d => d.IsPrimary);
        if (primary is not null)
        {
            return primary;
        }

        // 最后的兜底：返回列表中第一块显示器。
        return displays[0];
    }
}
